using DivyaLink.Models;
using DivyaLink.Services;

namespace DivyaLink.Services.Firmware;

/// <summary>
/// Firmware-specific behavior for flight modes, connect bootstrap, and parameter metadata.
/// </summary>
public interface IFirmwareAdapter
{
    FirmwareFamily Family { get; }
    string DisplayName { get; }

    string GetFlightModeString(uint customMode, VehicleProfile vehicleProfile);
    float GetModeValue(string modeName, VehicleProfile vehicleProfile);
    IReadOnlyList<string> GetAvailableModes(VehicleProfile vehicleProfile);

    /// <summary>MAV_CMD_DO_SET_MODE parameters for switching flight mode at runtime.</summary>
    (float param1, float param2, float param3) GetSetModeCommandParams(string modeName, VehicleProfile vehicleProfile);

    /// <summary>Parameters to prefetch after the first stable heartbeat.</summary>
    IReadOnlyList<string> GetConnectBootstrapParams(VehicleProfile vehicleProfile);

    /// <summary>File extensions accepted for bootloader flashing (.apj for ArduPilot, .px4 for PX4 — same container format).</summary>
    IReadOnlyList<string> SupportedFirmwareExtensions { get; }

    Task AnnotateParametersAsync(
        IEnumerable<ParameterEntry> entries,
        VehicleProfile vehicleProfile,
        CancellationToken ct = default);
}
