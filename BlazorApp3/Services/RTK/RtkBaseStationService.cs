// Services/RtkBaseStationService.cs
// Core RTK Base Station engine for DivyaLink GCS.
//
// DATA FLOW:
//   SerialPort.DataReceived → Channel<byte> → ProcessBytesLoopAsync
//     ├─ UbxStreamParser  → UBX-NAV-SVIN (Survey-In status)
//     │                   → UBX-NAV-SAT  (satellite bars)
//     └─ RtcmStreamParser → InjectGpsData (when phase ≥ Fixed)
//                         → constellation health tracking
//                         → stream statistics
//
// INJECTION GATE (matches Mission Planner behaviour):
//   RTCM is forwarded the moment it arrives from the receiver.
//   The hardware enforces the gate — u-blox only outputs RTCM after Survey-In
//   completes. Our software gate (phase ≥ Fixed) is a defensive belt-and-suspenders
//   against protocol edge cases (e.g. saved-position fixed mode skipping survey).

using System.Buffers;
using System.Collections.Frozen;
using System.IO.Ports;
using System.Text.Json;
using System.Threading.Channels;
using BlazorApp3.Models;

namespace BlazorApp3.Services;

public sealed class RtkBaseStationService : IAsyncDisposable
{
    // ── Dependencies ──────────────────────────────────────────────────────────
    private readonly MavlinkService                   _mavlink;
    private readonly ILogger<RtkBaseStationService>   _log;

