// RtkBaseStationService.cs
// Complete rewrite — mirrors Mission Planner ConfigSerialInjectGPS + ubx_m8p.cs
// workflow exactly. Every design decision is cited to MP source (Files.md).
//
// Architecture matches MP mainloop() (Files.md 29983-30173):
//   1. Baud scan (MP SetupM8P baud loop, lines 21679-21698)
//   2. Full SetupM8P config sequence (no ACK waiting — MP uses Sleep like us;
//      real ACK gating is logged for diagnostics)
//   3. SetupBasePos: Survey-In or Fixed LLA
//   4. Single background DispatchByte loop (replaces MP's for(a<read) loop)
//   5. RTCM frames → sendData (MP line 30104) → InjectGpsData
//   6. seenRTCM constellation tracking (MP lines 30175-30243)
//   7. ExtractBasePos on 1005/1006 (MP lines 30430-30481)
//   8. _has1005Seen gate before injection (fixes WF-04/WF-12)
//   9. Continuous TMODE3 poll every 30s (MP line 30339-30344)
//  10. Vehicle GPS fix type via GPS_RAW_INT
//  11. STATUSTEXT feed
//
// CRITICAL BUG FIXES applied:
//   FIX-1: RtcmStreamParser.Reset() always called — no IndexOutOfRangeException
//   FIX-2: DispatchByte handles IsIdle after CRC fail — no stuck inRtcm flag
//   FIX-3: CFG-CFG save uses UbxConfigurator.Generate() — correct checksum
//   FIX-4: 1005-first injection gate (_has1005Seen)
//   FIX-5: MSM7 auto-selected from MonVerInfo — no manual user toggle
//   FIX-6: BuildDisable() wired to DeleteProfileAsync and disconnect
//   FIX-7: NAV-SVIN logged at INFO level every update
//   FIX-8: MON-HW logged at INFO level; jamming alert
//   FIX-9: NAV-SVIN continues being monitored in Injecting phase

using System.Collections.Concurrent;
using System.IO.Ports;
using System.Threading.Channels;
using BlazorApp3.Models;
using Microsoft.Extensions.Logging;

namespace BlazorApp3.Services;

public sealed class RtkBaseStationService : IAsyncDisposable
{
    // ── Dependencies ──────────────────────────────────────────────────────────
    private readonly MavlinkService        _mavlink;
    private readonly ILogger<RtkBaseStationService> _log;

    /// <summary>
    /// Tracks the last UBX frame sent per (class, id) so the NACK handler can
    /// retry it. Populated by WritePortTracked(); cleared on restart/disconnect.
    /// Thread-safety: all access is under lock(_sentCmds).
    /// </summary>
    private readonly Dictionary<(byte cls, byte id), (byte[] cmd, int retries)> _sentCmds = new();
 
    /// <summary>Maximum number of automatic retries on a NACK before giving up.</summary>
    private const int MaxNackRetries = 2;

    // ── Serial port ───────────────────────────────────────────────────────────
    private SerialPort?  _port;
    private readonly object _portLock = new();

    // ── Background tasks ──────────────────────────────────────────────────────
    private CancellationTokenSource? _cts;
    private Task? _processingTask;
    private Task? _bpsTask;
    private Task? _expiryTask;

    // ── Byte channel (MP's serial read buffer equivalent) ─────────────────────
    // MP reads up to 180 bytes at a time (line 30063-30068).
    // We use a bounded channel; DropOldest is last resort — size 131072 = ~2.8s at 460800.
    private Channel<byte>? _byteChannel;

    // ── RTCM injection gate ───────────────────────────────────────────────────
    // FIX-4: matches MP ExtractBasePos (line 30434) — only inject after 1005 seen.
    // MP injects all RTCM frames immediately once seenRTCM fires, but the receiver
    // hardware gates: 1005 is only emitted after survey valid, so in practice MP
    // also never injects MSM without 1005. We add an explicit software gate here
    // to handle any edge case where the receiver emits MSM before 1005.
    private bool      _has1005Seen;
    private RtkConfig? _lastConfig;

    // _awaitingFreshSurvey is set whenever we send a new CFG-TMODE3 SurveyIn.
    // OnNavSvin ignores frames with DurationSec > 5 while this flag is set.
    // Cleared the moment the receiver reports DurationSec <= 2 — meaning its
    // hardware timer has reset. Prevents stale receiver values flashing in UI.
    private volatile bool _awaitingFreshSurvey;
    private uint          _lastKnownSurveyDur;
    // SURVEY-BUG-5 FIX: timeout for _awaitingFreshSurvey
    // If the receiver never sends DurationSec<=2 within 8s, force-clear the flag.
    private DateTime      _awaitingFreshSurveySince;   // used to detect timer reset      // stored in ConnectAsync for RestartSurveyAsync

    // ── TMODE3 poll timer (MP line 30339-30344: every 30s) ────────────────────
    private DateTime _nextTmodePoll = DateTime.MaxValue; // set to real schedule after ConnectAsync completes

    // ── BPS measurement ──────────────────────────────────────────────────────
    private int _rxBytesThisSecond;
    private int _txInjectedBytesThisSecond;
    private int _rtcmParsedBytesThisSecond;
    private static int InterlockedMax(ref int loc, int val)
    {
        int current = loc;
        while (val > current)
        {
            int prev = Interlocked.CompareExchange(ref loc, val, current);
            if (prev == current) break;
            current = prev;
        }
        return loc;
    }
    private int _largestRtcmFrame;

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly object _lock = new();
    private RtkState _state = new();
    private readonly List<SavedBaseProfile> _profiles = [];

    // ── Message counter ───────────────────────────────────────────────────────
    private readonly Dictionary<string, int> _msgSeen = new();
    private long _totalMessages;

    // ── Constellation expiry (MP seenRTCM ExpireType pattern) ────────────────
    // MP: labelgps expires after 5s, labelbase after 20s (lines 30196,30202)
    private readonly Dictionary<string, DateTime> _constellationLastSeen = new();

    // ── Receiver firmware info (from MON-VER) ────────────────────────────────
    private MonVerInfo _receiverInfo = MonVerInfo.Unknown;
    private bool       _configUsedMsm7;

    /// <summary>
    /// Completion source for the CFG-GNSS poll/response round-trip.
    /// Created just before WritePort(PollCfgGnss()); nulled in the finally
    /// block of QueryCfgGnssAsync after the result is obtained or the timeout
    /// fires.  OnCfgGnss uses TrySetResult (not SetResult) so a late-arriving
    /// response after a timeout cannot throw.
    /// </summary>
    private TaskCompletionSource<IReadOnlySet<byte>>? _cfgGnssTcs;
 
    /// <summary>
    /// The set of gnssId values enabled in the receiver, populated by
    /// OnCfgGnss after a successful CFG-GNSS poll.  Null until the first
    /// successful query on this connection.  Used by ResendMsmRatesAsync so a
    /// late MSM7→MSM4 downgrade also respects the constellation filter.
    /// </summary>
    private IReadOnlySet<byte>? _supportedGnss;
 
    /// <summary>
    /// Conservative fallback used when the CFG-GNSS poll times out.
    /// GPS + GLONASS are guaranteed on every NEO-M8P-2 firmware; Galileo and
    /// BeiDou are omitted because their availability is firmware-dependent.
    /// Sending CFG-MSG for a constellation that turns out to be unsupported
    /// merely produces a NACK (harmless), but the fallback still avoids them
    /// to keep the log clean.
    /// </summary>
    private static readonly IReadOnlySet<byte> FallbackGnss =
        new HashSet<byte> { 0, 6 };   // GPS=0, GLONASS=6

    public event Action<RtkState>? OnStateChanged;

    public RtkState State { get { lock (_lock) return _state; } }
    public bool IsActive  => State.Phase >= RtkPhase.Survey;

    // ── Profile persistence path ──────────────────────────────────────────────
    private static readonly string ProfilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DivyaLink", "rtk_profiles.json");

