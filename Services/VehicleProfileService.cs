// Services/VehicleProfileService.cs
//
// SINGLE SOURCE OF TRUTH for the active vehicle context (ArduCopter or ArduRover).
//
// PROFILE RESOLUTION RULES (A3 — Safety Critical):
//   1. When a physical FC is connected, its HEARTBEAT MAV_TYPE ALWAYS wins.
//      If a manual override is active and conflicts, it is cleared immediately
//      (auto-snap). OnAutoSnap event fires so the UI can show a toast + message log.
//   2. When disconnected, the manual override (simulation mode) takes effect.
//   3. On app startup: always clean auto-detect state, no persisted overrides (A4).
//
// THREAD SAFETY:
//   UpdateFromHeartbeat() is called from the MavlinkService reader thread.
//   All state mutations are protected by _lock.
//   Event handlers are fired outside the lock — subscribers must InvokeAsync
//   to marshal onto the Blazor circuit thread before calling StateHasChanged().

namespace BlazorApp3.Services;

// ── Enums ─────────────────────────────────────────────────────────────────────

public enum VehicleProfile  { ArduCopter, ArduRover }
public enum ProfileSource   { AutoDetect, ManualOverride }

// ── Event args ────────────────────────────────────────────────────────────────

/// <summary>
/// Carried by <see cref="VehicleProfileService.OnAutoSnap"/> when a connected FC's
/// MAV_TYPE overrides an active manual simulation setting.
/// </summary>
public sealed class AutoSnapEventArgs
{
    public VehicleProfile OverriddenProfile { get; init; }
    public VehicleProfile SnappedToProfile  { get; init; }

    /// <summary>Ready-made string for the message panel log entry.</summary>
    public string LogMessage =>
        $"[VEHICLE] GCS context auto-corrected: " +
        $"{OverriddenProfile} (manual) → {SnappedToProfile} " +
        $"to match the connected flight controller's reported type.";

    /// <summary>Shorter string for the toast notification.</summary>
    public string ToastMessage =>
        $"Context updated to {SnappedToProfile} to match connected vehicle.";
}

// ── Service ───────────────────────────────────────────────────────────────────

public sealed class VehicleProfileService
{
    // ── Internal state ────────────────────────────────────────────────────────

    private volatile VehicleProfile _activeProfile = VehicleProfile.ArduRover; // safe default
    private VehicleProfile? _manualOverride = null;
    private VehicleProfile? _autoDetected   = null;
    private readonly object _lock           = new();

    // ── Public read-only state ────────────────────────────────────────────────

    public VehicleProfile  ActiveProfile  => _activeProfile;
    public VehicleProfile? AutoDetected   => _autoDetected;
    public VehicleProfile? ManualOverride => _manualOverride;

    public ProfileSource Source =>
        _manualOverride.HasValue ? ProfileSource.ManualOverride : ProfileSource.AutoDetect;

    public bool IsManualOverrideActive => _manualOverride.HasValue;
    public bool IsRover  => _activeProfile == VehicleProfile.ArduRover;
    public bool IsCopter => _activeProfile == VehicleProfile.ArduCopter;

    public string ActiveProfileDisplayName => _activeProfile switch
    {
        VehicleProfile.ArduRover  => "ArduRover",
        VehicleProfile.ArduCopter => "ArduCopter",
        _                         => "Unknown"
    };

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired on any profile change (manual or auto).
    /// Subscribers must InvokeAsync before calling StateHasChanged().
    /// </summary>
    public event Action<VehicleProfile>? OnProfileChanged;

    /// <summary>
    /// Fired specifically when a manual override is cleared by an incoming HEARTBEAT.
    /// MavlinkService relays this to Setup.razor / MainLayout.razor for toast + log.
    /// </summary>
    public event Action<AutoSnapEventArgs>? OnAutoSnap;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by the Simulation toggle in the navigation profile panel.
    /// Active only when the FC is disconnected; auto-snap will clear it on connection.
    /// </summary>
    public void SetManualOverride(VehicleProfile profile)
    {
        lock (_lock)
        {
            _manualOverride = profile;
            _activeProfile  = profile;
        }
        OnProfileChanged?.Invoke(profile);
    }

