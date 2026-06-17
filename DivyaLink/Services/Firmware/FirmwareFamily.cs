namespace DivyaLink.Services.Firmware;

/// <summary>
/// Autopilot firmware family detected from HEARTBEAT.autopilot or chosen by the user for flashing.
/// </summary>
public enum FirmwareFamily
{
    Unknown,
    ArduPilot,
    PX4
}

public enum FirmwareProfileSource
{
    AutoDetect,
    ManualOverride
}