    // ── Serial port + byte pipeline ───────────────────────────────────────────
    private SerialPort? _port;
    private readonly Channel<byte> _byteChannel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(65536) { FullMode = BoundedChannelFullMode.DropOldest });

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    private CancellationTokenSource? _cts;
    private Task?                    _processingTask;
    private RtkConfig?               _currentConfig;

    // ── State (protected by _lock for compound updates) ───────────────────────
    private readonly Lock _lock  = new();
    private RtkState      _state = new();

    // ── BPS counters (Interlocked for lock-free hot-path reads) ──────────────
    private int  _rxBytesThisSecond;
    private int  _txBytesThisSecond;
    private long _totalMessages;
    private int  _largestRtcmFrame;

    // ── Constellation health ───────────────────────────────────────────────────
    private readonly Dictionary<string, DateTime> _constellationSeen = new();

    // ── Message counters ───────────────────────────────────────────────────────
    private readonly Dictionary<string, int> _msgCounters = new();

    // ── Profiles ────────────────────────────────────────────────────────────────
    private readonly string _profilesPath;
    private List<SavedBaseProfile> _profiles = [];

    // ── RTCM ID → constellation label map ─────────────────────────────────────
    private static readonly FrozenDictionary<int, string> RtcmConstellation =
        new Dictionary<int, string>
        {
            {1005,"base"}, {1006,"base"}, {4072,"base"},
            {1001,"gps"},{1002,"gps"},{1003,"gps"},{1004,"gps"},
            {1071,"gps"},{1072,"gps"},{1073,"gps"},{1074,"gps"},
            {1075,"gps"},{1076,"gps"},{1077,"gps"},
            {1009,"glonass"},{1010,"glonass"},{1011,"glonass"},{1012,"glonass"},
            {1081,"glonass"},{1082,"glonass"},{1083,"glonass"},{1084,"glonass"},
            {1085,"glonass"},{1086,"glonass"},{1087,"glonass"},
            {1091,"galileo"},{1092,"galileo"},{1093,"galileo"},{1094,"galileo"},
            {1095,"galileo"},{1096,"galileo"},{1097,"galileo"},
            {1121,"beidou"},{1122,"beidou"},{1123,"beidou"},{1124,"beidou"},
            {1125,"beidou"},{1126,"beidou"},{1127,"beidou"},
        }.ToFrozenDictionary();

    // ── Events ─────────────────────────────────────────────────────────────────
    // ── UI Drawer Sync Control State ───────────────────────────────────────────
    private bool _isDrawerOpen;
    public bool IsDrawerOpen
    {
        get => _isDrawerOpen;
        set
        {
            if (_isDrawerOpen != value)
            {
                _isDrawerOpen = value;
                OnStateChanged?.Invoke();
            }
        }
    }
    
    /// <summary>Fired on background threads — subscribers MUST InvokeAsync before StateHasChanged.</summary>
    public event Action? OnStateChanged;

    // ── Public state accessor ─────────────────────────────────────────────────
    public RtkState State { get { lock (_lock) return _state; } }

    // ── Constructor ───────────────────────────────────────────────────────────
    public RtkBaseStationService(MavlinkService mavlink, ILogger<RtkBaseStationService> log)
    {
        _mavlink = mavlink;
        _log     = log;

        _profilesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DivyaLink", "rtk_profiles.json");

        Directory.CreateDirectory(Path.GetDirectoryName(_profilesPath)!);
        LoadProfiles();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PUBLIC CONTROL API
    // ═══════════════════════════════════════════════════════════════════════════

    public static IReadOnlyList<string> GetAvailableComPorts() =>
        SerialPort.GetPortNames().OrderBy(p => p).ToArray();

    /// <summary>Open COM port, send autoconfig, start Survey-In.</summary>
    public async Task ConnectAsync(RtkConfig config)
    {
        if (_state.Phase != RtkPhase.Idle)
            await DisconnectAsync();

        _currentConfig = config;
        _log.LogInformation("[RTK] Connecting to {Port} @ {Baud}", config.ComPort, config.BaudRate);

        SetPhase(RtkPhase.Connecting, error: null);

        try
        {
            _port = new SerialPort(config.ComPort, config.BaudRate, Parity.None, 8, StopBits.One)
            {
                ReadTimeout  = 500,
                WriteTimeout = 2000,
                DtrEnable    = false,
                RtsEnable    = false
            };
            _port.DataReceived += OnDataReceived;
            _port.Open();

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            _processingTask = Task.Run(() => ProcessBytesLoopAsync(ct), ct);

            // Start BPS and constellation-health timers
            _ = Task.Run(() => BpsTimerLoopAsync(ct),          ct);
            _ = Task.Run(() => ConstellationExpiryLoopAsync(ct), ct);

            // Send autoconfig (if enabled) then Survey-In command
            // Send autoconfig
if (config.AutoConfig)
{
    foreach (var (cmd, delay) in UbxConfigurator.SetupM8P(config.M8p130Plus))
    {
        ct.ThrowIfCancellationRequested();

        _port.Write(
            cmd,
            0,
            cmd.Length);

        await Task.Delay(
            delay,
            ct);
    }

    // Save current receiver configuration
    // (equivalent to Mission Planner CFG-CFG save)

    byte[] saveCfgPayload =
[
    0x00, 0x00, 0x00, 0x00,   // clearMask  = 0 (clear nothing)
    0xFF, 0xFF, 0x00, 0x00,   // saveMask   = 0xFFFF (save all configuration)
    0x00, 0x00, 0x00, 0x00,   // loadMask   = 0 (load nothing)
    0x17                       // deviceMask = BBR(0x01) + Flash(0x02) + EEPROM(0x04)
                               //              + SpiFlash(0x10) = 0x17
];
byte[] saveCfg = UbxConfigurator.Generate(0x06, 0x09, saveCfgPayload);
_port.Write(saveCfg, 0, saveCfg.Length);
await Task.Delay(300, ct);

    await Task.Delay(
        300,
        ct);
}


// Start Survey-In
var surveyCmd =
    UbxConfigurator.BuildSurveyIn(
        (uint)config.MinDurationSec,
        config.TargetAccuracyM);

_port.Write(
    surveyCmd,
    0,
    surveyCmd.Length);

            SetPhase(RtkPhase.Survey, error: null);
            _log.LogInformation("[RTK] Survey-In started (target {Acc}m / {Dur}s)",
                config.TargetAccuracyM, config.MinDurationSec);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[RTK] Connection failed");
            CleanupPort();
            SetPhase(RtkPhase.Idle, error: ex.Message);
        }
    }

    /// <summary>
    /// Disconnect and reset all state.
    /// Bug 1 fix: ActiveProfileName is reset here (JSX handleDisconnect never did this).
    /// </summary>
    public async Task DisconnectAsync()
    {
        _log.LogInformation("[RTK] Disconnecting");
        _cts?.Cancel();
        if(_processingTask!=null)
{
    try
    {
        await _processingTask
            .WaitAsync(
                TimeSpan.FromSeconds(3));
    }
    catch
    {
    }
}
        while (_byteChannel.Reader.TryRead(out _))
{
}
        CleanupPort();

        lock (_lock)
        {
            _msgCounters.Clear();
            _constellationSeen.Clear();
            _rxBytesThisSecond = 0;
            _txBytesThisSecond = 0;
            _totalMessages     = 0;
            _largestRtcmFrame  = 0;
            _state = new RtkState              // Bug 1 fix: full reset including ActiveProfileName
            {
                SavedProfiles = _profiles,
            };
        }

        FireStateChanged();
        _log.LogInformation("[RTK] Disconnected");
    }

    /// <summary>
    /// Restart Survey-In from scratch.
    /// Bug 2 fix: Clears message counters (JSX restart didn't clear msgsSeen/msgsCount).
    /// </summary>
    public async Task RestartSurveyAsync()
    {
        if (_port is not { IsOpen: true } || _currentConfig == null) return;

        _log.LogInformation("[RTK] Restarting Survey-In");

        // Bug 2 fix: clear message counters so stale messages don't persist
        lock (_lock)
        {
            _msgCounters.Clear();
            _totalMessages    = 0;
            _largestRtcmFrame = 0;
            _state = _state with
            {
                Phase   = RtkPhase.Survey,
                Survey  = SurveyInStatus.Empty,
                Stream  = RtkStreamStats.Zero,
                Messages = [],
            };
        }
        FireStateChanged();

        var cmd = UbxConfigurator.BuildSurveyIn(
            (uint)_currentConfig.MinDurationSec, _currentConfig.TargetAccuracyM);
        _port.Write(cmd, 0, cmd.Length);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Load a saved base position into Fixed Mode — skips Survey-In.
    /// Bug 3 fix: Resets largestMsg and rtcmOverflow (JSX handleUseProfile didn't).
    /// </summary>
    public async Task UseProfileAsync(SavedBaseProfile profile)
    {
        if (_port is not { IsOpen: true }) return;

        _log.LogInformation("[RTK] Using saved profile '{Name}'", profile.Name);

        // Bug 3 fix: reset stream overflow state from previous survey
        lock (_lock)
        {
            _largestRtcmFrame = 0;
            _state = _state with
            {
                Phase             = RtkPhase.Fixed,
                FixedPosition     = new BasePosition(
                    profile.EcefX, profile.EcefY, profile.EcefZ,
                    profile.Lat,   profile.Lng,   profile.Alt),
                ActiveProfileName = profile.Name,
                Stream            = RtkStreamStats.Zero,  // Bug 3 fix
            };
        }
        FireStateChanged();

        var cmd = UbxConfigurator.BuildFixedLla(profile.Lat, profile.Lng, profile.Alt);
        _port.Write(cmd, 0, cmd.Length);

        // Transition to Injecting after brief Fixed display
        await Task.Delay(1500);
        if (_state.Phase == RtkPhase.Fixed)
        {
            lock (_lock) { _state = _state with { Phase = RtkPhase.Injecting }; }
            FireStateChanged();
        }
    }

    public async Task SaveCurrentPositionAsync(string name)
    {
        var pos = _state.FixedPosition;
        if (pos == null) return;

        var profile = new SavedBaseProfile(name, pos.EcefX, pos.EcefY, pos.EcefZ,
            pos.Lat, pos.Lng, pos.Alt, DateTime.UtcNow);

        _profiles.RemoveAll(p => p.Name == name);
        _profiles.Add(profile);
        SaveProfiles();
        lock (_lock) { _state = _state with { SavedProfiles = _profiles }; }
        FireStateChanged();
        await Task.CompletedTask;
    }

    public async Task DeleteProfileAsync(string name)
{
    // If deleting the currently active profile while in Fixed or Injecting state,
    // command the receiver out of TMODE3 so it stops emitting stale corrections.
    bool needsReceiverReset =
        _state.ActiveProfileName == name
        && _state.Phase >= RtkPhase.Fixed
        && _port is { IsOpen: true };

    _profiles.RemoveAll(p => p.Name == name);
    SaveProfiles();

    lock (_lock)
    {
        _state = _state with
        {
            SavedProfiles     = _profiles,
            ActiveProfileName = needsReceiverReset ? null : _state.ActiveProfileName,
        };
    }
    FireStateChanged();

    if (needsReceiverReset)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        try
        {
            foreach (var (cmd, delay) in UbxConfigurator.BuildDisable())
            {
                ct.ThrowIfCancellationRequested();
                _port!.Write(cmd, 0, cmd.Length);
                await Task.Delay(delay, ct);
            }
            _log.LogInformation("[RTK] TMODE3 disabled — profile '{Name}' deleted", name);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[RTK] Failed to send TMODE3 disable after profile delete");
        }
    }
}

    // ═══════════════════════════════════════════════════════════════════════════
    // BYTE INGESTION
    // ═══════════════════════════════════════════════════════════════════════════

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        var port  = (SerialPort)sender;
        int count = port.BytesToRead;
        if (count <= 0) return;

        var buf = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            count = port.Read(buf, 0, count);
            for (int i = 0; i < count; i++)
                _byteChannel.Writer.TryWrite(buf[i]);
            Interlocked.Add(ref _rxBytesThisSecond, count);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // BYTE PROCESSING LOOP — runs on dedicated background task
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task ProcessBytesLoopAsync(CancellationToken ct)
{
    var ubx   = new UbxStreamParser();
    var rtcm  = new RtcmStreamParser();
    bool inRtcm = false;

    try
    {
        await foreach (var b in _byteChannel.Reader.ReadAllAsync(ct))
        {
            DispatchByte(b, ubx, rtcm, ref inRtcm);
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        _log.LogError(ex, "[RTK] Byte loop error");
    }
}

/// <summary>
/// Routes a single incoming byte to the correct parser.
/// Extracted from ProcessBytesLoopAsync so that recovery from RTCM CRC
/// failure can re-dispatch the current byte without re-entering the loop.
/// </summary>
private void DispatchByte(byte b, UbxStreamParser ubx, RtcmStreamParser rtcm, ref bool inRtcm)
{
    // ── 0xD3 preamble always starts a new RTCM frame ──────────────────
    // This takes priority over any in-progress UBX frame, matching
    // Mission Planner's behaviour. If this byte is 0xD3 we can never be
    // in the middle of a CRC-failed RTCM frame (that has already reset).
    if (b == 0xD3)
    {
        ubx.Reset();
        rtcm.Reset();
        rtcm.Feed(b);   // stores preamble, advances to ReadLength
        inRtcm = true;
        return;
    }

    // ── RTCM continuation ────────────────────────────────────────────
    if (inRtcm)
    {
        bool complete = rtcm.Feed(b);

        if (complete)
        {
            // CRC-validated frame: inject and reset
            ProcessRtcmFrame(rtcm);
            rtcm.Reset();
            inRtcm = false;
            return;
        }

        if (rtcm.IsIdle)
        {
            // Feed() returned false and internally reset (PATCH 1 active).
            // The parser is back at WaitPreamble. Clear RTCM mode and
            // fall through to parse the current byte as UBX.
            // (b cannot be 0xD3 here — that was caught above.)
            inRtcm = false;
            // intentional fall-through to UBX path below
        }
        else
        {
            // Frame still accumulating — nothing more to do with this byte
            return;
        }
    }

    // ── UBX parsing ──────────────────────────────────────────────────
    int result = ubx.Feed(b);
    if (result > 0)
    {
        ProcessUbxFrame(ubx.Class, ubx.SubClass, ubx.Payload);
        ubx.Reset();
    }
    // result == 0  : frame in progress, wait for more bytes
    // result == -1 : UBX checksum failed; UbxStreamParser already reset itself
}
    // ── UBX frame dispatch ─────────────────────────────────────────────────────

    private void ProcessUbxFrame(byte cls, byte sub, ReadOnlySpan<byte> payload)
    {
        string name = $"Ubx{cls:X2}{sub:X2}";
        IncrementCounter(name);
        Interlocked.Increment(ref _totalMessages);

        if (cls == 0x05)
        {
            ProcessAck(sub,payload);
            return;
        }

        if (cls == 0x01 && sub == 0x3B) // NAV-SVIN
        {
            var sv = UbxNavSvinParser.Parse(payload);
            if (sv.HasValue) OnNavSvin(sv.Value);
        }
        else if (cls == 0x01 && sub == 0x35) // NAV-SAT
        {
            var sats = UbxNavSatParser.Parse(payload);
            lock (_lock) { _state = _state with { Satellites = sats }; }
            FireStateChanged();
        }
        else if (cls==0x0A && sub==0x04)
        {
            ParseMonVersion(payload);
        }
        else if (cls==0x0A && sub==0x09)
{
     ParseMonHardware(payload);
}
    }

    private void ProcessAck(byte msgType, ReadOnlySpan<byte> payload)
    {
    if(payload.Length < 2)
        return;

    byte ackClass=payload[0];
    byte ackId=payload[1];

    if(msgType==0x01)
    {
        _log.LogInformation(
            "[RTK] UBX ACK {Class:X2} {Id:X2}",
            ackClass,
            ackId);
    }
    else
    {
        _log.LogWarning(
            "[RTK] UBX NACK {Class:X2} {Id:X2}",
            ackClass,
            ackId);
    }
    }

    private void ParseMonVersion(
    ReadOnlySpan<byte> payload)
    {
    string text=
        System.Text.Encoding.ASCII
        .GetString(payload);

    _log.LogInformation(
        "[RTK] MON-VER: {Text}",
        text);
    }

    private void ParseMonHardware(
    ReadOnlySpan<byte> payload)
    {
    if(payload.Length<24)
        return;

    byte noise=
        payload[16];

    byte jam=
        payload[22];

    _log.LogInformation(
        "[RTK] Noise={Noise} Jam={Jam}",
        noise,
        jam);
    }

    private void OnNavSvin(NavSvinData sv)
    {
        var survey = new SurveyInStatus(sv.Dur, sv.Obs, sv.AccuracyM, sv.Valid, sv.Active);
        lock (_lock) { _state = _state with { Survey = survey }; }
        FireStateChanged();

        if (_state.Phase == RtkPhase.Survey && sv.Valid)
        {
            var (x, y, z) = sv.EcefMetres;
            var (lat, lng, alt) = EcefToLla(x, y, z);
            var pos = new BasePosition(x, y, z, lat, lng, alt);

            _log.LogInformation("[RTK] Survey-In valid — Lat={Lat:F7} Lng={Lng:F7} Alt={Alt:F2}m Acc={Acc:F3}m",
                lat, lng, alt, sv.AccuracyM);

            lock (_lock) { _state = _state with { Phase = RtkPhase.Fixed, FixedPosition = pos }; }
            FireStateChanged();

            // Transition to Injecting after brief display pause
            _ = Task.Delay(1500).ContinueWith(_ =>
            {
                if (_state.Phase == RtkPhase.Fixed)
                {
                    lock (_lock) { _state = _state with { Phase = RtkPhase.Injecting }; }
                    FireStateChanged();
                }
            }, TaskScheduler.Default);
        }
    }

    // ── RTCM frame dispatch ────────────────────────────────────────────────────

    private void ProcessRtcmFrame(RtcmStreamParser rtcm)
{
    int msgId = rtcm.MessageId;
    _log.LogInformation(
    "[RTCM] ID={Id}, Bytes={Bytes}",
    msgId,
    rtcm.TotalBytes
);
    int fBytes = rtcm.TotalBytes;

    string name = $"Rtcm{msgId}";

    IncrementCounter(name);
    Interlocked.Increment(ref _totalMessages);

    // constellation tracking
    if (RtcmConstellation.TryGetValue(msgId, out var constel))
    {
        lock (_lock)
        {
            _constellationSeen[constel] = DateTime.UtcNow;
        }
    }

    Interlocked.Add(ref _txBytesThisSecond, fBytes);

    InterlockedMax(ref _largestRtcmFrame, fBytes);

    var frame = rtcm.FrameWithCrc;

    // Mission Planner behavior:
    // RTCM received → inject immediately
    if (_mavlink.IsConnected)
    {
        try
        {
            _mavlink.InjectGpsData(
        frame,
        (ushort)frame.Length
    );

            _log.LogDebug(
                "[RTK] RTCM {MsgId} injected ({Bytes} bytes)",
                msgId,
                frame.Length);
        }
        catch(Exception ex)
        {
            _log.LogWarning(
                ex,
                "[RTK] RTCM injection failed {MsgId}",
                msgId);
        }
    }
}

    // ═══════════════════════════════════════════════════════════════════════════
    // BACKGROUND TIMER LOOPS
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task BpsTimerLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                int rx   = Interlocked.Exchange(ref _rxBytesThisSecond, 0) * 8;
                int tx   = Interlocked.Exchange(ref _txBytesThisSecond, 0) * 8;
                int lrg  = Volatile.Read(ref _largestRtcmFrame);
                long tot = Volatile.Read(ref _totalMessages);

                int frags    = lrg <= 0 ? 0 : (lrg + 179) / 180;
                bool overflow = lrg > 540;  // > 540B starts burning into the 720B ceiling

                // Snapshot message counters (under lock to ensure consistency)
                List<MessageEntry> msgs;
                lock (_lock)
                {
                    msgs = _msgCounters
                        .OrderByDescending(kv => kv.Value)
                        .Take(24)
                        .Select(kv => new MessageEntry(kv.Key, kv.Value))
                        .ToList();
                }

                var stream = new RtkStreamStats(rx, tx, tot, lrg, frags, overflow);

                lock (_lock)
                {
                    _state = _state with { Stream = stream, Messages = msgs };
                }
                FireStateChanged();
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ConstellationExpiryLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                var now      = DateTime.UtcNow;
                var defaults = ConstellationStatus.DefaultSet();

                var updated = defaults.Select(def =>
                {
                    bool seen = false;
                    lock (_lock)
                    {
                        if (_constellationSeen.TryGetValue(def.Id, out var last))
                            seen = !def.IsExpired(now) && (now - last) < (def.Id == "base"
                                ? ConstellationStatus.BaseExpiry
                                : ConstellationStatus.SignalExpiry);
                    }
                    return def with { IsActive = seen };
                }).ToList();

                lock (_lock) { _state = _state with { Constellations = updated }; }
                FireStateChanged();
            }
        }
        catch (OperationCanceledException) { }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // UTILITY
    // ═══════════════════════════════════════════════════════════════════════════

    private static (double lat, double lng, double alt) EcefToLla(double x, double y, double z)
    {
        // WGS84 iterative (Bowring's method, 10 iterations — converges to mm precision)
        const double a  = 6378137.0;
        const double f  = 1.0 / 298.257223563;
        const double e2 = 2 * f - f * f;

        double lng = Math.Atan2(y, x) * (180.0 / Math.PI);
        double p   = Math.Sqrt(x * x + y * y);
        double lat = Math.Atan2(z, p * (1 - e2));

        for (int i = 0; i < 10; i++)
        {
            double sl = Math.Sin(lat);
            double N  = a / Math.Sqrt(1 - e2 * sl * sl);
            lat = Math.Atan2(z + e2 * N * sl, p);
        }

        double sinLat = Math.Sin(lat);
        double N_f    = a / Math.Sqrt(1 - e2 * sinLat * sinLat);
        double alt    = p / Math.Cos(lat) - N_f;

        return (lat * (180.0 / Math.PI), lng, alt);
    }

    private void IncrementCounter(string name)
    {
        lock (_lock)
        {
            _msgCounters.TryGetValue(name, out int v);
            _msgCounters[name] = v + 1;
        }
    }

    private void SetPhase(RtkPhase phase, string? error)
    {
        lock (_lock) { _state = _state with { Phase = phase, ErrorMessage = error }; }
        FireStateChanged();
    }

    private void FireStateChanged() => OnStateChanged?.Invoke();

    private void CleanupPort()
    {
        try
        {
            if (_port != null)
            {
                _port.DataReceived -= OnDataReceived;
                if (_port.IsOpen) _port.Close();
                _port.Dispose();
                _port = null;
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[RTK] Port cleanup error"); }
    }

    private void LoadProfiles()
    {
        try
        {
            if (File.Exists(_profilesPath))
                _profiles = JsonSerializer.Deserialize<List<SavedBaseProfile>>(
                    File.ReadAllText(_profilesPath)) ?? [];
        }
        catch { _profiles = []; }
        lock (_lock) { _state = _state with { SavedProfiles = _profiles }; }
    }

    private void SaveProfiles()
    {
        try { File.WriteAllText(_profilesPath, JsonSerializer.Serialize(_profiles)); }
        catch (Exception ex) { _log.LogWarning(ex, "[RTK] Failed to save profiles"); }
    }

    // Interlocked.Max — not in BCL, implemented manually
    private static void InterlockedMax(ref int location, int value)
    {
        int current;
        do { current = Volatile.Read(ref location); }
        while (current < value && Interlocked.CompareExchange(ref location, value, current) != current);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _cts?.Dispose();
    }
}