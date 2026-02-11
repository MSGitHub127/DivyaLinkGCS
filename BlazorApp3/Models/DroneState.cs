public class DroneState
{
    public float Pitch { get; set; }
    public float Roll { get; set; }
    public float Yaw { get; set; }
    public string ConnectionStatus { get; set; } = "Disconnected!";
    public double Latitude { get; set; } = 0;
    public double Longitude { get; set; } = 0;
    public double Voltage { get; set; }
    public int BatteryPercent { get; set; }
    public double GroundSpeed { get; set; } 
    public double Altitude { get; set; }
    public int SatCount { get; set; } = 0;
    public DateTime LastHeartbeat { get; set; } = DateTime.MinValue;
    public string FlightMode { get; set; } = "Unknown";
    public float ClimbRate { get; set; } = 0f;
    public bool IsArmed { get; set; } = false;
}

