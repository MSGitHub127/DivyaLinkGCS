using BlazorApp3.Components.GCS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using System.IO.Ports;
using static MAVLink;

public enum ConnectionType { Serial, UDP, TCP }

public class MavlinkService : BackgroundService
{
    private readonly ILogger<MavlinkService> _logger;

    private readonly object _sync = new();
    private readonly MAVLink.MavlinkParse _parser = new();
    // Change from 50ms to 100ms (10Hz is standard for GCS stability)
    private readonly TimeSpan _uiNotifyMinPeriod = TimeSpan.FromMilliseconds(100);

    private SerialPort? _serialPort;

    private ConnectionType _connectionType = ConnectionType.Serial;
    private string _portName = "COM7";
    private int _baudRate = 115200;

    private bool _shouldBeConnected = false;

    // --- Connection dialog events (USED BY ConnectionDialog.razor) ---
    public event Action<string>? OnConnectionStatus;
    public event Action<bool>? OnConnectionDialog;

    private bool _connDialogOpen = false;
    private bool _gotFirstHeartbeatThisSession = false;

    // 1. Add a list to store messages
    public List<string> StatusMessages { get; private set; } = new();
    // 2. Add an event to notify UI when a new message arrives
    public event Action? OnMessageReceived;

    private void ReportConn(string msg) => OnConnectionStatus?.Invoke(msg);
    private void ShowConnDialog(bool show) => OnConnectionDialog?.Invoke(show);

    // --- CALIBRATION TRACKING ---
    public bool IsRcCalibrating { get; private set; } = false;
    public ushort[] RcMin { get; private set; } = new ushort[8];
    public ushort[] RcMax { get; private set; } = new ushort[8];
    public byte TargetSysId { get; set; } = 1;
    public byte TargetCompId { get; set; } = 1;
    // --- UI telemetry push ---
    public event Action? OnTelemetryUpdated;

    // UI notifications throttled (20 Hz)
    private DateTime _lastUiNotifyUtc = DateTime.MinValue;
    //private readonly TimeSpan _uiNotifyMinPeriod = TimeSpan.FromMilliseconds(50);

    // Packet rate
    private int _rxPacketCounter = 0;
    private DateTime _rxCounterWindowStartUtc = DateTime.UtcNow;
    private readonly object _sendLock = new object();

    public DateTime LastPacketTime { get; private set; } = DateTime.MinValue;
    // Battery anti-flicker
    private int? _lastGoodBatteryPercent = null;
    private DateTime _lastGoodBatteryUtc = DateTime.MinValue;
    private bool _wasAlerting = false;

    // Snapshot-based state (UI reads this)
    private DroneState _state = new();
    public byte target_sysid { get; set; } = 1;  // The drone's system ID (usually 1)
    public byte target_compid { get; set; } = 1;
    public DroneState State => _state;

