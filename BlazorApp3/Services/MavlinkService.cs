using Asv.IO;
using BlazorApp3.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using System.Collections.Concurrent;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using static MAVLink;

public enum ConnectionType { Serial, UDP, TCP }

public class MavlinkService : BackgroundService
{
    private readonly ILogger<MavlinkService> _logger;
    private readonly object _sync = new();
    private readonly MAVLink.MavlinkParse _parser = new();
    private readonly VehicleProfileService _vehicleProfile;

    // ADD THESE NEW FIELDS after line ~18 (after _parser declaration):
    private readonly IConfiguration _config;
    private int _tcpReconnectAttempts = 0;
    private System.Threading.Timer? _uiTickTimer;

    // UI Notification Throttle (10Hz is standard for GCS stability)
    private readonly TimeSpan _uiNotifyMinPeriod = TimeSpan.FromMilliseconds(66);
    private DateTime _lastUiNotifyUtc = DateTime.MinValue;
    private DateTime _lastRcEventUtc = DateTime.MinValue;
    private DateTime _lastAttitudeEventUtc = DateTime.MinValue; // throttles OnAttitudeUpdated to 50 Hz

    private SerialPort? _serialPort;
    private UdpClient? _udpClient;
    private TcpClient? _tcpClient;
    private NetworkStream? _tcpStream;
    private readonly ConcurrentDictionary<byte, IPEndPoint> _droneEndpoints = new();
    //private ConnectionType _connectionType = ConnectionType.Serial;
    //private string _portName = "COM7";
    private ConnectionType _connectionType;
    private string _portName;
    private int _baudRate;
    // Tracks the rolling sequence number for RTK injections (0-31)
    private int _rtcmSeqNo = 0;
    private bool _shouldBeConnected = false;

    public event Action<string>? OnConnectionStatus;
    public event Action<bool>? OnConnectionDialog;
    public event Action<string>? OnMessageReceived;
    public event Action? OnTelemetryUpdated;
    public event Action? OnAttitudeUpdated;
    public event Action<string[], string>? OnFlightModeParams;  // modes[6], channel
    public event Action<FailsafeParams>? OnFailsafeParams;
    public event Action<BatteryParams>? OnBatteryParams;
    public event Action<AutoSnapEventArgs>? OnVehicleAutoSnap;

    private bool _connDialogOpen = false;
    private bool _gotFirstHeartbeatThisSession = false;
    private bool _pendingArmToast = false;
    private bool _pendingDisarmToast = false;

    // ── MISSION TRANSACTION VERIFICATION FIELDS ──
    private TaskCompletionSource<byte>? _missionAckTcs;
    private TaskCompletionSource<bool>? _missionDownloadTcs;
    private List<WaypointModel> _verificationDownloadQueue = new();
    private int _expectedVerificationCount = 0;

    // ── RC connection tracking ────────────────────────────────────────────
    private DateTime _lastRcPacketUtc = DateTime.MinValue;  // last packet timestamp
    private bool _rcWasConnected = false;

    // RC_CHANNELS arrives at 10 Hz (wireless) or 50 Hz (wired).
    // If no valid packet arrives within this window → RC lost.
    // 2.5s gives 2× the wireless interval as tolerance for jitter.
    private static readonly TimeSpan RcTimeoutSpan = TimeSpan.FromSeconds(2.5);

    // Jitter / stale-data detection for rssi==255 receivers (FrSky, SBUS, PWM).
    // ArduPilot keeps sending frozen last-known PWM values after the transmitter
    // is turned off. We detect staleness by checking whether ANY channel has
    // changed across the last N packets. A live transmitter always drifts slightly.
    private ushort[] _prevRcChannels = new ushort[8];      // channels from prior packet
    private int _rcFrozenCount = 0;                       // consecutive frozen packets (disconnect evidence)
    private int _rcLiveCount = 0;                       // consecutive live/moving packets (connect evidence)
    private const int RcFrozenThreshold = 15;              // ~1.5s at 10Hz to confirm stale → disconnect
    private const int RcLiveThreshold = 3;               // 3 consecutive moving packets → confirm connect
                                                         // Fast enough to feel instant, slow enough to
                                                         // reject the single stale burst on drone connect
    private int _consecutiveHeartbeatCount = 0;
    private DateTime _lastHeartbeatForStability = DateTime.MinValue;
    public ConcurrentDictionary<byte, DroneState> ActiveSwarm { get; } = new();
    public byte PrimarySysId { get; set; } = 1;
    // Thread-safe Status Messages
    public List<string> StatusMessages { get; private set; } = new();
    public int StatusMessageCount { get { lock (StatusMessages) return StatusMessages.Count; } }
    private void ReportConn(string msg) => OnConnectionStatus?.Invoke(msg);
    private void ShowConnDialog(bool show) => OnConnectionDialog?.Invoke(show);

    // --- CALIBRATION & UI STATE ---
    public int LiveMagProgress { get; set; } = 0;
    public string LiveMagStatus { get; set; } = "SYSTEM STANDBY";
    public int LiveAccelProgress { get; set; } = 0;
    public string LiveAccelStatus { get; set; } = "SYSTEM STANDBY";
    public int LiveGyroProgress { get; set; } = 0;
    public string LiveGyroStatus { get; set; } = "SYSTEM STANDBY";
    public string ActiveCalType { get; set; } = "NONE";

    public event Action<ushort[]>? OnRcChannelsUpdated;

    // ── Toast notification system ─────────────────────────────────────────
    public enum ToastLevel { Info, Success, Warning, Error }
    public record ToastMessage(string Text, ToastLevel Level, string? Icon = null);
    public event Action<ToastMessage>? OnToast;
    public void Toast(string text, ToastLevel level = ToastLevel.Info, string? icon = null)
        => OnToast?.Invoke(new ToastMessage(text, level, icon));

    private Dictionary<byte, int> _magProgressTracker = new();
    private DateTime _accelCalStartedUtc = DateTime.MinValue;

    private Dictionary<byte, MAVLink.MAG_CAL_STATUS> _magReportTracker = new();
    public bool IsRcCalibrating { get; private set; } = false;

    // FLTMODE readback — tracks which FLTMODE params have arrived
    private readonly float[] _fltModeValues = new float[6];
    private int _fltModeReceived = 0;
    private float _fltModeChannel = 5;
    public ushort[] RcMin { get; private set; } = new ushort[8];
    public ushort[] RcMax { get; private set; } = new ushort[8];

    public byte target_sysid { get; set; } = 1;
    public byte target_compid { get; set; } = 1;

    // Wireless telemetry (SiK/ESP) has higher latency and heartbeat gaps.
    // Treat any baud rate <= 57600 as wireless for timeout tolerance purposes.
    public bool IsWireless => _baudRate <= 57600;
    private readonly SemaphoreSlim _tcpWriteLock = new(1, 1);
    // Heartbeat timeout: wired=3s, wireless=8s
    // SiK radios frequency-hop and can gap 2-3s without actually being disconnected.
    private double HeartbeatTimeoutSeconds => IsWireless ? 8.0 : 3.0;

    // Packet rate & Battery tracking
    private int _rxPacketCounter = 0;
    private DateTime _rxCounterWindowStartUtc = DateTime.UtcNow;
    private readonly object _sendLock = new object();
    public DateTime LastPacketTime { get; private set; } = DateTime.MinValue;

    private int? _lastGoodBatteryPercent = null;
    private DateTime _lastGoodBatteryUtc = DateTime.MinValue;
    private bool _wasAlerting = false;

    private DroneState _state = new();
    public DroneState State => _state;
    // ── ParameterManager hook ─────────────────────────────────────────────
    public ParameterManager? ParameterManager { get; set; }
    public bool HasVehicle => State.HasVehicleId;

    public void SendParamRequestList()
    {
        if (!HasVehicle) return;
        var packet = new MAVLink.mavlink_param_request_list_t
        {
            target_system = State.SystemId,
            target_component = State.ComponentId
        };
        // FIXED: Using your exact SendPacket signature (msgid, data)
        SendPacket(MAVLink.MAVLINK_MSG_ID.PARAM_REQUEST_LIST, packet);
        _logger.LogInformation("Sent PARAM_REQUEST_LIST to sysid={Sys}", State.SystemId);
    }

    public void SendParamRequestRead(int paramIndex, byte targetSystem, byte targetComponent)
    {
        var packet = new MAVLink.mavlink_param_request_read_t
        {
            target_system = targetSystem,
            target_component = targetComponent,
            param_index = (short)paramIndex,
            param_id = new byte[16] // Blank array for index-based fetch
        };
        // FIXED: Using your exact SendPacket signature
        SendPacket(MAVLink.MAVLINK_MSG_ID.PARAM_REQUEST_READ, packet);
    }
    private List<WaypointModel> _uploadQueue = new();

    public event Action<string>? OnVideoStatusChanged;
    public void ReportVideoStatus(string status) => OnVideoStatusChanged?.Invoke(status);

    public class PortProfile
    {
        public string PortId { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    // ── Parameter readback structs ───────────────────────────────────────
    public record FailsafeParams(
        int ThrEnable,    // FS_THR_ENABLE:  0=disabled 1=RTL 2=continue 3=land
        int ThrValue,     // FS_THR_VALUE:   PWM threshold (default 975)
        int BattEnable,   // FS_BATT_ENABLE: 0=disabled 1=land 2=RTL 5=hold
        int GcsEnable     // FS_GCS_ENABLE:  0=disabled 1=RTL 2=continue
    );
    public record BatteryParams(
        int Monitor,        // BATT_MONITOR
        int Capacity,       // BATT_CAPACITY (mAh)
        double LowVolt,     // BATT_LOW_VOLT (V)
        double CrtVolt,     // BATT_CRT_VOLT (V)
        double ArmVolt,     // BATT_ARM_VOLT (V)
        int LowMah,         // BATT_LOW_MAH
        double VoltMult,    // BATT_VOLT_MULT
        double AmpPerVlt,   // BATT_AMP_PERVLT
        double AmpOffset,   // BATT_AMP_OFFSET
        int NumCells        // BATT_NUM_CELLS
    );

    // ── Failsafe & battery param accumulator ─────────────────────────────
    private int _fsRecv = 0;           // bitmask: bit0=THR_EN,1=THR_VAL,2=BATT_EN,3=GCS_EN
    private int _fsThrEnable = 1;
    private int _fsThrValue = 975;
    private int _fsBattEnable = 2;
    private int _fsGcsEnable = 0;

    private int _battRecv = 0;
    private int _battMonitor = 4;
    private int _battCapacity = 3300;
    private double _battLowVolt = 0;
    private double _battCrtVolt = 0;
    private double _battArmVolt = 0;
    private int _battLowMah = 0;
    private double _battVoltMult = 10.1;
    private double _battAmpPerVlt = 17.0;
    private double _battAmpOffset = 0.0;
    private int _battNumCells = 3;

    public MavlinkService(ILogger<MavlinkService> logger, IConfiguration config, VehicleProfileService vehicleProfile)
    {
        _logger = logger;
        _config = config;
        _vehicleProfile = vehicleProfile;

        _vehicleProfile.OnAutoSnap += args =>
        {
            OnVehicleAutoSnap?.Invoke(args);
            _logger.LogInformation("[VEHICLE] {Message}", args.LogMessage);
        };

        // Load defaults from configuration
        var defaultType = _config["TcpConnection:DefaultConnectionType"] ?? "Serial";
        _connectionType = Enum.TryParse<ConnectionType>(defaultType, out var type)
            ? type
            : ConnectionType.Serial;

        _portName = _config["TcpConnection:DefaultHost"] ?? "COM7";
        _baudRate = int.Parse(_config["TcpConnection:DefaultBaudRate"] ?? "115200");

        _logger.LogInformation("[MAVLink] Default connection: {Type} on {Port} @ {Baud}",
            _connectionType, _portName, _baudRate);
    }

    public bool IsConnected
    {
        get
        {
            // Check transport layer is alive for each connection type
            bool transportAlive = _connectionType switch
            {
                ConnectionType.Serial => _serialPort?.IsOpen == true,
                ConnectionType.TCP => _tcpClient?.Connected == true,
                ConnectionType.UDP => _udpClient != null,
                _ => false
            };
            if (!transportAlive) return false;
            var hb = State.LastHeartbeat;
            if (hb == DateTime.MinValue) return false;
            return (DateTime.UtcNow - hb).TotalSeconds < HeartbeatTimeoutSeconds;
        }
    }

    // Stable cached link-health flag. Unlike IsConnected (which re-evaluates
    // DateTime.UtcNow on every call), this only flips false after 3 consecutive
    // CheckLinkHealth misses (1.5s). UI reads this — no flickering during normal
    // SiK heartbeat gaps which can be 1-2s between packets.
    public bool IsLinkHealthy { get; private set; } = false;

    public List<PortProfile> GetAvailablePorts()
    {
        var portProfiles = new List<PortProfile>();
        string[] basicPorts = SerialPort.GetPortNames();

        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_PnPEntity WHERE Caption like '%(COM%'");
                var hardwareList = searcher.Get().Cast<ManagementBaseObject>().ToList();

                foreach (string port in basicPorts)
                {
                    var match = hardwareList.FirstOrDefault(h => h["Caption"]?.ToString()?.Contains($"({port})") == true);
                    string fullName = match?["Caption"]?.ToString() ?? port;
                    portProfiles.Add(new PortProfile { PortId = port, DisplayName = fullName });
                }
                return portProfiles;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch full COM port names.");
        }

        return basicPorts.Select(p => new PortProfile { PortId = p, DisplayName = p }).ToList();
    }

    public string[] GetSafeStatusMessages()
    {
        lock (StatusMessages) return StatusMessages.ToArray();
    }

    public void ReportStatus(string msg) => ReportConn(msg);

    // ---------------- CONNECT / DISCONNECT ----------------

    // Added 'showDialog' parameter so background booting doesn't hijack the screen
    public void Connect(ConnectionType type, string portName, int speed, bool showDialog = true)
    {
        StopConnection();

        lock (_sync)
        {
            _connectionType = type;
            _portName = string.IsNullOrWhiteSpace(portName) ? _portName : portName;
            _baudRate = speed > 0 ? speed : _baudRate;
            _shouldBeConnected = true;
            _gotFirstHeartbeatThisSession = false;
        }
        _linkHealthFailCount = 0;
        _wasAlerting = false;
        _consecutiveHeartbeatCount = 0;
        _lastHeartbeatForStability = DateTime.MinValue;
        _fltModeReceived = 0;
        Array.Clear(_fltModeValues, 0, 6);
        _fsRecv = 0; _battRecv = 0;
        _battVoltMult = 10.1; _battAmpPerVlt = 17.0; _battAmpOffset = 0.0; _battNumCells = 3;
        // 15 Hz telemetry tick — enough for all text readouts.
        // The HUD horizon animation runs independently in JS at 60fps and
        // does not depend on this timer. Dropping from 30 Hz saves ~15
        // SignalR roundtrips/sec across every mounted component.
        _uiTickTimer?.Dispose(); _uiTickTimer = new System.Threading.Timer(_ => OnTelemetryUpdated?.Invoke(), null, 0, 66);

        CloseConnections();

        if (showDialog)
        {
            _connDialogOpen = true;
            ShowConnDialog(true);
        }

        ReportConn($"Opening {_portName} ...");

        UpdateState(s =>
        {
            s.ConnectionStatus = "Standby (Searching for Drone...)"; // Sleeker Standby message
            s.HasVehicleId = false;
            s.SystemId = 0;
            s.ComponentId = 0;
            s.LastHeartbeat = DateTime.MinValue;
        });

        NotifyUi(force: true);
    }