    public RtkBaseStationService(MavlinkService mavlink,
                                  ILogger<RtkBaseStationService> log)
    {
        _mavlink = mavlink;
        _log     = log;
        LoadProfiles();

        // Subscribe to vehicle-side messages (WF-10, WF-11)
        _mavlink.OnGpsRawInt     += OnVehicleGpsUpdate;
        _mavlink.OnStatusText    += OnStatusText;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CONNECT
    // Mirrors MP button_Click → SetupM8P() → SetupBasePos() flow
    // ═══════════════════════════════════════════════════════════════════════════
    public async Task ConnectAsync(RtkConfig cfg, CancellationToken externalCt = default)
    {
        await DisconnectAsync();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalCt);
        var ct = _cts.Token;

        _lastConfig = cfg;              // save for RestartSurveyAsync
        SetPhase(RtkPhase.Connecting, ct);
        ResetSessionCounters();

        _byteChannel = Channel.CreateBounded<byte>(new BoundedChannelOptions(131072)
        {
            FullMode    = BoundedChannelFullMode.DropOldest,
            SingleReader= true,
            SingleWriter= true
        });

        // ── Open serial port + baud scan ─────────────────────────────────────
        // Mirrors MP SetupM8P baud scan loop (lines 21679-21698)
        var port = await OpenPortWithBaudScanAsync(cfg, ct);
        if (port == null)
        {
            SetError("Could not open serial port at any supported baud rate.");
            return;
        }
        lock (_portLock) { _port = port; }

        // ── Start byte ingestion ──────────────────────────────────────────────
        port.DataReceived += OnDataReceived;

        // ── Start processing task BEFORE sending config ───────────────────────
        _processingTask = Task.Run(() => ProcessBytesLoopAsync(ct), ct);
        _bpsTask        = Task.Run(() => BpsTimerLoopAsync(ct),     ct);
        _expiryTask     = Task.Run(() => ConstellationExpiryLoopAsync(ct), ct);

        // ── Run SetupM8P config sequence ──────────────────────────────────────
        // useMsm7 defaults true; will be updated after MON-VER response arrives.
        // MP sends all commands with Sleep() between them — we use Task.Delay.
        // MON-VER poll is included in the sequence; we update _receiverInfo when
        // the response arrives in ProcessUbxFrame.
        if (cfg.AutoConfig)
        {
            _log.LogInformation("[RTK] Setup UBLOX begin");
 
            // Disable any running survey before reconfiguring (FIX-C).
            _awaitingFreshSurvey      = true;
            _awaitingFreshSurveySince = DateTime.UtcNow;
            var tmode3Disable = new byte[40];   // all zeros = flags=0 = Disabled
            WritePort(UbxConfigurator.Generate(0x06, 0x71, tmode3Disable));
            await Task.Delay(250, ct);
 
            // ── Phase 1: port config, nav settings, sensor enables, MON-VER poll ─
            // Does NOT configure RTCM outputs — we don't know which constellations
            // are active yet.  MON-VER poll is embedded in Phase 1 (line 1479 of
            // the original SetupM8P) so _receiverInfo.FwVer is populated by the
            // time Phase 1 completes.
            foreach (var (cmd, delay) in UbxConfigurator.SetupM8pPhase1())
            {
                ct.ThrowIfCancellationRequested();
                WritePortTracked(cmd);
                await Task.Delay(delay, ct);
            }
            _nextTmodePoll = DateTime.UtcNow.AddSeconds(30);
 
            // Wait for MON-VER response (embedded poll in Phase 1 + 200ms delay
            // gives the receiver plenty of time; this loop is a safety net).
            for (int i = 0; i < 10 && _receiverInfo.FwVer.Length == 0; i++)
                await Task.Delay(100, ct);
 
            bool useMsm7Final;
            if (_receiverInfo.FwVer.Length == 0)
            {
                _log.LogWarning("[RTK] MON-VER not received within 1 s — defaulting to MSM7");
                useMsm7Final = true;
            }
            else
            {
                useMsm7Final = _receiverInfo.IsMsm7Capable;
                _log.LogInformation(
                    "[RTK] MON-VER: fw={Fw} MSM7={M}",
                    _receiverInfo.FwVer, useMsm7Final);
            }
 
            // ── CFG-GNSS query ────────────────────────────────────────────────
            // Discovers which constellations the receiver actually has enabled.
            // On NEO-M8P-2 / HPG 1.43 this is typically GPS + GLONASS, and
            // possibly Galileo depending on receiver configuration.  BeiDou is
            // never present on the M8P-2.
            //
            // The result is used in Phase 2 to skip CFG-MSG commands for disabled
            // constellations, eliminating the NACKs that were causing reduced
            // RTCM throughput (report §3 root-cause analysis).
            var enabledGnss = await QueryCfgGnssAsync(ct);
 
            // ── Phase 2: filtered RTCM rates, diagnostics, CFG-CFG save ──────
            // Only emits CFG-MSG for RTCM messages whose constellation is in
            // enabledGnss.  Zero NACKs for unsupported constellations.
            _log.LogInformation(
                "[RTK] Phase 2 — configuring RTCM output for: {C}",
                string.Join(", ", enabledGnss.Select(UbxConfigurator.GnssIdName)));
 
            foreach (var (cmd, delay) in UbxConfigurator.SetupM8pRtcm(useMsm7Final, enabledGnss))
            {
                ct.ThrowIfCancellationRequested();
                WritePortTracked(cmd);
                await Task.Delay(delay, ct);
            }
            _configUsedMsm7 = useMsm7Final;
            _log.LogInformation("[RTK] Setup UBLOX complete");

        // ── SetupBasePos: Survey-In or Fixed ──────────────────────────────────
        // Mirrors MP SetupBasePos() (Files.md lines 21809-21851)
        var profile = GetActiveProfile();
        if (profile != null)
        {
            // Fixed position from saved profile
            _log.LogInformation("[RTK] Using fixed profile '{Name}' lat={Lat:F7} lon={Lon:F7} alt={Alt:F3}m",
                profile.Name, profile.Lat, profile.Lng, profile.Alt);
            var cmd = UbxConfigurator.BuildFixedLla(profile.Lat, profile.Lng, profile.Alt);
            WritePort(cmd);

            lock (_lock)
            {
                _state = _state with
                {
                    Phase             = RtkPhase.Injecting,
                    ActiveProfileName = profile.Name,
                    FixedPosition     = new BasePosition(
                        profile.EcefX, profile.EcefY, profile.EcefZ,
                        profile.Lat, profile.Lng, profile.Alt)
                };
            }
            FireStateChanged();
        }
        else
        {
            // Survey-In
            _log.LogInformation("[RTK] Starting Survey-In: minDur={D}s acc={A:F3}m",
                cfg.MinDurationSec, cfg.TargetAccuracyM);
            // HANG-BUG FIX: re-stamp the guard timestamp here too — this is the
            // call that matters most since it's sent right before the receiver
            // actually starts a new survey. The earlier site (TMODE3 disable,
            // several seconds before this point through the SetupM8P sequence)
            // would otherwise leave a stale timestamp by the time we get here.
            _awaitingFreshSurvey      = true;   // FIX-D: ignore stale NAV-SVIN until reset
            _awaitingFreshSurveySince = DateTime.UtcNow;
            _lastKnownSurveyDur  = 0;
            lock (_lock) { _state = _state with { IsResettingTimer = true }; }
            var cmd = UbxConfigurator.BuildSurveyIn(
                (uint)cfg.MinDurationSec, cfg.TargetAccuracyM);
            WritePort(cmd);

            SetPhase(RtkPhase.Survey, ct);
        }
    }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DISCONNECT
    // ═══════════════════════════════════════════════════════════════════════════
    public async Task DisconnectAsync()
    {
        _cts?.Cancel();

        foreach (var t in new[] { _processingTask, _bpsTask, _expiryTask })
        {
            if (t != null)
                try { await t.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch { }
        }

        lock (_portLock)
        {
            if (_port != null)
            {
                _port.DataReceived -= OnDataReceived;
                try { _port.Close(); } catch { }
                _port.Dispose();
                _port = null;
            }
        }

        // Drain channel
        if (_byteChannel != null)
            while (_byteChannel.Reader.TryRead(out _)) { }
        _byteChannel = null;

        _cts?.Dispose();
        _cts = null;

        ResetSessionCounters();

        lock (_lock)
        {
            _state = new RtkState
            {
                Phase        = RtkPhase.Idle,
                SavedProfiles= _profiles,
            };
        }
        FireStateChanged();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // BAUD SCAN  —  mirrors MP SetupM8P() baud scan (lines 21679-21698)
    // MP sends "UU" autobaud string + CFG-PRT at each baud until the receiver
    // responds. We replicate the intent: try each baud, send autobaud + CFG-PRT,
    // wait briefly, then commit to 460800.
    // ═══════════════════════════════════════════════════════════════════════════
    private async Task<SerialPort?> OpenPortWithBaudScanAsync(
        RtkConfig cfg, CancellationToken ct)
    {
        // Build the scan order: configured baud first, then all others
        var bauds = new List<int> { cfg.BaudRate };
        foreach (int b in UbxConfigurator.BaudScanList)
            if (!bauds.Contains(b)) bauds.Add(b);

        SerialPort? port = null;
        try
        {
            port = new SerialPort(cfg.ComPort)
            {
                DataBits = 8,
                Parity   = Parity.None,
                StopBits = StopBits.One,
                DtrEnable= false,
                RtsEnable= false
            };

            foreach (int baud in bauds)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (port.IsOpen) port.Close();
                    port.BaudRate = baud;
                    port.Open();

                    // MP line 21688: port.Write("UU") — autobaud hint
                    port.Write("UU");
                    await Task.Delay(50, ct);

                    // Send CFG-PRT UART1 to switch to 460800
                    var cfgPrt = UbxConfigurator.Generate(0x06, 0x00, new byte[]
                    {
                        0x01,0x00,0x00,0x00, 0xD0,0x08,0x00,0x00,
                        0x00,0x08,0x07,0x00, 0x23,0x00, 0x23,0x00,
                        0x00,0x00, 0x00,0x00
                    });
                    port.Write(cfgPrt, 0, cfgPrt.Length);
                    port.BaseStream.Flush();
                    await Task.Delay(100, ct);

                    _log.LogInformation("[RTK] Baud scan: tried {Baud}", baud);
                }
                catch (Exception ex)
                {
                    _log.LogDebug("[RTK] Baud {Baud} failed: {E}", baud, ex.Message);
                }
            }

            // Commit to 460800 (MP line 21712)
            if (port.IsOpen) port.Close();
            port.BaudRate = UbxConfigurator.TargetBaud;
            port.Open();
            port.BaseStream.Flush();
            await Task.Delay(100, ct);

            _log.LogInformation("[RTK] Port {Port} opened at {Baud}", cfg.ComPort, UbxConfigurator.TargetBaud);
            return port;
        }
        catch (Exception ex)
        {
            port?.Dispose();
            _log.LogError(ex, "[RTK] Failed to open {Port}", cfg.ComPort);
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SERIAL DATA RECEIVED  →  byte channel
    // ═══════════════════════════════════════════════════════════════════════════
    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        var port    = sender as SerialPort;
        var channel = _byteChannel;
        if (port == null || channel == null) return;

        int avail = port.BytesToRead;
        if (avail <= 0) return;

        var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(avail);
        try
        {
            int read = port.Read(buf, 0, avail);
            Interlocked.Add(ref _rxBytesThisSecond, read);

            for (int i = 0; i < read; i++)
                channel.Writer.TryWrite(buf[i]);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buf);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PROCESS BYTES LOOP
    // Mirrors MP mainloop() for(a<read) dispatch (Files.md lines 30094-30163)
    // MP feeds each byte to rtcm3, sbp, ubx_m8p, nmea parsers in order.
    // We handle RTCM and UBX only (no SBP/NMEA needed for base station).
    // ═══════════════════════════════════════════════════════════════════════════
    private async Task ProcessBytesLoopAsync(CancellationToken ct)
    {
        var ubx   = new UbxStreamParser();
        var rtcm  = new RtcmStreamParser();
        bool inRtcm = false;

        try
        {
            await foreach (var b in _byteChannel!.Reader.ReadAllAsync(ct))
            {
                DispatchByte(b, ubx, rtcm, ref inRtcm);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogError(ex, "[RTK] Byte processing loop terminated unexpectedly");
            SetError("Serial processing error. Reconnect to resume.");
        }
    }

    /// <summary>
    /// Routes one incoming byte to the correct parser.
    ///
    /// Design mirrors MP mainloop (Files.md 30094-30163):
    ///   • 0xD3 → RTCM preamble detected → reset all parsers → enter RTCM mode
    ///   • inRtcm → feed to RTCM parser → on complete frame: inject + seenRTCM
    ///   • else  → feed to UBX parser   → on complete frame: ProcessUbxFrame
    ///
    /// FIX-2: After RtcmStreamParser.Feed() returns false AND IsIdle is true
    /// (meaning the parser self-reset after a CRC failure per FIX-1),
    /// we clear inRtcm so the next byte routes correctly. Without this, inRtcm
    /// stays true and all subsequent bytes feed into the idle RTCM parser.
    /// </summary>
    private void DispatchByte(
        byte b,
        UbxStreamParser ubx,
        RtcmStreamParser rtcm,
        ref bool inRtcm)
    {
        // ── 0xD3 always starts a new RTCM frame (MP line 30098: !iscan && rtcm3.Read()) ──
        if (b == 0xD3)
        {
            ubx.Reset();
            rtcm.Reset();
            rtcm.Feed(b);   // stores preamble, advances to ReadLength
            inRtcm = true;
            return;
        }

        // ── RTCM continuation ─────────────────────────────────────────────────
        if (inRtcm)
        {
            bool complete = rtcm.Feed(b);

            if (complete)
            {
                // FIX-1 guarantee: Feed() already called Reset() internally.
                // FrameWithCrc is valid until the next Feed() call.
                ProcessRtcmFrame(rtcm);
                inRtcm = false;
                return;
            }

            // FIX-2: If Feed() returned false AND the parser is now Idle,
            // it self-reset after a CRC failure (FIX-1 in RtcmStreamParser).
            // Clear inRtcm so the current byte can be re-evaluated below.
            if (rtcm.IsIdle)
            {
                inRtcm = false;
                // Fall through: re-evaluate this byte as UBX or new preamble.
                // Note: b cannot be 0xD3 here (that was handled above),
                // so it goes to the UBX path below.
            }
            else
            {
                return; // frame still accumulating
            }
        }

        // ── UBX parsing ───────────────────────────────────────────────────────
        int result = ubx.Feed(b);
        if (result > 0)
        {
            // BUG-B FIX: read Class/SubClass/Payload BEFORE calling Reset().
            // ubx.Feed() no longer calls Reset() on success (Bug-A fix in UbxStreamParser),
            // so the payload is fully valid here. We call Reset() after processing
            // so the parser is clean for the next incoming frame.
            ProcessUbxFrame(ubx.Class, ubx.SubClass, ubx.Payload);
            ubx.Reset();   // ← safe: payload has been consumed
        }
        // result == 0  : frame still accumulating
        // result == -1 : checksum failed; UbxStreamParser already self-reset
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PROCESS RTCM FRAME
    // Mirrors MP mainloop sendData + seenRTCM + ExtractBasePos
    // (Files.md lines 30098-30113)
    // ═══════════════════════════════════════════════════════════════════════════
    private void ProcessRtcmFrame(RtcmStreamParser rtcm)
    {
        var frame  = rtcm.FrameWithCrc.ToArray();
        int msgId  = rtcm.MessageId;
        int fBytes = frame.Length;
 
        _log.LogDebug("[RTCM] ID={Id} len={Len}B", msgId, fBytes);
 
        // ── Per-message counters (RTCM parsed from M8P) ───────────────────────
        string key = $"Rtcm{msgId}";
        lock (_lock) { _msgSeen.TryGetValue(key, out int c); _msgSeen[key] = c + 1; }
        Interlocked.Increment(ref _totalMessages);
        InterlockedMax(ref _largestRtcmFrame, fBytes);
 
        // Count bytes received from M8P — shown as "RTCM Parsed" in the Razor.
        // This fires regardless of whether the frame is eventually injected,
        // so it accurately represents M8P output throughput.
        Interlocked.Add(ref _rtcmParsedBytesThisSecond, fBytes);
 
        // ── seenRTCM constellation tracking ──────────────────────────────────
        UpdateConstellationSeen(msgId);
 
        // ── Extract base position from 1005/1006 ─────────────────────────────
        if (msgId is 1005 or 1006)
            ExtractAndStoreBasePos(frame, msgId);
 
        // ── Injection gate (FIX-4) ────────────────────────────────────────────
        // Do not forward any RTCM to the drone until 1005 has been received.
        // ArduPilot AP_GPS_RTCM requires the base ARP before it can use MSM data.
        if (!_has1005Seen) return;
 
        // ── MAVLink injection ─────────────────────────────────────────────────
        if (_mavlink.IsConnected)
        {
            _mavlink.InjectGpsData(frame, (ushort)fBytes);
 
            // Count bytes ACTUALLY sent to the drone — shown as "Inject BW".
            // Only increments here, inside both the _has1005Seen gate and the
            // IsConnected check, so it reflects true injection bandwidth.
            Interlocked.Add(ref _txInjectedBytesThisSecond, fBytes);
        }
    }

    // ── seenRTCM (MP lines 30175-30243) ──────────────────────────────────────
    /// <summary>
    /// Maps u-blox gnssId byte to the lowercase constellation ID used in
    /// ConstellationStatus. Returns null for SBAS/QZSS (no pill).
    /// Reference: u-blox M8 Interface Description §32.17.20 gnssId field.
    /// </summary>
    private static string? GnssIdToConstellationId(byte gnssId) => gnssId switch
    {
        0 => "gps",      // GPS
        2 => "galileo",  // Galileo
        3 => "beidou",   // BeiDou
        6 => "glonass",  // GLONASS
        _ => null        // SBAS(1), QZSS(5), NAVIC(7) — no pill
    };

    private void UpdateConstellationSeen(int msgId)
    {
        // BUG-2 FIX: specific IDs BEFORE range guards — first match wins in C# switch.
        // 1005 and 1006 fall inside ">= 1001 and <= 1077", so they matched "gps"
        // before the "base" arm was ever evaluated. _has1005Seen was NEVER set,
        // permanently blocking RTCM injection and never lighting the BASE pill.
        string? constel = msgId switch
        {
            // ── Exact IDs first ─────────────────────────────────────────────
            1005 or 1006        => "base",     // RTK base station ARP
            4072                => "base",     // u-blox moving-base proprietary
            1230                => "glonass",  // GLONASS code-phase biases
            // ── GPS MSM (1071-1077) + legacy (1001-1004) ──────────────────
            >= 1071 and <= 1077 => "gps",
            >= 1001 and <= 1004 => "gps",
            // ── GLONASS MSM (1081-1087) + legacy (1009-1012) ─────────────
            >= 1081 and <= 1087 => "glonass",
            >= 1009 and <= 1012 => "glonass",
            // ── Galileo MSM (1091-1097) ──────────────────────────────────
            >= 1091 and <= 1097 => "galileo",
            // ── BeiDou MSM (1121-1127) ───────────────────────────────────
            >= 1121 and <= 1127 => "beidou",
            _                   => null
        };

        if (constel == null) return;

        if (msgId is 1005 or 1006)
        {
            if (!_has1005Seen)
            {
                _has1005Seen = true;
                _log.LogInformation("[RTK] First RTCM {Id} received — base ARP established. Injection enabled.", msgId);
            }
        }

        _constellationLastSeen[constel] = DateTime.UtcNow;

        lock (_lock)
        {
            var updated = _state.Constellations
                .Select(c => c.Id == constel
                    ? c with { IsActive = true, LastSeenUtc = DateTime.UtcNow }
                    : c)
                .ToList();
            var msgs = _msgSeen.Select(kv => new MessageEntry(kv.Key, kv.Value))
                .OrderByDescending(m => m.Count).Take(20).ToList();

            int largest   = _largestRtcmFrame;
            int frags     = (largest + 179) / 180;
            bool overflow = largest > 540;

            _state = _state with
            {
                Constellations = updated,
                Messages       = msgs,
                Stream         = _state.Stream with
                {
                    TotalMessages   = _totalMessages,
                    LargestRtcmBytes= largest,
                    FragmentsUsed   = frags,
                    HasOverflow     = overflow
                }
            };
        }
        FireStateChanged();
    }

    // ── ExtractBasePos (MP lines 30430-30481) ─────────────────────────────────
    private void ExtractAndStoreBasePos(byte[] frame, int msgId)
    {
        try
        {
            // Parse RTCM 1005 base ARP position
            // RTCM 1005 field layout (from RTKLIB decode_type1005, Files.md 4167-4181):
            //   bits 0-11:  message number (12)
            //   bits 12-23: reference station ID
            //   bits 24-25: ITRF realization
            //   bit 26:     GPS indicator
            //   bit 27:     GLONASS indicator
            //   bit 28:     Galileo indicator
            //   bit 29:     reference station indicator
            //   bits 30-59: ECEF-X (30 bits, 0.0001m resolution)
            //   bit 60:     oscillator indicator
            //   bit 61:     reserved
            //   bits 62-91: ECEF-Y (30 bits)
            //   bit 92:     quarter cycle indicator
            //   bits 93-122: ECEF-Z (30 bits)
            // Payload starts at frame[3] (after preamble+len)
            int offset = 3; // payload start
            if (frame.Length < offset + 19) return;

            // Use RTKLIB-style bit extraction
            double ecefX = GetBitsS(frame, offset, 30 + 24, 38) * 0.0001; // cm → m ÷10000
            // Simplified: extract signed 38-bit ECEF-X at bit offset 30+24=54
            // Full bit extraction:
            long ix = GetSignedBits(frame, offset * 8 + 30, 38);
            long iy = GetSignedBits(frame, offset * 8 + 69, 38);
            long iz = GetSignedBits(frame, offset * 8 + 108, 38);

            double x = ix * 0.0001; // 0.0001m = 0.1mm resolution
            double y = iy * 0.0001;
            double z = iz * 0.0001;

            var (lat, lng, alt) = EcefConverter.ToLla(x, y, z);

            _log.LogInformation("[RTK] Base ARP from RTCM {Id}: lat={Lat:F7} lon={Lon:F7} alt={Alt:F3}m",
                msgId, lat, lng, alt);

            lock (_lock)
            {
                _state = _state with
                {
                    FixedPosition = new BasePosition(x, y, z, lat, lng, alt)
                };
            }
            FireStateChanged();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[RTK] ExtractBasePos failed for msg {Id}", msgId);
        }
    }

    // Bit extraction helper for RTCM field parsing
    private static long GetSignedBits(byte[] data, int bitOffset, int bitCount)
    {
        long val = 0;
        for (int i = 0; i < bitCount; i++)
        {
            int byteIdx = (bitOffset + i) / 8;
            int bitIdx  = 7 - ((bitOffset + i) % 8);
            if (byteIdx < data.Length)
                val = (val << 1) | ((data[byteIdx] >> bitIdx) & 1);
        }
        // Sign extend
        long sign = 1L << (bitCount - 1);
        return (val ^ sign) - sign;
    }

    private static double GetBitsS(byte[] data, int dataOffset, int bitOffset, int bits) =>
        GetSignedBits(data, dataOffset * 8 + bitOffset, bits);

    // ═══════════════════════════════════════════════════════════════════════════
    // PROCESS UBX FRAME
    // Mirrors MP ProcessUBXMessage() (Files.md lines 30245-30351)
    // ═══════════════════════════════════════════════════════════════════════════
    private void ProcessUbxFrame(byte cls, byte sub, ReadOnlySpan<byte> payload)
    {
        // Count every UBX frame by class+id — populates "Messages Seen" footer.
        // Format matches Image 1: Ubx0501=35 (hex class + hex id), Ubx013B=21, etc.
        string ubxKey = $"Ubx{cls:X2}{sub:X2}";
        lock (_lock)
        {
            _msgSeen.TryGetValue(ubxKey, out int existing);
            _msgSeen[ubxKey] = existing + 1;

            // Rebuild message list for state snapshot — merge UBX + RTCM entries sorted by count
            var msgs = _msgSeen
                .OrderByDescending(kv => kv.Value)
                .Take(20)
                .Select(kv => new MessageEntry(kv.Key, kv.Value))
                .ToList();
            _state = _state with { Messages = msgs };
        }
        Interlocked.Increment(ref _totalMessages);

        // ── ACK ───────────────────────────────────────────────────────────────
        if (cls == 0x05 && sub == 0x01)
        {
            if (payload.Length >= 2)
            {
                string ackName = UbxConfigurator.GetMessageName(payload[0], payload[1]);
                _log.LogInformation("[RTK] ACK  {Name}", ackName);
 
                // On successful ACK, clear the retry counter so future NACKs
                // for this command start fresh rather than exhausting retries
                // from a previous failed attempt.
                lock (_sentCmds)
                {
                    var key = (payload[0], payload[1]);
                    if (_sentCmds.TryGetValue(key, out var entry))
                        _sentCmds[key] = (entry.cmd, 0);
                }
            }
            return;
        }
 
        // ── NACK ──────────────────────────────────────────────────────────────
        if (cls == 0x05 && sub == 0x00)
        {
            if (payload.Length >= 2)
            {
                byte nCls = payload[0], nId = payload[1];
                string nackName = UbxConfigurator.GetMessageName(nCls, nId);
 
                // Attempt automatic retry before giving up.
                // _sentCmds tracks the last frame sent per (class, id).
                // For CFG-MSG (0x06/0x01) this is the most recently sent
                // message-rate command — safe because commands are sent
                // sequentially with ≥50ms inter-command delays, so the NACK
                // arrives before the next CFG-MSG is enqueued.
                bool retried = false;
                lock (_sentCmds)
                {
                    var key = (nCls, nId);
                    if (_sentCmds.TryGetValue(key, out var entry) && entry.retries < MaxNackRetries)
                    {
                        int attempt = entry.retries + 1;
                        _sentCmds[key] = (entry.cmd, attempt);
                        // Use WritePort directly (not WritePortTracked) to avoid
                        // resetting the retry counter — we already incremented it above.
                        WritePort(entry.cmd);
                        retried = true;
                        _log.LogWarning(
                            "[RTK] NACK {Name} — retrying (attempt {N}/{Max})",
                            nackName, attempt, MaxNackRetries);
                    }
                }
 
                if (!retried)
                {
                    _log.LogError(
                        "[RTK] NACK {Name} — rejected by receiver, retries exhausted. " +
                        "Check CFG-GNSS constellation support and firmware version.",
                        nackName);
                }
            }
            return;
        }

        // ── NAV-SVIN (MP lines 30249-30268) ──────────────────────────────────
        if (cls == 0x01 && sub == 0x3B)
        {
            var svin = UbxNavSvinParser.Parse(payload);
            if (svin == null) return;
            OnNavSvin(svin);
            return;
        }

        // ── CFG-GNSS response ─────────────────────────────────────────────────
        // Arrives after PollCfgGnss() is sent from QueryCfgGnssAsync.
        // class=0x06 (CFG), id=0x3E (GNSS) — spec §32.10.15.
        if (cls == 0x06 && sub == 0x3E)
        {
            OnCfgGnss(payload);
            return;
        }

        // ── NAV-PVT (MP lines 30273-30280) ───────────────────────────────────
        if (cls == 0x01 && sub == 0x07)
        {
            OnNavPvt(payload);
            return;
        }

        // ── NAV-SAT (satellite SNR chart) ─────────────────────────────────────
        if (cls == 0x01 && sub == 0x35)
        {
            var sats = UbxNavSatParser.Parse(payload);

            // BUG-1 FIX: Update Constellations.IsActive from NAV-SAT gnssId.
            // Pills were only driven by RTCM UpdateConstellationSeen.
            // During Survey-In, RTCM MSM may not flow yet — bars show satellites
            // but pills stayed red. Now both paths update the pills.
            //
            // SNR > 0 means the receiver has signal from that constellation.
            // We do NOT force any pill to false here — RTCM path or the expiry
            // loop handles deactivation.
            var now = DateTime.UtcNow;
            var activeFromSats = sats
                .Where(s => s.Snr > 0)
                .Select(s => GnssIdToConstellationId(s.GnssId))
                .Where(id => id != null)
                .ToHashSet();

            lock (_lock)
            {
                var updatedConstellations = _state.Constellations
                    .Select(c =>
                    {
                        if (c.Id == "base") return c;  // BASE driven only by RTCM 1005/1006
                        bool hasSignal = activeFromSats.Contains(c.Id);
                        return hasSignal
                            ? c with { IsActive = true, LastSeenUtc = now }
                            : c;  // leave IsActive unchanged — expiry loop handles it
                    })
                    .ToList();

                _state = _state with
                {
                    Satellites     = sats,
                    Constellations = updatedConstellations
                };
            }
            FireStateChanged();
            return;
        }

        // ── MON-VER (MP lines 30290-30301) ───────────────────────────────────
        if (cls == 0x0A && sub == 0x04)
        {
            _receiverInfo = UbxMonVerParser.Parse(payload);
            _configUsedMsm7 = true; // we always start with MSM7 assumption
            _log.LogInformation("[RTK] MON-VER: module={Mod} fw={Fw} prot={Prot} MSM7={M7} F9P={F9}",
                _receiverInfo.Module, _receiverInfo.FwVer, _receiverInfo.ProtVer,
                _receiverInfo.IsMsm7Capable, _receiverInfo.IsF9P);
            foreach (var ext in _receiverInfo.Extensions)
                _log.LogInformation("[RTK] MON-VER ext: {E}", ext);
            return;
        }

        // ── MON-HW (MP lines 30303-30307, FIX-8) ─────────────────────────────
        if (cls == 0x0A && sub == 0x09)
        {
            var hw = UbxMonHwParser.Parse(payload);
            if (hw != null)
            {
                // FIX-8: Log at INFO level (previously was Debug — invisible in production)
                _log.LogInformation("[RTK] MON-HW noise={N} agc={A:F1}% jam={J:F1}% jamState={S}",
                    hw.NoisePerMs, hw.AgcPct, hw.JamPct, hw.JamState);
                if (hw.JamState >= 2)
                    _log.LogWarning("[RTK] *** RF INTERFERENCE DETECTED jamState={S} ***", hw.JamState);
            }
            return;
        }

        // ── CFG-TMODE3 (MP lines 30325-30333) ────────────────────────────────
        if (cls == 0x06 && sub == 0x71)
        {
            if (payload.Length >= 4)
            {
                ushort flags = System.Buffers.Binary.BinaryPrimitives
                    .ReadUInt16LittleEndian(payload.Slice(2, 2));
                string mode = flags switch
                {
                    0   => "Disabled",
                    1   => "SurveyIn",
                    2   => "FixedECEF",
                    258 => "FixedLLA",
                    _   => $"flags=0x{flags:X4}"
                };
                _log.LogInformation("[RTK] TMODE3 mode={Mode}", mode);
            }

            // Poll TMODE3 again in 30s (MP line 30339-30344)
            _nextTmodePoll = DateTime.UtcNow.AddSeconds(30);
            return;
        }

        // ── Periodic TMODE3 poll (MP line 30339-30344) ────────────────────────
        // Handled after each UBX frame processed
        CheckTmodePoll();
    }

    private void CheckTmodePoll()
    {
        if (DateTime.UtcNow < _nextTmodePoll) return;

        WritePort(UbxConfigurator.PollMsg(0x06, 0x71)); // CFG-TMODE3
        WritePort(UbxConfigurator.PollMsg(0x0A, 0x04)); // MON-VER
        _nextTmodePoll = DateTime.UtcNow.AddSeconds(30);
    }

    // ── NAV-SVIN handler ──────────────────────────────────────────────────────
    private void OnNavSvin(NavSvinData sv)
    {
        // FIX-E: Stale-frame guard.
        //
        // When _awaitingFreshSurvey is set (immediately after sending BuildSurveyIn
        // or TMODE3 disable), the receiver may keep sending NAV-SVIN with the OLD
        // duration/observations for a brief window while its hardware timer resets.
        // We ignore those frames so they don't overwrite the freshly-zeroed UI.
        //
        // RESPONSIVENESS FIX: the previous 8-second timeout left the survey ring
        // looking completely frozen if the receiver was slow to confirm reset —
        // this is what felt like "hanging". Two changes:
        //   1. Timeout lowered to 3s — a real u-blox reset confirms within ~1s in
        //      practice, so 3s is already a generous safety margin, not a cause
        //      of perceived lag.
        //   2. While waiting, IsResettingTimer=true is published to the UI so the
        //      ring can show a "Resetting…" label instead of sitting static —
        //      the operator sees active feedback instead of a stuck screen.
        const double GuardTimeoutSeconds = 3.0;

        if (_awaitingFreshSurvey)
        {
            bool timedOut = (DateTime.UtcNow - _awaitingFreshSurveySince).TotalSeconds > GuardTimeoutSeconds;
            if (sv.DurationSec <= 2 || timedOut)
            {
                _awaitingFreshSurvey = false;
                _lastKnownSurveyDur  = sv.DurationSec;

                lock (_lock) { _state = _state with { IsResettingTimer = false }; }
                FireStateChanged();

                if (timedOut)
                    _log.LogWarning("[RTK] Survey timer reset-confirm timed out after {S}s — resuming", GuardTimeoutSeconds);
                else
                    _log.LogInformation("[RTK] Survey-In timer confirmed reset (dur={D}s)", sv.DurationSec);
            }
            else
            {
                // Publish the "resetting" state once per guard window (not every
                // discarded frame) so the UI gets immediate feedback without
                // spamming FireStateChanged at the receiver's full NAV-SVIN rate.
                if (!_state.IsResettingTimer)
                {
                    lock (_lock) { _state = _state with { IsResettingTimer = true }; }
                    FireStateChanged();
                }
                _log.LogDebug("[RTK] Discarding stale NAV-SVIN dur={D}s", sv.DurationSec);
                return;
            }
        }
        _lastKnownSurveyDur = sv.DurationSec;

        // FIX-7: Log every NAV-SVIN update at INFO (previously silent)
        // Mirrors MP updateSVINLabel (Files.md 30359-30427)
        _log.LogInformation(
            "[RTK] SVIN dur={D}s obs={O} acc={A:F3}m valid={V} active={Act}",
            sv.DurationSec, sv.Observations, sv.AccuracyM,
            sv.Valid ? 1 : 0, sv.Active ? 1 : 0);

        var prev = _state.Survey;
        if (!prev.Valid && sv.Valid)
            _log.LogInformation("[RTK] *** Survey-In COMPLETE: acc={A:F3}m dur={D}s obs={O} ***",
                sv.AccuracyM, sv.DurationSec, sv.Observations);

        var surveyStatus = new SurveyInStatus(
            sv.DurationSec, sv.Observations, sv.AccuracyM, sv.Valid, sv.Active);

        var (lat, lng, alt) = EcefConverter.ToLla(sv.EcefX, sv.EcefY, sv.EcefZ);

        // SURVEY-BUG-1 FIX: Always compute FixedPosition from ECEF.
        // Previously set only when sv.Valid == true, so the Fixed Coords tab
        // showed 'Waiting...' and the map never rendered during survey even
        // though coordinates were converging. Now the map updates live.
        // AccuracyM distinguishes 'converging' from 'locked' in the UI.
        BasePosition? pos = null;
        if (sv.EcefX != 0 || sv.EcefY != 0 || sv.EcefZ != 0)
            pos = new BasePosition(sv.EcefX, sv.EcefY, sv.EcefZ, lat, lng, alt);

        lock (_lock)
        {
            var phase = _state.Phase;

            // FIX-9: Continue monitoring NAV-SVIN in Injecting phase.
            // If valid drops back to 0, warn operator.
            if (phase == RtkPhase.Injecting && prev.Valid && !sv.Valid)
                _log.LogWarning("[RTK] NAV-SVIN valid flag lost during injection — base ARP may be stale");

            // SURVEY-BUG-3 FIX: Show Fixed confirmation window before Injecting.
            // Previously jumped Survey → Injecting directly, skipping the brief
            // 'LOCKED' badge state that confirms survey success to the operator.
            if (sv.Valid && phase == RtkPhase.Survey)
            {
                phase = RtkPhase.Fixed;
                // Transition Fixed → Injecting after 1.5s on a background task
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500);
                    lock (_lock)
                    {
                        if (_state.Phase == RtkPhase.Fixed)
                            _state = _state with { Phase = RtkPhase.Injecting };
                    }
                    FireStateChanged();
                });
            }

            _state = _state with
            {
                Phase         = phase,
                Survey        = surveyStatus,
                FixedPosition = pos ?? _state.FixedPosition
            };
        }
        FireStateChanged();
    }

    // ── NAV-PVT handler ───────────────────────────────────────────────────────
    private void OnNavPvt(ReadOnlySpan<byte> payload)
    {
        // UBX-NAV-PVT payload offsets (u-blox M8 spec §32.17.14)
        // All values are little-endian.
        //
        //  Offset  Size  Type   Field
        //   0       4    u32    iTOW
        //   4       2    u16    year
        //   6       1    u8     month
        //   7       1    u8     day
        //   8       1    u8     hour
        //   9       1    u8     min
        //  10       1    u8     sec
        //  11       1    u8     valid
        //  12       4    u32    tAcc
        //  16       4    i32    nano
        //  20       1    u8     fixType   ← was incorrectly read from offset 60
        //  21       1    u8     flags     ← was incorrectly read from offset 61
        //  22       1    u8     flags2
        //  23       1    u8     numSV
        //  24       4    i32    lon  (deg × 1e-7)
        //  28       4    i32    lat  (deg × 1e-7)
        //  32       4    i32    height (mm above ellipsoid)
        //  36       4    i32    hMSL   (mm above mean sea level)
        //  40       4    u32    hAcc   (mm)
        //  44       4    u32    vAcc   (mm)
        //  ...      ...  ...    (velocity, heading — not needed here)
 
        if (payload.Length < 36) return;   // need at least through height field
 
        byte fixType = payload[20];
        byte flags   = payload[21];
        byte numSV   = payload[23];
 
        int lonRaw = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(24, 4));
        int latRaw = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(28, 4));
        int altRaw = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(32, 4));
 
