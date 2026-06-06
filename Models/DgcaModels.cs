// Models/DgcaModels.cs
// All C# representations of the DGCA DigitalSky Airspace Validation API.
// Endpoint: POST https://digitalsky.dgca.gov.in/digital-sky/flightplan/validate/airspace
//
// CRITICAL QUIRK: The `geom` field is NOT a GeoJSON object.
// It is a Base64-encoded UTF-8 string of a serialised GeoJSON FeatureCollection.
// See GeoJsonUtility.BuildFlightPlanGeom() for the encoding logic.

using System.Text.Json.Serialization;
using BlazorApp3.Services;

namespace BlazorApp3.Models;

// ── Coordinate ────────────────────────────────────────────────────────────────
// Shared coordinate model used by both the drawing JSInterop and the GeoJSON builder.
// Uses standard Lat/Lon order (as returned by Leaflet and used by MAVLink).
// GeoJSON requires the REVERSE [lon, lat] order — the utility handles this.

public sealed record Coordinate(double Lat, double Lon)
{
    /// <summary>Validates that the coordinate is within India's approximate bounding box.</summary>
    public bool IsWithinIndia() =>
        Lat is >= 6.0 and <= 37.5 &&
        Lon is >= 68.0 and <= 97.5;
}

// ── API Request ───────────────────────────────────────────────────────────────

public sealed class DgcaValidationRequest
{
    /// <summary>
    /// Maximum altitude in metres AGL.
    /// DGCA Green Zone limit is typically 60m (200ft) for micro-UAS.
    /// </summary>
    [JsonPropertyName("maxAltitude")]
    public double MaxAltitude { get; set; }

    /// <summary>Minimum altitude in metres AGL. Usually 0.</summary>
    [JsonPropertyName("minAltitude")]
    public double MinAltitude { get; set; }

    /// <summary>
    /// Mission start time in ISO-8601 format with milliseconds.
    /// Example: "2024-01-15T06:30:00.000Z"
    /// DGCA rejects requests with start times in the past.
    /// </summary>
    [JsonPropertyName("startDateTime")]
    public string StartDateTime { get; set; } = string.Empty;

    /// <summary>
    /// Mission end time in ISO-8601 format with milliseconds.
    /// Must be after StartDateTime. Maximum window: 24 hours (DGCA policy).
    /// </summary>
    [JsonPropertyName("endDateTime")]
    public string EndDateTime { get; set; } = string.Empty;

    /// <summary>
    /// Base64-encoded GeoJSON FeatureCollection string.
    /// Built by <see cref="GeoJsonUtility.BuildFlightPlanGeom"/>.
    /// This is NOT a JSON object — it is the Base64 encoding of the JSON text.
    /// </summary>
    [JsonPropertyName("geom")]
    public string Geom { get; set; } = string.Empty;

    // ── Factory helper ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a validated request from UI inputs.
    /// Throws <see cref="ArgumentException"/> if any field is invalid.
    /// </summary>
    public static DgcaValidationRequest Create(
        IReadOnlyList<Coordinate> polygon,
        double maxAltitude,
        double minAltitude,
        DateTime startUtc,
        DateTime endUtc)
    {
        if (polygon.Count < 3)
            throw new ArgumentException("Flight plan polygon must have at least 3 vertices.", nameof(polygon));
        if (maxAltitude <= minAltitude)
            throw new ArgumentException("maxAltitude must be greater than minAltitude.");
        if (startUtc >= endUtc)
            throw new ArgumentException("startDateTime must be before endDateTime.");
        if ((endUtc - startUtc).TotalHours > 24)
            throw new ArgumentException("Mission window cannot exceed 24 hours.");

        return new DgcaValidationRequest
        {
            MaxAltitude   = maxAltitude,
            MinAltitude   = minAltitude,
            StartDateTime = startUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            EndDateTime   = endUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Geom          = GeoJsonUtility.BuildFlightPlanGeom(polygon)
        };
    }
}

