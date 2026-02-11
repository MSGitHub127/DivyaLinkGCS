using System.IO.Ports;

public enum ConnectionType { Serial, UDP, TCP }

public class MavlinkService : BackgroundService
{
    private readonly ILogger<MavlinkService> _logger;
    private readonly object _sync = new();
    private readonly MAVLink.MavlinkParse _parser = new();

    private SerialPort? _serialPort;
    private ConnectionType _connectionType = ConnectionType.Serial;
    private string _portName = "COM7";
    private int _baudRate = 115200;
    private bool _shouldBeConnected = true;
    public event Action? OnTelemetryUpdated;

    // The UI binds to this object to display data
    public DroneState State { get; } = new();

    private DateTime _lastPacketTime = DateTime.MinValue;

    public bool IsConnected =>
        _serialPort?.IsOpen == true &&
        (DateTime.UtcNow - State.LastHeartbeat).TotalSeconds < 3;

    public MavlinkService(ILogger<MavlinkService> logger)
    {
        _logger = logger;
    }

    public string[] GetAvailablePorts()
    {
        return SerialPort.GetPortNames();
    }

    public void Connect(ConnectionType type, string portName, int speed)
    {
        StopConnection();
        lock (_sync)
        {
            _connectionType = type;
            _portName = string.IsNullOrWhiteSpace(portName) ? _portName : portName;
            _baudRate = speed > 0 ? speed : _baudRate;
            _shouldBeConnected = true;
        }

        CloseSerialPort();
        State.ConnectionStatus = $"Connecting to {_portName}...";
    }

    public void StopConnection()
    {
        lock (_sync)
        {
            _shouldBeConnected = false;
        }

        CloseSerialPort();
        State.ConnectionStatus = "Disconnected";
        State.LastHeartbeat = DateTime.MinValue;
        _lastPacketTime = DateTime.MinValue;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool shouldConnect;
            lock (_sync) { shouldConnect = _shouldBeConnected; }

            if (!shouldConnect)
            {
                CloseSerialPort(); // Ensure port is released when user clicks Disconnect
                await Task.Delay(500, stoppingToken);
                continue;
            }

            // If we are supposed to be connected but port is null/closed, try once.
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                if (!TryOpenSerialPort(_portName, _baudRate))
                {
                    // If open fails, wait longer before retrying to avoid locking COM7
                    await Task.Delay(3000, stoppingToken);
                    continue;
                }
                _lastPacketTime = DateTime.UtcNow;
            }

            try
            {
                var port = _serialPort;
                if (port != null && port.IsOpen && port.BytesToRead > 0)
                {
                    var packet = _parser.ReadPacket(port.BaseStream);
                    if (packet != null)
                    {
                        _lastPacketTime = DateTime.UtcNow;
                        HandlePacket(packet);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Comm error. Cleaning up port for retry...");
                CloseSerialPort(); // CRITICAL: Release the handle immediately on error
            }

            await Task.Delay(5, stoppingToken); // Small delay to prevent CPU spiking
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
            State.ConnectionStatus = $"Connected: {portName} @ {speed}";
            _logger.LogInformation("Connected to serial port {Port} at {Baud}", portName, speed);
            return true;
        }
        catch (Exception ex)
        {
            State.ConnectionStatus = $"Searching for {portName}...";
            _logger.LogWarning(ex, "Could not open serial port {Port} at {Baud}", portName, speed);
            CloseSerialPort();
            return false;
        }
    }

    private void HandlePacket(MAVLink.MAVLinkMessage packet)
    {
        if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.HEARTBEAT)
        {
            State.LastHeartbeat = DateTime.UtcNow;
            State.ConnectionStatus = "Receiving Data";
            return;
        }

