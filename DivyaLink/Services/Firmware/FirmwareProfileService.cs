using static MAVLink;

namespace DivyaLink.Services.Firmware;

/// <summary>
/// Tracks the active autopilot firmware (ArduPilot vs PX4) from HEARTBEAT or user override.
/// </summary>
public sealed class FirmwareProfileService
{
    private readonly ArduPilotFirmwareAdapter _ardupilot;
    private readonly Px4FirmwareAdapter _px4;
    private readonly object _lock = new();

    private volatile FirmwareFamily _activeFirmware = FirmwareFamily.ArduPilot;
    private FirmwareFamily? _autoDetected;
    private FirmwareFamily? _manualOverride;

    public FirmwareProfileService(
        ArduPilotFirmwareAdapter ardupilot,
        Px4FirmwareAdapter px4)
    {
        _ardupilot = ardupilot;
        _px4 = px4;
    }

    public FirmwareFamily ActiveFirmware => _activeFirmware;
    public FirmwareFamily? AutoDetected => _autoDetected;
    public FirmwareFamily? ManualOverride => _manualOverride;

    public FirmwareProfileSource Source =>
        _manualOverride.HasValue ? FirmwareProfileSource.ManualOverride : FirmwareProfileSource.AutoDetect;

    public IFirmwareAdapter ActiveAdapter => _activeFirmware switch
    {
        FirmwareFamily.PX4 => _px4,
        FirmwareFamily.ArduPilot => _ardupilot,
        _ => _ardupilot
    };

    public string ActiveFirmwareDisplayName => ActiveAdapter.DisplayName;

    public event Action<FirmwareFamily>? OnFirmwareChanged;

    /// <summary>Used when disconnected — e.g. firmware flash page target selection.</summary>
    public void SetManualOverride(FirmwareFamily firmware)
    {
        lock (_lock)
        {
            _manualOverride = firmware;
            _activeFirmware = firmware;
        }
        OnFirmwareChanged?.Invoke(firmware);
    }

    public void ClearManualOverride()
    {
        FirmwareFamily active;
        lock (_lock)
        {
            _manualOverride = null;
            active = _autoDetected ?? _activeFirmware;
            _activeFirmware = active;
        }
        OnFirmwareChanged?.Invoke(active);
    }

    /// <summary>Called from MavlinkService on every HEARTBEAT from the autopilot component.</summary>
    public bool UpdateFromHeartbeat(byte autopilot, byte mavType)
    {
        var detected = MapAutopilot(autopilot);
        if (detected == null) return false;

        FirmwareFamily? changed = null;

        lock (_lock)
        {
            _autoDetected = detected.Value;

            if (_manualOverride.HasValue && _manualOverride.Value != detected.Value)
                _manualOverride = null;

            if (_activeFirmware != detected.Value)
            {
                _activeFirmware = detected.Value;
                changed = detected.Value;
            }
        }

        if (changed.HasValue)
        {
            OnFirmwareChanged?.Invoke(changed.Value);
            return true;
        }

        return false;
    }

    public static FirmwareFamily? MapAutopilot(byte autopilot) => autopilot switch
    {
        (byte)MAV_AUTOPILOT.ARDUPILOTMEGA => FirmwareFamily.ArduPilot,
        (byte)MAV_AUTOPILOT.PX4 => FirmwareFamily.PX4,
        (byte)MAV_AUTOPILOT.GENERIC => null,
        _ => null
    };
}
