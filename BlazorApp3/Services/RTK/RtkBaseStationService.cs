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
    private RtkConfig? _lastConfig;      // stored in ConnectAsync for RestartSurveyAsync

    // ── TMODE3 poll timer (MP line 30339-30344: every 30s) ────────────────────
    private DateTime _nextTmodePoll = DateTime.MaxValue; // set to real schedule after ConnectAsync completes

    // ── BPS measurement ──────────────────────────────────────────────────────
    private int _rxBytesThisSecond;
    private int _txBytesThisSecond;
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
            foreach (var (cmd, delay) in UbxConfigurator.SetupM8P(useMsm7: true))
            {
                ct.ThrowIfCancellationRequested();
                WritePort(cmd);
                await Task.Delay(delay, ct);
            }
            _log.LogInformation("[RTK] Setup UBLOX complete");
            // Schedule the first periodic TMODE3/MON-VER poll 30s from now.
            // Initialising to MaxValue above suppresses accidental early polls
            // from the first UBX frame arriving during the setup sequence.
            _nextTmodePoll = DateTime.UtcNow.AddSeconds(30);

            // Wait for MON-VER response to arrive and update _receiverInfo.
            // The SetupM8P sequence includes PollMsg(MON-VER) with a 200ms delay,
            // so the response should arrive well within this window.
            // We poll up to 1s in 100ms increments so we react as soon as data arrives.
            for (int i = 0; i < 10 && _receiverInfo.FwVer.Length == 0; i++)
                await Task.Delay(100, ct);

            // If MON-VER confirmed this firmware does NOT support MSM7, re-send
            // just the eight MSM rate commands to switch to MSM4.
            // (The rest of SetupM8P — PRT, RATE, NAV5, NMEA, TMODE3 — is unaffected.)
            if (_receiverInfo.FwVer.Length > 0 && !_receiverInfo.IsMsm7Capable && _configUsedMsm7)
            {
                _log.LogInformation("[RTK] Firmware {Fw} requires MSM4 — reconfiguring RTCM rates",
                    _receiverInfo.FwVer);
                await ResendMsmRatesAsync(useMsm7: false, ct);
            }
            else if (_receiverInfo.FwVer.Length == 0)
            {
                _log.LogWarning("[RTK] MON-VER not received within 1s — proceeding with MSM7 (default)");
            }
        }

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
            var cmd = UbxConfigurator.BuildSurveyIn(
                (uint)cfg.MinDurationSec, cfg.TargetAccuracyM);
            WritePort(cmd);

            SetPhase(RtkPhase.Survey, ct);
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

        // Counter update
        string key = $"Rtcm{msgId}";
        lock (_lock) { _msgSeen.TryGetValue(key, out int c); _msgSeen[key] = c + 1; }
        Interlocked.Increment(ref _totalMessages);
        InterlockedMax(ref _largestRtcmFrame, fBytes);
        Interlocked.Add(ref _txBytesThisSecond, fBytes);

        // seenRTCM constellation tracking (MP lines 30175-30243)
        // FIX-4: Track 1005 first — don't inject until base ARP is available
        UpdateConstellationSeen(msgId);

        // Extract base position from 1005/1006 (MP lines 30430-30481)
        if (msgId is 1005 or 1006)
            ExtractAndStoreBasePos(frame, msgId);

        // FIX-4: Software gate — only inject after 1005/1006 has been seen.
        // Rationale: u-blox hardware emits MSM observables as soon as TMODE3
        // is set, but 1005 only appears after NAV-SVIN valid=1.
        // ArduPilot AP_GPS_RTCM requires 1005 before it can use MSM data.
        if (!_has1005Seen) return;

        // Inject via MAVLink
        if (_mavlink.IsConnected)
            _mavlink.InjectGpsData(frame, (ushort)fBytes);
    }

    // ── seenRTCM (MP lines 30175-30243) ──────────────────────────────────────
    private void UpdateConstellationSeen(int msgId)
    {
        string? constel = msgId switch
        {
            >= 1001 and <= 1077 => "gps",
            1005 or 1006 or 4072 => "base",
            >= 1009 and <= 1087 => "glonass",
            >= 1091 and <= 1097 => "galileo",
            >= 1121 and <= 1127 => "beidou",
            1230 => "glonass",  // GLONASS bias
            _ => null
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

        // ── ACK (MP line 30283-30284) ─────────────────────────────────────────
        if (cls == 0x05 && sub == 0x01)
        {
            if (payload.Length >= 2)
                _log.LogInformation("[RTK] ACK  class=0x{C:X2} id=0x{I:X2}", payload[0], payload[1]);
            return;
        }

        // ── NACK (MP line 30286-30288) ────────────────────────────────────────
        if (cls == 0x05 && sub == 0x00)
        {
            if (payload.Length >= 2)
                _log.LogWarning("[RTK] NACK class=0x{C:X2} id=0x{I:X2} — command rejected by receiver",
                    payload[0], payload[1]);
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
            lock (_lock) { _state = _state with { Satellites = sats }; }
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
        var pos = sv.Valid
            ? new BasePosition(sv.EcefX, sv.EcefY, sv.EcefZ, lat, lng, alt)
            : null;

        lock (_lock)
        {
            var phase = _state.Phase;

            // FIX-9: Continue monitoring NAV-SVIN in Injecting phase.
            // If valid drops back to 0, warn operator.
            if (phase == RtkPhase.Injecting && prev.Valid && !sv.Valid)
                _log.LogWarning("[RTK] NAV-SVIN valid flag lost during injection — base ARP may be stale");

            if (sv.Valid && phase == RtkPhase.Survey)
                phase = RtkPhase.Injecting;

            _state = _state with
            {
                Phase         = phase,
                Survey        = surveyStatus,
                FixedPosition = pos ?? _state.FixedPosition
            };
        }
        FireStateChanged();
    }

    // ── NAV-PVT handler (MP lines 30273-30280) ────────────────────────────────
    private void OnNavPvt(ReadOnlySpan<byte> payload)
    {
        // offset 60: fixType (u8), offset 61: flags (u8)
        if (payload.Length < 62) return;
        byte fixType = payload[60];
        byte flags   = payload[61];
        if (fixType >= 3 && (flags & 1) != 0)
        {
            // gnssFixOk — base has a valid PVT fix
            _log.LogDebug("[RTK] NAV-PVT fixType={F} flags=0x{Fl:X2}", fixType, flags);
        }
    }

    private byte _lastVehicleFix = 255;

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
        if (fixType != _lastVehicleFix)
        {
            _log.LogInformation("[RTK] Vehicle GPS: {Label} (fix_type={F})", label, fixType);
            _lastVehicleFix = fixType;
        }

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
        await Task.Delay(300, ct);

        // Step 2: Reset all local session state so the UI shows 0s / 0 obs.
        // Do NOT reset the serial port or re-run SetupM8P.
        _has1005Seen = false;
        _largestRtcmFrame = 0;
        _totalMessages = 0;
        Interlocked.Exchange(ref _rxBytesThisSecond, 0);
        Interlocked.Exchange(ref _txBytesThisSecond, 0);

        lock (_lock)
        {
            _msgSeen.Clear();
            _constellationLastSeen.Clear();
            _state = _state with
            {
                Phase         = RtkPhase.Survey,
                Survey        = SurveyInStatus.Empty,
                FixedPosition = null,
                Messages      = [],
                Stream        = RtkStreamStats.Zero,
                Constellations= ConstellationStatus.DefaultSet(),
            };
        }
        FireStateChanged();

        // Step 3: Send fresh CFG-TMODE3 Survey-In command.
        // The receiver resets its hardware timer to 0s on receipt.
        var cfg = _lastConfig ?? RtkConfig.Default;
        var cmd = UbxConfigurator.BuildSurveyIn(
            (uint)cfg.MinDurationSec, cfg.TargetAccuracyM);
        WritePort(cmd);

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
                int rxBps = Interlocked.Exchange(ref _rxBytesThisSecond, 0) * 8;
                int txBps = Interlocked.Exchange(ref _txBytesThisSecond, 0) * 8;
                lock (_lock)
                {
                    _state = _state with
                    {
                        Stream = _state.Stream with { RxBps = rxBps, TxBps = txBps }
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
        byte msm7rate = useMsm7 ? (byte)1 : (byte)0;
        byte msm4rate = useMsm7 ? (byte)0 : (byte)1;

        (byte cls, byte id, byte rate)[] msgs =
        {
            (0xF5,0x4A,msm4rate),(0xF5,0x4D,msm7rate),  // GPS
            (0xF5,0x54,msm4rate),(0xF5,0x57,msm7rate),  // GLONASS
            (0xF5,0x5E,msm4rate),(0xF5,0x61,msm7rate),  // Galileo
            (0xF5,0x7C,msm4rate),(0xF5,0x7F,msm7rate),  // BeiDou
        };
        foreach (var (c, i, r) in msgs)
        {
            ct.ThrowIfCancellationRequested();
            WritePort(UbxConfigurator.TurnOnOff(c, i, r));
            await Task.Delay(50, ct);
        }
        _configUsedMsm7 = useMsm7;
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
        _receiverInfo             = MonVerInfo.Unknown;
        _configUsedMsm7           = false;
        _rxBytesThisSecond        = 0;
        _txBytesThisSecond        = 0;
        _largestRtcmFrame         = 0;
        _totalMessages            = 0;
        _nextTmodePoll            = DateTime.MinValue;
        lock (_lock) { _msgSeen.Clear(); _constellationLastSeen.Clear(); }
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