    /// <summary>
    /// Clears the manual override and returns to auto-detected state.
    /// Called by "Reset to auto-detect" in the Simulation panel.
    /// </summary>
    public void ClearManualOverride()
    {
        VehicleProfile active;
        lock (_lock)
        {
            _manualOverride = null;
            active = _autoDetected ?? _activeProfile;
            _activeProfile = active;
        }
        OnProfileChanged?.Invoke(active);
    }

    /// <summary>
    /// Called by MavlinkService on every HEARTBEAT.
    /// Returns true if the active profile changed (caller should show toast + log).
    /// Implements the A3 auto-snap safety rule.
    /// </summary>
    public bool UpdateFromHeartbeat(byte mavType)
    {
        var detected = MapMavType(mavType);
        if (detected == null) return false; // unknown MAV_TYPE — profile unchanged

        AutoSnapEventArgs? snapArgs = null;
        VehicleProfile?    changed  = null;

        lock (_lock)
        {
            _autoDetected = detected.Value;

            if (_manualOverride.HasValue && _manualOverride.Value != detected.Value)
            {
                // ── AUTO-SNAP: FC type conflicts with manual override ──────────
                snapArgs = new AutoSnapEventArgs
                {
                    OverriddenProfile = _manualOverride.Value,
                    SnappedToProfile  = detected.Value
                };
                _manualOverride = null;
                _activeProfile  = detected.Value;
                changed         = detected.Value;
            }
            else if (_activeProfile != detected.Value)
            {
                _activeProfile = detected.Value;
                changed        = detected.Value;
            }
        }

        // Fire events outside the lock (never hold a lock while raising events)
        if (snapArgs != null)
        {
            OnAutoSnap?.Invoke(snapArgs);
            OnProfileChanged?.Invoke(snapArgs.SnappedToProfile);
            return true;
        }

        if (changed.HasValue)
        {
            OnProfileChanged?.Invoke(changed.Value);
            return true;
        }

        return false;
    }

    // ── Flight mode helpers (used by MavlinkService) ─────────────────────────

    /// <summary>
    /// Returns the human-readable mode name for the active profile.
    /// Direct replacement for MavlinkService's hardcoded GetFlightModeString().
    /// </summary>
    public string GetFlightModeString(uint customMode)
    {
        lock (_lock)
        {
            return _activeProfile == VehicleProfile.ArduRover
                ? GetRoverModeString(customMode)
                : GetCopterModeString(customMode);
        }
    }

    /// <summary>
    /// Returns the numeric MAVLink mode ID for a given mode name string.
    /// Used by SaveFlightMode() when writing FLTMODEn parameters.
    /// </summary>
    public float GetModeValue(string modeName)
    {
        lock (_lock)
        {
            return _activeProfile == VehicleProfile.ArduRover
                ? GetRoverModeValue(modeName)
                : GetCopterModeValue(modeName);
        }
    }

    /// <summary>
    /// Ordered list of available mode name strings for UI dropdowns.
    /// Changes automatically when the profile changes.
    /// </summary>
    public IReadOnlyList<string> GetAvailableModes()
    {
        lock (_lock)
        {
            return _activeProfile == VehicleProfile.ArduRover ? RoverModes : CopterModes;
        }
    }

    // ── Static mode tables ────────────────────────────────────────────────────

    public static readonly IReadOnlyList<string> RoverModes =
    [
        "MANUAL", "ACRO", "STEERING", "HOLD", "LOITER",
        "FOLLOW", "SIMPLE", "AUTO", "RTL", "SMART_RTL",
        "GUIDED", "INITIALISING"
    ];

