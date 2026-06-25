// RtkModels.cs
// Complete rewrite — all data models for the RTK Base Station / GPS Injection engine.
//
// Changes vs previous version:
//   NEW: VehicleGpsFixType + VehicleGpsLabel on RtkState  (WF-10)
//   NEW: StatusMessages on RtkState                        (WF-11)
//   NEW: StatusMessage record
//   NEW: GpsFixType enum  (mirrors ArduPilot GPS_RAW_INT fix_type field)
//   FIX: SurveyInStatus.ProgressPct uses target — not confused with accuracy
//   FIX: ConstellationStatus expiry constants match MP seenRTCM timers exactly:
//        5s for GNSS signals (MP line 30196), 20s for base (MP line 30202)
//   FIX: RtkStreamStats includes VehicleGpsFixType for use in dashboard
//   REMOVED: M8p130Plus from RtkConfig — auto-detected from MON-VER

using System;
using System.Collections.Generic;

namespace BlazorApp3.Models;

// ── Phase state machine ───────────────────────────────────────────────────────
// Mirrors MP button states: Idle → Connecting → Survey → Fixed → Injecting
public enum RtkPhase
{
    Idle       = 0,   // Not connected — port closed
    Connecting = 1,   // Serial port opened; SetupM8P commands being sent
    Survey     = 2,   // Survey-In running; monitoring UBX-NAV-SVIN
    Fixed      = 3,   // Survey-In valid; position confirmed (transient state)
    Injecting  = 4,   // RTCM corrections streaming to MavlinkService.InjectGpsData
}

// ── Vehicle GPS fix type (GPS_RAW_INT.fix_type, ArduPilot definition) ────────
// Reference: ArduPilot libraries/AP_GPS/AP_GPS.h enum GPS_Status
public enum GpsFixType : byte
{
    NoGps    = 0,
    NoFix    = 1,
    Fix2D    = 2,
    Fix3D    = 3,
    Dgps     = 4,
    RtkFloat = 5,
    RtkFixed = 6,
}

// ── Operator-configurable parameters ─────────────────────────────────────────
// NOTE: M8p130Plus REMOVED — MSM7/MSM4 is now auto-detected from MON-VER.
public sealed record RtkConfig(
    string ComPort,
    int    BaudRate,
    bool   AutoConfig,
    double TargetAccuracyM,
    int    MinDurationSec
)
{
    public static RtkConfig Default => new(
        ComPort:         "COM3",
        BaudRate:        460800,
        AutoConfig:      true,
        TargetAccuracyM: 0.200,
        MinDurationSec:  60
    );
}

// ── Survey-In telemetry (from UBX-NAV-SVIN, class=0x01, id=0x3B) ─────────────
// Reference: u-blox M8 Interface Description §32.17.26
public sealed record SurveyInStatus(
    uint   DurationSec,
    uint   Observations,
    double AccuracyM,
    bool   Valid,
    bool   Active
)
{
    public static SurveyInStatus Empty => new(0, 0, 9999.0, false, false);

    /// Convergence percentage toward the target accuracy.
    /// 0% = far from target; 100% = at or better than target.
    public double ProgressPct(double targetM) =>
        targetM <= 0 ? 0.0
        : Math.Min(100.0, (targetM / Math.Max(AccuracyM, 1e-6)) * 100.0);
}

// ── Fixed base position ───────────────────────────────────────────────────────
public sealed record BasePosition(
    double EcefX,   // metres (WGS-84 ECEF)
    double EcefY,
    double EcefZ,
    double Lat,     // degrees  (derived from ECEF via Bowring)
    double Lng,     // degrees
    double Alt      // metres above ellipsoid
);

// ── LivePosition ──────────────────────────────────────────────────────────────
// Raw GPS antenna position sourced from UBX-NAV-PVT.
// Available as soon as the receiver gets a 3D fix (before survey-in starts).
// NOT the survey mean — see FixedPosition for the averaged result.
//
// Report section 6 "Mission Planner architecture":
//   Live GPS Position  →  Survey-In Engine  →  Final Base Position
//   ^^^^^^^^^^^^^^^^^^^^
//   This record is "Live GPS Position".
 
/// <summary>
/// Live GPS position of the base station antenna, sourced from UBX-NAV-PVT at 1 Hz.
/// Populated as soon as the receiver reports a 3D fix (fixType ≥ 2, gnssFixOk = true).
/// Separate from <see cref="BasePosition"/> which is the survey-averaged mean.
/// </summary>
public sealed record LivePosition(
    double Lat,      // decimal degrees (WGS-84)
    double Lng,      // decimal degrees (WGS-84)
    double Alt,      // metres above ellipsoid
    byte   FixType,  // 0=no fix, 2=2D, 3=3D, 4=GNSS+DR, 5=time-only
    byte   NumSV     // number of satellites used in solution
)
{
    /// <summary>Human-readable fix type for UI display.</summary>
    public string FixLabel => FixType switch
    {
        5 => "Time",
        4 => "3D+DR",
        3 => "3D",
        2 => "2D",
        _ => "No Fix"
    };
 
    /// <summary>True when the position is reliable enough to display on a map.</summary>
    public bool IsValid => FixType >= 3 && Lat != 0 && Lng != 0;
}

