using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using System.IO.Ports;
using System.Management;
using System.Text;
using static MAVLink;

public enum ConnectionType { Serial, UDP, TCP }

public class MavlinkService : BackgroundService
{
    private readonly ILogger<MavlinkService> _logger;
    private readonly object _sync = new();
    private readonly MAVLink.MavlinkParse _parser = new();

    // UI Notification Throttle (10Hz is standard for GCS stability)
    private readonly TimeSpan _uiNotifyMinPeriod = TimeSpan.FromMilliseconds(100);
    private DateTime _lastUiNotifyUtc = DateTime.MinValue;

    private SerialPort? _serialPort;
    private ConnectionType _connectionType = ConnectionType.Serial;
    private string _portName = "COM7";
    private int _baudRate = 115200;
    private bool _shouldBeConnected = false;

    public event Action<string>? OnConnectionStatus;
    public event Action<bool>? OnConnectionDialog;
    public event Action<string>? OnMessageReceived;
    public event Action? OnTelemetryUpdated;

    private bool _connDialogOpen = false;
    private bool _gotFirstHeartbeatThisSession = false;

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

    private Dictionary<byte, int> _magProgressTracker = new();
    private DateTime _accelCalStartedUtc = DateTime.MinValue;

    private Dictionary<byte, MAVLink.MAG_CAL_STATUS> _magReportTracker = new();
    public bool IsRcCalibrating { get; private set; } = false;
    public ushort[] RcMin { get; private set; } = new ushort[8];
    public ushort[] RcMax { get; private set; } = new ushort[8];

    public byte target_sysid { get; set; } = 1;
    public byte target_compid { get; set; } = 1;

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
    private List<WaypointModel> _uploadQueue = new();

    public class PortProfile
    {
        public string PortId { get; set; } = "";
        public string DisplayName { get; set; } = "";
    }

    public MavlinkService(ILogger<MavlinkService> logger)
    {
        _logger = logger;
    }

    public bool IsConnected
    {
        get
        {
            if (_serialPort?.IsOpen != true) return false;
            var hb = State.LastHeartbeat;
            if (hb == DateTime.MinValue) return false;
            return (DateTime.UtcNow - hb).TotalSeconds < 3;
        }
    }

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

    public void Connect(ConnectionType type, string portName, int speed)
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

        CloseSerialPort();

        _connDialogOpen = true;
        ShowConnDialog(true);
        ReportConn($"Opening {_portName} @ {_baudRate} ...");

