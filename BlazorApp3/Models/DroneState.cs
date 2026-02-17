public class DroneState
{
    // --- Vehicle identity (discovered from HEARTBEAT) ---
    public string ConnectionStatus { get; set; } = "Disconnected";
    public DateTime LastHeartbeat { get; set; } = DateTime.MinValue;
    public bool HasVehicleId { get; set; }
    public byte SystemId { get; set; }
    public byte ComponentId { get; set; }
    public double RxPacketsPerSec { get; set; }
    public DateTime LastPacketUtc { get; set; }

    // Telemetry
    public float Roll { get; set; }
    public float Pitch { get; set; }
    public float Yaw { get; set; } // The value used for UI
    public float Altitude { get; set; } // Relative Alt in Meters
    public bool HasRelAlt { get; set; } // Track if we have valid RelAlt

    // Heading Logic
    public float AttitudeYawDeg { get; set; }
    public float HeadingDeg { get; set; }
    public float HeadingRawDeg { get; set; }
    public string HeadingSource { get; set; } = "Unknown";

    // GPS & Speed
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int SatCount { get; set; }
    public float GroundSpeed { get; set; }
    public float AirSpeed { get; set; }
    public float ClimbRate { get; set; }

    // Battery
    public double Voltage { get; set; }
    public float BatteryPercent { get; set; }

    // State
    public bool IsArmed { get; set; }
    public string FlightMode { get; set; } = "Unknown"; // Critical for your UI

    public DroneState() { }

    // Copy ctor for atomic snapshots
    public DroneState(DroneState other)
    {
        // Connection / IDs
        ConnectionStatus = other.ConnectionStatus;
        HasVehicleId = other.HasVehicleId;
        SystemId = other.SystemId;
        ComponentId = other.ComponentId;
        LastHeartbeat = other.LastHeartbeat;

        // GPS / attitude
        Latitude = other.Latitude;
        Longitude = other.Longitude;
        Altitude = other.Altitude;
        Roll = other.Roll;
        Pitch = other.Pitch;
        Yaw = other.Yaw;

        // Speeds
        GroundSpeed = other.GroundSpeed;
        ClimbRate = other.ClimbRate;

        // Battery (IMPORTANT)
        Voltage = other.Voltage;
        BatteryPercent = other.BatteryPercent;

        // Any fields you added later
        HeadingDeg = other.HeadingDeg;
        HeadingRawDeg = other.HeadingRawDeg;
        HeadingSource = other.HeadingSource;
        AttitudeYawDeg = other.AttitudeYawDeg;
        HasRelAlt = other.HasRelAlt;

        // Stats
        RxPacketsPerSec = other.RxPacketsPerSec;
        LastPacketUtc = other.LastPacketUtc;

        // Flags
        IsArmed = other.IsArmed;
    }
}
