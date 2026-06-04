// Models/ParameterDefinition.cs
// Extended parameter model used by TuningGroup.razor for all control types.
// Supports numeric spinboxes, dropdown selects, and checkboxes — covering
// every ArduPilot parameter type including notch filter enums and bitmasks.

namespace BlazorApp3.Models;

// ── Control type discriminator ────────────────────────────────────────────────

public enum ParameterControlType
{
    Numeric,    // Spinner with up/down arrows — PIDs, frequencies, speeds
    Select,     // Dropdown with integer→label mapping — Enable/Disable, Mode enums
    Checkbox,   // Single boolean toggle stored as 0.0 / 1.0 float in ArduPilot
}

// ── Parameter definition ──────────────────────────────────────────────────────

public sealed class ParameterDefinition
{
    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>Modern ArduPilot parameter name (preferred, tried first).</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Legacy parameter name for older firmware.
    /// Example: "STEER2SRV_P" is legacy for "ATC_STR_RAT_P".
    /// Tried if Name is not present in ParameterManager.
    /// </summary>
    public string? LegacyName { get; init; }

    // ── Display ───────────────────────────────────────────────────────────────

    /// <summary>Short label shown in the left column of the parameter row.</summary>
    public required string Label { get; init; }

    /// <summary>Unit displayed after the label (e.g. "Hz", "m/s", "cm/s").</summary>
    public string? Unit { get; init; }

    /// <summary>Tooltip shown on hover — full parameter description.</summary>
    public string? Tooltip { get; init; }

    // ── Control ───────────────────────────────────────────────────────────────

    public ParameterControlType ControlType { get; init; } = ParameterControlType.Numeric;

    // Numeric fields
    public float Min  { get; init; } = 0f;
    public float Max  { get; init; } = 9999f;
    public float Step { get; init; } = 0.01f;

    /// <summary>
    /// Options for Select controls. Key = numeric value stored in ArduPilot (as float).
    /// Example: { 0, "Disabled" }, { 1, "Enabled" }
    /// </summary>
    public IReadOnlyDictionary<int, string>? Options { get; init; }

    /// <summary>ArduPilot default value. Shown as placeholder when FC not connected.</summary>
    public float Default { get; init; } = 0f;
}

// ── Parameter group definition ────────────────────────────────────────────────

public sealed class ParameterGroup
{
    public required string Title       { get; init; }
    public required string AccentColor { get; init; }
    public required IReadOnlyList<ParameterDefinition> Params { get; init; }
}

// ── Common option dictionaries ─────────────────────────────────────────────────

public static class ParamOptions
{
    public static readonly Dictionary<int, string> EnableDisable = new()
    {
        { 0, "Disabled" }, { 1, "Enabled" }
    };

    public static readonly Dictionary<int, string> HntchMode = new()
    {
        { 0, "Fixed" }, { 1, "Throttle" }, { 2, "RPM Sensor" },
        { 3, "ESC RPM" }, { 4, "FFT" }, { 5, "RPM Sensor 2" }, { 6, "DFFT" }
    };

    public static readonly Dictionary<int, string> HntchHarmonics = new()
    {
        { 0, "None" }, { 1, "1st" }, { 2, "2nd" }, { 3, "1st+2nd" },
        { 4, "3rd" }, { 5, "1st+3rd" }, { 7, "1st+2nd+3rd" },
        { 8, "4th" }, { 15, "1st-4th" }
    };

    public static readonly Dictionary<int, string> MotorType = new()
    {
        { 0, "Normal" }, { 1, "OneShot" }, { 2, "OneShot125" },
        { 3, "BrushedWithRelay" }, { 4, "BrushedBiPolar" },
        { 5, "DShot150" }, { 6, "DShot300" }, { 7, "DShot600" }, { 8, "DShot1200" }
    };
}