    public void StopConnection()
    {
        // Auto-restore Pixhawk LED lights on disconnect
        //if (IsConnected)
        //{
        //    SendParameter("NTF_LED_BRIGHT", 3.0f);
        //    Console.WriteLine("[SYSTEM] GCS Disconnecting. Re-enabling Pixhawk RGB LEDs.");
        //}

        lock (_sync) { _shouldBeConnected = false; }
        _uiTickTimer?.Dispose();
        _uiTickTimer = null;
        CloseConnections();

        WipeDataToBlankSlate();

        if (_connDialogOpen)
        {
            _connDialogOpen = false;
            ShowConnDialog(false);
        }

        NotifyUi(force: true);
    }

    // ---------------- BACKGROUND LOOP ----------------
    //
    // Architecture: two tasks, separated by responsibility.
    //
    //  ExecuteAsync  — health monitor only. Opens port, watches heartbeat timeout,
    //                  manages reconnect. Runs every 500ms. Never touches the stream.
    //
    //  ReadLoopAsync — dedicated reader. Uses BaseStream.ReadAsync so it NEVER blocks
    //                  the thread. Fills a ring buffer with raw bytes. Parser consumes
    //                  bytes one at a time. A partial packet just waits in the ring
    //                  buffer until the next chunk arrives — no blocking, no stalling.
    //
    // This is the same architecture used by QGroundControl, Mission Planner, MAVProxy.
    // The old approach (poll BytesToRead → ReadPacket) blocks mid-packet on wireless
    // because MavlinkParse.ReadPacket calls BaseStream.Read() and waits for bytes that
    // haven't arrived yet through the radio, freezing the entire health-check loop.

    private CancellationTokenSource? _readerCts;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool shouldConnect;
            lock (_sync) { shouldConnect = _shouldBeConnected; }

            // Health check runs every tick regardless of connection state
            CheckLinkHealth();

            if (!shouldConnect)
            {
                StopReadLoop();
                CloseConnections();
                await Task.Delay(250, stoppingToken);
                continue;
            }

            if (_connectionType == ConnectionType.Serial && _serialPort == null)
            {
                StopReadLoop();
                if (!TryOpenSerialPort(_portName, _baudRate))
                {
                    ReportConn("Waiting for Serial device...");
                    await Task.Delay(3000, stoppingToken);
                    continue;
                }
                ReportConn($"Port {_portName} opened. Listening...");
                _readerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _ = Task.Run(() => ReadLoopAsync(_serialPort!, _readerCts.Token), stoppingToken);
            }
            else if (_connectionType == ConnectionType.UDP && _udpClient == null)
            {
                StopReadLoop();
                if (!TryOpenUdpPort(_portName))
                {
                    await Task.Delay(3000, stoppingToken);
                    continue;
                }
                _readerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _ = Task.Run(() => UdpReadLoopAsync(_udpClient!, _readerCts.Token), stoppingToken);
            }
            else if (_connectionType == ConnectionType.TCP && _tcpClient == null)
            {
                StopReadLoop();
                if (!await TryOpenTcpClientAsync(_portName))
                {
                    await Task.Delay(3000, stoppingToken);
                    continue;
                }
                _readerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _ = Task.Run(() => TcpReadLoopAsync(_tcpClient!, _tcpStream!, _readerCts.Token), stoppingToken);
            }
            // Health check loop runs at 500ms — fast enough to detect link loss
            // without burning CPU. The reader task handles all actual data.
            await Task.Delay(500, stoppingToken);
        }