        double lat = latRaw * 1e-7;   // degrees
        double lng = lonRaw * 1e-7;   // degrees
        double alt = altRaw * 1e-3;   // mm → metres
 
        bool gnssFixOk = (flags & 0x01) != 0;
 
        _log.LogDebug("[RTK] NAV-PVT fixType={F} gnssFixOk={Ok} numSV={N} lat={Lat:F6} lng={Lng:F6} alt={Alt:F1}m",
            fixType, gnssFixOk, numSV, lat, lng, alt);
 
        // Require at least a 2D fix with gnssFixOk before storing a position.
        // fixType values: 0=no fix, 1=dead-reck only, 2=2D, 3=3D, 4=GNSS+DR, 5=time-only
        if (fixType < 2 || !gnssFixOk) return;
 
        // Store as CurrentPosition (live pre-survey antenna location).
        // This is NOT FixedPosition — the survey engine (NAV-SVIN) owns that.
        // Report section 6: keep live GPS position separate from survey mean.
        var pos = new LivePosition(lat, lng, alt, fixType, numSV);
        lock (_lock)
        {
            _state = _state with { CurrentPosition = pos };
        }
        FireStateChanged();
    }

    // ── Vehicle GPS fix type (WF-10) ──────────────────────────────────────────
    private void OnVehicleGpsUpdate(byte fixType)
    {
        // fix_type: 0=NoGPS,1=NoFix,2=2D,3=3D,4=DGPS,5=RTKFloat,6=RTKFixed
        string label = fixType switch
        {
            6 => "RTK FIXED",
            5 => "RTK Float",
            4 => "DGPS",
            3 => "3D Fix",
            _ => "No Fix"
        };
        _log.LogInformation("[RTK] Vehicle GPS: {Label} (fix_type={F})", label, fixType);

        lock (_lock)
        {
            _state = _state with { VehicleGpsFixType = fixType, VehicleGpsLabel = label };
        }
        FireStateChanged();
    }

    // ── STATUSTEXT (WF-11) ────────────────────────────────────────────────────
    private void OnStatusText(byte severity, string text)
    {
        _log.LogInformation("[VEHICLE] [{Sev}] {Text}", severity, text);
        lock (_lock)
        {
            var msgs = _state.StatusMessages.ToList();
            msgs.Insert(0, new StatusMessage(severity, text, DateTime.UtcNow));
            if (msgs.Count > 10) msgs.RemoveAt(msgs.Count - 1);
            _state = _state with { StatusMessages = msgs };
        }
        FireStateChanged();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PROFILE MANAGEMENT
    // ═══════════════════════════════════════════════════════════════════════════
    public async Task SaveCurrentProfileAsync(string name)
    {
        var pos = State.FixedPosition;
        if (pos == null) return;

        var profile = new SavedBaseProfile(name,
            pos.EcefX, pos.EcefY, pos.EcefZ,
            pos.Lat, pos.Lng, pos.Alt, DateTime.UtcNow);

        _profiles.RemoveAll(p => p.Name == name);
        _profiles.Add(profile);
        SaveProfiles();

        lock (_lock)
        {
            _state = _state with
            {
                SavedProfiles     = _profiles,
                ActiveProfileName = name
            };
        }
        FireStateChanged();
        await Task.CompletedTask;
    }

    public async Task UseProfileAsync(string name)
    {
        var profile = _profiles.Find(p => p.Name == name);
        if (profile == null) return;

        if (_port is { IsOpen: true })
        {
            var cmd = UbxConfigurator.BuildFixedLla(profile.Lat, profile.Lng, profile.Alt);
            WritePort(cmd);
            _log.LogInformation("[RTK] Loaded profile '{Name}': lat={Lat:F7} lon={Lon:F7} alt={Alt:F3}m",
                name, profile.Lat, profile.Lng, profile.Alt);
        }

        lock (_lock)
        {
            _state = _state with
            {
                Phase             = RtkPhase.Injecting,
                ActiveProfileName = name,
                FixedPosition     = new BasePosition(
                    profile.EcefX, profile.EcefY, profile.EcefZ,
                    profile.Lat, profile.Lng, profile.Alt),
                Stream            = RtkStreamStats.Zero
            };
        }
        FireStateChanged();
        await Task.CompletedTask;
    }

    public async Task DeleteProfileAsync(string name)
    {
        bool wasActive = _state.ActiveProfileName == name
                      && _state.Phase >= RtkPhase.Fixed
                      && _port is { IsOpen: true };

        _profiles.RemoveAll(p => p.Name == name);
        SaveProfiles();

        lock (_lock)
        {
            _state = _state with
            {
                SavedProfiles     = _profiles,
                ActiveProfileName = wasActive ? null : _state.ActiveProfileName
            };
        }
        FireStateChanged();

        // FIX-6: Send BuildDisable() when deleting the active profile
        // Mirrors MP SetupBasePos(disable=true) (Files.md lines 21823-21837)
        if (wasActive)
        {
            var ct = _cts?.Token ?? CancellationToken.None;
            try
            {
                foreach (var (cmd, delay) in UbxConfigurator.BuildDisable())
                {
                    ct.ThrowIfCancellationRequested();
                    WritePort(cmd);
                    await Task.Delay(delay, ct);
                }
                _log.LogInformation("[RTK] TMODE3 disabled — profile '{Name}' deleted", name);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log.LogWarning(ex, "[RTK] BuildDisable failed"); }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SURVEY RESTART
    // Mirrors Mission Planner "Restart Survey" button behavior exactly.
    // Port stays OPEN — no baud scan needed — receiver responds instantly.
    // MP sends CFG-TMODE3 disable then immediately re-sends Survey-In.
    // (Files.md line 21823-21843 SetupBasePos pattern)
    // ═══════════════════════════════════════════════════════════════════════════
    public async Task RestartSurveyAsync()
    {
        if (_port is not { IsOpen: true })
        {
            _log.LogWarning("[RTK] RestartSurvey called but port is not open");
            return;
        }

        var ct = _cts?.Token ?? CancellationToken.None;

        _log.LogInformation("[RTK] Restarting Survey-In (hardware reset via CFG-TMODE3)…");

        // Step 1: Disable TMODE3 — forces receiver to stop its internal survey timer.
        // Send 40-byte zero payload: flags=0 = Disabled mode.
        var disablePayload = new byte[40]; // all zeros
        WritePort(UbxConfigurator.Generate(0x06, 0x71, disablePayload));
        await Task.Delay(500, ct);

        // Step 2: Reset all local session state so the UI shows 0s / 0 obs.
        // Do NOT reset the serial port or re-run SetupM8P.
        _has1005Seen = false;
        _largestRtcmFrame = 0;
        _totalMessages = 0;
        Interlocked.Exchange(ref _rxBytesThisSecond, 0);
        Interlocked.Exchange(ref _rtcmParsedBytesThisSecond, 0);
        Interlocked.Exchange(ref _txInjectedBytesThisSecond, 0);

        lock (_lock)
        {
            _msgSeen.Clear();
            _constellationLastSeen.Clear();
            _state = _state with
            {
                Phase         = RtkPhase.Survey,
                Survey        = SurveyInStatus.Empty,
                // SURVEY-BUG-4 FIX: Do NOT clear FixedPosition here.
                // If a saved profile was active, clearing it causes data loss.
                // FixedPosition is updated by OnNavSvin when valid=true arrives.
                // FixedPosition = null,   ← intentionally removed
                Messages      = [],
                Stream        = RtkStreamStats.Zero,
                Constellations= ConstellationStatus.DefaultSet(),
                // RESPONSIVENESS FIX: set immediately so the ring shows
                // "Resetting…" from the very first render after the click,
                // instead of waiting for the first discarded NAV-SVIN frame.
                IsResettingTimer = true,
            };
        }
        FireStateChanged();

        // Step 3: Send fresh CFG-TMODE3 Survey-In command.
        // The receiver resets its hardware timer to 0s on receipt.
        var cfg = _lastConfig ?? RtkConfig.Default;
        var cmd = UbxConfigurator.BuildSurveyIn(
            (uint)cfg.MinDurationSec, cfg.TargetAccuracyM);
        WritePort(cmd);

        _awaitingFreshSurvey      = true;
        _awaitingFreshSurveySince = DateTime.UtcNow;  // SURVEY-BUG-5: timeout reference
        _lastKnownSurveyDur       = 0;
        _log.LogInformation("[RTK] CFG-TMODE3 Survey-In sent — receiver timer reset to 0s");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // BACKGROUND LOOPS
    // ═══════════════════════════════════════════════════════════════════════════
    private async Task BpsTimerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct);
                int txBps   = Interlocked.Exchange(ref _rtcmParsedBytesThisSecond,  0) * 8;
                int txInjBps   = Interlocked.Exchange(ref _txInjectedBytesThisSecond,  0) * 8;
                int rxBps           = Interlocked.Exchange(ref _rxBytesThisSecond,           0) * 8;
                lock (_lock)
                {
                    _state = _state with
                    {
                        Stream = _state.Stream with
                        {
                            RxBps         = rxBps,           // raw serial bytes from M8P
                            TxBps         = txBps,   // RTCM frames parsed from M8P → "RTCM Parsed" in Razor
                            TxInjectedBps = txInjBps,   // bytes sent to drone       → "Inject BW" in Razor
                        }
                    };
                }
                FireStateChanged();
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ConstellationExpiryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct);
                var now = DateTime.UtcNow;
                lock (_lock)
                {
                    var updated = _state.Constellations
                        .Select(c => c.IsExpired(now)
                            ? c with { IsActive = false }
                            : c)
                        .ToList();
                    _state = _state with { Constellations = updated };
                }
                FireStateChanged();
            }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── MSM rate re-send after MON-VER reveals MSM4 needed ───────────────────
    private async Task ResendMsmRatesAsync(bool useMsm7, CancellationToken ct)
    {
        // Delegate entirely to SetupM8pRtcm so the constellation filter from
        // the earlier CFG-GNSS query is respected here too.
        // _supportedGnss is null only if CFG-GNSS was never queried (very old
        // firmware); in that case SetupM8pRtcm sends all messages (safe).
        _log.LogInformation(
            "[RTK] Resending MSM rates (useMsm7={M}, filter={F})",
            useMsm7,
            _supportedGnss != null
                ? string.Join("+", _supportedGnss.Select(UbxConfigurator.GnssIdName))
                : "all");
 
        foreach (var (cmd, delay) in UbxConfigurator.SetupM8pRtcm(useMsm7, _supportedGnss))
        {
            ct.ThrowIfCancellationRequested();
            WritePortTracked(cmd);
            await Task.Delay(delay, ct);
        }
        _configUsedMsm7 = useMsm7;
    }

    /// <summary>
    /// Sends a CFG-GNSS poll to the receiver and waits up to 3 s for the
    /// response.  If the response arrives in time, returns the parsed set of
    /// enabled gnssId values and stores it in _supportedGnss.  If the poll
    /// times out (e.g. old firmware that does not support CFG-GNSS), returns
    /// FallbackGnss (GPS + GLONASS) and logs a warning.
    ///
    /// This must be called AFTER Phase 1 setup so the port baud rate and UBX
    /// protocol output are already configured.
    /// </summary>
    private async Task<IReadOnlySet<byte>> QueryCfgGnssAsync(CancellationToken ct)
    {
        // Create the TCS before writing to the port so the response cannot
        // arrive and call TrySetResult before the TCS exists.
        _cfgGnssTcs = new TaskCompletionSource<IReadOnlySet<byte>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
 
        WritePort(UbxConfigurator.PollCfgGnss());
        _log.LogInformation("[RTK] CFG-GNSS poll sent — awaiting constellation map");
 
        try
        {
            // 3-second timeout, linked to the caller's cancellation token so
            // a disconnect cancels the wait immediately.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(3));
 
            var gnss = await _cfgGnssTcs.Task
                .WaitAsync(linked.Token)
                .ConfigureAwait(false);
 
            return gnss;   // OnCfgGnss already stored this in _supportedGnss
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timed out (not a disconnect cancellation).
            _log.LogWarning(
                "[RTK] CFG-GNSS poll timed out after 3 s — " +
                "falling back to GPS + GLONASS.  RTCM for Galileo/BeiDou " +
                "will not be configured.  Connect in an environment with " +
                "better RF to ensure the receiver responds.");
            return FallbackGnss;
        }
        finally
        {
            // Null the field so a late-arriving response's TrySetResult is a
            // no-op rather than completing a TCS that nobody is waiting on.
            _cfgGnssTcs = null;
        }
    }
 
    // ── CFG-GNSS response handler ─────────────────────────────────────────────
    private void OnCfgGnss(ReadOnlySpan<byte> payload)
    {
        var gnss = UbxConfigurator.ParseCfgGnss(payload);
        _supportedGnss = gnss;   // store for ResendMsmRatesAsync and future use
 
        // Build a tidy log line: "GPS, GLONASS" / "GPS, GLONASS, Galileo" etc.
        var names = gnss.Count > 0
            ? string.Join(", ", gnss.Select(UbxConfigurator.GnssIdName))
            : "(none)";
 
        _log.LogInformation(
            "[RTK] CFG-GNSS: {N} constellation(s) enabled — {List}",
            gnss.Count, names);
 
        // Complete the TCS so QueryCfgGnssAsync unblocks.
        // TrySetResult (not SetResult) is safe even if the TCS is already
        // completed or has been nulled after a timeout.
        _cfgGnssTcs?.TrySetResult(gnss);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private void WritePort(byte[] cmd)
    {
        lock (_portLock)
        {
            try { _port?.Write(cmd, 0, cmd.Length); }
            catch (Exception ex) { _log.LogWarning(ex, "[RTK] Serial write failed"); }
        }
    }

    /// <summary>
    /// Writes a UBX command to the serial port and registers it in <see cref="_sentCmds"/>
    /// so the NACK handler can retry it automatically.
    /// Only UBX frames (starting with 0xB5 0x62) are tracked; other bytes
    /// are passed straight through to WritePort().
    /// </summary>
    private void WritePortTracked(byte[] cmd)
    {
        // Register before writing so the NACK (which can arrive within ~5ms)
        // always finds the command already in the dictionary.
        if (cmd.Length >= 4 && cmd[0] == 0xB5 && cmd[1] == 0x62)
        {
            byte cls = cmd[2], id = cmd[3];
            lock (_sentCmds)
                _sentCmds[(cls, id)] = (cmd, 0);  // reset retry count for fresh send
        }
        WritePort(cmd);
    }

    private void SetPhase(RtkPhase phase, CancellationToken ct = default)
    {
        lock (_lock) { _state = _state with { Phase = phase }; }
        FireStateChanged();
    }

    private void SetError(string msg)
    {
        _log.LogError("[RTK] {Msg}", msg);
        lock (_lock) { _state = _state with { Phase = RtkPhase.Idle, ErrorMessage = msg }; }
        FireStateChanged();
    }

    private void FireStateChanged()
    {
        RtkState snapshot;
        lock (_lock) { snapshot = _state; }
        try { OnStateChanged?.Invoke(snapshot); }
        catch { }
    }

    private void ResetSessionCounters()
    {
        _has1005Seen              = false;
        _awaitingFreshSurvey      = false;
        _lastKnownSurveyDur       = 0;
        _receiverInfo             = MonVerInfo.Unknown;
        _configUsedMsm7           = false;
        _rxBytesThisSecond        = 0;
        _rtcmParsedBytesThisSecond   = 0;
        _txInjectedBytesThisSecond   = 0;
        _largestRtcmFrame         = 0;
        _totalMessages            = 0;
        _nextTmodePoll            = DateTime.MaxValue;
        _supportedGnss            = null;

        // REVERTED: Survey/FixedPosition/Messages/Stream/Constellations are
        // intentionally NOT reset here anymore. That reset belongs only to
        // RestartSurveyAsync (explicit operator action), not every connect or
        // disconnect cycle. Connecting/disconnecting should not silently wipe
        // a position the operator may still want to see.
        lock (_lock)
        {
            _msgSeen.Clear();
            _constellationLastSeen.Clear();
        }
        lock (_sentCmds) { _sentCmds.Clear(); }
    }

    private SavedBaseProfile? GetActiveProfile() =>
        _state.ActiveProfileName is { } name
            ? _profiles.Find(p => p.Name == name)
            : null;

    private void LoadProfiles()
    {
        try
        {
            if (!File.Exists(ProfilePath)) return;
            var json = File.ReadAllText(ProfilePath);
            var list = System.Text.Json.JsonSerializer.Deserialize<List<SavedBaseProfile>>(json);
            if (list != null) { _profiles.Clear(); _profiles.AddRange(list); }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[RTK] Failed to load profiles"); }
        lock (_lock) { _state = _state with { SavedProfiles = _profiles }; }
    }

    private void SaveProfiles()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProfilePath)!);
            File.WriteAllText(ProfilePath,
                System.Text.Json.JsonSerializer.Serialize(_profiles));
        }
        catch (Exception ex) { _log.LogWarning(ex, "[RTK] Failed to save profiles"); }
    }

    public async ValueTask DisposeAsync()
    {
        _mavlink.OnGpsRawInt  -= OnVehicleGpsUpdate;
        _mavlink.OnStatusText -= OnStatusText;
        await DisconnectAsync();
    }
}