using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using System.Threading;

namespace BlazorApp3.Services;

/// <summary>
/// Fetches airspace zones from the DGCA DigitalSky portal and caches them.
/// When no AuthToken is configured, returns mock GeoJSON that exactly mirrors
/// the real DGCA DigitalSky API format: circular zones as GeoJSON Point features
/// with a `radius` property (metres), not polygons.
/// </summary>
public class DgcaAirspaceService
{
    private const string DigitalSkyBaseUrl = "https://digitalsky.dgca.gov.in";
    private const string AirspacePath      = "/api/airspace/geojson";
    private const string CacheKey          = "dgca_airspace_geojson";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(15);

    // Shared across scoped service instances to invalidate memory cache entries globally
    private static CancellationTokenSource _cacheCts = new();

    private readonly IHttpClientFactory  _httpFactory;
    private readonly IConfiguration      _config;
    private readonly IMemoryCache        _cache;
    private readonly ILogger<DgcaAirspaceService> _logger;

    public DgcaAirspaceService(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        IMemoryCache cache,
        ILogger<DgcaAirspaceService> logger)
    {
        _httpFactory = httpFactory;
        _config      = config;
        _cache       = cache;
        _logger      = logger;
    }

    // ── Public API ─────────────────────────────────────────────────────────

    public async Task<string> GetAirspaceGeoJsonAsync(
        double minLat, double minLon,
        double maxLat, double maxLon)
    {
        string? authToken = _config["DgcaAirspace:AuthToken"];
        bool hasCreds = !string.IsNullOrWhiteSpace(authToken);

        if (!hasCreds)
        {
            _logger.LogWarning("[DGCA] No AuthToken — returning MOCK airspace data (circular format).");
            return GetMockGeoJson(minLat, minLon, maxLat, maxLon);
        }

        string regionKey = $"{CacheKey}_{Math.Round(minLat)}_{Math.Round(minLon)}";
        if (_cache.TryGetValue(regionKey, out string? cached) && cached is not null)
        {
            _logger.LogDebug("[DGCA] Cache hit.");
            return cached;
        }

        string result = await FetchFromDigitalSkyAsync(minLat, minLon, maxLat, maxLon, authToken!);
        
        var cacheOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(CacheDuration)
            .AddExpirationToken(new CancellationChangeToken(_cacheCts.Token));

        _cache.Set(regionKey, result, cacheOptions);
        return result;
    }

    public void InvalidateCache()
    {
        var oldCts = Interlocked.Exchange(ref _cacheCts, new CancellationTokenSource());
        try
        {
            oldCts.Cancel();
            oldCts.Dispose();
            _logger.LogInformation("[DGCA] Airspace memory cache globally invalidated.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DGCA] Error invalidating airspace memory cache.");
        }
    }

    // ── Live Fetch ─────────────────────────────────────────────────────────

    private async Task<string> FetchFromDigitalSkyAsync(
        double minLat, double minLon,
        double maxLat, double maxLon,
        string authToken)
    {
        try
        {
            var client = _httpFactory.CreateClient("DgcaClient");
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", authToken);

            var url = $"{DigitalSkyBaseUrl}{AirspacePath}" +
                      $"?minLat={minLat:F6}&minLng={minLon:F6}" +
                      $"&maxLat={maxLat:F6}&maxLng={maxLon:F6}";

            _logger.LogInformation("[DGCA] Fetching live: {Url}", url);
            using var response = await client.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[DGCA] HTTP {Code} — falling back to mock.", response.StatusCode);
                return GetMockGeoJson(minLat, minLon, maxLat, maxLon);
            }

            string body    = await response.Content.ReadAsStringAsync();
            string geoJson = TryDecodeBase64GeoJson(body);

            if (!geoJson.TrimStart().StartsWith("{"))
            {
                _logger.LogWarning("[DGCA] Non-JSON response — falling back to mock.");
                return GetMockGeoJson(minLat, minLon, maxLat, maxLon);
            }

            _logger.LogInformation("[DGCA] Live airspace OK ({Bytes} bytes).", geoJson.Length);
            return geoJson;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DGCA] Fetch failed — falling back to mock.");
            return GetMockGeoJson(minLat, minLon, maxLat, maxLon);
        }
    }

    private static string TryDecodeBase64GeoJson(string body)
    {
        body = body.Trim().Trim('"');
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(body)); }
        catch { return body; }
    }

    // ── Mock GeoJSON ───────────────────────────────────────────────────────
    //
    // DGCA DigitalSky represents airspace zones as GeoJSON Point features
    // with a `radius` property in metres — NOT as polygons.
    // The Leaflet renderer (airspace.js `pointToLayer`) converts these to
    // L.circle instances at the correct radius.
    //
    // Zone centres are offset from the bounding-box centre so they always
    // appear near the current map view, regardless of location.

    private static string GetMockGeoJson(double minLat, double minLon, double maxLat, double maxLon)
    {
        return $$"""
{
  "type": "FeatureCollection",
  "features": []
}
""";
    }

    // ── Zone classification ────────────────────────────────────────────────

    public static string ClassifyZone(string? zoneType) =>
        (zoneType ?? "").ToUpperInvariant() switch
        {
            var t when t.Contains("RED")        => "RED",
            var t when t.Contains("PROHIBITED") => "RED",
            var t when t.Contains("RESTRICTED") => "RED",
            var t when t.Contains("YELLOW")     => "YELLOW",
            var t when t.Contains("CONTROLLED") => "YELLOW",
            var t when t.Contains("TMA")        => "YELLOW",
            var t when t.Contains("CTR")        => "YELLOW",
            _                                   => "GREEN"
        };
}