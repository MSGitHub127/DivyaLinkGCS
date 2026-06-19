using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;
using System.Threading;

namespace DivyaLink.Services;

/// <summary>
/// Fetches airspace zones from the DGCA DigitalSky portal and caches them.
///
/// When no AuthToken is configured, returns an EMPTY GeoJSON FeatureCollection
/// so no zones are rendered on the map.
///
/// To enable live airspace data add to appsettings.json:
///   "DgcaAirspace": { "AuthToken": "<your-token>" }
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
            // No token configured — return empty so no zones appear on the map.
            // Previously this returned mock zones; that behaviour is now disabled.
            // To enable live airspace add "DgcaAirspace:AuthToken" to appsettings.json.
            _logger.LogDebug("[DGCA] No AuthToken — airspace overlay disabled (returning empty).");
            return EmptyGeoJson();
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
                // API unavailable — return empty rather than showing fake zones.
                _logger.LogWarning("[DGCA] HTTP {Code} — returning empty (no mock fallback).",
                    response.StatusCode);
                return EmptyGeoJson();
            }

            string body    = await response.Content.ReadAsStringAsync();
            string geoJson = TryDecodeBase64GeoJson(body);

            if (!geoJson.TrimStart().StartsWith("{"))
            {
                _logger.LogWarning("[DGCA] Non-JSON response — returning empty.");
                return EmptyGeoJson();
            }

            _logger.LogInformation("[DGCA] Live airspace OK ({Bytes} bytes).", geoJson.Length);
            return geoJson;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DGCA] Fetch failed — returning empty.");
            return EmptyGeoJson();
        }
    }

    private static string TryDecodeBase64GeoJson(string body)
    {
        body = body.Trim().Trim('"');
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(body)); }
        catch { return body; }
    }

    // ── Empty GeoJSON ──────────────────────────────────────────────────────
    // Returned whenever airspace data is unavailable (no token, HTTP error,
    // parse failure). Leaflet's airspace.loadZones() receives a FeatureCollection
    // with zero features and clears any previously-rendered zones without throwing.
    private static string EmptyGeoJson() =>
        """{"type":"FeatureCollection","features":[]}""";

    // ── Zone classification ────────────────────────────────────────────────
    // Kept intact — still used by the proximity-check path in airspace.js
    // when processing LIVE data from DigitalSky.

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

    // ── GetMockGeoJson REMOVED ─────────────────────────────────────────────
    // The private GetMockGeoJson() method that generated fake RED/YELLOW/GREEN
    // circular zones has been deleted. If you need to restore it for development
    // purposes, retrieve it from version control and guard it with:
    //   #if DEBUG
    //   private static string GetMockGeoJson(...) { ... }
    //   #endif
    // and update the no-token path above to call it only in DEBUG builds.
}