        StopReadLoop();
    }

    private void StopReadLoop()
    {
        try { _readerCts?.Cancel(); } catch { }
        _readerCts = null;
    }

    // Dedicated non-blocking MAVLink reader loop.
    //
    // WHY THIS ARCHITECTURE:
    // MavlinkParse.ReadPacket(BaseStream) is SYNCHRONOUS. It reads the header,
    // calculates packet length, then calls BaseStream.Read() again to get the
    // remaining bytes. On wireless, a partial packet in the buffer causes this
    // second Read() to BLOCK the thread until all bytes arrive through the radio
    // (can take 50-200ms per gap). While blocked, CheckLinkHealth() never runs,
    // heartbeats go unprocessed, the timeout fires, dialog opens. This repeats
    // every few seconds in a loop.
    //
    // SOLUTION: Read raw bytes with ReadAsync (non-blocking, async) into a flat
    // buffer. Then parse them ourselves with a MAVLink state machine that holds
    // its state between calls — if a packet is incomplete, we just return and
    // wait for more bytes next ReadAsync. No blocking, no stalling, ever.
    //
    // This is the approach used by QGroundControl (C++ async reads) and
    // the pattern recommended in the Sparx Engineering .NET SerialPort guide.
    private async Task ReadLoopAsync(SerialPort port, CancellationToken ct)
    {
        const int CHUNK = 512;
        var chunk = new byte[CHUNK];
        var accum = new byte[300]; // MAVLink2 max = 10 + 255 payload + 2 CRC + 13 sig = 280
        int accumLen = 0;

        _logger.LogInformation("[MAVLink] Reader started on {port}", port.PortName);

        try
        {
            var stream = port.BaseStream;

            while (!ct.IsCancellationRequested && port.IsOpen)
            {
                int read = 0;
                try
                {
                    // CRITICAL FIX: Removed the 150ms CancelAfter(). 
                    // This stops the infinite OperationCanceledExceptions that were choking the server.
                    read = await stream.ReadAsync(chunk, 0, CHUNK, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; } // Clean exit
                catch (Exception ex) when (ex is System.IO.IOException || ex is InvalidOperationException || ex is UnauthorizedAccessException)
                {
                    _logger.LogWarning("[MAVLink] Serial read error: {msg}", ex.Message);
                    break;
                }

                if (read == 0)
                {
                    await Task.Delay(10, ct);
                    continue;
                }

                for (int i = 0; i < read; i++)
                {
                    var pkt = ParseMavlinkByte(chunk[i], accum, ref accumLen);
                    if (pkt != null) HandlePacket(pkt);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MAVLink] Reader exited unexpectedly");
        }

        _logger.LogInformation("[MAVLink] Reader stopped");

        if (_shouldBeConnected)
        {
            CloseConnections();
            WipeDataToBlankSlate();
            NotifyUi(force: true);
        }
    }

    // MAVLink byte-by-byte state machine parser.
    // Holds partial packet state in 'accum'. Returns a complete MAVLinkMessage
    // when a full valid packet is assembled, or null to keep accumulating.
    //
    // MAVLink2 frame layout:
    //   [0]  0xFD  start marker
    //   [1]  len   payload length
    //   [2]  incompat_flags
    //   [3]  compat_flags
    //   [4]  seq
    //   [5]  sysid
    //   [6]  compid
    //   [7..9] msgid (3 bytes, little-endian)
    //   [10..10+len-1] payload
    //   [10+len]   crc low
    //   [10+len+1] crc high
    //   (+13 bytes if incompat_flags & 0x01 = signature)
    //
    // MAVLink1 frame layout:
    //   [0]  0xFE  start marker
    //   [1]  len
    //   [2]  seq
    //   [3]  sysid
    //   [4]  compid
    //   [5]  msgid (1 byte)
    //   [6..6+len-1] payload
    //   [6+len]   crc low
    //   [6+len+1] crc high
    private MAVLink.MAVLinkMessage? ParseMavlinkByte(byte b, byte[] accum, ref int accumLen)
    {
        const byte STX1 = 0xFE; // MAVLink 1
        const byte STX2 = 0xFD; // MAVLink 2

        // If buffer is empty, only accept a start byte
        if (accumLen == 0)
        {
            if (b == STX1 || b == STX2) { accum[accumLen++] = b; }
            return null;
        }

        // Reject runaway accumulation (corrupt stream) — reset and wait for next STX
        if (accumLen >= accum.Length) { accumLen = 0; return null; }

        accum[accumLen++] = b;

        bool isMav2 = accum[0] == STX2;

        // Minimum bytes needed to know total packet length:
        // MAVLink2: need at least 3 bytes (STX + len + incompat_flags)
        // MAVLink1: need at least 2 bytes (STX + len)
        int headerMin = isMav2 ? 3 : 2;
        if (accumLen < headerMin) return null;

        int payloadLen = accum[1];
        int totalLen;
        if (isMav2)
        {
            bool signed = (accum[2] & 0x01) != 0;
            totalLen = 10 + payloadLen + 2 + (signed ? 13 : 0);
        }
        else
        {
            totalLen = 6 + payloadLen + 2;
        }

        // Haven't received full packet yet
        if (accumLen < totalLen) return null;

        // Full packet in buffer — hand it to MavlinkParse via a MemoryStream.
        // This is safe because the packet is COMPLETE — no blocking read will occur.
        try
        {
            var ms = new System.IO.MemoryStream(accum, 0, totalLen);
            var pkt = _parser.ReadPacket(ms);
            return pkt;
        }
        catch
        {
            // Bad CRC or malformed packet — discard and reset
            return null;
        }
        finally
        {
            accumLen = 0; // Ready for next packet
        }
    }

    private bool TryOpenSerialPort(string portName, int speed)
    {
        try
        {
            CloseConnections();
            _serialPort = new SerialPort(portName, speed, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 500,
                WriteTimeout = 500,
                // DTR/RTS must be FALSE for wireless SiK/ESP telemetry radios.
                // CP2102 and FTDI chips toggle DTR on port open, which the radio
                // interprets as a hardware reset — dropping the link 1-2s after connect.
                // Wired Pixhawk USB (115200) is fine with DTR enabled.
                DtrEnable = !IsWireless,
                RtsEnable = !IsWireless
            };
            _serialPort.Open();

            UpdateState(s => s.ConnectionStatus = $"Port open: {portName} @ {speed} (waiting heartbeat...)");
            ReportConn("Port opened. Waiting for HEARTBEAT...");
            NotifyUi(force: true);
            return true;
        }
        catch (Exception ex)
        {
            UpdateState(s => s.ConnectionStatus = $"Searching for {portName}...");
            ReportConn($"Failed to open port: {ex.Message}");
            CloseConnections();
            NotifyUi(force: true);
            return false;
        }
    }

    private bool TryOpenUdpPort(string portInput)
    {
        try
        {
            CloseConnections();

            // If the user types nothing or an IP, default to the standard 14550 MAVLink port
            int port = 14550;
            if (int.TryParse(portInput, out int parsedPort)) port = parsedPort;

            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            // Listen on all network interfaces (Wi-Fi, Ethernet, etc.)
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, port));

            UpdateState(s => s.ConnectionStatus = $"Listening on UDP {port}...");
            ReportConn($"UDP Server started on port {port}. Waiting for drones...");
            NotifyUi(force: true);
            return true;
        }
        catch (Exception ex)
        {
            ReportConn($"Failed to start UDP: {ex.Message}");
            CloseConnections();
            NotifyUi(force: true);
            return false;
        }
    }

    private async Task UdpReadLoopAsync(UdpClient client, CancellationToken ct)
    {
        _logger.LogInformation("[MAVLink] UDP Swarm Reader started");
        byte[] accum = new byte[300];
        int accumLen = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await client.ReceiveAsync(ct);
                var chunk = result.Buffer;
                var endpoint = result.RemoteEndPoint;

                for (int i = 0; i < chunk.Length; i++)
                {
                    var pkt = ParseMavlinkByte(chunk[i], accum, ref accumLen);
                    if (pkt != null)
                    {
                        // Map the drone's SysID to its Wi-Fi IP address instantly
                        if (pkt.sysid != 0 && pkt.sysid != 255)
                        {
                            _droneEndpoints[pkt.sysid] = endpoint;
                        }
                        HandlePacket(pkt);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "[MAVLink] UDP reader exited"); }

        if (_shouldBeConnected)
        {
            CloseConnections();
            WipeDataToBlankSlate();
            NotifyUi(force: true);
        }
    }

    private async Task<bool> TryOpenTcpClientAsync(string target)
    {
        try
        {
            CloseConnections();

            // Get settings from configuration
            string ip = _config["TcpConnection:DefaultHost"] ?? "192.168.45.1";
            int port = int.Parse(_config["TcpConnection:DefaultPort"] ?? "5760");
            int timeoutSeconds = int.Parse(_config["TcpConnection:ConnectTimeoutSeconds"] ?? "5");
            bool enableKeepAlive = bool.Parse(_config["TcpConnection:EnableKeepAlive"] ?? "true");
            int keepAliveInterval = int.Parse(_config["TcpConnection:KeepAliveIntervalSeconds"] ?? "10");

            // Parse user input if provided
            if (!string.IsNullOrWhiteSpace(target))
            {
                if (target.Contains(':'))
                {
                    var parts = target.Split(':', 2);
                    ip = parts[0].Trim();
                    if (int.TryParse(parts[1], out int p)) port = p;
                }
                else
                {
                    ip = target.Trim();
                }
            }

            _logger.LogInformation("[TCP] Connecting to {IP}:{Port} (timeout: {Timeout}s)",
                ip, port, timeoutSeconds);

            _tcpClient = new TcpClient { NoDelay = true }; // Disable Nagle for low latency

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await _tcpClient.ConnectAsync(ip, port, cts.Token);
            }
            catch (OperationCanceledException)
            {
                _tcpClient?.Close();
                throw new TimeoutException($"Connection timeout after {timeoutSeconds}s");
            }

            if (!_tcpClient.Connected)
            {
                throw new Exception($"Failed to connect to {ip}:{port}");
            }

            _tcpStream = _tcpClient.GetStream();

            // Configure socket options
            var socket = _tcpClient.Client;
            if (enableKeepAlive)
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

                if (OperatingSystem.IsWindows())
                {
                    int keepAliveMs = keepAliveInterval * 1000;
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, keepAliveMs);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, keepAliveMs);
                    socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
                }
            }

            _tcpStream.ReadTimeout = int.Parse(_config["TcpConnection:ReadTimeoutSeconds"] ?? "30") * 1000;
            _tcpReconnectAttempts = 0;

            UpdateState(s => s.ConnectionStatus = $"Connected to TCP {ip}:{port}");
            ReportConn($"✓ TCP Connected to {ip}:{port}");
            NotifyUi(force: true);

            _logger.LogInformation("[TCP] Connection established");
            Toast($"TCP connected to {ip}:{port}", ToastLevel.Success, "🔌");

            return true;
        }
        catch (Exception ex)
        {
            // Only log it to the console, don't spam the user's UI with red Toasts
            //_logger.LogWarning($"[TCP] Waiting for drone on {ip}:{port}...");

            // Just elegantly update the status text in the UI
            UpdateState(s => s.ConnectionStatus = "Standby (Waiting for Drone...)");

            CloseConnections();
            NotifyUi(force: true);

            return false;
        }
    }

    private async Task TcpReadLoopAsync(TcpClient client, NetworkStream stream, CancellationToken ct)
    {
        _logger.LogInformation("[TCP] Read loop started");

        const int CHUNK = 1024;
        var chunk = new byte[CHUNK];
        var accum = new byte[300];
        int accumLen = 0;
        int consecutiveErrors = 0;
        const int MAX_ERRORS = 5;

        try
        {
            while (!ct.IsCancellationRequested && client.Connected)
            {
                try
                {
                    int bytesRead = await stream.ReadAsync(chunk, 0, CHUNK, ct).ConfigureAwait(false);

                    if (bytesRead > 0)
                    {
                        consecutiveErrors = 0;

                        for (int i = 0; i < bytesRead; i++)
                        {
                            var pkt = ParseMavlinkByte(chunk[i], accum, ref accumLen);
                            if (pkt != null) HandlePacket(pkt);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("[TCP] Server closed connection");
                        Toast("TCP connection closed by server", ToastLevel.Warning, "🔌");
                        break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (IOException ioEx)
                {
                    consecutiveErrors++;
                    _logger.LogWarning("[TCP] I/O error ({Count}/{Max}): {Msg}",
                        consecutiveErrors, MAX_ERRORS, ioEx.Message);

                    if (consecutiveErrors >= MAX_ERRORS)
                    {
                        _logger.LogError("[TCP] Too many errors, disconnecting");
                        break;
                    }
                    await Task.Delay(100, ct);
                }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    _logger.LogError(ex, "[TCP] Error ({Count}/{Max})", consecutiveErrors, MAX_ERRORS);
                    if (consecutiveErrors >= MAX_ERRORS) break;
                    await Task.Delay(500, ct);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[TCP] Read loop exception");
        }

        // Auto-reconnect infinitely for Skydroid C12
        if (_shouldBeConnected)
        {
            _logger.LogInformation("[TCP] Connection dropped. Returning to standby...");

            // Silently clean up. The main ExecuteAsync loop will automatically 
            // try to reconnect in 3 seconds, forever, until the drone comes back online!
            CloseConnections();
            WipeDataToBlankSlate();
            UpdateState(s => s.ConnectionStatus = "Standby (Waiting for Drone...)");
            NotifyUi(force: true);
        }
    }

    private void WipeDataToBlankSlate()
    {
        // Reset RC + sensor tracking state
        _rcWasConnected = false;
        _lastRcPacketUtc = DateTime.MinValue;
        Array.Clear(_prevRcChannels, 0, 8);
        _rcFrozenCount = 0;
        _rcLiveCount = 0;
        IsLinkHealthy = false;
        _lastGpsFix = -1;
        _lastBattPct = -1;
        _sentLowBattToast = false;
        _sentCritBattToast = false;
        _lastFlightMode = "";
        _sensorAlerted.Clear();
        ActiveSwarm.Clear(); // Clears ghost drones on disconnect

        // Immediately clear RC connected flag in DroneState so the HUD
        // icon turns grey the moment the drone disconnects — not after the
        // 5-second RC timeout fires (which is guarded by _shouldBeConnected).
        UpdateState(s => s.IsRcConnected = false);

        // 1. Reset all Calibration UI variables back to default
        LiveMagProgress = 0;
        LiveMagStatus = "SYSTEM STANDBY";
        LiveAccelProgress = 0;
        LiveAccelStatus = "SYSTEM STANDBY";
        LiveGyroProgress = 0;
        LiveGyroStatus = "SYSTEM STANDBY";
        ActiveCalType = "NONE";
        IsRcCalibrating = false;

        _magProgressTracker.Clear();
        _magReportTracker.Clear();

        // 2. Clear the Messages terminal
        lock (StatusMessages) { StatusMessages.Clear(); }

        // 3. Nuke the DroneState and replace it with a brand new, empty one
        lock (_sync)
        {
            _state = new DroneState();
            _state.ConnectionStatus = "Disconnected";
        }
    }

    // ---------------- PACKET HANDLING ----------------

    private void HandlePacket(MAVLink.MAVLinkMessage packet)
    {
        var now = DateTime.UtcNow;
        LastPacketTime = now;

        // ── SWARM TELEMETRY INTERCEPTOR ─────────────────────────────────────────
        // If the packet comes from a drone that isn't our primary target, 
        // update its specific entry in the ActiveSwarm dictionary.
        if (packet.sysid != target_sysid && packet.sysid != 0 && packet.sysid != 255)
        {
            var swarmNode = ActiveSwarm.GetOrAdd(packet.sysid, id => new DroneState { SystemId = id, HasVehicleId = true });

            lock (swarmNode)
            {
                swarmNode.LastHeartbeat = DateTime.UtcNow;

                if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.HEARTBEAT)
                {
                    var hb = (MAVLink.mavlink_heartbeat_t)packet.data;
                    _vehicleProfile.UpdateFromHeartbeat(hb.type);
                    swarmNode.FlightMode = GetFlightModeString(hb.custom_mode);
                    swarmNode.IsArmed = (((MAVLink.MAV_MODE_FLAG)hb.base_mode) & MAVLink.MAV_MODE_FLAG.SAFETY_ARMED) != 0;
                }
                else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT)
                {
                    var pos = (MAVLink.mavlink_global_position_int_t)packet.data;
                    swarmNode.Altitude = pos.relative_alt / 1000.0f;
                }
                else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.SYS_STATUS)
                {
                    var sys = (MAVLink.mavlink_sys_status_t)packet.data;
                    if (sys.battery_remaining <= 100) swarmNode.BatteryPercent = sys.battery_remaining;
                }
            }
        }
        // ────────────────────────────────────────────────────────────────────────

        _rxPacketCounter++;
        if ((now - _rxCounterWindowStartUtc).TotalSeconds >= 1.0)
        {
            var pps = _rxPacketCounter / (now - _rxCounterWindowStartUtc).TotalSeconds;
            _rxPacketCounter = 0;
            _rxCounterWindowStartUtc = now;
            UpdateState(s => { s.RxPacketsPerSec = pps; s.LastPacketUtc = now; });
        }

        if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.HEARTBEAT)
        {
            var hb = (MAVLink.mavlink_heartbeat_t)packet.data;
            _vehicleProfile.UpdateFromHeartbeat(hb.type);

            UpdateState(s =>
            {
                s.LastHeartbeat = now;
                s.ConnectionStatus = "Receiving Data";

                if (!s.HasVehicleId)
                {
                    s.SystemId = packet.sysid;
                    s.ComponentId = packet.compid;
                    s.HasVehicleId = true;
                    target_sysid = packet.sysid;
                    target_compid = packet.compid;
                    TryConfigureMessageIntervals();
                }

                if (packet.compid == (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_AUTOPILOT1)
                {
                    s.CustomMode = hb.custom_mode;
                    s.FlightMode = GetFlightModeString(hb.custom_mode);
                }

                bool newArmed = (((MAVLink.MAV_MODE_FLAG)hb.base_mode) & MAVLink.MAV_MODE_FLAG.SAFETY_ARMED) != 0;
                bool wasArmed = s.IsArmed;
                s.IsArmed = newArmed;
                if (newArmed && !wasArmed) _pendingArmToast = true;
                if (!newArmed && wasArmed) _pendingDisarmToast = true;
            });
            if (_pendingArmToast) { _pendingArmToast = false; Toast("System ARMED — motors active", ToastLevel.Warning, "🔴"); }
            if (_pendingDisarmToast) { _pendingDisarmToast = false; Toast("System disarmed", ToastLevel.Info, "🟢"); }

            // Count consecutive heartbeats to confirm link stability before closing dialog.
            // Wired: 1 heartbeat is enough (USB is reliable).
            // Wireless: require 2 heartbeats (≥1s apart) to confirm radio link is stable.
            var hbNow = DateTime.UtcNow;
            if ((hbNow - _lastHeartbeatForStability).TotalSeconds < 3.0)
                _consecutiveHeartbeatCount++;
            else
                _consecutiveHeartbeatCount = 1; // gap too long, restart count
            _lastHeartbeatForStability = hbNow;

            int requiredHeartbeats = IsWireless ? 2 : 1;

            if (!_gotFirstHeartbeatThisSession && _consecutiveHeartbeatCount >= requiredHeartbeats)
            {
                _gotFirstHeartbeatThisSession = true;
                IsLinkHealthy = true;
                ReportConn($"Heartbeat confirmed (Sys:{packet.sysid} Comp:{packet.compid}) ✅ Link Stable");
                Toast($"Drone connected — Sys:{packet.sysid} · {(IsWireless ? "Radio link" : "USB link")}", ToastLevel.Success, "📡");

                if (_connDialogOpen)
                {
                    _connDialogOpen = false;
                    ShowConnDialog(false);
                }

                // Auto-disable Pixhawk LEDs on bench arrival
                //SendParameter("NTF_LED_BRIGHT", 0.0f);

                // Request all params on connect — spaced to avoid radio FIFO overflow
                _ = Task.Run(async () =>
                {
                    await Task.Delay(800);
                    for (int fi = 1; fi <= 6; fi++) { RequestParamRead($"FLTMODE{fi}"); await Task.Delay(IsWireless ? 80 : 20); }
                    RequestParamRead("FLTMODE_CH");

                    await Task.Delay(IsWireless ? 200 : 50);
                    // Failsafe params
                    RequestParamRead("FS_THR_ENABLE"); await Task.Delay(IsWireless ? 80 : 20);
                    RequestParamRead("FS_THR_VALUE"); await Task.Delay(IsWireless ? 80 : 20);
                    RequestParamRead("FS_BATT_ENABLE"); await Task.Delay(IsWireless ? 80 : 20);
                    RequestParamRead("FS_GCS_ENABLE"); await Task.Delay(IsWireless ? 80 : 20);
                    // Battery params
                    RequestParamRead("BATT_MONITOR"); await Task.Delay(IsWireless ? 80 : 20);
                    RequestParamRead("BATT_CAPACITY"); await Task.Delay(IsWireless ? 80 : 20);
                    RequestParamRead("BATT_LOW_VOLT"); await Task.Delay(IsWireless ? 80 : 20);
                    RequestParamRead("BATT_CRT_VOLT"); await Task.Delay(IsWireless ? 80 : 20);
                    RequestParamRead("BATT_ARM_VOLT"); await Task.Delay(IsWireless ? 80 : 20);
                    RequestParamRead("BATT_LOW_MAH"); await Task.Delay(IsWireless ? 80 : 20);
                    RequestParamRead("BATT_VOLT_MULT"); await Task.Delay(IsWireless ? 80 : 20);
                    RequestParamRead("BATT_AMP_PERVLT"); await Task.Delay(IsWireless ? 80 : 20);
                    RequestParamRead("BATT_AMP_OFFSET"); await Task.Delay(IsWireless ? 80 : 20);
                    RequestParamRead("BATT_NUM_CELLS"); await Task.Delay(IsWireless ? 80 : 20);
                    // Frame geometry
                    RequestParamRead("FRAME_CLASS"); await Task.Delay(IsWireless ? 80 : 20);
                    RequestParamRead("FRAME_TYPE");

                    // Retry any missed params (SiK drops ~5% of packets)
                    await Task.Delay(IsWireless ? 4000 : 1500);
                    if ((_fsRecv & 0b0001) == 0) { RequestParamRead("FS_THR_ENABLE"); await Task.Delay(IsWireless ? 80 : 20); }
                    if ((_fsRecv & 0b0010) == 0) { RequestParamRead("FS_THR_VALUE"); await Task.Delay(IsWireless ? 80 : 20); }
                    if ((_fsRecv & 0b0100) == 0) { RequestParamRead("FS_BATT_ENABLE"); await Task.Delay(IsWireless ? 80 : 20); }
                    if ((_fsRecv & 0b1000) == 0) { RequestParamRead("FS_GCS_ENABLE"); await Task.Delay(IsWireless ? 80 : 20); }
                    if ((_battRecv & 0x01) == 0) { RequestParamRead("BATT_MONITOR"); await Task.Delay(IsWireless ? 80 : 20); }
                    if ((_battRecv & 0x02) == 0) { RequestParamRead("BATT_CAPACITY"); await Task.Delay(IsWireless ? 80 : 20); }
                    if ((_battRecv & 0x04) == 0) { RequestParamRead("BATT_LOW_VOLT"); await Task.Delay(IsWireless ? 80 : 20); }
                    if ((_battRecv & 0x08) == 0) { RequestParamRead("BATT_CRT_VOLT"); await Task.Delay(IsWireless ? 80 : 20); }
                    if ((_battRecv & 0x10) == 0) { RequestParamRead("BATT_ARM_VOLT"); await Task.Delay(IsWireless ? 80 : 20); }
                    if ((_battRecv & 0x20) == 0) { RequestParamRead("BATT_LOW_MAH"); await Task.Delay(IsWireless ? 80 : 20); }
                });
            }
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.ATTITUDE)
        {
            var att = (MAVLink.mavlink_attitude_t)packet.data;
            float roll = att.roll * (180.0f / (float)Math.PI);
            float pitch = att.pitch * (180.0f / (float)Math.PI);
            float yawDeg = Normalize360(att.yaw * (180.0f / (float)Math.PI));

            UpdateState(s =>
            {
                s.Roll = roll;
                s.Pitch = pitch;
                s.AttitudeYawDeg = yawDeg;

                if (s.HeadingSource == "Unknown")
                {
                    s.HeadingRawDeg = yawDeg;
                    s.HeadingDeg = yawDeg;
                    s.HeadingSource = "ATTITUDE (fallback)";
                    s.Yaw = s.HeadingDeg;
                }
            });
            // Throttle attitude events at the source: cap at 50 Hz.
            // ArduPilot IMU can stream at 100+ Hz; subscribers (HUD JS interop)
            // already have their own guards, but firing the event itself
            // allocates a delegate invocation per packet on the thread pool.
            var _attNow = DateTime.UtcNow;
            if ((_attNow - _lastAttitudeEventUtc).TotalMilliseconds >= 20)
            {
                _lastAttitudeEventUtc = _attNow;
                OnAttitudeUpdated?.Invoke();
            }
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT)
        {
            var pos = (MAVLink.mavlink_global_position_int_t)packet.data;
            UpdateState(s =>
            {
                s.Latitude = pos.lat / 10000000.0;
                s.Longitude = pos.lon / 10000000.0;
                s.Altitude = pos.relative_alt / 1000.0f;
                s.HasRelAlt = true;

                if (pos.hdg != 65535 && s.HeadingSource != "VFR_HUD.heading")
                {
                    float raw = Normalize360(pos.hdg / 100.0f);
                    s.HeadingRawDeg = raw;
                    s.HeadingDeg = (s.HeadingSource == "Unknown" || s.HeadingDeg == 0) ? raw : SmoothHeading(s.HeadingDeg, raw, 0.15f);
                    s.HeadingSource = "GLOBAL_POSITION_INT.hdg";
                    s.Yaw = s.HeadingDeg;
                }
            });
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.GPS_RAW_INT)
        {
            var gps = (MAVLink.mavlink_gps_raw_int_t)packet.data;
            UpdateState(s =>
            {
                if (packet.compid == target_compid && gps.satellites_visible != 255)
                {
                    if (gps.satellites_visible > 0 || gps.fix_type <= 1)
                    {
                        s.SatCount = gps.satellites_visible;
                        s.SatellitesVisible = gps.satellites_visible;
                    }
                    s.GpsFixType = gps.fix_type;
                }
            });
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.SYS_STATUS)
        {
            var sys = (MAVLink.mavlink_sys_status_t)packet.data;

            // ── Sensor health check (only after first heartbeat, at most every 5s) ──
            if (_gotFirstHeartbeatThisSession && (DateTime.UtcNow - _lastSensorHealthCheck).TotalSeconds >= 5)
            {
                _lastSensorHealthCheck = DateTime.UtcNow;
                uint present = sys.onboard_control_sensors_present;
                uint health = sys.onboard_control_sensors_health;
                uint unhealthy = present & ~health;  // present but not healthy

                // MAVLink sensor bit flags (MAV_SYS_STATUS_SENSOR)
                const uint COMPASS = 0x04;   // MAV_SYS_STATUS_SENSOR_3D_MAG
                const uint GYRO = 0x02;   // MAV_SYS_STATUS_SENSOR_3D_GYRO
                const uint ACCEL = 0x01;   // MAV_SYS_STATUS_SENSOR_3D_ACCEL
                const uint GPS_SENS = 0x20;   // MAV_SYS_STATUS_SENSOR_GPS
                const uint BARO = 0x08;   // MAV_SYS_STATUS_SENSOR_ABSOLUTE_PRESSURE

                if ((unhealthy & COMPASS) != 0 && !_sensorAlerted.Contains("COMPASS"))
                { _sensorAlerted.Add("COMPASS"); Toast("Compass sensor unhealthy — calibration required", ToastLevel.Error, "🧭"); }
                else if ((health & COMPASS) != 0)
                    _sensorAlerted.Remove("COMPASS");

                if ((unhealthy & GYRO) != 0 && !_sensorAlerted.Contains("GYRO"))
                { _sensorAlerted.Add("GYRO"); Toast("Gyroscope sensor unhealthy — recalibrate", ToastLevel.Error, "⚙️"); }
                else if ((health & GYRO) != 0)
                    _sensorAlerted.Remove("GYRO");

                if ((unhealthy & ACCEL) != 0 && !_sensorAlerted.Contains("ACCEL"))
                { _sensorAlerted.Add("ACCEL"); Toast("Accelerometer unhealthy — recalibrate", ToastLevel.Error, "⚙️"); }
                else if ((health & ACCEL) != 0)
                    _sensorAlerted.Remove("ACCEL");

                if ((unhealthy & BARO) != 0 && !_sensorAlerted.Contains("BARO"))
                { _sensorAlerted.Add("BARO"); Toast("Barometer sensor unhealthy", ToastLevel.Warning, "📊"); }
                else if ((health & BARO) != 0)
                    _sensorAlerted.Remove("BARO");

                if ((present & GPS_SENS) != 0 && (unhealthy & GPS_SENS) != 0 && !_sensorAlerted.Contains("GPS"))
                { _sensorAlerted.Add("GPS"); Toast("GPS sensor unhealthy — check wiring", ToastLevel.Warning, "🛰️"); }
                else if ((health & GPS_SENS) != 0)
                    _sensorAlerted.Remove("GPS");
            }

            UpdateState(s =>
            {
                if (sys.voltage_battery > 0) s.Voltage = sys.voltage_battery / 1000.0;
                if (sys.current_battery >= 0) s.CurrentDraw = sys.current_battery / 100.0;  // cA → A
                int pct = sys.battery_remaining;

                if (pct == 255 || pct < 0 || pct > 100) return;

                if (pct >= 1)
                {
                    s.BatteryPercent = pct;
                    _lastGoodBatteryPercent = pct;
                    _lastGoodBatteryUtc = DateTime.UtcNow;
                    return;
                }

                if (_lastGoodBatteryPercent.HasValue && (DateTime.UtcNow - _lastGoodBatteryUtc).TotalSeconds < 5) return;
                s.BatteryPercent = 0;
                _lastGoodBatteryPercent = 0;
                _lastGoodBatteryUtc = DateTime.UtcNow;
            });
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.VFR_HUD)
        {
            var hud = (MAVLink.mavlink_vfr_hud_t)packet.data;
            UpdateState(s =>
            {
                s.GroundSpeed = hud.groundspeed;
                s.AirSpeed = hud.airspeed;
                s.ClimbRate = hud.climb;
                if (!s.HasRelAlt) s.Altitude = hud.alt;
            });
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.STATUSTEXT)
        {
            var status = (MAVLink.mavlink_statustext_t)packet.data;
            string text = System.Text.Encoding.ASCII.GetString(status.text).Trim('\0', ' ');
            string upper = text.ToUpper();

            // ── ACCEL CALIBRATION STATUSTEXT HANDLER ──────────────────────────────────
            // ArduPilot 4.x sequence:
            //   1. FC sends "Calibration started"  → we ACK with pos=0 to begin
            //   2. FC sends "Place vehicle level and press any key" → UI shows PLACE LEVEL + NEXT
            //   3. User clicks NEXT → AckCalibrationStep() sends 42429 with pos=1..6
            //   4. FC sends next "Place vehicle..." → repeat until all 6 done
            //   5. FC sends "Calibration successful" → SUCCESS
            if (ActiveCalType == "ACCEL" && upper.Contains("CALIBRATION STARTED"))
            {
                // FC expects a COMMAND_ACK (msg 77) back, not a COMMAND_LONG
                // command=241 (the original cal command), result=0 (MAV_RESULT_ACCEPTED)
                SendAck(241, 0);
                NotifyUi(force: true);
            }
            else if (ActiveCalType == "ACCEL" && upper.Contains("PLACE VEHICLE"))
            {
                if (upper.Contains("LEVEL")) { LiveAccelStatus = "PLACE LEVEL"; LiveAccelProgress = 0; }
                else if (upper.Contains("LEFT")) { LiveAccelStatus = "PLACE LEFT"; LiveAccelProgress = 16; }
                else if (upper.Contains("RIGHT")) { LiveAccelStatus = "PLACE RIGHT"; LiveAccelProgress = 33; }
                else if (upper.Contains("DOWN")) { LiveAccelStatus = "NOSE DOWN"; LiveAccelProgress = 50; }
                else if (upper.Contains("UP")) { LiveAccelStatus = "NOSE UP"; LiveAccelProgress = 66; }
                else if (upper.Contains("BACK")) { LiveAccelStatus = "ON BACK"; LiveAccelProgress = 83; }
                NotifyUi(force: true);
            }
            // 2. Catch Failure Status
            else if (upper.Contains("FAIL") || upper.Contains("ERROR") || upper.Contains("CANCELLED"))
            {
                // Guard: ArduPilot sends a STATUSTEXT "Calibration failed" a few ms AFTER
                // the MAG_CAL_REPORT packet. If MAG_CAL_REPORT already resolved the outcome
                // (ActiveCalType="NONE"), never let a late STATUSTEXT overwrite the result.
                if (upper.Contains("PREARM"))
                {
                    // PreArm check failures shown as warning toasts
                    Toast(text, ToastLevel.Warning, "⚠️");
                }
                else
                {
                    if (ActiveCalType == "ACCEL" && (upper.Contains("ACCEL") || upper.Contains("CANCELLED")))
                    {
                        LiveAccelStatus = "FAILED"; LiveAccelProgress = 0; ActiveCalType = "NONE";
                        Toast("Accel calibration failed", ToastLevel.Error, "❌");
                    }
                    else if (ActiveCalType == "GYRO" && (upper.Contains("GYRO") || upper.Contains("CANCELLED")))
                    {
                        LiveGyroStatus = "FAILED"; LiveGyroProgress = 0; ActiveCalType = "NONE";
                        Toast("Gyro calibration failed", ToastLevel.Error, "❌");
                    }
                    else if (ActiveCalType == "MAG" && upper.Contains("CANCELLED"))
                    {
                        LiveMagStatus = "CANCELLED"; LiveMagProgress = 0; ActiveCalType = "NONE";
                        Toast("Magnetometer calibration cancelled", ToastLevel.Warning, "🧲");
                    }
                }
                NotifyUi(force: true);
            }
            // ── SUCCESS / FAIL STATUSTEXT ──────────────────────────────────────────────
            else if ((upper.Contains("SUCCESS") || upper.Contains("CALIBRATED") || upper.Contains("DONE")) && !upper.Contains("PREARM") && !upper.Contains("NOT"))
            {
                if (ActiveCalType == "GYRO" || upper.Contains("GYRO"))
                {
                    LiveGyroStatus = "SUCCESS"; LiveGyroProgress = 100; ActiveCalType = "NONE";
                    Toast("Gyroscope calibration complete ✓", ToastLevel.Success, "✅");
                    NotifyUi(force: true);
                }
                else if (ActiveCalType == "ACCEL")
                {
                    if ((DateTime.UtcNow - _accelCalStartedUtc).TotalSeconds > 5.0 && LiveAccelProgress >= 83)
                    {
                        LiveAccelStatus = "SUCCESS"; LiveAccelProgress = 100; ActiveCalType = "NONE";
                        Toast("Accelerometer calibration complete ✓", ToastLevel.Success, "✅");
                        NotifyUi(force: true);
                    }
                }
                // Compass success comes via MAG_CAL_REPORT packet, not STATUSTEXT
            }
            // 4. Indestructible Compass Progress Fallback
            if ((upper.Contains("MAG") || upper.Contains("COMPASS")) && upper.Contains("%"))
            {
                int pctIndex = upper.IndexOf("%");
                int startIndex = pctIndex - 1;
                while (startIndex >= 0 && char.IsDigit(upper[startIndex])) startIndex--;
                startIndex++;
                if (startIndex < pctIndex && int.TryParse(upper.Substring(startIndex, pctIndex - startIndex), out int val))
                {
                    LiveMagProgress = val;
                    LiveMagStatus = "CALIBRATING...";
                    NotifyUi(force: false);
                }
            }
            // 5. Compass Start Fallback (In case COMMAND_ACK is dropped)
            if (ActiveCalType == "MAG" && (upper.Contains("STARTED") || upper.Contains("CALIBRATING")))
            {
                if (LiveMagStatus == "STARTING...")
                {
                    LiveMagStatus = "CALIBRATING...";
                    NotifyUi(force: true);
                }
            }

            // Log to Messages Tab
            string logEntry = $"[{DateTime.Now:HH:mm:ss}] {text}";
            lock (StatusMessages)
            {
                StatusMessages.Insert(0, logEntry);
                if (StatusMessages.Count > 100) StatusMessages.RemoveAt(StatusMessages.Count - 1);
            }
            OnMessageReceived?.Invoke(logEntry);
        }
        // RC_CHANNELS (msg 65) — primary RC presence signal
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.RC_CHANNELS)
        {
            var rc = (MAVLink.mavlink_rc_channels_t)packet.data;
            ushort[] latestChannels = new ushort[] {
                rc.chan1_raw, rc.chan2_raw, rc.chan3_raw, rc.chan4_raw,
                rc.chan5_raw, rc.chan6_raw, rc.chan7_raw, rc.chan8_raw
            };
            UpdateState(s => {
                for (int i = 0; i < 8; i++) s.RawChannels[i] = latestChannels[i];
            });

            if (IsRcCalibrating)
            {
                for (int i = 0; i < 8; i++)
                {
                    if (latestChannels[i] < RcMin[i] && latestChannels[i] > 800) RcMin[i] = latestChannels[i];
                    if (latestChannels[i] > RcMax[i] && latestChannels[i] < 2200) RcMax[i] = latestChannels[i];
                }
            }

            // ── RC presence detection — four-layer approach ───────────────────
            //
            // 1. chan_count == 0  → no RC hardware detected                    → NOT connected
            // 2. rssi == 0        → signal explicitly lost                     → NOT connected
            // 3. rssi 1–254       → positive RSSI (CRSF, ELRS, some FrSky)    → CONNECTED
            // 4. rssi == 255      → RSSI unsupported (PPM, SBUS, budget rcvr)
            //                       ArduPilot keeps sending frozen PWM values
            //                       after transmitter off — need jitter check:
            //
            //    JITTER CHECK: A live transmitter always produces ±1–3µs ADC
            //    noise even at rest. Count consecutive packets where all 8
            //    channels are byte-identical. Beyond RcFrozenThreshold → stale.
            //
            //    PACKET-GAP TIMER: CheckLinkHealth() declares RC lost if
            //    _lastRcPacketUtc is not refreshed within RcTimeoutSpan.
            //    This is the backstop when frozen values slip past the jitter check.

            // ── RC presence detection ─────────────────────────────────────────
            //
            // PROBLEM THIS SOLVES:
            // ArduPilot sends RC_CHANNELS continuously even when no RC transmitter
            // is present, filling channels with the last-known frozen PWM values.
            // These look like valid data (900-2100 PWM range) so simple PWM checks
            // incorrectly declare RC connected on drone connect.
            //
            // FOUR-LAYER SOLUTION:
            //
            // Layer 1 — chancount == 0
            //   No RC hardware at all. Immediate NOT-connected.
            //
            // Layer 2 — rssi == 0
            //   Signal explicitly lost. Immediate NOT-connected.
            //
            // Layer 3 — rssi 1–254
            //   Positive RSSI (CRSF, ELRS, some FrSky). Immediate CONNECTED.
            //
            // Layer 4 — rssi == 255 (RSSI not supported — most PPM/SBUS receivers)
            //   A) NOT YET CONNECTED: require RcLiveThreshold consecutive packets
            //      where at least one channel has CHANGED vs the previous packet.
            //      Stale replayed values never change → counter never reaches threshold.
            //      A live transmitter has ADC noise → channels drift → counter rises fast.
            //
            //   B) ALREADY CONNECTED: require RcFrozenThreshold consecutive packets
            //      where ALL channels are byte-identical before declaring lost.
            //      Prevents false disconnects from brief identical bursts.
            //
            // Layer 5 — Packet-gap backstop (CheckLinkHealth)
            //   _lastRcPacketUtc is only refreshed when rcValid=true.
            //   If no valid stamp for RcTimeoutSpan → force disconnect.
            //   Catches any edge case the above layers miss.

            bool rcValid;

            if (rc.chancount == 0)
            {
                // Layer 1: no RC hardware
                rcValid = false;
                _rcFrozenCount = 0;
                _rcLiveCount = 0;
            }
            else if (rc.rssi == 0)
            {
                // Layer 2: signal explicitly lost
                rcValid = false;
                _rcFrozenCount = 0;
                _rcLiveCount = 0;
            }
            else if (rc.rssi > 0 && rc.rssi < 255)
            {
                // Layer 3: positive RSSI — definitive connected
                rcValid = true;
                _rcFrozenCount = 0;
                _rcLiveCount = 0;
            }
            else
            {
                // Layer 4: rssi == 255, RSSI unsupported

                // Guard: at least 1 channel in valid PWM range
                bool anyValidPwm = latestChannels.Any(c => c >= 900 && c <= 2100);

                if (!anyValidPwm)
                {
                    rcValid = false;
                    _rcFrozenCount = 0;
                    _rcLiveCount = 0;
                }
                else if (!_rcWasConnected)
                {
                    // ── Layer 4A: NOT YET CONNECTED — confirm connection ──────
                    // Count packets where at least one channel differs from previous.
                    // Stale replayed data: all channels frozen → counter stays 0 → never connects.
                    // Live transmitter: ADC noise causes drift → counter hits threshold → connects.
                    bool anyChanged = false;
                    for (int i = 0; i < 8; i++)
                    {
                        if (latestChannels[i] != _prevRcChannels[i])
                        {
                            anyChanged = true;
                            break;
                        }
                    }

                    if (anyChanged)
                        _rcLiveCount++;
                    else
                        _rcLiveCount = 0;  // frozen packet resets — stale data can't accumulate

                    _rcFrozenCount = 0;
                    rcValid = false;  // not valid yet — waiting for threshold
                                      // rcValid=true fires below once _rcWasConnected flips
                }
                else
                {
                    // ── Layer 4B: ALREADY CONNECTED — detect disconnection ────
                    // Count consecutive packets where ALL channels are identical.
                    // Short identical bursts are normal (stick held still).
                    // Sustained freeze → transmitter off → ArduPilot replaying stale data.
                    bool allFrozen = true;
                    for (int i = 0; i < 8; i++)
                    {
                        if (latestChannels[i] != _prevRcChannels[i])
                        {
                            allFrozen = false;
                            break;
                        }
                    }

                    _rcFrozenCount = allFrozen ? _rcFrozenCount + 1 : 0;
                    _rcLiveCount = 0;
                    rcValid = _rcFrozenCount < RcFrozenThreshold;
                }
            }

            // Always update snapshot for next comparison
            Array.Copy(latestChannels, _prevRcChannels, 8);

            // ── Apply state transitions ───────────────────────────────────────

            // Check if live-counter just hit threshold (Layer 4A connect confirmation)
            if (!_rcWasConnected && _rcLiveCount >= RcLiveThreshold)
            {
                _rcWasConnected = true;
                _rcLiveCount = 0;
                _rcFrozenCount = 0;
                _lastRcPacketUtc = DateTime.UtcNow;
                UpdateState(s => s.IsRcConnected = true);
                Toast("RC transmitter connected", ToastLevel.Success, "🎮");
            }
            else if (rcValid && _rcWasConnected)
            {
                // Already connected and still valid — just refresh timer
                _lastRcPacketUtc = DateTime.UtcNow;
            }
            else if (rcValid && rc.rssi > 0 && rc.rssi < 255 && !_rcWasConnected)
            {
                // Layer 3 immediate connect (positive RSSI)
                _rcWasConnected = true;
                _lastRcPacketUtc = DateTime.UtcNow;
                UpdateState(s => s.IsRcConnected = true);
                Toast("RC transmitter connected", ToastLevel.Success, "🎮");
            }
            else if (!rcValid && _rcWasConnected)
            {
                // Disconnection confirmed
                _rcWasConnected = false;
                _rcFrozenCount = 0;
                _rcLiveCount = 0;
                Array.Clear(_prevRcChannels, 0, 8);
                UpdateState(s => s.IsRcConnected = false);
                Toast("RC signal lost", ToastLevel.Error, "🎮");
            }

            // Fire AFTER state is updated so subscribers see correct IsRcConnected
            // THROTTLE TO 10Hz: Prevents 50Hz RC packets from crashing the Blazor Server WebSockets!
            var nowUtc = DateTime.UtcNow;
            if ((nowUtc - _lastRcEventUtc).TotalMilliseconds >= 100)
            {
                _lastRcEventUtc = nowUtc;
                OnRcChannelsUpdated?.Invoke(latestChannels);
            }
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST || packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST_INT)
        {
            bool isInt = packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST_INT;
            ushort seq = isInt 
                ? ((MAVLink.mavlink_mission_request_int_t)packet.data).seq 
                : ((MAVLink.mavlink_mission_request_t)packet.data).seq;

            HandleMissionRequest(seq, isInt, packet.sysid, packet.compid);
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MISSION_ACK)
        {
            var ack = (MAVLink.mavlink_mission_ack_t)packet.data;
            
            // Route status parameters straight back up to our active task thread tracking token
            lock (_sync)
            {
                _missionAckTcs?.TrySetResult(ack.type);
            }

            string status = ack.type == 0 ? "Mission Upload Success" : $"Mission Error: {ack.type}";
            string logEntry = $"[{DateTime.Now:HH:mm:ss}] {status}";

            lock (StatusMessages) StatusMessages.Insert(0, logEntry);
            OnMessageReceived?.Invoke(logEntry);
        }
        // ── NEW DOWNWARD MISSION TRANSACTION HANDLERS FOR VERIFICATION ──
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MISSION_COUNT)
        {
            var mCount = (MAVLink.mavlink_mission_count_t)packet.data;
            if (_missionDownloadTcs != null)
            {
                lock (_sync)
                {
                    _expectedVerificationCount = mCount.count;
                    _verificationDownloadQueue.Clear();
                }

                // Fire off immediate read command request for index zero
                RequestWaypointIndexItem(0);
            }
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MISSION_ITEM || packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MISSION_ITEM_INT)
        {
            if (_missionDownloadTcs != null)
            {
                lock (_sync)
                {
                    double lat = 0;
                    double lng = 0;
                    float alt = 0;
                    ushort command = 0;

                    if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MISSION_ITEM)
                    {
                        var mItem = (MAVLink.mavlink_mission_item_t)packet.data;
                        lat = mItem.x;
                        lng = mItem.y;
                        alt = mItem.z;
                        command = mItem.command;
                    }
                    else
                    {
                        var mItemInt = (MAVLink.mavlink_mission_item_int_t)packet.data;
                        lat = mItemInt.x / 10000000.0;
                        lng = mItemInt.y / 10000000.0;
                        alt = mItemInt.z;
                        command = mItemInt.command;
                    }

                    _verificationDownloadQueue.Add(new WaypointModel
                    {
                        Lat = lat,
                        Lng = lng,
                        Alt = (int)alt,
                        Command = ((MAVLink.MAV_CMD)command).ToString()
                    });

                    if (_verificationDownloadQueue.Count < _expectedVerificationCount)
                    {
                        RequestWaypointIndexItem((uint)_verificationDownloadQueue.Count);
                    }
                    else
                    {
                        // Conclude download handshake loop pass sequence by sending a final ACCEPTED ACK to the vehicle
                        var clearAck = new MAVLink.mavlink_mission_ack_t
                        {
                            target_system = target_sysid,
                            target_component = target_compid,
                            type = (byte)MAVLink.MAV_MISSION_RESULT.MAV_MISSION_ACCEPTED,
                            mission_type = (byte)MAVLink.MAV_MISSION_TYPE.MISSION
                        };
                        SendPacket(MAVLink.MAVLINK_MSG_ID.MISSION_ACK, clearAck);

                        // Give the serial port thread pool 150ms to completely transmit the binary 
                        // packet frame over the telemetry radio before letting the verification task conclude.
                        var tcsToResolve = _missionDownloadTcs;
                        _ = Task.Run(async () => 
                        {
                            await Task.Delay(150);
                            tcsToResolve?.TrySetResult(true);
                        });
                    }
                }
            }
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MAG_CAL_PROGRESS)
        {
            var progress = (MAVLink.mavlink_mag_cal_progress_t)packet.data;

            // Only advance — never rewind, never overwrite completed result
            if (progress.completion_pct > LiveMagProgress && LiveMagProgress < 100)
                LiveMagProgress = progress.completion_pct;
            if (ActiveCalType == "MAG")
                LiveMagStatus = "CALIBRATING...";
            NotifyUi(force: true);
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MAG_CAL_REPORT)
        {
            var report = (MAVLink.mavlink_mag_cal_report_t)packet.data;
            // MAG_CAL_STATUS values (ardupilotmega.xml):
            // 4=SUCCESS  5=FAILED(no converge)  6=BAD_ORIENTATION  7=BAD_RADIUS
            // Status 6 and 7 both reach 100% then send a non-SUCCESS report —
            // old code had no case for them, so they fell through to STATUSTEXT
            // which then stomped the result with a generic "FAILED".
            switch (report.cal_status)
            {
                case (byte)MAVLink.MAG_CAL_STATUS.MAG_CAL_SUCCESS:
                    LiveMagStatus = "SUCCESS";
                    LiveMagProgress = 100;
                    ActiveCalType = "NONE";
                    SetMessageInterval(191, 0);
                    SetMessageInterval(192, 0);
                    SendCommand((MAVLink.MAV_CMD)42425, 0, 0, 0, 0, 0, 0, 0);
                    Toast("Magnetometer calibration complete — offsets saved ✓", ToastLevel.Success, "🧲");
                    break;
                case (byte)MAVLink.MAG_CAL_STATUS.MAG_CAL_FAILED:
                    if (LiveMagStatus != "SUCCESS")
                    {
                        LiveMagStatus = "FAILED — MOVE DRONE MORE"; LiveMagProgress = 0; ActiveCalType = "NONE";
                        Toast("Magnetometer calibration failed — rotate drone more", ToastLevel.Error, "🧲");
                    }
                    SetMessageInterval(191, 0); SetMessageInterval(192, 0);
                    break;
                case 6:
                    LiveMagStatus = "FAILED — BAD ORIENTATION"; LiveMagProgress = 0; ActiveCalType = "NONE";
                    SetMessageInterval(191, 0); SetMessageInterval(192, 0);
                    Toast("Mag cal failed — bad compass orientation (check mount)", ToastLevel.Error, "🧲");
                    break;
                case 7:
                    LiveMagStatus = "FAILED — MAGNETIC INTERFERENCE"; LiveMagProgress = 0; ActiveCalType = "NONE";
                    SetMessageInterval(191, 0); SetMessageInterval(192, 0);
                    Toast("Mag cal failed — magnetic interference nearby", ToastLevel.Error, "🧲");
                    break;
            }
            NotifyUi(force: true);
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.PARAM_VALUE)
        {
            var param = (MAVLink.mavlink_param_value_t)packet.data;
            // ── NEW: Route to ParameterManager FIRST ──
            ParameterManager?.HandleParamValue(param);
            string paramName = System.Text.Encoding.ASCII.GetString(param.param_id).Trim('\0', ' ');

            if (paramName == "FRAME_CLASS")
            {
                int fc = (int)param.param_value;
                int mc = fc switch { 1 => 4, 2 => 6, 3 => 8, 5 => 6, 7 => 3, _ => 4 };
                UpdateState(s => { s.FrameClass = fc; s.MotorCount = mc; });
                NotifyUi(force: true);
            }
            else if (paramName == "FRAME_TYPE")
            {
                int ft = (int)param.param_value;
                UpdateState(s => s.FrameType = ft);
                NotifyUi(force: true);
            }
            else if (paramName.StartsWith("FLTMODE") && paramName.Length == 8 && char.IsDigit(paramName[7]))
            {
                // FLTMODE1–6: index 0–5
                int modeIdx = paramName[7] - '1';
                if (modeIdx >= 0 && modeIdx < 6)
                {
                    _fltModeValues[modeIdx] = param.param_value;
                    _fltModeReceived |= (1 << modeIdx);
                    if (_fltModeReceived == 0b111111) // all 6 received
                        FireFlightModeParams();
                }
            }
            else if (paramName == "FLTMODE_CH")
            {
                _fltModeChannel = param.param_value;
                if (_fltModeReceived == 0b111111) FireFlightModeParams();
            }
            // ── Failsafe params ──────────────────────────────────────────
            else if (paramName == "FS_THR_ENABLE") { _fsThrEnable = (int)param.param_value; _fsRecv |= 1; CheckFireFailsafe(); }
            else if (paramName == "FS_THR_VALUE") { _fsThrValue = (int)param.param_value; _fsRecv |= 2; CheckFireFailsafe(); }
            else if (paramName == "FS_BATT_ENABLE") { _fsBattEnable = (int)param.param_value; _fsRecv |= 4; CheckFireFailsafe(); }
            else if (paramName == "FS_GCS_ENABLE") { _fsGcsEnable = (int)param.param_value; _fsRecv |= 8; CheckFireFailsafe(); }
            // ── Battery monitor params ───────────────────────────────────
            else if (paramName == "BATT_MONITOR") { _battMonitor = (int)param.param_value; _battRecv |= 1; CheckFireBattery(); }
            else if (paramName == "BATT_CAPACITY") { _battCapacity = (int)param.param_value; _battRecv |= 2; CheckFireBattery(); }
            else if (paramName == "BATT_LOW_VOLT") { _battLowVolt = param.param_value; _battRecv |= 4; CheckFireBattery(); }
            else if (paramName == "BATT_CRT_VOLT") { _battCrtVolt = param.param_value; _battRecv |= 8; CheckFireBattery(); }
            else if (paramName == "BATT_ARM_VOLT") { _battArmVolt = param.param_value; _battRecv |= 16; CheckFireBattery(); }
            else if (paramName == "BATT_LOW_MAH") { _battLowMah = (int)param.param_value; _battRecv |= 32; CheckFireBattery(); }
            else if (paramName == "BATT_VOLT_MULT") { _battVoltMult = param.param_value; _battRecv |= 64; CheckFireBattery(); }
            else if (paramName == "BATT_AMP_PERVLT") { _battAmpPerVlt = param.param_value; _battRecv |= 128; CheckFireBattery(); }
            else if (paramName == "BATT_AMP_OFFSET") { _battAmpOffset = param.param_value; _battRecv |= 256; CheckFireBattery(); }
            else if (paramName == "BATT_NUM_CELLS") { _battNumCells = (int)param.param_value; _battRecv |= 512; CheckFireBattery(); }
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.COMMAND_ACK)
        {
            var ack = (MAVLink.mavlink_command_ack_t)packet.data;

            // Accel — FC ACKs the cal start command (cmd 241)
            // result=0 = ACCEPTED (cal is now running, FC will send "Calibration started" STATUSTEXT next)
            // result=2 = DENIED (must be disarmed)
            // result=4 = FAILED
            if (ack.command == 241 && ActiveCalType == "ACCEL")
            {
                if (ack.result == 2)
                { LiveAccelStatus = "DENIED — DISARM FIRST"; LiveAccelProgress = 0; ActiveCalType = "NONE"; NotifyUi(force: true); }
                else if (ack.result == 4)
                { LiveAccelStatus = "FAILED"; LiveAccelProgress = 0; ActiveCalType = "NONE"; NotifyUi(force: true); }
                // result=0 means accepted — do NOT treat as success here, wait for STATUSTEXT flow
            }

            // Gyro Rejections
            if (ack.command == 241 && ActiveCalType == "GYRO")
            {
                if (ack.result == 3) { LiveGyroStatus = "AUTO AT BOOT"; LiveGyroProgress = 100; ActiveCalType = "NONE"; NotifyUi(force: true); }
                else if (ack.result == 4 || ack.result == 2) { LiveGyroStatus = "FAILED"; LiveGyroProgress = 0; ActiveCalType = "NONE"; NotifyUi(force: true); }
                else if (ack.result == 0) { LiveGyroStatus = "SUCCESS"; LiveGyroProgress = 100; ActiveCalType = "NONE"; NotifyUi(force: true); }
            }
            // Compass Rejections & Fallbacks
            // Compass Rejections & Success
            // 42424=START  42425=ACCEPT  42426=CANCEL
            if (ActiveCalType == "MAG" && ack.command == 42424)
            {
                if (ack.result == 0)
                {
                    LiveMagStatus = "CALIBRATING...";
                    NotifyUi(force: true);
                }
                else if (ack.result == 3)  // UNSUPPORTED — old firmware, try legacy cal cmd
                {
                    SendCommand((MAVLink.MAV_CMD)241, 0, 1, 0, 0, 0, 0, 0);
                }
                else if (ack.result == 2)  // DENIED — vehicle is armed
                {
                    LiveMagStatus = "DENIED — DISARM FIRST";
                    LiveMagProgress = 0; ActiveCalType = "NONE";
                    SetMessageInterval(191, 0); SetMessageInterval(192, 0);
                    NotifyUi(force: true);
                }
                else  // result=4 FAILED — COMPASS_USE=0 or no compass detected
                {
                    LiveMagStatus = "FAILED — CHECK COMPASS_USE";
                    LiveMagProgress = 0; ActiveCalType = "NONE";
                    SetMessageInterval(191, 0); SetMessageInterval(192, 0);
                    NotifyUi(force: true);
                }
            }
            if (ActiveCalType == "MAG" && ack.command == 241 && ack.result == 0)
            {
                LiveMagStatus = "CALIBRATING..."; NotifyUi(force: true);
            }

            string ackStatus = ack.result switch { 0 => "ACCEPTED", 1 => "TEMP REJECTED", 2 => "DENIED", 3 => "UNSUPPORTED", 4 => "FAILED", _ => "UNKNOWN" };
            string logEntry = $"CMD ACK ({ack.command}): {ackStatus}";

            lock (StatusMessages)
            {
                StatusMessages.Insert(0, logEntry);
                if (StatusMessages.Count > 100) StatusMessages.RemoveAt(StatusMessages.Count - 1);
            }
            OnMessageReceived?.Invoke(logEntry);
        }

        NotifyUi(force: false);
    }

    // ---------------- STATE / UI ----------------

    private void UpdateState(Action<DroneState> mutate)
    {
        lock (_sync)
        {
            var next = new DroneState(_state);
            mutate(next);
            _state = next;
        }
    }

    private void NotifyUi(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastUiNotifyUtc) < _uiNotifyMinPeriod) return;
        _lastUiNotifyUtc = now;
        OnTelemetryUpdated?.Invoke();
    }

    // ---------------- HELPERS ----------------

    private static float Normalize360(float deg)
    {
        deg %= 360f;
        if (deg < 0) deg += 360f;
        return deg;
    }

    private static float AngleDelta(float fromDeg, float toDeg)
    {
        float delta = Normalize360(toDeg) - Normalize360(fromDeg);
        if (delta > 180f) delta -= 360f;
        if (delta < -180f) delta += 360f;
        return delta;
    }

    private static float SmoothHeading(float currentDeg, float newDeg, float alpha)
    {
        float delta = AngleDelta(currentDeg, newDeg);
        return Normalize360(currentDeg + alpha * delta);
    }

    private bool TryGetTarget(out byte sysId, out byte compId)
    {
        sysId = target_sysid;
        compId = target_compid;
        return true;
    }

    private void SendBuffer(byte[] buffer, byte targetSysId = 0)
    {
        // Offload sending to a background thread so the Blazor UI thread NEVER freezes
        _ = Task.Run(async () =>
        {
            if (_connectionType == ConnectionType.Serial && _serialPort?.IsOpen == true)
            {
                try
                {
                    lock (_sendLock) { _serialPort.BaseStream.Write(buffer, 0, buffer.Length); }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Error sending Serial packet"); }
            }
            else if (_connectionType == ConnectionType.UDP && _udpClient != null)
            {
                try
                {
                    // If Target is 0 (Broadcast), blast the command to EVERY IP address we know about
                    if (targetSysId == 0)
                    {
                        foreach (var endpoint in _droneEndpoints.Values.Distinct())
                        {
                            _udpClient.Send(buffer, buffer.Length, endpoint);
                        }
                    }
                    // Unicast: Send directly to the targeted drone's IP address
                    else if (_droneEndpoints.TryGetValue(targetSysId, out var specificEndpoint))
                    {
                        _udpClient.Send(buffer, buffer.Length, specificEndpoint);
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Error sending UDP packet"); }
            }
            else if (_connectionType == ConnectionType.TCP && _tcpStream != null)
            {
                // 1. Wait asynchronously for the lock (does not freeze the thread)
                await _tcpWriteLock.WaitAsync();
                try
                {
                    // 2. Write asynchronously to the OS network buffer
                    await _tcpStream.WriteAsync(buffer, 0, buffer.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error sending TCP packet");
                }
                finally
                {
                    // 3. Always release the lock
                    _tcpWriteLock.Release();
                }
            }
        });
    }

    private void SendPacket(MAVLink.MAVLINK_MSG_ID msgid, object data)
    {
        var buffer = _parser.GenerateMAVLinkPacket20(msgid, data);

        // Peek at the MAVLink packet to see who it's addressed to
        byte routeTarget = target_sysid;

        var type = data.GetType();
        var targetField = type.GetField("target_system");
        if (targetField != null)
        {
            routeTarget = (byte)targetField.GetValue(data);
        }

        _logger.LogInformation(
    "[RTCM TX] msg={Msg} target_sysid={SysId} routeTarget={Route}",
    msgid,
    target_sysid,
    routeTarget);

        SendBuffer(buffer, routeTarget);
    }

    private void TryConfigureMessageIntervals()
    {
        // ── Bandwidth budget ──────────────────────────────────────────────────
        // Wireless (57600 baud) ≈ 5760 B/s usable after radio overhead (~20%).
        // Wired  (115200 baud) ≈ 11520 B/s — can afford high-rate RC streaming.
        //
        // Wireless profile keeps total stream ≈ 2200 B/s (38% of link) leaving
        // 62% headroom for commands, ACKs, calibration data, and radio overhead.
        // ─────────────────────────────────────────────────────────────────────
        _ = SendMessageIntervalsAsync();
    }

    // Sends rate config commands with spacing to avoid UART overflow on wireless.
    // At 57600 baud, 10 COMMAND_LONGs back-to-back = ~65ms TX time which saturates
    // the radio FIFO and causes garbled packets / FC restarts MAVLink negotiation.
    private async Task SendMessageIntervalsAsync()
    {
        try
        {
            // Each command is ~37 bytes. Wireless FIFO is typically 64-128 bytes.
            // Send one at a time with 40ms gap on wireless, 0ms on wired.
            int gapMs = IsWireless ? 40 : 0;

            if (IsWireless)
            {
                // ── WIRELESS profile — total ≈ 2100 B/s ──────────────────────
                // HEARTBEAT 1Hz  = 9B×1   =   9 B/s
                // ATTITUDE  4Hz  = 28B×4  = 112 B/s  (smooth HUD without flooding)
                // GLOBAL_POS 2Hz = 52B×2  = 104 B/s
                // GPS_RAW   1Hz  = 52B×1  =  52 B/s
                // SYS_STATUS 1Hz = 31B×1  =  31 B/s
                // VFR_HUD   2Hz  = 32B×2  =  64 B/s
                // STATUSTEXT 2Hz = 54B×2  = 108 B/s
                // RC_CHANNELS 10Hz= 34B×10= 340 B/s  (usable for RC cal, not flooding)
                // ─────────────────────────────────────────────────────────────
                await SetIntervalAsync((uint)MAVLink.MAVLINK_MSG_ID.HEARTBEAT, 1); await Task.Delay(gapMs);
                await SetIntervalAsync((uint)MAVLink.MAVLINK_MSG_ID.ATTITUDE, 4); await Task.Delay(gapMs);
                await SetIntervalAsync((uint)MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT, 2); await Task.Delay(gapMs);
                await SetIntervalAsync((uint)MAVLink.MAVLINK_MSG_ID.GPS_RAW_INT, 1); await Task.Delay(gapMs);
                await SetIntervalAsync((uint)MAVLink.MAVLINK_MSG_ID.SYS_STATUS, 1); await Task.Delay(gapMs);
                await SetIntervalAsync((uint)MAVLink.MAVLINK_MSG_ID.VFR_HUD, 2); await Task.Delay(gapMs);
                await SetIntervalAsync((uint)MAVLink.MAVLINK_MSG_ID.STATUSTEXT, 2); await Task.Delay(gapMs);
                await SetIntervalAsync((uint)MAVLink.MAVLINK_MSG_ID.RC_CHANNELS, 10); await Task.Delay(gapMs);
            }
            else
            {
                // ── WIRED profile — full rate, all at once (no gap needed) ───
                SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.HEARTBEAT, 1);
                SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.ATTITUDE, 50);
                SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT, 10);
                SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.GPS_RAW_INT, 1);
                SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.SYS_STATUS, 1);
                SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.VFR_HUD, 15);
                SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.STATUSTEXT, 10);
                SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.RC_CHANNELS, 50);
                SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.RC_CHANNELS_RAW, 50);
            }

            // Legacy fallback — REQUEST_DATA_STREAM for older ArduPilot firmware
            TryGetTarget(out var sysId, out var compId);
            int rcRate = IsWireless ? 10 : 50;
            var reqRc = new MAVLink.mavlink_request_data_stream_t
            {
                target_system = sysId,
                target_component = compId,
                req_stream_id = 3,
                req_message_rate = (ushort)rcRate,
                start_stop = 1
            };
            SendPacket(MAVLink.MAVLINK_MSG_ID.REQUEST_DATA_STREAM, reqRc);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to configure message intervals"); }
    }

    private Task SetIntervalAsync(uint msgId, double hz)
    {
        SetMessageInterval(msgId, hz);
        return Task.CompletedTask;
    }

    private void SetMessageInterval(uint msgId, double hz)
    {
        var intervalUs = hz <= 0 ? -1 : (float)(1_000_000.0 / hz);
        TryGetTarget(out var sysId, out var compId);

        var cmd = new MAVLink.mavlink_command_long_t
        {
            target_system = sysId,
            target_component = compId,
            command = (ushort)MAVLink.MAV_CMD.SET_MESSAGE_INTERVAL,
            confirmation = 0,
            param1 = msgId,
            param2 = intervalUs
        };
        SendPacket(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, cmd);
    }

    // ---------------- COMMANDS & CALIBRATION ----------------

    public void SendCommand(MAVLink.MAV_CMD command, float p1, float p2, float p3, float p4, float p5, float p6, float p7)
    {
        if (!TryGetTarget(out byte sysId, out byte compId)) { sysId = 1; compId = 1; }

        var cmd = new MAVLink.mavlink_command_long_t
        {
            command = (ushort)command,
            param1 = p1,
            param2 = p2,
            param3 = p3,
            param4 = p4,
            param5 = p5,
            param6 = p6,
            param7 = p7,
            target_system = sysId,
            target_component = compId,
            confirmation = 0
        };

        // Route this through SendPacket instead of locking the UI thread with _serialPort.Write
        SendPacket(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, cmd);
        _logger.LogDebug("[MAVLink] CMD Sent: {Command}", command);
    }
    // For accel calibration: ArduPilot expects a COMMAND_ACK back from the GCS
    // (not a COMMAND_LONG). This is the original MAVProxy/QGC protocol.
    private void SendAck(ushort command, byte result)
    {
        if (_serialPort == null || !_serialPort.IsOpen) return;
        TryGetTarget(out var sysId, out var compId);
        var ack = new MAVLink.mavlink_command_ack_t
        {
            command = command,
            result = result
        };
        SendPacket(MAVLink.MAVLINK_MSG_ID.COMMAND_ACK, ack);
        Console.WriteLine($"ACK Sent: cmd={command} result={result}");
        _logger.LogDebug("[MAVLink] CMD Sent: {Command}", command);
    }

    public void RequestParamRead(string paramId)
    {
        TryGetTarget(out var sysId, out var compId);
        var req = new MAVLink.mavlink_param_request_read_t
        {
            target_system = sysId,
            target_component = compId,
            param_id = System.Text.Encoding.ASCII.GetBytes(paramId.PadRight(16, '\0')),
            param_index = -1  // -1 = look up by name
        };
        SendPacket(MAVLink.MAVLINK_MSG_ID.PARAM_REQUEST_READ, req);
    }

    public void SendParameter(string paramId, float value)
    {
        TryGetTarget(out var sysId, out var compId);
        var req = new MAVLink.mavlink_param_set_t
        {
            target_system = sysId,
            target_component = compId,
            param_value = value,
            param_type = (byte)MAVLink.MAV_PARAM_TYPE.REAL32
        };

        byte[] temp = System.Text.Encoding.ASCII.GetBytes(paramId);
        req.param_id = new byte[16];
        Array.Copy(temp, req.param_id, Math.Min(temp.Length, 16));

        SendPacket(MAVLink.MAVLINK_MSG_ID.PARAM_SET, req);
    }

    /// <summary>
    /// Injects a complete RTCM3 correction frame into the connected vehicle's GPS
    /// via MAVLink GPS_RTCM_DATA messages (message ID 233).
    ///
    /// Called by NtripService for each parsed RTCM3 frame received from the caster.
    /// Runs on the NtripService background thread — SendPacket() is thread-safe.
    ///
    /// Fragmentation protocol (verified against Mission Planner MAVLinkInterface.cs):
    ///   Max 180 bytes per GPS_RTCM_DATA payload.
    ///   Max 4 fragments = 720 bytes total. Frames larger than 720 bytes are rejected.
    ///   flags byte: bit0=isfrag | bits1-2=fragmentId | bits3-7=sequenceId (0-31)
    ///   For messages whose length is a multiple of 180, a zero-length terminating
    ///   fragment is appended so the FC knows the buffer is complete.
    /// </summary>
    public void InjectGpsData(byte[] data, ushort length)
{
    if (data == null || data.Length == 0)
        return;

    const int msglen = 180;

    int dataLength = data.Length;

    if (length > msglen * 4)
    {
        _logger.LogWarning(
            "[RTK] Oversized RTCM frame dropped ({Bytes})",
            length);

        return;
    }

    int seq =
        Interlocked.Increment(ref _rtcmSeqNo) & 0x1F;

    // Ceiling: how many 180-byte chunks to carry the data
int dataPackets = (length + msglen - 1) / msglen;

// For exact multiples of 180, append a zero-length terminator so ArduPilot
// knows the reassembly buffer is complete. For non-exact, the final short
// packet itself serves as the implicit terminator.
bool needsTerminator = (length % msglen == 0);
int nopackets = needsTerminator ? dataPackets + 1 : dataPackets;

// Safety: frames > 720B are already rejected above. The only way nopackets
// can be 5 is for exactly 720B (4 data + 1 terminator) — allow it.
if (nopackets > 5)
    nopackets = 5;

    byte[] buffer = new byte[msglen];

    for (int a = 0; a < nopackets; a++)
    {
        var gps =
            new MAVLink.mavlink_gps_rtcm_data_t();

        gps.flags = 0;

        if (nopackets > 1)
            gps.flags |= 0x01;

        gps.flags |=
            (byte)((a & 0x03) << 1);

        gps.flags |=
            (byte)(seq << 3);

        Array.Clear(buffer);

        int copy =
            Math.Min(
            dataLength - a * msglen,
            msglen);

        gps.data = buffer;

        if (copy > 0)
        {
            Array.Copy(
                data,
                a * msglen,
                gps.data,
                0,
                copy);
        }

        gps.len = (byte)copy;

        SendPacket(
            MAVLink.MAVLINK_MSG_ID.GPS_RTCM_DATA,
            gps);
    }

    _logger.LogTrace(
        "[RTK] Injected RTCM {Bytes}B as {Packets} packets seq={Seq}",
        length,
        nopackets,
        seq);

    _logger.LogInformation(
    "[RTCM] data.Length={DataLen} length={Length}",
    data.Length,
    length);

    _logger.LogWarning(
    "[RTCM-INJECT] len={Len}",
    length);
}

    public void StartCalibration(string type)
    {
        switch (type.ToUpper())
        {
            case "ACCEL":
                LiveAccelProgress = 0;
                LiveAccelStatus = "WAITING...";
                _accelCalStartedUtc = DateTime.UtcNow;
                ActiveCalType = "ACCEL";
                // param5=1 → full 3D interactive cal (original MAVProxy/QGC protocol)
                // param5=4 → simple 1-axis only (silent, no position prompts)
                SendCommand((MAVLink.MAV_CMD)241, 0, 0, 0, 0, 1, 0, 0);
                _ = WatchAccelTimeoutAsync();
                break;
            case "GYRO":
                LiveGyroProgress = 50; LiveGyroStatus = "CALIBRATING...";
                ActiveCalType = "GYRO";
                SendCommand((MAVLink.MAV_CMD)241, 1, 0, 0, 0, 0, 0, 0);
                break;
            case "MAG":
                LiveMagProgress = 0;
                LiveMagStatus = "STARTING...";
                ActiveCalType = "MAG";

                // (a) Subscribe to progress/report packets — ArduPilot does NOT stream
                //     these automatically. Must be requested right before cal starts.
                SetMessageInterval(191, 4);  // MAG_CAL_PROGRESS at 4Hz
                SetMessageInterval(192, 4);  // MAG_CAL_REPORT   at 4Hz

                // (b) Reset COMPASS_LEARN — if set to 3 (EKF-learn), FC silently
                //     rejects MAV_CMD_DO_START_MAG_CAL with result=4 (FAILED)
                SendParameter("COMPASS_LEARN", 0);

                // (c) MAV_CMD_DO_START_MAG_CAL (42424)
                //     param1=0  → all compasses (bitmask; 0=all per MAVLink spec)
                //     param2=0  → NO retry (1 caused endless restart loop at 0%)
                //     param3=1  → autosave on success
                //     param4/5  → 0
                SendCommand((MAVLink.MAV_CMD)42424, 0, 0, 1, 0, 0, 0, 0);
                break;
            case "RADIO":
                for (int i = 0; i < 8; i++) { RcMin[i] = 2200; RcMax[i] = 800; }
                IsRcCalibrating = true;
                break;
            case "CANCEL_ACCEL":
            case "CANCEL_GYRO":
                ActiveCalType = "NONE";
                SendCommand((MAVLink.MAV_CMD)241, 0, 0, 0, 0, 0, 0, 0);
                NotifyUi(force: true);
                break;
            case "CANCEL_MAG":
                ActiveCalType = "NONE";
                SetMessageInterval(191, 0);  // stop progress stream
                SetMessageInterval(192, 0);  // stop report stream
                // 42426 = MAV_CMD_DO_CANCEL_MAG_CAL
                SendCommand((MAVLink.MAV_CMD)42426, 0, 0, 0, 0, 0, 0, 0);
                NotifyUi(force: true);
                break;
        }
    }

    public void AckCalibrationStep()
    {
        if (LiveAccelStatus == "COMPUTING..." || LiveAccelStatus == "RECORDING...") return;

        // MAV_CMD_ACCELCAL_VEHICLE_POS (42429) — tell FC to sample current position
        // param1 = position number (1=level, 2=left, 3=right, 4=nose-down, 5=nose-up, 6=back)
        int posParam = LiveAccelStatus switch
        {
            "PLACE LEVEL" => 1,
            "PLACE LEFT" => 2,
            "PLACE RIGHT" => 3,
            "NOSE DOWN" => 4,
            "NOSE UP" => 5,
            "ON BACK" => 6,
            _ => 1
        };
        // Protocol: send COMMAND_ACK back to FC (not COMMAND_LONG)
        // command = step number (1-6), result = 1 (MAV_RESULT_ACCEPTED)
        SendAck((ushort)posParam, 1);

        // If this was the last position, FC will compute — otherwise wait for next "PLACE VEHICLE" prompt
        LiveAccelStatus = LiveAccelStatus == "ON BACK" ? "COMPUTING..." : "RECORDING...";
        NotifyUi(force: true);
    }

    // If FC never sends the first "PLACE VEHICLE" prompt within 8s, show a clear error
    private async Task WatchAccelTimeoutAsync()
    {
        var startedAt = _accelCalStartedUtc;
        await Task.Delay(8000);
        if (ActiveCalType == "ACCEL" && LiveAccelStatus == "WAITING..."
            && _accelCalStartedUtc == startedAt)
        {
            LiveAccelStatus = "NO RESPONSE — DISARM?";
            LiveAccelProgress = 0;
            ActiveCalType = "NONE";
            NotifyUi(force: true);
        }
    }

    public async Task SaveRcCalibrationAsync()
    {
        IsRcCalibrating = false;

        // Loop through all 8 channels (4 sticks + 4 switches)
        for (int i = 0; i < 8; i++)
        {
            // Only save if the sticks were actually moved (prevents saving dead channels)
            if (RcMin[i] < 1400 && RcMax[i] > 1600)
            {
                SendParameter($"RC{i + 1}_MIN", RcMin[i]);
                await Task.Delay(50); // 50ms buffer prevents serial dropping

                SendParameter($"RC{i + 1}_MAX", RcMax[i]);
                await Task.Delay(50);

                // Throttle (usually CH3) trim sits at the absolute minimum. 
                // All other sticks (Roll, Pitch, Yaw) trim at their mathematical center.
                float trim = (i == 2) ? RcMin[i] : (RcMin[i] + RcMax[i]) / 2.0f;
                SendParameter($"RC{i + 1}_TRIM", trim);
                await Task.Delay(50);
            }
        }

        lock (StatusMessages) StatusMessages.Insert(0, $"[{DateTime.Now:HH:mm:ss}] RC Calibration Saved!");
        NotifyUi(force: true);
    }

    // ---------------- FLIGHT CONTROL ----------------

    public void SendArmRequest(bool arm, bool force = false)
    {
        if (!TryGetTarget(out var sysId, out var compId)) return;
        var req = new MAVLink.mavlink_command_long_t
        {
            target_system = sysId,
            target_component = compId,
            command = (ushort)MAVLink.MAV_CMD.COMPONENT_ARM_DISARM,
            param1 = arm ? 1 : 0,
            param2 = (!arm && force) ? 21196 : 0
        };
        SendPacket(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, req);
    }

    public void SetFlightMode(string modeName)
    {
        uint modeId = modeName.ToUpper() switch { "STABILIZE" => 0, "ALT_HOLD" => 2, "AUTO" => 3, "GUIDED" => 4, "LOITER" => 5, "RTL" => 6, "LAND" => 9, _ => 0 };
        TryGetTarget(out var sysId, out var compId);
        var req = new MAVLink.mavlink_command_long_t { target_system = sysId, target_component = compId, command = (ushort)MAVLink.MAV_CMD.DO_SET_MODE, param1 = 1, param2 = modeId };
        SendPacket(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, req);
    }

    public void SaveFlightMode(int position, string modeName)
    {
        // ArduCopter FLTMODE numeric values (matches custom_mode in HEARTBEAT)
        float modeValue = _vehicleProfile.GetModeValue(modeName);
        SendParameter($"FLTMODE{position}", modeValue);
    }

    public void SetEscCalibrationMode() => SendParameter("ESC_CALIBRATION", 3);

    private void CheckFireFailsafe()
    {
        // Fire on core 3 (THR_EN + THR_VAL + BATT_EN). GCS_EN absent on older firmware.
        if ((_fsRecv & 0b0111) == 0b0111)
            OnFailsafeParams?.Invoke(new FailsafeParams(_fsThrEnable, _fsThrValue, _fsBattEnable, _fsGcsEnable));
    }

    private void CheckFireBattery()
    {
        // Fire on core 6 params (bits 0-5). Calibration params may not exist on older firmware.
        if ((_battRecv & 0x3F) == 0x3F)
            OnBatteryParams?.Invoke(new BatteryParams(
                _battMonitor, _battCapacity, _battLowVolt, _battCrtVolt,
                _battArmVolt, _battLowMah, _battVoltMult, _battAmpPerVlt,
                _battAmpOffset, _battNumCells));
    }

    public void SaveBatteryParams(int monitor, int capacity,
        double lowVolt, double crtVolt, double armVolt, int lowMah,
        double voltMult, double ampPerVlt, double ampOffset, int numCells)
    {
        SendParameter("BATT_MONITOR", monitor);
        SendParameter("BATT_CAPACITY", capacity);
        SendParameter("BATT_LOW_VOLT", (float)lowVolt);
        SendParameter("BATT_CRT_VOLT", (float)crtVolt);
        SendParameter("BATT_ARM_VOLT", (float)armVolt);
        SendParameter("BATT_LOW_MAH", lowMah);
        SendParameter("BATT_VOLT_MULT", (float)voltMult);
        SendParameter("BATT_AMP_PERVLT", (float)ampPerVlt);
        SendParameter("BATT_AMP_OFFSET", (float)ampOffset);
        SendParameter("BATT_NUM_CELLS", numCells);
        Toast("Battery parameters saved", ToastLevel.Success, "🔋");
    }

    public void SaveFailsafeParams(int thrEnable, int thrValue, int battEnable, int gcsEnable)
    {
        SendParameter("FS_THR_ENABLE", thrEnable);
        SendParameter("FS_THR_VALUE", thrValue);
        SendParameter("FS_BATT_ENABLE", battEnable);
        SendParameter("FS_GCS_ENABLE", gcsEnable);
        Toast("Failsafe parameters saved", ToastLevel.Success, "🛡️");
    }

    private void FireFlightModeParams()
    {
        // Convert float mode IDs back to string names for the UI
        string[] names = _fltModeValues.Select(v => GetFlightModeString((uint)v)).ToArray();
        string ch = ((int)_fltModeChannel).ToString();
        OnFlightModeParams?.Invoke(names, ch);
    }
    public void SetFrameConfig(int frameClass, int frameType)
    {
        SendParameter("FRAME_CLASS", frameClass);
        SendParameter("FRAME_TYPE", frameType);
        int mc = frameClass switch { 1 => 4, 2 => 6, 3 => 8, 5 => 6, 7 => 3, _ => 4 };
        UpdateState(s => { s.FrameClass = frameClass; s.FrameType = frameType; s.MotorCount = mc; });
        NotifyUi(force: true);
        Toast("Frame configuration applied — reboot required", ToastLevel.Warning, "🔧");
    }
    public void RequestDataStreams() => TryConfigureMessageIntervals();

    public void TestMotor(int motorIndex, float throttlePercent, float durationSec)
    {
        var cmd = new MAVLink.mavlink_command_long_t
        {
            target_system = target_sysid,
            target_component = target_compid,
            command = (ushort)MAVLink.MAV_CMD.DO_MOTOR_TEST,
            param1 = motorIndex,
            param2 = 0,
            param3 = throttlePercent,
            param4 = durationSec
        };
        SendPacket(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, cmd);
    }

    public void TestAllMotors(float throttlePercent, float durationSec)
    {
        for (int i = 1; i <= State.MotorCount; i++) TestMotor(i, throttlePercent, durationSec);
    }

    public async void TestMotorSequence(float throttlePercent, float durationSec)
    {
        for (int i = 1; i <= State.MotorCount; i++) { TestMotor(i, throttlePercent, durationSec); await Task.Delay((int)(durationSec * 1000) + 500); }
    }

    // ---------------- MISSIONS ----------------

    public async Task<bool> UploadMissionAsync(IList<WaypointModel> items)
    {
        // 🛡️ HARDWARE CONNECTION GUARD
        // Prevent the UI from hanging on "Uploading..." if the physical link is dead
        if (!IsLinkHealthy || !IsConnected)
        {
            Toast("Upload failed: No active connection to the Flight Controller.", ToastLevel.Error, "📡");
            return false;
        }

        if (items == null || items.Count == 0)
        {
            Toast("Cannot upload an empty waypoint route list.", ToastLevel.Warning, "📋");
            return false;
        }

        lock (_sync)
        {
            _uploadQueue = items.ToList();
            _missionAckTcs = new TaskCompletionSource<byte>();
        }

        try
        {
            _logger.LogInformation("[MAVLink] Initiating mission upload transaction. Count: {Count}, SysId: {SysId}, CompId: {CompId}", _uploadQueue.Count, target_sysid, target_compid);
            
            // 1. Send the standard MAVLink mission count packet block
            var count = new MAVLink.mavlink_mission_count_t 
            { 
                target_system = target_sysid, 
                target_component = (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_AUTOPILOT1, 
                count = (ushort)_uploadQueue.Count, 
                mission_type = (byte)MAVLink.MAV_MISSION_TYPE.MISSION 
            };
            SendPacket(MAVLink.MAVLINK_MSG_ID.MISSION_COUNT, count);

            // 2. Wait for the vehicle to complete downloading all parts and send a final MISSION_ACK
            int timeoutSec = IsWireless ? 12 : 5;
            var ackResult = await _missionAckTcs.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSec));

            if (ackResult != (byte)MAVLink.MAV_MISSION_RESULT.MAV_MISSION_ACCEPTED)
            {
                _logger.LogError("[MAVLink] Flight Controller rejected mission configuration. Code: {Result}", ackResult);
                return false;
            }

            // 🛑 HARDWARE STABILIZATION DELAY (Crucial for physical Pixhawk setups)
            // Give ArduPilot's internal storage engine time to write the new mission items to 
            // the physical EEPROM flash chips before we flood the 57600 baud radio link with requests.
            _logger.LogInformation("[MAVLink] Upload transaction acknowledged. Allowing hardware EEPROM to settle...");
            await Task.Delay(1200); 

            _logger.LogInformation("[MAVLink] Settle window complete. Starting verification read-back...");
            
            // 3. Trigger the verification read-back cycle to prove EEPROM security
            bool isDataGenuine = await DownloadAndVerifyMissionItemsAsync();
            return isDataGenuine;
        }
        catch (TimeoutException)
        {
            Toast("Mission transaction timed out. Autopilot not responding.", ToastLevel.Error, "⏳");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected exception during mission verification path execution.");
            return false;
        }
        finally
        {
            lock (_sync) { _missionAckTcs = null; }
        }
    }

    private async Task<bool> DownloadAndVerifyMissionItemsAsync()
    {
        lock (_sync)
        {
            _verificationDownloadQueue.Clear();
            _expectedVerificationCount = 0;
            _missionDownloadTcs = new TaskCompletionSource<bool>();
        }

        // Send a request demanding the current full route list layout back from the flight controller
        var reqList = new MAVLink.mavlink_mission_request_list_t
        {
            target_system = target_sysid,
            target_component = (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_AUTOPILOT1,
            mission_type = (byte)MAVLink.MAV_MISSION_TYPE.MISSION
        };
        SendPacket(MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST_LIST, reqList);

        // Increased timeout variables to sustain wireless latency spikes and RF jitter
        int timeoutSec = IsWireless ? 20 : 8;
        try
        {
            // Wait for the background packet processor thread to parse all incoming MISSION_ITEM entries
            bool downloadComplete = await _missionDownloadTcs.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSec));
            if (!downloadComplete) return false;

            // Deep Data Array Verification Check Pass
            return RunDeepValidationCheck();
        }
        catch (TimeoutException)
        {
            Toast("EEPROM validation pass timed out. Verification failed.", ToastLevel.Error, "⏳");
            return false;
        }
        finally
        {
            lock (_sync) { _missionDownloadTcs = null; }
        }
    }

    private bool RunDeepValidationCheck()
    {
        // Coordinate geometry tolerance offsets to accommodate single-precision float variations
        const double CoordEpsilon = 0.000001; 
        const double AltEpsilon = 0.1;

        lock (_sync)
        {
            if (_verificationDownloadQueue.Count != _uploadQueue.Count)
            {
                Toast($"Verification Fail: Drone reported {_verificationDownloadQueue.Count} points instead of {_uploadQueue.Count}!", ToastLevel.Error, "🚨");
                return false;
            }

            for (int i = 1; i < _uploadQueue.Count; i++)
            {
                var local = _uploadQueue[i];
                var remote = _verificationDownloadQueue[i];

                if (Math.Abs(local.Lat - remote.Lat) > CoordEpsilon ||
                    Math.Abs(local.Lng - remote.Lng) > CoordEpsilon ||
                    Math.Abs(local.Alt - remote.Alt) > AltEpsilon)
                {
                    _logger.LogError("[MAVLink Fail] Index #{Idx} Mismatch! GCS: {gLat},{gLng} vs Drone: {dLat},{dLng}", i, local.Lat, local.Lng, remote.Lat, remote.Lng);
                    Toast($"CRITICAL: Corrupted coordinates detected at sequence point #{i}!", ToastLevel.Error, "🚨");
                    return false;
                }
            }
        }

        Toast($"Handshake Complete. All {_uploadQueue.Count} waypoints verified inside Autopilot EEPROM!", ToastLevel.Success, "🛡️");
        return true;
    }

    private void HandleMissionRequest(ushort seq, bool isInt, byte srcSysid, byte srcCompid)
    {
        if (seq < _uploadQueue.Count)
        {
            var wp = _uploadQueue[seq];
            if (isInt)
            {
                var item = new MAVLink.mavlink_mission_item_int_t
                {
                    seq = seq,
                    target_system = srcSysid,
                    target_component = srcCompid,
                    command = (ushort)TranslateCommand(wp.Command),
                    frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT_INT,
                    x = (int)(wp.Lat * 10000000),
                    y = (int)(wp.Lng * 10000000),
                    z = (float)wp.Alt,
                    autocontinue = 1,
                    // ArduPilot mission protocol:
                    //   seq=0 is the home position  → current=0
                    //   seq=1 is the first waypoint → current=1 (tells FC where to start)
                    //   seq>1 all other items        → current=0
                    current = (byte)(seq == 1 ? 1 : 0),
                    mission_type = (byte)MAVLink.MAV_MISSION_TYPE.MISSION
                };
                SendPacket(MAVLink.MAVLINK_MSG_ID.MISSION_ITEM_INT, item);
            }
            else
            {
                var item = new MAVLink.mavlink_mission_item_t
                {
                    seq = seq,
                    target_system = srcSysid,
                    target_component = srcCompid,
                    command = (ushort)TranslateCommand(wp.Command),
                    frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT_INT,
                    x = (float)wp.Lat,
                    y = (float)wp.Lng,
                    z = (float)wp.Alt,
                    autocontinue = 1,
                    current = (byte)(seq == 1 ? 1 : 0),
                    mission_type = (byte)MAVLink.MAV_MISSION_TYPE.MISSION
                };
                SendPacket(MAVLink.MAVLINK_MSG_ID.MISSION_ITEM, item);
            }
        }
    }

    private void RequestWaypointIndexItem(uint sequentialIndex)
    {
        // Explicitly targeted to the primary Autopilot system component registry engine
        var req = new MAVLink.mavlink_request_data_stream_t(); // Fallback instance safety initialization check passed
        var missionReq = new MAVLink.mavlink_mission_request_t
        {
            target_system = target_sysid,
            target_component = (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_AUTOPILOT1,
            seq = (ushort)sequentialIndex,
            mission_type = (byte)MAVLink.MAV_MISSION_TYPE.MISSION
        };
        
        SendPacket(MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST, missionReq);
    }

    private MAVLink.MAV_CMD TranslateCommand(string cmd) => cmd.ToUpper() switch
    {
        "TAKEOFF" => MAVLink.MAV_CMD.TAKEOFF,
        "LAND" => MAVLink.MAV_CMD.LAND,
        "RTL" => MAVLink.MAV_CMD.RETURN_TO_LAUNCH,
        "LOITER" => MAVLink.MAV_CMD.LOITER_UNLIM,
        _ => MAVLink.MAV_CMD.WAYPOINT
    };
    public void SendReposition(double lat, double lon, float alt) => SendCommand(MAVLink.MAV_CMD.DO_REPOSITION, -1, 1, 0, 0, (float)lat, (float)lon, alt);
    public async Task DownloadFile(IJSRuntime js, string fileName, string content) => await js.InvokeVoidAsync("downloadFile", fileName, content);


    // ── SWARM BROADCAST METHODS ──────────────────────────────────────────────
    public void BroadcastCommand(MAVLink.MAV_CMD command, float p1 = 0, float p2 = 0, float p3 = 0, float p4 = 0, float p5 = 0, float p6 = 0, float p7 = 0)
    {
        var cmd = new MAVLink.mavlink_command_long_t
        {
            command = (ushort)command,
            param1 = p1,
            param2 = p2,
            param3 = p3,
            param4 = p4,
            param5 = p5,
            param6 = p6,
            param7 = p7,
            target_system = 0, // 0 = BROADCAST TO ALL DRONES
            target_component = 0,
            confirmation = 0
        };
        SendPacket(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, cmd);
        _logger.LogDebug("[SWARM] Broadcast CMD Sent: {Command}", command);
    }

    public void BroadcastArmRequest(bool arm)
    {
        var req = new MAVLink.mavlink_command_long_t
        {
            target_system = 0, // BROADCAST
            target_component = 0,
            command = (ushort)MAVLink.MAV_CMD.COMPONENT_ARM_DISARM,
            param1 = arm ? 1 : 0,
            param2 = 0
        };
        SendPacket(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, req);
    }

    public void BroadcastFlightMode(string modeName)
    {
        uint modeId = modeName.ToUpper() switch { "STABILIZE" => 0, "ALT_HOLD" => 2, "AUTO" => 3, "GUIDED" => 4, "LOITER" => 5, "RTL" => 6, "LAND" => 9, _ => 0 };
        var req = new MAVLink.mavlink_command_long_t
        {
            target_system = 0, // BROADCAST
            target_component = 0,
            command = (ushort)MAVLink.MAV_CMD.DO_SET_MODE,
            param1 = 1,
            param2 = modeId
        };
        SendPacket(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, req);
    }

    public void ArmNode(byte sysId, bool arm, bool force = false)
    {
        var req = new MAVLink.mavlink_command_long_t
        {
            target_system = sysId,
            target_component = 0, // 0 = broadcast to all components on that system
            command = (ushort)MAVLink.MAV_CMD.COMPONENT_ARM_DISARM,
            param1 = arm ? 1 : 0,
            param2 = (!arm && force) ? 21196 : 0
        };
        SendPacket(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, req);
    }

    public void SetFlightModeForNode(byte sysId, string modeName)
    {
        uint modeId = modeName.ToUpper() switch { "STABILIZE" => 0, "ALT_HOLD" => 2, "AUTO" => 3, "GUIDED" => 4, "LOITER" => 5, "RTL" => 6, "LAND" => 9, _ => 0 };
        var req = new MAVLink.mavlink_command_long_t
        {
            target_system = sysId,
            target_component = 0,
            command = (ushort)MAVLink.MAV_CMD.DO_SET_MODE,
            param1 = 1,
            param2 = modeId
        };
        SendPacket(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, req);
    }

    public void SendCommandToNode(byte sysId, MAVLink.MAV_CMD command, float p1 = 0, float p2 = 0, float p3 = 0, float p4 = 0, float p5 = 0, float p6 = 0, float p7 = 0)
    {
        var cmd = new MAVLink.mavlink_command_long_t
        {
            command = (ushort)command,
            param1 = p1,
            param2 = p2,
            param3 = p3,
            param4 = p4,
            param5 = p5,
            param6 = p6,
            param7 = p7,
            target_system = sysId,
            target_component = 0,
            confirmation = 0
        };
        SendPacket(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, cmd);
        _logger.LogDebug("[SWARM] CMD Sent to SYS {SysId}: {Command}", sysId, command);
    }

    public void RemoveSwarmNode(byte sysId)
    {
        ActiveSwarm.TryRemove(sysId, out _);
    }

    // ---------------- CLEANUP ----------------

    private int _linkHealthFailCount = 0;
    private DateTime _lastSensorHealthCheck = DateTime.MinValue;
    private readonly System.Collections.Generic.HashSet<string> _sensorAlerted = new();
    private int _lastGpsFix = -1;   // -1=unknown, track transitions
    private float _lastBattPct = -1;   // track low-battery threshold crossing
    private bool _sentLowBattToast = false;
    private bool _sentCritBattToast = false;
    private string _lastFlightMode = "";   // track mode changes

    private void CheckLinkHealth()
    {
        var now = DateTime.UtcNow;

        // ── RC transmitter timeout ──────────────────────────────────────────
        // Runs unconditionally — independent of drone connection state.
        // If drone disconnects while RC was connected, this still fires and
        // correctly flips IsRcConnected to false without waiting for the
        // _shouldBeConnected / _gotFirstHeartbeatThisSession guard below.
        if (_rcWasConnected && _lastRcPacketUtc != DateTime.MinValue &&
            (now - _lastRcPacketUtc) > RcTimeoutSpan)
        {
            _rcWasConnected = false;
            UpdateState(s => s.IsRcConnected = false);
            NotifyUi(force: true);
            Toast("RC signal lost", ToastLevel.Error, "🎮");
        }

        if (_shouldBeConnected && _gotFirstHeartbeatThisSession)
        {
            // ── Heartbeat / telemetry link ─────────────────────────────────
            if ((now - State.LastHeartbeat).TotalSeconds > HeartbeatTimeoutSeconds)
            {
                _linkHealthFailCount++;
                if (_linkHealthFailCount >= 3)
                {
                    // Confirmed loss — flip the stable cached flag
                    if (IsLinkHealthy)
                    {
                        IsLinkHealthy = false;
                        NotifyUi(force: true);
                    }
                    if (!_wasAlerting)
                    {
                        _wasAlerting = true;
                        _connDialogOpen = true;
                        ReportConn("LINK LOST — CHECK TELEMETRY RADIO");
                        ShowConnDialog(true);
                        Toast("Telemetry link lost — check radio", ToastLevel.Error, "📡");
                    }
                }
            }
            else
            {
                _linkHealthFailCount = 0;
                // Heartbeat arrived — mark link as healthy
                if (!IsLinkHealthy)
                {
                    IsLinkHealthy = true;
                    NotifyUi(force: true);
                }
                if (_wasAlerting)
                {
                    _wasAlerting = false;
                    Toast("Telemetry link recovered", ToastLevel.Success, "📡");
                    if (_connDialogOpen) { _connDialogOpen = false; ShowConnDialog(false); }
                }
            }

            // ── GPS fix quality ─────────────────────────────────────────────
            int fixNow = State.GpsFixType;
            if (_lastGpsFix != -1 && fixNow != _lastGpsFix)
            {
                if (fixNow >= 3 && _lastGpsFix < 3)
                    Toast($"GPS 3D fix acquired ({State.SatCount} sats)", ToastLevel.Success, "🛰️");
                else if (fixNow < 3 && _lastGpsFix >= 3)
                    Toast($"GPS fix lost (fix type {fixNow})", ToastLevel.Warning, "🛰️");
                else if (fixNow == 0 && _lastGpsFix > 0)
                    Toast("GPS signal lost entirely", ToastLevel.Error, "🛰️");
            }
            _lastGpsFix = fixNow;

            // ── Battery thresholds ──────────────────────────────────────────
            float batt = State.BatteryPercent;
            if (batt > 0 && _lastBattPct > 0)
            {
                if (!_sentLowBattToast && batt <= 25 && batt > 10)
                {
                    _sentLowBattToast = true;
                    Toast($"Low battery — {batt:0}% remaining", ToastLevel.Warning, "🪫");
                }
                if (!_sentCritBattToast && batt <= 10)
                {
                    _sentCritBattToast = true;
                    Toast($"CRITICAL battery — {batt:0}% — land immediately!", ToastLevel.Error, "🔋");
                }
                // Reset flags if battery is recharged (bench use)
                if (batt > 30) { _sentLowBattToast = false; _sentCritBattToast = false; }
            }
            if (batt > 0) _lastBattPct = batt;

            // ── Flight mode change notification ────────────────────────────
            string modeNow = State.FlightMode;
            if (!string.IsNullOrEmpty(_lastFlightMode) && modeNow != _lastFlightMode
                && modeNow != "Unknown")
            {
                ToastLevel lvl = modeNow is "RTL" or "LAND" ? ToastLevel.Warning :
                                 modeNow is "AUTO" or "GUIDED" ? ToastLevel.Info :
                                 ToastLevel.Info;
                Toast($"Flight mode → {modeNow}", lvl, "✈️");
            }
            if (modeNow != "Unknown") _lastFlightMode = modeNow;
        }
    }

    private string GetFlightModeString(uint customMode)
    {
        // VehicleProfileService owns the mode tables for both profiles.
        // This method now correctly returns Rover or Copter mode names
        // based on the currently active vehicle context.
        return _vehicleProfile.GetFlightModeString(customMode);
    }

    private void CloseConnections()
    {
        try
        {
            if (_serialPort?.IsOpen == true)
            {
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();
                _serialPort.Close();
            }
            _serialPort?.Dispose();
        }
        catch { }
        finally { _serialPort = null; }

        try
        {
            _udpClient?.Close();
            _udpClient?.Dispose();
        }
        catch { }
        finally { _udpClient = null; _droneEndpoints.Clear(); }

        try
        {
            _tcpStream?.Close();
            _tcpStream?.Dispose();
            _tcpClient?.Close();
            _tcpClient?.Dispose();
        }
        catch { }
        finally { _tcpStream = null; _tcpClient = null; }
    }



    public override void Dispose()
    {
        CloseConnections();
        base.Dispose();
    }
}