using DivyaLink.Services;
using Microsoft.AspNetCore.Mvc;

namespace DivyaLink.Controllers;

/// <summary>
/// Exposes DGCA DigitalSky airspace zones as a plain REST endpoint.
/// JS fetches this on every map 'moveend' event.
/// Returns raw GeoJSON — no Blazor circuit involved.
/// </summary>
[ApiController]
[Route("api/airspace")]
public class AirspaceController : ControllerBase
{
    private readonly DgcaAirspaceService _svc;
    private readonly ILogger<AirspaceController> _logger;

    public AirspaceController(DgcaAirspaceService svc, ILogger<AirspaceController> logger)
    {
        _svc    = svc;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/airspace?minLat=&minLon=&maxLat=&maxLon=
    /// Returns a GeoJSON FeatureCollection for the given bounding box.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAirspace(
        [FromQuery] double minLat,
        [FromQuery] double minLon,
        [FromQuery] double maxLat,
        [FromQuery] double maxLon)
    {
        // Basic sanity check — reject garbage coordinates
        if (minLat < -90 || maxLat > 90 || minLon < -180 || maxLon > 180 || minLat >= maxLat)
        {
            _logger.LogWarning("[AirspaceController] Invalid bbox: {MinLat},{MinLon},{MaxLat},{MaxLon}",
                minLat, minLon, maxLat, maxLon);
            return BadRequest("Invalid bounding box.");
        }

        string geoJson = await _svc.GetAirspaceGeoJsonAsync(minLat, minLon, maxLat, maxLon);

        // Return as application/json with no extra envelope — JS parses it directly
        return Content(geoJson, "application/json");
    }
}