// ── API Response ──────────────────────────────────────────────────────────────

public sealed class DgcaValidationResponse
{
    /// <summary>"APPROVED", "CONDITIONAL", or "REJECTED"</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>Human-readable summary from DGCA.</summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>Individual zone violations found in the flight path.</summary>
    [JsonPropertyName("violations")]
    public List<DgcaViolation> Violations { get; set; } = [];

    /// <summary>Reference number for this validation — keep for compliance records.</summary>
    [JsonPropertyName("referenceNumber")]
    public string? ReferenceNumber { get; set; }

    // Convenience helpers for the UI layer
    public bool IsApproved    => Status.Equals("APPROVED",    StringComparison.OrdinalIgnoreCase);
    public bool IsConditional => Status.Equals("CONDITIONAL", StringComparison.OrdinalIgnoreCase);
    public bool IsRejected    => Status.Equals("REJECTED",    StringComparison.OrdinalIgnoreCase);
    public bool HasViolations => Violations.Count > 0;
}

public sealed class DgcaViolation
{
    /// <summary>"RED", "YELLOW", or "AMBER"</summary>
    [JsonPropertyName("zoneType")]
    public string ZoneType { get; set; } = string.Empty;

    /// <summary>Name of the airspace zone (e.g., "IGI Airport CTR").</summary>
    [JsonPropertyName("zoneName")]
    public string? ZoneName { get; set; }

    /// <summary>Human-readable description of why this zone was flagged.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>GeoJSON geometry of the conflicting zone boundary (for map overlay).</summary>
    [JsonPropertyName("geometry")]
    public object? Geometry { get; set; }

    // Colour mapping for direct use in Tailwind / Blazor class binding
    public string BadgeColour => ZoneType.ToUpperInvariant() switch
    {
        "RED"    => "bg-[#f85149]/20 text-[#f85149] border-[#f85149]/40",
        "YELLOW" => "bg-[#e3b341]/20 text-[#e3b341] border-[#e3b341]/40",
        "AMBER"  => "bg-[#f0b429]/20 text-[#f0b429] border-[#f0b429]/40",
        _        => "bg-[#8b949e]/20 text-[#8b949e] border-[#8b949e]/40"
    };
}

// ── API Error (400 Bad Request) ───────────────────────────────────────────────

public sealed class DgcaApiError
{
    [JsonPropertyName("error")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("details")]
    public List<string> Details { get; set; } = [];

    /// <summary>Catches any extra fields DGCA adds without breaking deserialization.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?> ExtraFields { get; set; } = [];
}

// ── Validation Result (wrapper returned to the Blazor component) ──────────────
// Avoids throwing exceptions into the UI layer for expected API failures.

public sealed class DgcaValidationResult
{
    public bool IsSuccess { get; init; }
    public DgcaValidationResponse? Response { get; init; }
    public DgcaApiError? ApiError { get; init; }
    public string? TransportError { get; init; }

    public static DgcaValidationResult Success(DgcaValidationResponse r) =>
        new() { IsSuccess = true, Response = r };

    public static DgcaValidationResult Failure(DgcaApiError error) =>
        new() { IsSuccess = false, ApiError = error };

    public static DgcaValidationResult NetworkFailure(string message) =>
        new() { IsSuccess = false, TransportError = message };

    /// <summary>Single display message for toast notifications.</summary>
    public string ToastMessage =>
        Response?.Message
        ?? ApiError?.Message
        ?? TransportError
        ?? "Unknown error";
}

// ── Drawing result DTO (received from Leaflet JS via JSInvokable) ─────────────

public sealed class DrawnPolygonDto
{
    [JsonPropertyName("coordinates")]
    public List<CoordinateDto> Coordinates { get; set; } = [];

    [JsonPropertyName("areaKm2")]
    public double AreaKm2 { get; set; }
}

public sealed class CoordinateDto
{
    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }

    public Coordinate ToCoordinate() => new(Lat, Lon);
}