    public static readonly IReadOnlyList<string> CopterModes =
    [
        "STABILIZE", "ACRO", "ALT_HOLD", "AUTO", "GUIDED", "LOITER", "RTL",
        "CIRCLE", "LAND", "DRIFT", "SPORT", "FLIP", "AUTOTUNE", "POSHOLD",
        "BRAKE", "THROW", "AVOID_ADSB", "GUIDED_NOGPS", "SMART_RTL",
        "FLOWHOLD", "FOLLOW", "ZIGZAG"
    ];

    // ── Mode string resolvers ─────────────────────────────────────────────────

    private static string GetRoverModeString(uint mode) => mode switch
    {
        0  => "MANUAL",
        1  => "ACRO",
        3  => "STEERING",
        4  => "HOLD",
        5  => "LOITER",
        6  => "FOLLOW",
        7  => "SIMPLE",
        10 => "AUTO",
        11 => "RTL",
        12 => "SMART_RTL",
        15 => "GUIDED",
        16 => "INITIALISING",
        _  => $"MODE({mode})"
    };

    private static string GetCopterModeString(uint mode) => mode switch
    {
        0  => "STABILIZE",
        1  => "ACRO",
        2  => "ALT_HOLD",
        3  => "AUTO",
        4  => "GUIDED",
        5  => "LOITER",
        6  => "RTL",
        7  => "CIRCLE",
        9  => "LAND",
        11 => "DRIFT",
        13 => "SPORT",
        14 => "FLIP",
        15 => "AUTOTUNE",
        16 => "POSHOLD",
        17 => "BRAKE",
        18 => "THROW",
        19 => "AVOID_ADSB",
        20 => "GUIDED_NOGPS",
        21 => "SMART_RTL",
        22 => "FLOWHOLD",
        23 => "FOLLOW",
        24 => "ZIGZAG",
        _  => $"MODE({mode})"
    };

    private static float GetRoverModeValue(string name) =>
        name.Trim().ToUpperInvariant() switch
        {
            "MANUAL"       => 0,
            "ACRO"         => 1,
            "STEERING"     => 3,
            "HOLD"         => 4,
            "LOITER"       => 5,
            "FOLLOW"       => 6,
            "SIMPLE"       => 7,
            "AUTO"         => 10,
            "RTL"          => 11,
            "SMART_RTL"    => 12,
            "GUIDED"       => 15,
            "INITIALISING" => 16,
            _              => 0
        };

    private static float GetCopterModeValue(string name) =>
        name.Trim().ToUpperInvariant() switch
        {
            "STABILIZE"    => 0,
            "ACRO"         => 1,
            "ALT_HOLD"     => 2,
            "AUTO"         => 3,
            "GUIDED"       => 4,
            "LOITER"       => 5,
            "RTL"          => 6,
            "CIRCLE"       => 7,
            "LAND"         => 9,
            "DRIFT"        => 11,
            "SPORT"        => 13,
            "FLIP"         => 14,
            "AUTOTUNE"     => 15,
            "POSHOLD"      => 16,
            "BRAKE"        => 17,
            "THROW"        => 18,
            "AVOID_ADSB"   => 19,
            "GUIDED_NOGPS" => 20,
            "SMART_RTL"    => 21,
            "FLOWHOLD"     => 22,
            "FOLLOW"       => 23,
            "ZIGZAG"       => 24,
            _              => 0
        };

    // ── MAV_TYPE mapper ───────────────────────────────────────────────────────

    private static VehicleProfile? MapMavType(byte mavType) => mavType switch
    {
        // ArduCopter variants: Quad, Hexa, Octo, Tri
        2 or 13 or 14 or 15 => VehicleProfile.ArduCopter,
        // ArduRover: ground rovers of any kind
        10                  => VehicleProfile.ArduRover,
        // All others (fixed-wing, sub, blimp, etc.) — leave profile unchanged
        _                   => null
    };
}