    // Inside MavlinkService.cs
    public async Task DownloadFile(IJSRuntime js, string fileName, string content)
    {
        await js.InvokeVoidAsync("downloadFile", fileName, content);
    }
    private void SendPacket(object msg)
    {
        // Right now, this is a placeholder. 
        // Later, we will put your SerialPort writing logic here.
        Console.WriteLine($"Ready to send: {msg.GetType().Name}");
    }
    private void SendPacket(MAVLink.MAVLINK_MSG_ID msgid, object data)
    {
        var buffer = _parser.GenerateMAVLinkPacket20(msgid, data);
        SendBuffer(buffer);
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

    public MavlinkService(ILogger<MavlinkService> logger)
    {
        _logger = logger;
    }

    public string[] GetAvailablePorts() => SerialPort.GetPortNames();

    public string[] GetSafeStatusMessages()
    {
        lock (StatusMessages)
        {
            return StatusMessages.ToArray();
        }
    }

    public void ReportStatus(string msg)
    {
        // This allows MainLayout to push messages like "Connecting (25s)..."
        ReportConn(msg);
    }

    // ---------------- CONNECT / DISCONNECT ----------------

    public void Connect(ConnectionType type, string portName, int speed)
    {
        // Stop any previous session
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

        // OPEN DIALOG
        _connDialogOpen = true;
        ShowConnDialog(true);
        ReportConn("Starting connection...");
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
        lock (_sync) { _shouldBeConnected = false; }

        CloseSerialPort();

        UpdateState(s =>
        {
            s.ConnectionStatus = "Disconnected";
            s.LastHeartbeat = DateTime.MinValue;

            // clean reset (you asked previously, but you later said “leave it”,
            // so I’m NOT zeroing everything here. If you want full reset again, tell me.)
            s.HasVehicleId = false;
            s.SystemId = 0;
            s.ComponentId = 0;
        });

        // CLOSE DIALOG if open
        if (_connDialogOpen)
        {
            _connDialogOpen = false;
            ShowConnDialog(false);
        }

        NotifyUi(force: true);
    }

    // ---------------- BACKGROUND LOOP ----------------
    // --- FLIGHT MODE CONFIGURATION ---
    public void SaveFlightMode(int position, string modeName)
    {
        // ArduPilot uses specific integers for flight modes
        // 0:Stabilize, 2:AltHold, 5:Loiter, 6:RTL, 10:Auto
        float modeValue = modeName.ToUpper() switch
        {
            "STABILIZE" => 0,
            "ALT_HOLD" => 2,
            "LOITER" => 5,
            "RTL" => 6,
            "AUTO" => 10,
            _ => 0
        };

        // ArduPilot parameters for modes are FLTMODE1 through FLTMODE6
        SendParameter($"FLTMODE{position}", modeValue);
    }

    // --- ESC CALIBRATION MODE ---
    public void SetEscCalibrationMode()
    {
        // Setting ESC_CALIBRATION to 3 tells the drone 
        // to enter ESC cal mode on the next boot.
        SendParameter("ESC_CALIBRATION", 3);
    }

    // --- MOTOR TESTING ---
    public void TestMotor(int motorIndex, bool start)
    {
        var cmd = new MAVLink.mavlink_command_long_t();
        cmd.target_system = target_sysid;
        cmd.target_component = target_compid;

        // MAV_CMD_DO_MOTOR_TEST = 183
        cmd.command = (ushort)MAVLink.MAV_CMD.DO_MOTOR_TEST;

        // Param 1: Motor instance (1, 2, 3, 4...)
        cmd.param1 = motorIndex;
        // Param 2: Test type (0 = percent throttle)
        cmd.param2 = 0;
        // Param 3: Throttle value (Start at 5% for safety, 0 to stop)
        cmd.param3 = start ? 5 : 0;
        // Param 4: Timeout (seconds)
        cmd.param4 = 2;

        SendPacket(cmd);
    }
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

            // Serial only for now
            // Inside ExecuteAsync method...

            if (_serialPort == null || !_serialPort.IsOpen)
            {
                // Try to open the port
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
                if (port != null && port.IsOpen && port.BytesToRead > 0)
                {
                    var packet = _parser.ReadPacket(port.BaseStream);
                    if (packet != null)
                        HandlePacket(packet);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Serial Port Exception. Restarting link...");
                ReportConn("DATA LINK INTERRUPTED. RECONNECTING...");
                CloseSerialPort();
            }

            await Task.Delay(15, stoppingToken);
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

            UpdateState(s =>
            {
                s.ConnectionStatus = $"Port open: {portName} @ {speed} (waiting heartbeat...)";
            });

            ReportConn("Port opened. Waiting for HEARTBEAT...");
            _logger.LogInformation("Port opened {Port} at {Baud}", portName, speed);

            NotifyUi(force: true);
            return true;
        }
        catch (Exception ex)
        {
            UpdateState(s => s.ConnectionStatus = $"Searching for {portName}...");
            ReportConn($"Failed to open port: {ex.Message}");
            _logger.LogWarning(ex, "Could not open serial port {Port} at {Baud}", portName, speed);
            CloseSerialPort();
            NotifyUi(force: true);
            return false;
        }
    }