        UpdateState(s =>
        {
            s.ConnectionStatus = $"Connecting to {_portName}...";
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
        if (IsConnected)
        {
            SendParameter("NTF_LED_BRIGHT", 3.0f);
            Console.WriteLine("[SYSTEM] GCS Disconnecting. Re-enabling Pixhawk RGB LEDs.");
        }

        lock (_sync) { _shouldBeConnected = false; }
        CloseSerialPort();

        WipeDataToBlankSlate();

        if (_connDialogOpen)
        {
            _connDialogOpen = false;
            ShowConnDialog(false);
        }

        NotifyUi(force: true);
    }

    // ---------------- BACKGROUND LOOP ----------------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool shouldConnect;
            lock (_sync) { shouldConnect = _shouldBeConnected; }
            CheckLinkHealth();

            if (!shouldConnect)
            {
                CloseSerialPort();
                await Task.Delay(250, stoppingToken);
                continue;
            }

            if (_serialPort == null || !_serialPort.IsOpen)
            {
                if (!TryOpenSerialPort(_portName, _baudRate))
                {
                    ReportConn("Waiting for device..");
                    await Task.Delay(3000, stoppingToken);
                    continue;
                }
                TryConfigureMessageIntervals();
                ReportConn($"Port {_portName} Opened. Listening for Heartbeat...");
            }

            try
            {
                var port = _serialPort;
                if (port != null && port.IsOpen)
                {
                    // Drain ALL queued packets per tick.
                    // Old code: 1 packet per 15ms tick = RC packet buried under burst of
                    // heartbeat+attitude+GPS+status and arrives 60-90ms late visually.
                    int drained = 0;
                    while (port.BytesToRead > 0 && drained < 64)
                    {
                        var packet = _parser.ReadPacket(port.BaseStream);
                        if (packet != null) HandlePacket(packet);
                        drained++;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Serial Port Exception. Restarting link...");
                ReportConn("DATA LINK INTERRUPTED. RECONNECTING...");
                CloseSerialPort();
                WipeDataToBlankSlate();
                NotifyUi(force: true);
            }

            await Task.Delay(5, stoppingToken);
        }
    }

    private bool TryOpenSerialPort(string portName, int speed)
    {
        try
        {
            CloseSerialPort();
            _serialPort = new SerialPort(portName, speed, Parity.None, 8, StopBits.One)
            {
                ReadTimeout = 500,
                WriteTimeout = 500,
                DtrEnable = true,
                RtsEnable = true
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
            CloseSerialPort();
            NotifyUi(force: true);
            return false;
        }
    }

    private void WipeDataToBlankSlate()
    {
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

            UpdateState(s =>
            {
                s.LastHeartbeat = now;
                s.ConnectionStatus = "Receiving Data";

                if (!s.HasVehicleId)
                {
                    s.SystemId = packet.sysid;
                    s.ComponentId = packet.compid;
                    s.HasVehicleId = true;
                    TryConfigureMessageIntervals();
                }

                if (packet.compid == (byte)MAVLink.MAV_COMPONENT.MAV_COMP_ID_AUTOPILOT1)
                {
                    s.CustomMode = hb.custom_mode;
                    s.FlightMode = GetFlightModeString(hb.custom_mode);
                }

                s.IsArmed = (((MAVLink.MAV_MODE_FLAG)hb.base_mode) & MAVLink.MAV_MODE_FLAG.SAFETY_ARMED) != 0;
            });

            if (!_gotFirstHeartbeatThisSession)
            {
                _gotFirstHeartbeatThisSession = true;
                ReportConn($"Heartbeat received (Sys:{packet.sysid} Comp:{packet.compid}) ✅ Connected");

                if (_connDialogOpen)
                {
                    _connDialogOpen = false;
                    ShowConnDialog(false);
                }

                // Auto-disable Pixhawk LEDs on bench arrival
                SendParameter("NTF_LED_BRIGHT", 0.0f);
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
            UpdateState(s =>
            {
                if (sys.voltage_battery > 0) s.Voltage = sys.voltage_battery / 1000.0;
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
                if (!upper.Contains("PREARM"))
                {
                    if (ActiveCalType == "ACCEL" && (upper.Contains("ACCEL") || upper.Contains("CANCELLED")))
                    {
                        LiveAccelStatus = "FAILED"; LiveAccelProgress = 0; ActiveCalType = "NONE";
                    }
                    else if (ActiveCalType == "GYRO" && (upper.Contains("GYRO") || upper.Contains("CANCELLED")))
                    {
                        LiveGyroStatus = "FAILED"; LiveGyroProgress = 0; ActiveCalType = "NONE";
                    }
                    else if (ActiveCalType == "MAG" && upper.Contains("CANCELLED"))
                    {
                        // Only explicit cancel — report handler owns all other outcomes
                        LiveMagStatus = "CANCELLED"; LiveMagProgress = 0; ActiveCalType = "NONE";
                    }
                    // ActiveCalType="NONE" means MAG_CAL_REPORT already handled it — do NOT touch LiveMagStatus
                }
                NotifyUi(force: true);
            }
            // ── SUCCESS / FAIL STATUSTEXT ──────────────────────────────────────────────
            else if ((upper.Contains("SUCCESS") || upper.Contains("CALIBRATED") || upper.Contains("DONE")) && !upper.Contains("PREARM") && !upper.Contains("NOT"))
            {
                if (ActiveCalType == "GYRO" || upper.Contains("GYRO"))
                {
                    LiveGyroStatus = "SUCCESS"; LiveGyroProgress = 100; ActiveCalType = "NONE";
                    NotifyUi(force: true);
                }
                else if (ActiveCalType == "ACCEL")
                {
                    // Guard: 5s gate + progress >= 83 blocks stale boot STATUSTEXTs from faking success
                    if ((DateTime.UtcNow - _accelCalStartedUtc).TotalSeconds > 5.0 && LiveAccelProgress >= 83)
                    {
                        LiveAccelStatus = "SUCCESS"; LiveAccelProgress = 100; ActiveCalType = "NONE";
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
        // Catch BOTH older RAW (35) and modern RC_CHANNELS (65) packets
        // Catch BOTH older RAW (35) and modern RC_CHANNELS (65) packets
        // YOUR FIX: Listen ONLY for the modern RC_CHANNELS packet (ID 65)
        // Around line 562 — move OnRcChannelsUpdated OUTSIDE the IsRcCalibrating check:
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

            // Always fire — throttling is handled in the component
            OnRcChannelsUpdated?.Invoke(latestChannels);
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST || packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST_INT)
        {
            var req = packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST
                ? new MAVLink.mavlink_mission_request_int_t { seq = ((MAVLink.mavlink_mission_request_t)packet.data).seq }
                : (MAVLink.mavlink_mission_request_int_t)packet.data;

            HandleMissionRequest(req);
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MISSION_ACK)
        {
            var ack = (MAVLink.mavlink_mission_ack_t)packet.data;
            string status = ack.type == 0 ? "Mission Upload Success" : $"Mission Error: {ack.type}";
            string logEntry = $"[{DateTime.Now:HH:mm:ss}] {status}";

            lock (StatusMessages) StatusMessages.Insert(0, logEntry);
            OnMessageReceived?.Invoke(logEntry);
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
                    // 42425 = MAV_CMD_DO_ACCEPT_MAG_CAL — persist offsets to EEPROM
                    SendCommand((MAVLink.MAV_CMD)42425, 0, 0, 0, 0, 0, 0, 0);
                    break;
                case (byte)MAVLink.MAG_CAL_STATUS.MAG_CAL_FAILED:
                    if (LiveMagStatus != "SUCCESS")
                    { LiveMagStatus = "FAILED — MOVE DRONE MORE"; LiveMagProgress = 0; ActiveCalType = "NONE"; }
                    SetMessageInterval(191, 0); SetMessageInterval(192, 0);
                    break;
                case 6: // MAG_CAL_BAD_ORIENTATION — reached 100% but mount orientation wrong
                    LiveMagStatus = "FAILED — BAD ORIENTATION"; LiveMagProgress = 0; ActiveCalType = "NONE";
                    SetMessageInterval(191, 0); SetMessageInterval(192, 0);
                    break;
                case 7: // MAG_CAL_BAD_RADIUS — reached 100% but magnetic interference
                    LiveMagStatus = "FAILED — MAGNETIC INTERFERENCE"; LiveMagProgress = 0; ActiveCalType = "NONE";
                    SetMessageInterval(191, 0); SetMessageInterval(192, 0);
                    break;
            }
            NotifyUi(force: true);
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.PARAM_VALUE)
        {
            var param = (MAVLink.mavlink_param_value_t)packet.data;
            string paramName = System.Text.Encoding.ASCII.GetString(param.param_id).Trim('\0', ' ');

            if (paramName == "FRAME_CLASS")
            {
                State.MotorCount = (int)param.param_value switch { 1 => 4, 2 => 6, 3 => 8, 5 => 6, 7 => 3, _ => 4 };
                NotifyUi(force: true);
            }
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
        var s = _state;
        if (s.HasVehicleId) { sysId = s.SystemId; compId = s.ComponentId; return true; }
        sysId = 1; compId = 1; return false;
    }

    private void SendBuffer(byte[] buffer)
    {
        var port = _serialPort;
        if (port == null || !port.IsOpen) return;
        try { port.Write(buffer, 0, buffer.Length); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error sending MAVLink packet"); }
    }

    private void SendPacket(MAVLink.MAVLINK_MSG_ID msgid, object data)
    {
        var buffer = _parser.GenerateMAVLinkPacket20(msgid, data);
        SendBuffer(buffer);
    }

    private void TryConfigureMessageIntervals()
    {
        try
        {
            // 1. Modern MAVLink 2.0 Commands
            SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.HEARTBEAT, 1);
            SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.ATTITUDE, 20);
            SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT, 10);
            SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.GPS_RAW_INT, 1);
            SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.SYS_STATUS, 1);
            SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.VFR_HUD, 5);
            SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.STATUSTEXT, 10);
            // RC at 50Hz — 20ms between packets makes calibration bars smooth
            SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.RC_CHANNELS, 50);
            SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.RC_CHANNELS_RAW, 50);

