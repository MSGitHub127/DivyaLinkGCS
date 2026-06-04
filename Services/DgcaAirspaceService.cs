using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

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
        _cache.Set(regionKey, result, CacheDuration);
        return result;
    }

    public void InvalidateCache() => _cache.Remove(CacheKey);

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
        // Centre of whatever region the user is currently viewing
        double cLat = (minLat + maxLat) / 2.0;
        double cLon = (minLon + maxLon) / 2.0;

        // Helper: GeoJSON Point feature with radius (DGCA circular zone format)
        static string Circle(
            string type, string name, string designator,
            double lat, double lon, int radiusMetres,
            int floorAgl, int ceilingAgl, string reason) =>
        $$"""
        {
          "type": "Feature",
          "geometry": {
            "type": "Point",
            "coordinates": [{{lon:F6}}, {{lat:F6}}]
          },
          "properties": {
            "type":           "{{type}}",
            "name":           "{{name}}",
            "designator":     "{{designator}}",
            "radius":         {{radiusMetres}},
            "lowerLimit":     {{floorAgl}},
            "upperLimit":     {{ceilingAgl}},
            "lowerLimitUnit": "AGL",
            "upperLimitUnit": "AGL",
            "reason":         "{{reason}}"
          }
        }
        """;

        return $$"""
{
  "type": "FeatureCollection",
  "features": [

    {{Circle(
        "RED",
        "MOCK-P101 Prohibited Zone",
        "P-101",
        cLat + 0.055, cLon - 0.080,
        radiusMetres: 1500,
        floorAgl: 0, ceilingAgl: 500,
        "MOCK DATA — Military airfield perimeter. Replace with live DGCA token."
    )}},

    {{Circle(
        "RED",
        "MOCK-R102 Restricted Zone",
        "R-102",
        cLat - 0.040, cLon + 0.095,
        radiusMetres: 800,
        floorAgl: 0, ceilingAgl: 300,
        "MOCK DATA — Government sensitive infrastructure."
    )}},

    {{Circle(
        "YELLOW",
        "MOCK-CTR Controlled Zone",
        "VAAH-CTR",
        cLat + 0.010, cLon + 0.035,
        radiusMetres: 5000,
        floorAgl: 0, ceilingAgl: 1500,
        "MOCK DATA — ATC clearance required above 400ft AGL."
    )}},

    {{Circle(
        "YELLOW",
        "MOCK-TMA Terminal Area",
        "VAAH-TMA",
        cLat - 0.080, cLon - 0.050,
        radiusMetres: 3500,
        floorAgl: 300, ceilingAgl: 3000,
        "MOCK DATA — High-traffic approach corridor."
    )}},

    {{Circle(
        "GREEN",
        "MOCK-G201 Uncontrolled Airspace",
        "UAS-G201",
        cLat - 0.060, cLon + 0.075,
        radiusMetres: 4000,
        floorAgl: 0, ceilingAgl: 400,
        "MOCK DATA — BVLOS operations require NPNT approval."
    )}},

    {{Circle(
        "GREEN",
        "MOCK-G202 Uncontrolled Rural",
        "UAS-G202",
        cLat + 0.090, cLon + 0.090,
        radiusMetres: 3000,
        floorAgl: 0, ceilingAgl: 400,
        "MOCK DATA — Standard DGCA drone rules apply."
    )}}

  ]
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