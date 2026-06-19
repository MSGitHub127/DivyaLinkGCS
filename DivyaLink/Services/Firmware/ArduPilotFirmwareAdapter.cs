using DivyaLink.Models;
using DivyaLink.Services;

namespace DivyaLink.Services.Firmware;

public sealed class ArduPilotFirmwareAdapter : IFirmwareAdapter
{
    private readonly ParameterMetadataService _metadata;

    public ArduPilotFirmwareAdapter(ParameterMetadataService metadata) => _metadata = metadata;

    public FirmwareFamily Family => FirmwareFamily.ArduPilot;
    public string DisplayName => "ArduPilot";
    public IReadOnlyList<string> SupportedFirmwareExtensions => [".apj"];

    public string GetFlightModeString(uint customMode, VehicleProfile vehicleProfile) =>
        vehicleProfile == VehicleProfile.ArduRover
            ? GetRoverModeString(customMode)
            : GetCopterModeString(customMode);

    public float GetModeValue(string modeName, VehicleProfile vehicleProfile) =>
        vehicleProfile == VehicleProfile.ArduRover
            ? GetRoverModeValue(modeName)
            : GetCopterModeValue(modeName);

    public IReadOnlyList<string> GetAvailableModes(VehicleProfile vehicleProfile) =>
        vehicleProfile == VehicleProfile.ArduRover
            ? VehicleProfileService.RoverModes
            : VehicleProfileService.CopterModes;

    public (float param1, float param2, float param3) GetSetModeCommandParams(string modeName, VehicleProfile vehicleProfile)
    {
        // ArduPilot: param1 = 1 (custom mode enabled), param2 = custom_mode value
        var modeId = GetModeValue(modeName, vehicleProfile);
        return (1f, modeId, 0f);
    }

    public IReadOnlyList<string> GetConnectBootstrapParams(VehicleProfile vehicleProfile)
    {
        var list = new List<string>();
        for (int i = 1; i <= 6; i++) list.Add($"FLTMODE{i}");
        list.Add("FLTMODE_CH");
        list.AddRange([
            "FS_THR_ENABLE", "FS_THR_VALUE", "FS_BATT_ENABLE", "FS_GCS_ENABLE",
            "BATT_MONITOR", "BATT_CAPACITY", "BATT_LOW_VOLT", "BATT_CRT_VOLT",
            "BATT_ARM_VOLT", "BATT_LOW_MAH", "BATT_VOLT_MULT", "BATT_AMP_PERVLT",
            "BATT_AMP_OFFSET", "BATT_NUM_CELLS", "FRAME_CLASS", "FRAME_TYPE"
        ]);
        return list;
    }

    public async Task AnnotateParametersAsync(
        IEnumerable<ParameterEntry> entries,
        VehicleProfile vehicleProfile,
        CancellationToken ct = default)
    {
        var vehicleType = vehicleProfile == VehicleProfile.ArduRover
            ? ArduPilotVehicleType.Rover
            : ArduPilotVehicleType.ArduCopter;
        await _metadata.AnnotateAsync(entries, vehicleType, ct);
    }

    private static string GetRoverModeString(uint mode) => mode switch
    {
        0  => "MANUAL", 1 => "ACRO", 3 => "STEERING", 4 => "HOLD", 5 => "LOITER",
        6  => "FOLLOW", 7 => "SIMPLE", 10 => "AUTO", 11 => "RTL", 12 => "SMART_RTL",
        15 => "GUIDED", 16 => "INITIALISING", _ => $"MODE({mode})"
    };

    private static string GetCopterModeString(uint mode) => mode switch
    {
        0  => "STABILIZE", 1 => "ACRO", 2 => "ALT_HOLD", 3 => "AUTO", 4 => "GUIDED",
        5  => "LOITER", 6 => "RTL", 7 => "CIRCLE", 9 => "LAND", 11 => "DRIFT",
        13 => "SPORT", 14 => "FLIP", 15 => "AUTOTUNE", 16 => "POSHOLD", 17 => "BRAKE",
        18 => "THROW", 19 => "AVOID_ADSB", 20 => "GUIDED_NOGPS", 21 => "SMART_RTL",
        22 => "FLOWHOLD", 23 => "FOLLOW", 24 => "ZIGZAG", _ => $"MODE({mode})"
    };

    private static float GetRoverModeValue(string name) =>
        name.Trim().ToUpperInvariant() switch
        {
            "MANUAL" => 0, "ACRO" => 1, "STEERING" => 3, "HOLD" => 4, "LOITER" => 5,
            "FOLLOW" => 6, "SIMPLE" => 7, "AUTO" => 10, "RTL" => 11, "SMART_RTL" => 12,
            "GUIDED" => 15, "INITIALISING" => 16, _ => 0
        };

    private static float GetCopterModeValue(string name) =>
        name.Trim().ToUpperInvariant() switch
        {
            "STABILIZE" => 0, "ACRO" => 1, "ALT_HOLD" => 2, "AUTO" => 3, "GUIDED" => 4,
            "LOITER" => 5, "RTL" => 6, "CIRCLE" => 7, "LAND" => 9, "DRIFT" => 11,
            "SPORT" => 13, "FLIP" => 14, "AUTOTUNE" => 15, "POSHOLD" => 16, "BRAKE" => 17,
            "THROW" => 18, "AVOID_ADSB" => 19, "GUIDED_NOGPS" => 20, "SMART_RTL" => 21,
            "FLOWHOLD" => 22, "FOLLOW" => 23, "ZIGZAG" => 24, _ => 0
        };
}