    // ---------------- PACKET HANDLING ----------------

    private void HandlePacket(MAVLink.MAVLinkMessage packet)
    {
        var now = DateTime.UtcNow;

        // Packet stats
        _rxPacketCounter++;
        if ((now - _rxCounterWindowStartUtc).TotalSeconds >= 1.0)
        {
            var pps = _rxPacketCounter / (now - _rxCounterWindowStartUtc).TotalSeconds;
            _rxPacketCounter = 0;
            _rxCounterWindowStartUtc = now;

            UpdateState(s =>
            {
                s.RxPacketsPerSec = pps;
                s.LastPacketUtc = now;
            });
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

                var armed = (((MAVLink.MAV_MODE_FLAG)hb.base_mode) & MAVLink.MAV_MODE_FLAG.SAFETY_ARMED) != 0;
                s.IsArmed = armed;
            });

            // FIRST HEARTBEAT = truly connected => CLOSE dialog
            if (!_gotFirstHeartbeatThisSession)
            {
                _gotFirstHeartbeatThisSession = true;
                ReportConn($"Heartbeat received (Sys:{packet.sysid} Comp:{packet.compid}) ✅ Connected");

                if (_connDialogOpen)
                {
                    _connDialogOpen = false;
                    ShowConnDialog(false);
                }
            }
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.ATTITUDE)
        {
            var att = (MAVLink.mavlink_attitude_t)packet.data;

            float roll = att.roll * (180.0f / (float)Math.PI);
            float pitch = att.pitch * (180.0f / (float)Math.PI);
            float yawDeg = att.yaw * (180.0f / (float)Math.PI);
            yawDeg = Normalize360(yawDeg);

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

                    if (s.HeadingSource == "Unknown" || s.HeadingDeg == 0)
                        s.HeadingDeg = raw;
                    else
                        s.HeadingDeg = SmoothHeading(s.HeadingDeg, raw, alpha: 0.15f);

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
                if (sys.voltage_battery > 0)
                    s.Voltage = sys.voltage_battery / 1000.0;

                int pct = sys.battery_remaining;

                if (pct == 255 || pct < 0 || pct > 100)
                    return;

                if (pct >= 1)
                {
                    s.BatteryPercent = pct;
                    _lastGoodBatteryPercent = pct;
                    _lastGoodBatteryUtc = DateTime.UtcNow;
                    return;
                }

                // pct == 0 -> ignore if we recently had good value (prevents flicker)
                if (_lastGoodBatteryPercent.HasValue &&
                    (DateTime.UtcNow - _lastGoodBatteryUtc).TotalSeconds < 5)
                {
                    return;
                }

                s.BatteryPercent = 0;
                _lastGoodBatteryPercent = 0;
                _lastGoodBatteryUtc = DateTime.UtcNow;
            });
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.VFR_HUD)
        {
            var hud = (MAVLink.mavlink_vfr_hud_t)packet.data;
            float rawHeading = Normalize360(hud.heading);

            UpdateState(s =>
            {
                s.GroundSpeed = hud.groundspeed;
                s.AirSpeed = hud.airspeed;
                s.ClimbRate = hud.climb;

                if (!s.HasRelAlt)
                    s.Altitude = hud.alt;
            });
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.STATUSTEXT)
        {
            var status = (MAVLink.mavlink_statustext_t)packet.data;

            // 1. Get raw bytes
            byte[] textBytes = status.text;

            // 2. Convert to string and trim ALL null terminators and whitespace
            string text = System.Text.Encoding.ASCII.GetString(textBytes).Trim('\0', ' ');
            Console.WriteLine($"[MAVLINK DEBUG] Received Text: {text}");

            if (!string.IsNullOrEmpty(text))
            {
                string logEntry = $"[{DateTime.Now:HH:mm:ss}] {text}";

                lock (StatusMessages)
                {
                    StatusMessages.Insert(0, logEntry);
                    if (StatusMessages.Count > 100) StatusMessages.RemoveAt(StatusMessages.Count - 1);
                }

                // 3. CRITICAL: Ensure the UI event is triggered
                OnMessageReceived?.Invoke();
            }
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.RC_CHANNELS_RAW)
        {
            var rc = (MAVLink.mavlink_rc_channels_raw_t)packet.data;
            UpdateState(s => {
                s.RawChannels[0] = rc.chan1_raw;
                s.RawChannels[1] = rc.chan2_raw;
                s.RawChannels[2] = rc.chan3_raw;
                s.RawChannels[3] = rc.chan4_raw;
                s.RawChannels[4] = rc.chan5_raw;
                s.RawChannels[5] = rc.chan6_raw;
                s.RawChannels[6] = rc.chan7_raw;
                s.RawChannels[7] = rc.chan8_raw;
            });

            if(IsRcCalibrating)
            {
                for(int i = 0; i < 8; i++)
                {
                    if (State.RawChannels[i] < RcMin[i] && State.RawChannels[i] > 800) RcMin[i] = State.RawChannels[i];
                    if (State.RawChannels[i] > RcMax[i] && State.RawChannels[i] < 2200) RcMax[i] = State.RawChannels[i];
                }
            }
        }
        // Inside the HandlePacket method...
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST)
        {
            var req = (MAVLink.mavlink_mission_request_int_t)packet.data;
            // Map the old request type to your internal handler
            var reqInt = new MAVLink.mavlink_mission_request_int_t { seq = req.seq };
            HandleMissionRequest(reqInt);
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MISSION_REQUEST_INT)
        {
            var req = (MAVLink.mavlink_mission_request_int_t)packet.data;
            HandleMissionRequest(req);
        }
        else if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.MISSION_ACK)
        {
            var ack = (MAVLink.mavlink_mission_ack_t)packet.data;
            string status = ack.type == 0 ? "Mission Upload Success" : $"Mission Error: {ack.type}";

            // Log it so it shows up in your Message Tab
            lock (StatusMessages)
            {
                StatusMessages.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {status}");
            }
            OnMessageReceived?.Invoke();
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
        if (!force && (now - _lastUiNotifyUtc) < _uiNotifyMinPeriod)
            return;

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

    // ---------------- SENDING ----------------

    private bool TryGetTarget(out byte sysId, out byte compId)
    {
        var s = _state;
        if (s.HasVehicleId)
        {
            sysId = s.SystemId;
            compId = s.ComponentId;
            return true;
        }

        sysId = 1;
        compId = 1;
        return false;
    }

    private void SendBuffer(byte[] buffer)
    {
        var port = _serialPort;
        if (port == null || !port.IsOpen) return;

        try { port.Write(buffer, 0, buffer.Length); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error sending MAVLink packet"); }
    }

    private void TryConfigureMessageIntervals()
    {
        try
        {
            SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.HEARTBEAT, 1);
            SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.ATTITUDE, 20);
            SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT, 10);
            SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.GPS_RAW_INT, 1);
            SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.SYS_STATUS, 1);
            SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.VFR_HUD, 5);
            SetMessageInterval((uint)MAVLink.MAVLINK_MSG_ID.STATUSTEXT, 2);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to configure message intervals");
        }
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

        var buffer = _parser.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, cmd);
        SendBuffer(buffer);
    }

    public void SendArmRequest(bool arm)
    {
        TryGetTarget(out var sysId, out var compId);

        var req = new MAVLink.mavlink_command_long_t
        {
            target_system = sysId,
            target_component = compId,
            command = (ushort)MAVLink.MAV_CMD.COMPONENT_ARM_DISARM,
            param1 = arm ? 1 : 0,
            param2 = 21196
        };

        var buffer = _parser.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, req);
        SendBuffer(buffer);
    }

    public void SetFlightMode(string modeName)
    {
        uint modeId = modeName.ToUpper() switch
        {
            "STABILIZE" => 0,
            "ALT_HOLD" => 2,
            "AUTO" => 3,
            "GUIDED" => 4,
            "LOITER" => 5,
            "RTL" => 6,
            "LAND" => 9,
            _ => 0
        };

        TryGetTarget(out var sysId, out var compId);

        var req = new MAVLink.mavlink_command_long_t
        {
            target_system = sysId,
            target_component = compId,
            command = (ushort)MAVLink.MAV_CMD.DO_SET_MODE,
            param1 = 1,
            param2 = modeId
        };

        var buffer = _parser.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, req);
        SendBuffer(buffer);
    }

    public void RequestDataStreams()
    {
        TryConfigureMessageIntervals();
    }

    public override void Dispose()
    {
        CloseSerialPort();
        base.Dispose();
    }

    private void CloseSerialPort()
    {
        try
        {
            if (_serialPort == null) return;

            if (_serialPort.IsOpen)
            {
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();
                _serialPort.Close();
            }

            _serialPort.Dispose();
        }
        catch
        {
            // ignore close errors
        }
        finally
        {
            _serialPort = null;
        }
    }

    // --- ADD THIS TO MavlinkService.cs ---
    private List<WaypointModel> _uploadQueue = new();

    public void UploadMission(List<WaypointModel> waypoints)
    {
        if (waypoints == null || waypoints.Count == 0) return;
        _uploadQueue = waypoints;

        // Step 1: Start the transaction by sending the count
        var msg = new MAVLink.mavlink_mission_count_t
        {
            count = (ushort)_uploadQueue.Count,
            target_system = 1,
            target_component = 1,
            mission_type = (byte)MAVLink.MAV_MISSION_TYPE.MISSION
        };

        SendPacket(MAVLink.MAVLINK_MSG_ID.MISSION_COUNT, msg);
    }

    // Call this inside your MAVLink message parser loop
    private void HandleMissionRequest(MAVLink.mavlink_mission_request_int_t req)
    {
        if (req.seq < _uploadQueue.Count)
        {
            var wp = _uploadQueue[req.seq];
            var item = new MAVLink.mavlink_mission_item_int_t
            {
                seq = (ushort)req.seq,
                target_system = 1,
                target_component = 1,
                command = (ushort)MAVLink.MAV_CMD.WAYPOINT,
                frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT,
                x = (int)(wp.Lat * 1e7), // Scaled to 7 decimal places for MAVLink
                y = (int)(wp.Lng * 1e7),
                z = (float)wp.Alt,
                autocontinue = 1
            };

            SendPacket(MAVLink.MAVLINK_MSG_ID.MISSION_ITEM_INT, item);
        }
    }


    // 3. Helper to send Parameters (CRITICAL for Setup Tab)
    public void SendParameter(string paramId, float value)
    {
        TryGetTarget(out var sysId, out var compId); //

        var req = new MAVLink.mavlink_param_set_t
        {
            target_system = sysId,
            target_component = compId,
            param_value = value,
            param_type = (byte)MAVLink.MAV_PARAM_TYPE.REAL32
        };

        // Correctly format the 16-character parameter ID
        byte[] temp = System.Text.Encoding.ASCII.GetBytes(paramId);
        req.param_id = new byte[16];
        Array.Copy(temp, req.param_id, Math.Min(temp.Length, 16));

        var buffer = _parser.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.PARAM_SET, req); //
        SendBuffer(buffer); //
    }

    private static string GetFlightModeString(uint customMode)
    {
        return customMode switch
        {
            0 => "STABILIZE",
            2 => "ALT_HOLD",
            3 => "AUTO",
            4 => "GUIDED",
            5 => "LOITER",
            6 => "RTL",
            9 => "LAND",
            _ => "Unknown"
        };
    }
    public void SendCommand(MAVLink.MAV_CMD command, float p1, float p2, float p3, float p4, float p5, float p6, float p7)
    {
        if (_serialPort == null || !_serialPort.IsOpen) return;

        if (!TryGetTarget(out byte sysId, out byte compId))
        {
            // Fallback to default broadcast IDs if no vehicle is identified yet
            sysId = 1;
            compId = 1;
        }

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
            target_system = sysId,    // Standard Drone System ID
            target_component = compId, // Target all components
            confirmation = 0
        };

        var packet = _parser.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, cmd);

        // Send the packet safely
        lock (_sendLock) // Ensure thread safety if you have a lock object, otherwise just send
        {
            _serialPort.Write(packet, 0, packet.Length);
        }

        Console.WriteLine($"CMD Sent: {command}");
    }

    // Helper for "Go To" (Reposition) in Guided Mode
    public void SendReposition(double lat, double lon, float alt)
    {
        // MAV_CMD_DO_REPOSITION: p1=speed(-1=no change), p4=yaw(NaN), p5=lat, p6=lon, p7=alt
        // Lat/Lon must be in integer format (deg * 1E7) for some commands, 
        // but DO_REPOSITION usually takes float/int depending on MAVLink version. 
        // For standard Int32 MAVLink 1.0/2.0 library usage:

        SendCommand(MAVLink.MAV_CMD.DO_REPOSITION,
            -1, // Speed (-1 default)
            1, // 1 = Force Change to Guided Mode
            0,
            0,
            (float)lat, // Lat
            (float)lon, // Lon
            alt); // Alt (meters)
    }

    private void CheckLinkHealth()
    {
        if (_shouldBeConnected && _gotFirstHeartbeatThisSession)
        {
            var timeSinceLast = (DateTime.UtcNow - State.LastHeartbeat).TotalSeconds;

            if (timeSinceLast > 3.0) // 3-second timeout threshold [cite: 151, 170]
            {
                if (!_wasAlerting)
                {
                    _wasAlerting = true;
                    ReportConn("CRITICAL: LINK LOST"); // Updates the status text [cite: 228-229]
                    ShowConnDialog(true); // Pops the window back up [cite: 218, 221]
                }
            }
            else
            {
                if (_wasAlerting)
                {
                    _wasAlerting = false;
                    ShowConnDialog(false); // Auto-close if link recovers
                }
            }
        }
    }

    public async Task<bool> UploadMissionAsync(dynamic items)
    {
        try
        {
            var count = new MAVLink.mavlink_mission_count_t();
            count.target_system = target_sysid;
            count.target_component = target_compid;
            count.count = (ushort)items.Count;
            count.mission_type = (byte)MAVLink.MAV_MISSION_TYPE.MISSION;

            SendPacket(count);

            for (ushort i = 0; i < items.Count; i++)
            {
                var wp = items[i];
                var msg = new MAVLink.mavlink_mission_item_int_t();

                msg.target_system = target_sysid;
                msg.target_component = target_compid;
                msg.seq = i;
                msg.frame = (byte)MAVLink.MAV_FRAME.GLOBAL_RELATIVE_ALT_INT;
                msg.command = (ushort)TranslateCommand(wp.Command);
                msg.current = (byte)(i == 0 ? 1 : 0);
                msg.autocontinue = 1;

                msg.x = (int)(wp.Lat * 10000000);
                msg.y = (int)(wp.Lng * 10000000);
                msg.z = (float)wp.Alt;
                msg.mission_type = (byte)MAVLink.MAV_MISSION_TYPE.MISSION;

                SendPacket(msg);
            }
            return true;
        }
        catch { return false; }
    }

    private MAVLink.MAV_CMD TranslateCommand(string cmd)
    {
        return cmd.ToUpper() switch
        {
            "TAKEOFF" => MAVLink.MAV_CMD.TAKEOFF,
            "LAND" => MAVLink.MAV_CMD.LAND,
            "RTL" => MAVLink.MAV_CMD.RETURN_TO_LAUNCH,
            _ => MAVLink.MAV_CMD.WAYPOINT
        };
    }

    // --- Placeholder Methods for your specific Serial/UDP implementation ---
    private void SendPacket<T>(T packet, MAVLINK_MSG_ID msgId) { /* Your byte serialization/writing logic */ }
    private Task<T?> WaitForMessageAsync<T>(MAVLINK_MSG_ID msgId, int timeoutMs) { /* Your listener logic */ return Task.FromResult(default(T)); }
    // Inside MavlinkService.cs
    public void SetFrameConfig(int frameType, int frameClass)
    {
        // FRAME_TYPE and FRAME_CLASS are standard ArduPilot parameters
        SendParameter("FRAME_TYPE", frameType);
        SendParameter("FRAME_CLASS", frameClass);
    }

    public void StartCalibration(string type)
    {
        switch (type.ToUpper())
        {
            case "ACCEL":
                // Param 5 = 1 starts the 6-axis Accel Cal
                SendCommand(MAVLink.MAV_CMD.PREFLIGHT_CALIBRATION, 0, 0, 0, 0, 1, 0, 0);
                break;
            case "RADIO":
                // Start internal C# tracking
                IsRcCalibrating = true;
                for (int i = 0; i < 8; i++) { RcMin[i] = 2200; RcMax[i] = 800; }
                break;
        }
    }

    public void AckCalibrationStep()
    {
        // This tells the drone "I have moved the drone to the next position, proceed"
        SendCommand(MAVLink.MAV_CMD.ACCELCAL_VEHICLE_POS, 0, 0, 0, 0, 0, 0, 0);
    }

    public void SaveRcCalibration()
    {
        IsRcCalibrating = false;
        // Save the Max and Min for the 4 primary flight channels to the drone's memory
        for (int i = 0; i < 4; i++)
        {
            SendParameter($"RC{i + 1}_MIN", RcMin[i]);
            SendParameter($"RC{i + 1}_MAX", RcMax[i]);
            SendParameter($"RC{i + 1}_TRIM", State.RawChannels[i]);
        }
    }

    public void SendArmRequest(bool arm, bool force = false)
    {
        if (!TryGetTarget(out var sysId, out var compId)) return;

        var req = new MAVLink.mavlink_command_long_t
        {
            target_system = sysId,
            target_component = compId,
            command = (ushort)MAVLink.MAV_CMD.COMPONENT_ARM_DISARM,
            // Param1: 1 to arm, 0 to disarm
            param1 = arm ? 1 : 0,
            // Param2: 21196 is the force-disarm magic number for ArduPilot
            param2 = (!arm && force) ? 21196 : 0
        };

        var buffer = _parser.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, req);
        SendBuffer(buffer);
    }

    // --- MOTOR TESTING (MISSION PLANNER STYLE) ---
    public void TestMotor(int motorIndex, float throttlePercent, float durationSec)
    {
        var cmd = new MAVLink.mavlink_command_long_t();

        // Ensure we are targeting the correct drone and component
        cmd.target_system = target_sysid;
        cmd.target_component = target_compid;

        // MAV_CMD_DO_MOTOR_TEST = 183
        cmd.command = (ushort)MAVLink.MAV_CMD.DO_MOTOR_TEST;

        // Param 1: Motor instance (1, 2, 3, 4...)
        cmd.param1 = motorIndex;

        // Param 2: Test type (0 = percent throttle, 1 = PWM, 2 = Pilot throttle)
        cmd.param2 = 0;

        // Param 3: Throttle value (%)
        cmd.param3 = throttlePercent;

        // Param 4: Timeout (seconds)
        cmd.param4 = durationSec;

        // Params 5, 6, 7 are unused for this command
        cmd.param5 = 0;
        cmd.param6 = 0;
        cmd.param7 = 0;

        // Generate and send the MAVLink packet
        SendPacket(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, cmd);

        Console.WriteLine($"[MOTOR TEST] Commanded Motor {motorIndex} at {throttlePercent}% for {durationSec}s");
    }
}
