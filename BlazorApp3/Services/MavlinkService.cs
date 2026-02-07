using System.IO.Ports;
//using MavLink;
//using BlazorApp3.Models; // Ensure this matches your project's folder structure

public enum ConnectionType { Serial, UDP, TCP }
public class MavlinkService : BackgroundService
{
    private readonly ILogger<MavlinkService> _logger;
    private SerialPort? _serialPort;
    private readonly MAVLink.MavlinkParse _parser = new();
    public bool IsConnected => _serialPort != null &&
                          _serialPort.IsOpen &&
                          (DateTime.Now - State.LastHeartbeat).TotalSeconds < 5;
    private bool _continueReading;
    //Newly added Ports field to handle multiple connections (e.g., COM7, COM8, etc.)
    private Stream? _linkStream;
    private bool _isRunning;

    // The UI binds to this object to display data
    public DroneState State { get; set; } = new();

    // Add this field to the class to fix CS0103
    private DateTime _lastPacketTime;

    public string[] GetAvailablePorts()
    {
        return System.IO.Ports.SerialPort.GetPortNames();
    }

    public MavlinkService(ILogger<MavlinkService> logger)
    {
        _logger = logger;
    }

    public void Connect(ConnectionType type, string portName, int speed)
    {
        StopConnection();
        _isRunning = true;

        Task.Run(() => {
            try
            {
                if (type == ConnectionType.Serial)
                {
                    var serial = new System.IO.Ports.SerialPort(portName, speed);
                    serial.Open();
                    _linkStream = serial.BaseStream;
                    State.ConnectionStatus = $"Connected: {portName}";
                }
                // UDP/TCP logic would be initialized here

                ReadLoop();
            }
            catch (Exception ex)
            {
                State.ConnectionStatus = $"Error: {ex.Message}";
                _isRunning = false;
            }
        });
    }

    public void StopConnection()
    {
        try
        {
            if (_serialPort != null)
            {
                // Stop the reading thread first!
                _continueReading = false;

                if (_serialPort.IsOpen)
                {
                    _serialPort.DiscardInBuffer();
                    _serialPort.DiscardOutBuffer();
                    _serialPort.Close();
                }
                _serialPort.Dispose();
                _serialPort = null;
            }
        }
        catch { /* Ignore hang-ups on yanked cables */ }
        finally
        {
            State.ConnectionStatus = "Disconnected";
            // Reset heartbeat so UI doesn't think it's still "alive"
            State.LastHeartbeat = DateTime.MinValue;
        }
    }

    private void ReadLoop()
    {
        var parser = new MAVLink.MavlinkParse();
        while (_isRunning && _linkStream != null)
        {
            try
            {
                int b = _linkStream.ReadByte();
                if (b == -1) break;
                var packet = parser.ReadPacket(new MemoryStream(new[] { (byte)b }));
                if (packet != null) HandlePacket(packet);
            }
            catch { break; }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                try
                {
                    _serialPort = new SerialPort("COM7", 115200, Parity.None, 8, StopBits.One);
                    _serialPort.Open();
                    State.ConnectionStatus = "Connected to COM7";
                    _logger.LogInformation("Connected to Pixhawk.");

                    // Ask the Pixhawk to start sending data immediately upon connection
                    RequestDataStreams();
                }
                catch
                {
                    State.ConnectionStatus = "Searching for Pixhawk...";
                    await Task.Delay(2000, stoppingToken); // Wait 2 seconds before trying again
                    continue; // Skip the rest and try to open the port again
                }
            }

            // If we are here, the port is open. Let's read data.
            try
            {
                if (_serialPort.BytesToRead > 0)
                {
                    var packet = _parser.ReadPacket(_serialPort.BaseStream);
                    if (packet != null)
                    {
                        _lastPacketTime = DateTime.Now;
                        HandlePacket(packet);
                    }
                }

                // Check for data timeout (unplugged while port technically "open")
                if ((DateTime.Now - _lastPacketTime).TotalSeconds > 2)
                {
                    State.ConnectionStatus = "Data Lost - Check Cable";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Serial port lost. Attempting reconnect...");
                _serialPort?.Close();
                State.ConnectionStatus = "Disconnected";
            }

            await Task.Delay(1, stoppingToken); // High-speed polling
        }
    }

    private void HandlePacket(MAVLink.MAVLinkMessage packet)
    {
        // Record the exact time this packet arrived
        _lastPacketTime = DateTime.Now;

        if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.HEARTBEAT)
        {
            State.ConnectionStatus = "Receiving Data";
        }

        if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.ATTITUDE)
        {
            var att = (MAVLink.mavlink_attitude_t)packet.data;
            State.Pitch = att.pitch * (180 / (float)Math.PI);
            State.Roll = att.roll * (180 / (float)Math.PI);
            State.Yaw = att.yaw * (180 / (float)Math.PI);
        }

        if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.GLOBAL_POSITION_INT)
        {
            var pos = (MAVLink.mavlink_global_position_int_t)packet.data;
            State.Latitude = pos.lat / 10000000.0;
            State.Longitude = pos.lon / 10000000.0;
            // Heading is sent in centidegrees (0-36000)
            State.Yaw = pos.hdg / 100.0f;
        }

        if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.SYS_STATUS)
        {
            var sys = (MAVLink.mavlink_sys_status_t)packet.data;
            State.Voltage = sys.voltage_battery / 1000.0; 
            State.BatteryPercent = sys.battery_remaining;
        }

        if (packet.msgid == (uint)MAVLink.MAVLINK_MSG_ID.VFR_HUD)
        {
            var hud = (MAVLink.mavlink_vfr_hud_t)packet.data;
            State.GroundSpeed = hud.groundspeed; 
            State.Altitude = hud.alt;
        }
    }
    // Call this from a button in Home.razor to send data back to Pixhawk
    public void SendBuffer(byte[] buffer)
    {
        if (_serialPort != null && _serialPort.IsOpen)
        {
            _serialPort.Write(buffer, 0, buffer.Length);
        }
    }

    public override void Dispose()
    {
        if (_serialPort != null)
        {
            if (_serialPort.IsOpen) _serialPort.Close();
            _serialPort.Dispose();
        }
        base.Dispose();
    }
    public void RequestDataStreams()
    {
        // Request Attitude data at 10Hz
        var request = new MAVLink.mavlink_request_data_stream_t
        {
            target_system = 1,
            target_component = 1,
            req_stream_id = (byte)MAVLink.MAV_DATA_STREAM.EXTRA1, // Attitude
            req_message_rate = 10, // 10 times per second
            start_stop = 1 // Start
        };

        byte[] buffer = _parser.GenerateMAVLinkPacket20(MAVLink.MAVLINK_MSG_ID.REQUEST_DATA_STREAM, request);
        SendBuffer(buffer);
    }
}