            // 2. LEGACY FALLBACK: covers older ArduPilot that ignores SET_MESSAGE_INTERVAL
            TryGetTarget(out var sysId, out var compId);
            var reqRc = new MAVLink.mavlink_request_data_stream_t
            {
                target_system = sysId,
                target_component = compId,
                req_stream_id = 3,   // MAV_DATA_STREAM_RC_CHANNELS
                req_message_rate = 50,
                start_stop = 1
            };
            SendPacket(MAVLink.MAVLINK_MSG_ID.REQUEST_DATA_STREAM, reqRc);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to configure message intervals"); }
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
        if (_serialPort == null || !_serialPort.IsOpen) return;
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

        var packet = _parser.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, cmd);
        lock (_sendLock) { _serialPort.Write(packet, 0, packet.Length); }
        Console.WriteLine($"CMD Sent: {command}");
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
        float modeValue = modeName.ToUpper() switch { "STABILIZE" => 0, "ALT_HOLD" => 2, "LOITER" => 5, "RTL" => 6, "AUTO" => 10, _ => 0 };
        SendParameter($"FLTMODE{position}", modeValue);
    }

    public void SetEscCalibrationMode() => SendParameter("ESC_CALIBRATION", 3);
    public void SetFrameConfig(int frameType, int frameClass) { SendParameter("FRAME_TYPE", frameType); SendParameter("FRAME_CLASS", frameClass); }
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

    public async Task<bool> UploadMissionAsync(dynamic items)
    {
        try
        {
            _uploadQueue = items;
            var count = new MAVLink.mavlink_mission_count_t { target_system = target_sysid, target_component = target_compid, count = (ushort)items.Count, mission_type = (byte)MAVLink.MAV_MISSION_TYPE.MISSION };
            SendPacket(MAVLink.MAVLINK_MSG_ID.MISSION_COUNT, count);
            return true;
        }
        catch { return false; }
    }

    private void HandleMissionRequest(MAVLink.mavlink_mission_request_int_t req)
    {
        if (req.seq < _uploadQueue.Count)
        {
            var wp = _uploadQueue[req.seq];
            var item = new MAVLink.mavlink_mission_item_int_t
            {
                seq = (ushort)req.seq,
                target_system = target_sysid,
                target_component = target_compid,
                command = (ushort)TranslateCommand(wp.Command),
                frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT_INT,
                x = (int)(wp.Lat * 10000000),
                y = (int)(wp.Lng * 10000000),
                z = (float)wp.Alt,
                autocontinue = 1,
                current = (byte)(req.seq == 0 ? 1 : 0),
                mission_type = (byte)MAVLink.MAV_MISSION_TYPE.MISSION
            };
            SendPacket(MAVLink.MAVLINK_MSG_ID.MISSION_ITEM_INT, item);
        }
    }

    private MAVLink.MAV_CMD TranslateCommand(string cmd) => cmd.ToUpper() switch { "TAKEOFF" => MAVLink.MAV_CMD.TAKEOFF, "LAND" => MAVLink.MAV_CMD.LAND, "RTL" => MAVLink.MAV_CMD.RETURN_TO_LAUNCH, _ => MAVLink.MAV_CMD.WAYPOINT };
    public void SendReposition(double lat, double lon, float alt) => SendCommand(MAVLink.MAV_CMD.DO_REPOSITION, -1, 1, 0, 0, (float)lat, (float)lon, alt);
    public async Task DownloadFile(IJSRuntime js, string fileName, string content) => await js.InvokeVoidAsync("downloadFile", fileName, content);

    // ---------------- CLEANUP ----------------

    private void CheckLinkHealth()
    {
        if (_shouldBeConnected && _gotFirstHeartbeatThisSession)
        {
            if ((DateTime.UtcNow - State.LastHeartbeat).TotalSeconds > 3.0)
            {
                if (!_wasAlerting) { _wasAlerting = true; ReportConn("CRITICAL: LINK LOST"); ShowConnDialog(true); }
            }
            else
            {
                if (_wasAlerting) { _wasAlerting = false; ShowConnDialog(false); }
            }
        }
    }

    private static string GetFlightModeString(uint customMode) => customMode switch { 0 => "STABILIZE", 2 => "ALT_HOLD", 3 => "AUTO", 4 => "GUIDED", 5 => "LOITER", 6 => "RTL", 9 => "LAND", _ => "Unknown" };

    private void CloseSerialPort()
    {
        try { if (_serialPort?.IsOpen == true) { _serialPort.DiscardInBuffer(); _serialPort.DiscardOutBuffer(); _serialPort.Close(); } _serialPort?.Dispose(); }
        catch { /* ignore close errors */ }
        finally { _serialPort = null; }
    }

    public override void Dispose()
    {
        CloseSerialPort();
        base.Dispose();
    }
}