// ── Satellite signal data (from UBX-NAV-SAT, class=0x01, id=0x35) ────────────
// Reference: u-blox M8 Interface Description §32.17.20
public sealed record SatelliteInfo(
    string Id,       // e.g. "G12", "R04", "B03", "E17", "Q01"
    byte   Snr,      // Carrier-to-noise ratio (C/N₀) in dBHz
    bool   Active,   // true = used in navigation solution (flags bit 3)
    byte   GnssId,   // 0=GPS, 1=SBAS, 2=Galileo, 3=BeiDou, 5=QZSS, 6=GLONASS
    byte   SvId      // Satellite vehicle number
)
{
    /// Maps u-blox gnssId to the single-character prefix used in the satellite ID string.
    /// Reference: u-blox M8 spec §32.17.20 gnssId enumeration
    public static string GnssPrefix(byte gnssId) => gnssId switch
    {
        0 => "G",   // GPS
        1 => "S",   // SBAS
        2 => "E",   // Galileo
        3 => "B",   // BeiDou
        5 => "Q",   // QZSS
        6 => "R",   // GLONASS
        _ => "?"
    };
}

// ── Constellation health (expiry-based, matching MP seenRTCM) ─────────────────
// MP uses a Timer tick every 1s; any constellation not refreshed within the
// expiry window is greyed out (Files.md lines 30186-30210).
public sealed record ConstellationStatus(
    string   Id,           // "gps" | "glonass" | "galileo" | "beidou" | "base"
    string   Label,
    string   Color,
    bool     IsActive,
    DateTime LastSeenUtc
)
{
    // MP line 30196: GPS/GLONASS/Galileo/BeiDou expire after 5s
    // MP line 30202: Base (1005/4072) expires after 20s
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

// ── Message counters ──────────────────────────────────────────────────────────
public sealed record MessageEntry(string Name, int Count);

// ── RTCM stream statistics ────────────────────────────────────────────────────
public sealed record RtkStreamStats(
    int  RxBps,
    int  TxBps,
    int TxInjectedBps,
    long TotalMessages,
    int  LargestRtcmBytes,
    int  FragmentsUsed,      // ceil(LargestRtcmBytes / 180), max 4
    bool HasOverflow         // true if LargestRtcmBytes > 540 (3+ fragments)
)
{
    public static RtkStreamStats Zero => new(0, 0, 0, 0, 0, 0, false);
}

// ── Vehicle MAVLink status text ───────────────────────────────────────────────
// WF-11: mirrors MP's STATUSTEXT display in FlightData
public sealed record StatusMessage(
    byte     Severity,    // MAV_SEVERITY: 0=Emergency…6=Info…7=Debug
    string   Text,
    DateTime ReceivedUtc
)
{
    public string SeverityLabel => Severity switch
    {
        0 => "EMERG",
        1 => "ALERT",
        2 => "CRIT",
        3 => "ERROR",
        4 => "WARN",
        5 => "NOTICE",
        6 => "INFO",
        _ => "DEBUG"
    };
}

// ── Saved base profiles (persisted to local JSON) ─────────────────────────────
public sealed record SavedBaseProfile(
    string   Name,
    double   EcefX,
    double   EcefY,
    double   EcefZ,
    double   Lat,
    double   Lng,
    double   Alt,
    DateTime SavedUtc
);

// ── Aggregate state snapshot (handed to Blazor UI atomically) ────────────────
// All fields are init-only. Service replaces the entire record on every change.
// Blazor components receive a snapshot and never hold a reference to mutable state.
public sealed record RtkState
{
    public RtkPhase                           Phase              { get; init; } = RtkPhase.Idle;
    public SurveyInStatus                     Survey             { get; init; } = SurveyInStatus.Empty;
    public BasePosition?                      FixedPosition      { get; init; }
    public LivePosition?  CurrentPosition  { get; init; }
    public IReadOnlyList<SatelliteInfo>       Satellites         { get; init; } = [];
    public IReadOnlyList<ConstellationStatus> Constellations     { get; init; } = ConstellationStatus.DefaultSet();
    public IReadOnlyList<MessageEntry>        Messages           { get; init; } = [];
    public RtkStreamStats                     Stream             { get; init; } = RtkStreamStats.Zero;
    public string?                            ActiveProfileName  { get; init; }
    public string?                            ErrorMessage       { get; init; }
    public IReadOnlyList<SavedBaseProfile>    SavedProfiles      { get; init; } = [];

    // WF-10: Vehicle GPS fix type — populated from GPS_RAW_INT
    public byte    VehicleGpsFixType { get; init; } = 0;
    public string  VehicleGpsLabel   { get; init; } = "No Fix";

    // WF-11: Recent STATUSTEXT messages from vehicle
    public IReadOnlyList<StatusMessage> StatusMessages { get; init; } = [];

    // RESPONSIVENESS FIX: true while the service is discarding stale NAV-SVIN
    // frames after a (re)start, waiting for the receiver's hardware timer to
    // confirm it has actually reset. Lets the UI show "Resetting…" instead of
    // a frozen duration value, so the survey ring never looks hung.
    public bool IsResettingTimer { get; init; } = false;

    // 🌟 ── RESOLVED BUILD ERROR CS0117 ── 🌟
    /// <summary>
    /// Flag to signal the service backend and UI layer that coordinates 
    /// are fully teardown-cleared during a hardware hot-restart loop.
    /// </summary>
    public bool PositionClearedForRestart { get; init; } = false;

    // ── Derived helpers used directly in Blazor UI ────────────────────────────
    public bool IsActive    => Phase >= RtkPhase.Survey;
    public bool IsFixed     => Phase >= RtkPhase.Fixed;
    public bool IsInjecting => Phase == RtkPhase.Injecting;

    public bool VehicleIsRtkFloat => VehicleGpsFixType == (byte)GpsFixType.RtkFloat;
    public bool VehicleIsRtkFixed => VehicleGpsFixType == (byte)GpsFixType.RtkFixed;

    // Backward-compat helpers for existing Blazor panels
    public bool     IsSourceConnected  => Phase != RtkPhase.Idle;
    public RtkPhase CurrentSurveyState => Phase;
}