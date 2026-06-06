// Services/GeoJsonUtility.cs
// Constructs the exact GeoJSON FeatureCollection structure required by the
// DGCA DigitalSky airspace validation API and encodes it as a Base64 string.
//
// DGCA GEOM FIELD CONTRACT:
//   1. Build a GeoJSON FeatureCollection containing exactly one Feature
//   2. The Feature's geometry is a Polygon
//   3. GeoJSON coordinate order is [LONGITUDE, LATITUDE] — the OPPOSITE of Leaflet
//   4. The polygon ring MUST be closed (first point repeated as last point)
//   5. Serialize the FeatureCollection to a compact JSON string (no indentation)
//   6. UTF-8 encode the JSON string → byte[]
//   7. Base64 encode the byte[] → the final `geom` string
//
// WHY NOT USE System.Text.Json FOR THE INTERNAL MODELS?
//   The GeoJSON coordinate arrays are jagged (double[][]) — STJ handles this
//   correctly, but we avoid reflection overhead on a hot path by using
//   a manual string builder for the compact serialization below.

using System.Text;
using System.Text.Json;
using BlazorApp3.Models;

namespace BlazorApp3.Services;

public static class GeoJsonUtility
{
    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    /// Takes a closed or open list of coordinates (Lat/Lon order from Leaflet/MAVLink),
    /// builds the exact GeoJSON FeatureCollection the DGCA API requires,
    /// and returns it as a Base64-encoded string ready for the <c>geom</c> field.
    /// </summary>
    /// <param name="coordinates">
    ///   At least 3 points in Lat/Lon order.  Closing point (first == last) is
    ///   NOT required — this method always appends it automatically.
    /// </param>
    /// <exception cref="ArgumentException">Fewer than 3 coordinates supplied.</exception>
    public static string BuildFlightPlanGeom(IReadOnlyList<Coordinate> coordinates)
    {
        if (coordinates.Count < 3)
            throw new ArgumentException(
                "A GeoJSON Polygon requires at least 3 distinct vertices.", nameof(coordinates));

        // Validate all coordinates are plausible (not NaN / out-of-range)
        foreach (var c in coordinates)
        {
            if (double.IsNaN(c.Lat) || double.IsNaN(c.Lon))
                throw new ArgumentException($"NaN coordinate detected: ({c.Lat}, {c.Lon})");
            if (c.Lat is < -90 or > 90 || c.Lon is < -180 or > 180)
                throw new ArgumentException($"Out-of-range coordinate: ({c.Lat}, {c.Lon})");
        }

        var json = BuildFeatureCollectionJson(coordinates);
        var bytes = Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Reverses a Base64 geom string back to a formatted GeoJSON string.
    /// Useful for debug logging and displaying decoded payloads in the UI.
    /// </summary>
    public static string DecodeGeomToJson(string base64Geom)
    {
        var bytes = Convert.FromBase64String(base64Geom);
        var json  = Encoding.UTF8.GetString(bytes);

        // Re-format for readability (debug / logging only — never send indented to API)
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement,
            new JsonSerializerOptions { WriteIndented = true });
    }

    // ── Internal GeoJSON builder ──────────────────────────────────────────────

    /// <summary>
    /// Builds a compact GeoJSON FeatureCollection string.
    ///
    /// Output structure:
    /// <code>
    /// {
    ///   "type":"FeatureCollection",
    ///   "features":[{
    ///     "type":"Feature",
    ///     "geometry":{
    ///       "type":"Polygon",
    ///       "coordinates":[[[lon0,lat0],[lon1,lat1],...,[lon0,lat0]]]
    ///     },
    ///     "properties":{}
    ///   }]
    /// }
    /// </code>
    /// </summary>
    private static string BuildFeatureCollectionJson(IReadOnlyList<Coordinate> coords)
    {
        var sb = new StringBuilder(256 + coords.Count * 24);

        sb.Append("{\"type\":\"FeatureCollection\",\"features\":[{\"type\":\"Feature\",");
        sb.Append("\"geometry\":{\"type\":\"Polygon\",\"coordinates\":[[");

        // GeoJSON uses [longitude, latitude] — NOT the Leaflet/MAVLink [lat, lon] order
        for (int i = 0; i < coords.Count; i++)
        {
            if (i > 0) sb.Append(',');
            AppendCoord(sb, coords[i].Lon, coords[i].Lat);
        }

        // Close the ring: GeoJSON spec §3.1.6 requires first == last
        var first = coords[0];
        sb.Append(',');
        AppendCoord(sb, first.Lon, first.Lat);

        sb.Append("]]},\"properties\":{}}]}");
        return sb.ToString();
    }

    /// <summary>Appends a coordinate pair as [lon,lat] with 7 decimal places (~1cm precision).</summary>
    private static void AppendCoord(StringBuilder sb, double lon, double lat)
    {
        sb.Append('[');
        sb.Append(lon.ToString("F7", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(',');
        sb.Append(lat.ToString("F7", System.Globalization.CultureInfo.InvariantCulture));
        sb.Append(']');
    }

    // ── Area calculation ──────────────────────────────────────────────────────

    /// <summary>
    /// Computes the approximate area of a polygon in km² using the spherical
    /// excess formula.  Accurate to ~0.1% for polygons under 500km².
    /// Used to warn operators about unusually large flight plan areas.
    /// </summary>
    public static double ComputeAreaKm2(IReadOnlyList<Coordinate> coords)
    {
        if (coords.Count < 3) return 0;

        const double R = 6371.0; // Earth radius in km
        double area = 0;
        int n = coords.Count;

        for (int i = 0; i < n; i++)
        {
            var c1 = coords[i];
            var c2 = coords[(i + 1) % n];

            double lon1 = c1.Lon * Math.PI / 180;
            double lon2 = c2.Lon * Math.PI / 180;
            double lat1 = c1.Lat * Math.PI / 180;
            double lat2 = c2.Lat * Math.PI / 180;

            area += (lon2 - lon1) * (2 + Math.Sin(lat1) + Math.Sin(lat2));
        }

        return Math.Abs(area * R * R / 2);
    }
}
