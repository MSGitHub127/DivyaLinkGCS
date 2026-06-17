using DivyaLink.Models;
using DivyaLink.Services;

namespace DivyaLink.Services.Firmware;

/// <summary>
/// PX4 flight-mode encoding: custom_mode is a union of main_mode (byte 2) and sub_mode (byte 3).
/// Reference: PX4 px4_custom_mode.h / mavros px4_custom_mode.hpp
/// </summary>
public sealed class Px4FirmwareAdapter : IFirmwareAdapter
{
    private readonly Px4ParameterMetadataService _metadata;

    public Px4FirmwareAdapter(Px4ParameterMetadataService metadata) => _metadata = metadata;

    public FirmwareFamily Family => FirmwareFamily.PX4;
    public string DisplayName => "PX4";
    public IReadOnlyList<string> SupportedFirmwareExtensions => [".px4"];

    public string GetFlightModeString(uint customMode, VehicleProfile vehicleProfile)
    {
        var main = (byte)((customMode >> 16) & 0xFF);
        var sub  = (byte)((customMode >> 24) & 0xFF);

        return main switch
        {
            1 => "MANUAL",
            2 => "ALTCTL",
            3 => sub switch { 4 => "POSCTL SLOW", _ => "POSCTL" },
            4 => GetAutoSubMode(sub),
            5 => "ACRO",
            6 => "OFFBOARD",
            7 => "STABILIZED",
            8 => "RATTITUDE",
            10 => "TERMINATION",
            11 => "ALTITUDE_CRUISE",
            _ => $"PX4({main},{sub})"
        };
    }

    public float GetModeValue(string modeName, VehicleProfile vehicleProfile)
    {
        // Return encoded custom_mode for DO_SET_MODE param2
        return modeName.Trim().ToUpperInvariant() switch
        {
            "MANUAL"      => Encode(1, 0),
            "ALTCTL"      => Encode(2, 0),
            "POSCTL"      => Encode(3, 0),
            "POSCTL SLOW" => Encode(3, 4),
            "ACRO"        => Encode(5, 0),
            "OFFBOARD"    => Encode(6, 0),
            "STABILIZED"  => Encode(7, 0),
            "RATTITUDE"   => Encode(8, 0),
            "AUTO"        => Encode(4, 4), // AUTO.MISSION
            "MISSION"     => Encode(4, 4),
            "HOLD"        => Encode(4, 3), // AUTO.LOITER
            "LOITER"      => Encode(4, 3),
            "RTL"         => Encode(4, 5),
            "LAND"        => Encode(4, 6),
            "TAKEOFF"     => Encode(4, 2),
            "FOLLOW"      => Encode(4, 8),
            "PRECLAND"    => Encode(4, 9),
            _             => Encode(1, 0)
        };
    }

    public IReadOnlyList<string> GetAvailableModes(VehicleProfile vehicleProfile) =>
        vehicleProfile == VehicleProfile.ArduRover
            ? Px4RoverModes
            : Px4CopterModes;

    public (float param1, float param2, float param3) GetSetModeCommandParams(string modeName, VehicleProfile vehicleProfile)
    {
        // PX4: param1 = MAV_MODE_FLAG_CUSTOM_MODE_ENABLED (1), param2 = custom_mode uint32
        var customMode = GetModeValue(modeName, vehicleProfile);
        return (1f, customMode, 0f);
    }

    public IReadOnlyList<string> GetConnectBootstrapParams(VehicleProfile vehicleProfile) =>
    [
        "SYS_AUTOSTART", "SYS_MC_EST_GROUP", "COM_FLTMODE1", "COM_FLTMODE2", "COM_FLTMODE3",
        "COM_FLTMODE4", "COM_FLTMODE5", "COM_FLTMODE6",
        "BAT1_N_CELLS", "BAT1_V_EMPTY", "BAT1_V_CHARGED", "BAT1_CAPACITY",
        "COM_LOW_BAT_ACT", "COM_DL_LOSS_T", "COM_RC_LOSS_T",
        "MAV_TYPE", "EKF2_MAG_TYPE"
    ];

    public Task AnnotateParametersAsync(
        IEnumerable<ParameterEntry> entries,
        VehicleProfile vehicleProfile,
        CancellationToken ct = default) =>
        _metadata.AnnotateAsync(entries, ct);

    private static string GetAutoSubMode(byte sub) => sub switch
    {
        1 => "AUTO.READY",
        2 => "AUTO.TAKEOFF",
        3 => "AUTO.LOITER",
        4 => "AUTO.MISSION",
        5 => "AUTO.RTL",
        6 => "AUTO.LAND",
        8 => "AUTO.FOLLOW",
        9 => "AUTO.PRECLAND",
        _ => $"AUTO({sub})"
    };

    private static uint Encode(byte main, byte sub) =>
        (uint)((main << 16) | (sub << 24));

    public static readonly IReadOnlyList<string> Px4CopterModes =
    [
        "MANUAL", "STABILIZED", "ACRO", "ALTCTL", "POSCTL", "OFFBOARD",
        "MISSION", "HOLD", "RTL", "LAND", "TAKEOFF", "FOLLOW"
    ];

    public static readonly IReadOnlyList<string> Px4RoverModes =
    [
        "MANUAL", "ACRO", "STABILIZED", "POSCTL", "MISSION", "HOLD", "RTL"
    ];
}
