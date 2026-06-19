// Models/ParameterEntry.cs
// Single source of truth for one flight-controller parameter.
// Combines the raw MAVLink PARAM_VALUE fields with the human-readable
// metadata downloaded from the ArduPilot apm.pdef.xml dictionary.

using static MAVLink;

namespace DivyaLink.Models;

/// <summary>
/// Represents one flight-controller parameter in its full form:
/// MAVLink wire data + human-readable metadata + UI state.
/// </summary>
public sealed class ParameterEntry
{
    // ── MAVLink wire data ─────────────────────────────────────────────────

    /// <summary>
    /// Raw parameter name from the FC (e.g. "ATC_RAT_RLL_P").
    /// Exactly as received over MAVLink — 16-char ASCII, null-stripped.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Current value held by the FC.  Always stored as float (MAVLink wire type).
    /// Use <see cref="IntValue"/> / <see cref="UIntValue"/> for integer params.
    /// </summary>
    public float Value { get; set; }

    /// <summary>
    /// Snapshot of <see cref="Value"/> at the time of download (or last confirmed write).
    /// Used to detect unsaved edits.
    /// </summary>
    public float OriginalValue { get; set; }

    /// <summary>
    /// 0-based position in the full parameter list (param_index from PARAM_VALUE).
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// Total number of parameters reported by the FC (param_count from PARAM_VALUE).
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// MAVLink type tag — determines whether we display an integer, float, or enum.
    /// </summary>
    public MAV_PARAM_TYPE ParamType { get; set; }

    /// <summary>
    /// UTC timestamp of the last PARAM_VALUE received for this parameter.
    /// </summary>
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;

    // ── Metadata (populated by ParameterMetadataService) ─────────────────

    /// <summary>
    /// Short human name from the ArduPilot dictionary (e.g. "Rate Roll kP").
    /// Empty string if the param is not in the dictionary (custom / undocumented).
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Full description from the ArduPilot dictionary.
    /// Safe to display in a tooltip or details panel.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Physical unit string (e.g. "Hz", "m/s", "deg").
    /// Empty if not specified in the dictionary.
    /// </summary>
    public string Units { get; set; } = string.Empty;

    /// <summary>Minimum allowed value from the dictionary Range field.  Null if not specified.</summary>
    public float? RangeMin { get; set; }

    /// <summary>Maximum allowed value from the dictionary Range field.  Null if not specified.</summary>
    public float? RangeMax { get; set; }

    /// <summary>Suggested editing step from the dictionary Increment field.  Null if not specified.</summary>
    public float? Increment { get; set; }

    /// <summary>
    /// Ordered list of allowed values for enum-type parameters.
    /// Key = integer code, Value = human label (e.g. 0 → "Stabilize").
    /// Null for non-enum parameters.
    /// </summary>
    public IReadOnlyDictionary<int, string>? AllowedValues { get; set; }

    // ── Write-queue state ─────────────────────────────────────────────────

    /// <summary>
    /// True when a PARAM_SET has been sent and we are waiting for the echo.
    /// </summary>
    public bool IsWritePending { get; set; }

    // ── Derived helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Top-level group prefix derived from the parameter name.
    /// "ATC_RAT_RLL_P" → "ATC".  Parameters with no underscore → their full name.
    /// </summary>
    public string Group =>
        Id.Contains('_') ? Id[..Id.IndexOf('_')] : Id;

    /// <summary>
    /// True when <see cref="Value"/> differs from <see cref="OriginalValue"/>.
    /// Used to highlight unsaved edits in the UI.
    /// </summary>
    public bool IsDirty =>
        Math.Abs(Value - OriginalValue) > 1e-7f;

    /// <summary>Whether the parameter holds an integer type on the wire.</summary>
    public bool IsInteger => ParamType is
        MAV_PARAM_TYPE.INT8  or MAV_PARAM_TYPE.INT16  or MAV_PARAM_TYPE.INT32 or
        MAV_PARAM_TYPE.UINT8 or MAV_PARAM_TYPE.UINT16 or MAV_PARAM_TYPE.UINT32;

    /// <summary>Whether the parameter should be rendered as a dropdown in the UI.</summary>
    public bool IsEnum => AllowedValues is { Count: > 0 };

    /// <summary>
    /// Type-safe integer accessor.  Only meaningful when <see cref="IsInteger"/> is true.
    /// </summary>
    public int IntValue
    {
        get => (int)Value;
        set => Value = value;
    }

    /// <summary>
    /// Display-friendly value string.
    /// — Enum params:    "2 (AltHold)"
    /// — Integer params: "1024"
    /// — Float params:   "0.135" (6 significant figures, stripped trailing zeros)
    /// </summary>
    public string DisplayValue =>
        IsEnum && AllowedValues!.TryGetValue((int)Value, out var label)
            ? $"{(int)Value} ({label})"
            : IsInteger
                ? ((int)Value).ToString()
                : Value.ToString("G6");

    /// <summary>
    /// True if <see cref="Value"/> is within the documented range (or no range specified).
    /// </summary>
    public bool IsInRange =>
        (RangeMin is null || Value >= RangeMin) &&
        (RangeMax is null || Value <= RangeMax);

    // ── Lifecycle ─────────────────────────────────────────────────────────

    /// <summary>
    /// Call after a write has been echo-confirmed by the FC.
    /// Resets dirty state and clears write-pending flag.
    /// </summary>
    public void CommitWrite()
    {
        OriginalValue   = Value;
        IsWritePending  = false;
        LastUpdatedUtc  = DateTime.UtcNow;
    }
}