        if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.ATTITUDE)
        {
            var att = (MAVLink.mavlink_attitude_t)packet.data;

            // Convert Radians to Degrees
            State.Roll = att.roll * (180.0f / (float)Math.PI);
            State.Pitch = att.pitch * (180.0f / (float)Math.PI);

            // Fix Yaw: Convert -180/180 range to 0-360 compass range
            float yawDeg = att.yaw * (180.0f / (float)Math.PI);
            if (yawDeg < 0) yawDeg += 360;
            State.Yaw = yawDeg;
        }

        if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT)
        {
            var pos = (MAVLink.mavlink_global_position_int_t)packet.data;
            State.Latitude = pos.lat / 10000000.0;
            State.Longitude = pos.lon / 10000000.0;
            // FIX: Use 'relative_alt' (Height above Home)
            // The value is in millimeters, so divide by 1000 to get Meters.
            State.Altitude = pos.relative_alt / 1000.0f;
            return;
        }

        if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.SYS_STATUS)
        {
            var sys = (MAVLink.mavlink_sys_status_t)packet.data;
            State.Voltage = sys.voltage_battery / 1000.0;
            State.BatteryPercent = sys.battery_remaining;
            return;
        }

        if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.VFR_HUD)
        {
            var hud = (MAVLink.mavlink_vfr_hud_t)packet.data;
            State.GroundSpeed = hud.groundspeed;
            State.ClimbRate = hud.climb;
            if (State.Altitude == 0)
            {
                State.Altitude = hud.alt;
            }
        }
        OnTelemetryUpdated?.Invoke();   
    }

    private void SendBuffer(byte[] buffer)
    {
        if (_serialPort != null && _serialPort.IsOpen)
        {
            try
            {
                _serialPort.Write(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending packet: {ex.Message}");
            }
        }
    }

    public void SendCommand(MAVLink.MAV_CMD command, float param1)
    {
        // Create the command structure
        var req = new MAVLink.mavlink_command_long_t
        {
            target_system = 1,      // Target the Drone (SysID 1)
            target_component = 1,   // Target the Flight Controller (CompID 1)
            command = (ushort)command,
            param1 = param1,        // e.g., 1 to Arm, 0 to Disarm
            confirmation = 0,
            param2 = 0,
            param3 = 0,
            param4 = 0,
            param5 = 0,
            param6 = 0,
            param7 = 0
        };

        // Pack it into bytes (Using MAVLink 2.0)
        byte[] buffer = _parser.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, req);

        // Send it
        SendBuffer(buffer);
    }

    public void RequestDataStreams()
    {
        var request = new MAVLink.mavlink_request_data_stream_t
        {
            target_system = 1,
            target_component = 1,
            req_stream_id = (byte)MAVLink.MAV_DATA_STREAM.EXTRA2,
            req_message_rate = 10,
            start_stop = 1
        };

        var buffer = _parser.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.REQUEST_DATA_STREAM, request);
        SendBuffer(buffer);
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
            if (_serialPort == null)
            {
                return;
            }

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
            // Ignore close errors from cable unplug/disposal races.
        }
        finally
        {
            _serialPort = null;
        }
    }

    public void SendArmRequest(bool arm)
    {
        var req = new MAVLink.mavlink_command_long_t
        {
            target_system = 1,
            target_component = 1,
            command = (ushort)MAVLink.MAV_CMD.COMPONENT_ARM_DISARM,
            param1 = arm ? 1 : 0, // 1 to Arm, 0 to Disarm
            param2 = 21196 // Force arming (optional, 21196 is 'magic number' to bypass some safety)
        };

        var packet = _parser.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, req);
        SendBuffer(packet);
    }

    public void SetFlightMode(string modeName)
    {
        // Mapping 7 Essential Modes to ArduPilot IDs
        uint modeId = modeName.ToUpper() switch
        {
            "STABILIZE" => 0,  // Manual Control
            "ALT_HOLD" => 2,   // Holds Height (Good for filming)
            "AUTO" => 3,       // Runs Mission
            "GUIDED" => 4,     // "Fly Here" commands
            "LOITER" => 5,     // GPS Lock (Most used)
            "RTL" => 6,        // Return to Launch (Safety)
            "LAND" => 9,       // Emergency Landing
            _ => 0             // Default
        };

        var req = new MAVLink.mavlink_command_long_t
        {
            target_system = 1,
            target_component = 1,
            command = (ushort)MAVLink.MAV_CMD.DO_SET_MODE,
            param1 = 1, // Custom Mode Enabled
            param2 = modeId
        };

        var packet = _parser.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.COMMAND_LONG, req);
        SendBuffer(packet);
    }
}
