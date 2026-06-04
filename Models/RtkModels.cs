// Models/RtkModels.cs
// All data models for the RTK Base Station / GPS Injection engine.
// All records are immutable — state is replaced atomically, never mutated in place.

using System;
using System.Collections.Generic;

namespace BlazorApp3.Models;

// ── Phase state machine ───────────────────────────────────────────────────────
public enum RtkPhase
{
    Idle       = 0,   // Not connected
    Connecting = 1,   // Serial port opened; SetupM8P commands being sent
    Survey     = 2,   // Survey-In in progress; monitoring UBX-NAV-SVIN
    Fixed      = 3,   // Survey-In valid; position locked (brief transitional)
    Injecting  = 4,   // RTCM corrections streaming → MavlinkService.InjectGpsData()
}

// ── Operator-configurable parameters ────────────────────────────────────────
public sealed record RtkConfig(
    string ComPort,
    int    BaudRate,
    bool   AutoConfig,
    bool   M8p130Plus,       // false=MSM7 (1077/1087), true=MSM4 (1074/1084)
    double TargetAccuracyM,  // Survey-In stops when accuracy ≤ this value
    int    MinDurationSec    // Survey-In stops when duration ≥ this value (seconds)
)
{
    public static RtkConfig Default => new(
        ComPort:          "COM3",
        BaudRate:         460800,
        AutoConfig:       true,
        M8p130Plus:       false,
        TargetAccuracyM:  0.200,
        MinDurationSec:   60
    );
}

// ── Survey-In telemetry (from UBX-NAV-SVIN, class=0x01, id=0x3B) ───────────
public sealed record SurveyInStatus(
    uint   DurationSec,       // Elapsed Survey-In time (seconds)
    uint   Observations,      // Number of valid position fixes observed
    double AccuracyM,          // Current position accuracy (metres)
    bool   Valid,              // true when Survey-In has successfully converged
    bool   Active              // true while Survey-In is in progress
)
{
    public static SurveyInStatus Empty => new(0, 0, 94568.32, false, false);

    /// <summary>Convergence percentage toward the target accuracy.</summary>
    public double ProgressPct(double targetM) =>
        Math.Min(100.0, (targetM / Math.Max(AccuracyM, 1e-6)) * 100.0);
}

// ── Fixed base position ───────────────────────────────────────────────────────
public sealed record BasePosition(
    double EcefX,   // metres
    double EcefY,
    double EcefZ,
    double Lat,     // degrees
    double Lng,     // degrees
    double Alt      // metres above ellipsoid
);

// ── Satellite signal data (from UBX-NAV-SAT, class=0x01, id=0x35) ───────────
public sealed record SatelliteInfo(
    string Id,        // e.g. "G12", "R04", "B03", "E17"
    byte   Snr,       // Carrier-to-noise ratio in dB-Hz
    bool   Active,    // true = used in navigation solution (flags bit 3)
    byte   GnssId,    // 0=GPS, 2=Galileo, 3=BeiDou, 6=GLONASS
    byte   SvId       // Satellite vehicle number
)
{
    public static string GnssPrefix(byte gnssId) => gnssId switch
    {
        0 => "G",
        2 => "E",
        3 => "B",
        5 => "Q",
        6 => "R",
        _ => "?"
    };
}

// ── Constellation health (expiry-based, matching Mission Planner's seenRTCM) ─
public sealed record ConstellationStatus(
    string   Id,           // "gps" | "glonass" | "galileo" | "beidou" | "base"
    string   Label,
    string   Color,
    bool     IsActive,
    DateTime LastSeenUtc
)
{
    public static readonly TimeSpan SignalExpiry = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan BaseExpiry   = TimeSpan.FromSeconds(20);

    public bool IsExpired(DateTime now) =>
        Id == "base"
            ? now - LastSeenUtc > BaseExpiry
            : now - LastSeenUtc > SignalExpiry;

    public static IReadOnlyList<ConstellationStatus> DefaultSet() =>
    [
        new("gps",     "GPS",     "#00f2ff", false, DateTime.MinValue),
        new("glonass", "GLONASS", "#3fb950", false, DateTime.MinValue),
        new("beidou",  "BeiDou",  "#f0b429", false, DateTime.MinValue),
        new("galileo", "Galileo", "#ff6b6b", false, DateTime.MinValue),
        new("base",    "Base",    "#a78bfa", false, DateTime.MinValue),
    ];
}

// ── Message counters (UBX + RTCM message-seen tracking) ──────────────────────
public sealed record MessageEntry(string Name, int Count);

// ── RTCM stream statistics ────────────────────────────────────────────────────
public sealed record RtkStreamStats(
    int  RxBps,             // Received bytes/sec × 8
    int  TxBps,             // Injected bytes/sec × 8
    long TotalMessages,     // Total UBX + RTCM frames processed
    int  LargestRtcmBytes,  // Largest single RTCM frame seen (bytes)
    int  FragmentsUsed,     // ceil(LargestRtcmBytes / 180), max 4
    bool HasOverflow        // true if LargestRtcmBytes > 540 (approaching 720B limit)
)
{
    public static RtkStreamStats Zero => new(0, 0, 0, 0, 0, false);
}

// ── Saved base profiles (persisted to local JSON) ────────────────────────────
public sealed record SavedBaseProfile(
    string Name,
    double EcefX,
    double EcefY,
    double EcefZ,
    double Lat,
    double Lng,
    double Alt,
    DateTime SavedUtc
);

// ── Aggregate state snapshot (single object handed to Blazor UI) ─────────────
public sealed record RtkState
{
    public RtkPhase                          Phase            { get; init; } = RtkPhase.Idle;
    public SurveyInStatus                    Survey           { get; init; } = SurveyInStatus.Empty;
    public BasePosition?                     FixedPosition    { get; init; }
    public IReadOnlyList<SatelliteInfo>      Satellites       { get; init; } = [];
    public IReadOnlyList<ConstellationStatus>Constellations   { get; init; } = ConstellationStatus.DefaultSet();
    public IReadOnlyList<MessageEntry>       Messages         { get; init; } = [];
    public RtkStreamStats                    Stream           { get; init; } = RtkStreamStats.Zero;
    public string?                           ActiveProfileName{ get; init; }   // Bug 1 fix: reset on disconnect
    public string?                           ErrorMessage     { get; init; }
    public IReadOnlyList<SavedBaseProfile>   SavedProfiles    { get; init; } = [];

    // Derived helpers used directly in the UI
    public bool IsActive    => Phase >= RtkPhase.Survey;
    public bool IsFixed     => Phase >= RtkPhase.Fixed;
    public bool IsInjecting => Phase == RtkPhase.Injecting;

    // Backward-compatibility properties used by legacy Setup.razor panels
    public bool IsSourceConnected => Phase != RtkPhase.Idle;
    public RtkPhase CurrentSurveyState => Phase;
}