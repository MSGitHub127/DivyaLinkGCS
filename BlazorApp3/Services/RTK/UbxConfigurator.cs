// Services/UbxConfigurator.cs
// Static UBX command builder — a pure port of Mission Planner's ubx_m8p.cs.
// Produces (byte[] command, int delayMs) tuples.
// All serial I/O and timing is the caller's responsibility (RtkBaseStationService).
// This makes the builder fully testable without a physical COM port.
//
// REFERENCES:
//   MissionPlanner/ExtLibs/Ubx/ubx_m8p.cs — SetupM8P(), SetupBasePos()
//   UBX-CFG-TMODE3 payload verified against u-blox M8P/F9P Interface Description

using System.Buffers.Binary;

namespace BlazorApp3.Services;

public static class UbxConfigurator
{
    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the complete autoconfig command sequence for an M8P or F9P receiver.
    /// Execute commands in order, honoring DelayMs between each.
    /// </summary>
    /// <param name="m8p130Plus">
    /// false = use MSM7 messages (1077/1087/1097/1127) — default for M8P and F9P.
    /// true  = use MSM4 messages (1074/1084/1094/1124) — older M8P firmware.
    /// </param>
    /// <param name="addNavSat">Always true in DivyaLink — enables satellite bar chart.</param>
    public static IEnumerable<(byte[] Command, int DelayMs)> SetupM8P(
    bool m8p130Plus = false,
    bool addNavSat = true)
{
    // UART1 config
    yield return (Cfg_Prt_Uart1(), 100);

    // USB config
    yield return (Cfg_Prt_Usb(), 300);

    // Navigation rate = 1Hz
    yield return (
        Generate(
            0x06,
            0x08,
            [
                0xE8,0x03,
                0x01,0x00,
                0x01,0x00
            ]),
        100);

    // Stationary model
    var nav5 = new byte[36];

    nav5[0] = 0x01;
    nav5[1] = 0x00;
    nav5[2] = 0x02;

    yield return (
        Generate(
            0x06,
            0x24,
            nav5),
        100);

    //--------------------------------------------------------
    // Disable all NMEA output
    //--------------------------------------------------------

    byte[] nmeaIds =
    [
        0x00,
        0x01,
        0x02,
        0x03,
        0x04,
        0x05,
        0x06,
        0x07,
        0x08,
        0x09,
        0x0A,
        0x0D,
        0x0F
    ];

    foreach(var id in nmeaIds)
    {
        yield return (
            TurnOnOff(
                0xF0,
                id,
                0),
            20);
    }

    //--------------------------------------------------------
    // Required Mission Planner messages
    //--------------------------------------------------------

    yield return (
        TurnOnOff(
            0x01,
            0x3B,
            1),
        50);

    yield return (
        TurnOnOff(
            0x01,
            0x07,
            1),
        50);

    if(addNavSat)
    {
        yield return (
            TurnOnOff(
                0x01,
                0x35,
                1),
            50);
    }

    //--------------------------------------------------------
    // RTCM output
    //--------------------------------------------------------

    byte msm4 = m8p130Plus ? (byte)1 : (byte)0;
    byte msm7 = m8p130Plus ? (byte)0 : (byte)1;

    // Base position
    yield return (
        TurnOnOff(
            0xF5,
            0x05,
            5),
        50);

    // GPS
    yield return (
        TurnOnOff(
            0xF5,
            0x4A,
            msm4),
        50);

    yield return (
        TurnOnOff(
            0xF5,
            0x4D,
            msm7),
        50);

    // GLONASS
    yield return (
        TurnOnOff(
            0xF5,
            0x54,
            msm4),
        50);

    yield return (
        TurnOnOff(
            0xF5,
            0x57,
            msm7),
        50);

    // Galileo
    yield return (
        TurnOnOff(
            0xF5,
            0x5E,
            msm4),
        50);

    yield return (
        TurnOnOff(
            0xF5,
            0x61,
            msm7),
        50);

    // BeiDou
    yield return (
        TurnOnOff(
            0xF5,
            0x7C,
            msm4),
        50);

    yield return (
        TurnOnOff(
            0xF5,
            0x7F,
            msm7),
        50);

    // GLONASS biases
    yield return (
        TurnOnOff(
            0xF5,
            0xE6,
            5),
        50);

    //--------------------------------------------------------
    // Diagnostics only
    //--------------------------------------------------------

    yield return (
        TurnOnOff(
            0x0A,
            0x09,
            1),
        50);
}
    /// <summary>
    /// Builds a UBX-CFG-TMODE3 Survey-In command.
    /// The receiver will start accumulating position fixes until BOTH conditions are met:
    ///   - elapsed time ≥ minDurationSec
    ///   - estimated accuracy ≤ accuracyM
    /// Only then does the u-blox hardware begin emitting RTCM corrections.
    /// </summary>
    public static byte[] BuildSurveyIn(uint minDurationSec, double accuracyM)
    {
        // svinAccLimit is stored in 0.1mm units (i.e., multiply metres by 10,000)
        uint svinAccLimit = (uint)(accuracyM * 10_000.0);
        return Generate(0x06, 0x71, BuildTmode3SurveyIn(minDurationSec, svinAccLimit));
    }

    /// <summary>
    /// Builds a UBX-CFG-TMODE3 Fixed Mode (LLA) command.
    /// Use when loading a saved base profile — receiver outputs RTCM immediately.
    /// </summary>
    public static byte[] BuildFixedLla(double lat, double lng, double altM)
    {
        return Generate(0x06, 0x71, BuildTmode3FixedLla(lat, lng, altM));
    }

    /// <summary>
    /// Builds the three-command sequence to disable TMODE3, save to battery-backed RAM,
    /// and cold-restart the receiver. Used when removing a saved fixed position.
    /// The caller should wait 3 seconds after sending the restart command.
    /// </summary>
    public static IEnumerable<(byte[] Command, int DelayMs)> BuildDisable()
    {
        // 1. Disable TMODE3 (flags=0)
        var disablePayload = new byte[40]; // all zeros → flags=0
        yield return (Generate(0x06, 0x71, disablePayload), 500);

        // 2. Save configuration to BBR (battery-backed RAM)
        // CFG-CFG: clearMask=0, saveMask=0xFFFF, loadMask=0, deviceMask=0x17 (BBR+Flash)
        byte[] saveBbr = [0x00,0x00,0x00,0x00, 0xFF,0xFF,0x00,0x00, 0x00,0x00,0x00,0x00, 0x17];
        yield return (Generate(0x06, 0x09, saveBbr), 300);

        // 3. Cold start (CFG-RST: navBbrMask=0xFFFF=hot, resetMode=0x01=forced)
        // 0xFFFF = clear all BBR + hot start; 0x01 = controlled software reset
        byte[] rst = [0xFF, 0xFF, 0x01, 0x00];
        yield return (Generate(0x06, 0x04, rst), 3000); // 3s for receiver restart
    }

    // ── Packet builder ─────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a complete UBX packet with correct Fletcher-8 checksum.
    /// Format: [0xB5][0x62][cls][id][len_lo][len_hi][payload...][ck_a][ck_b]
    /// </summary>
    public static byte[] Generate(byte cls, byte id, byte[] payload)
    {
        int len  = payload.Length;
        var data = new byte[6 + len + 2];
        data[0] = 0xB5; data[1] = 0x62;
        data[2] = cls;  data[3] = id;
        data[4] = (byte)(len & 0xFF); data[5] = (byte)((len >> 8) & 0xFF);
        Array.Copy(payload, 0, data, 6, len);

        uint a = 0, b = 0;
        for (int i = 2; i < 6 + len; i++) { a += data[i]; b += a; }
        data[6 + len]     = (byte)(a & 0xFF);
        data[6 + len + 1] = (byte)(b & 0xFF);
        return data;
    }

    /// <summary>
    /// Builds UBX-CFG-MSG (0x06/0x01) to enable or disable a message at rate N.
    /// rate=0 → off; rate=1 → every 1Hz; rate=N → every N seconds.
    /// Sets the same rate on both UART1 (byte[3]) and USB (byte[5]).
    /// </summary>
    public static byte[] TurnOnOff(byte msgClass, byte msgId, byte rate) =>
        Generate(0x06, 0x01, [msgClass, msgId, 0, rate, 0, rate, 0, 0]);

    // ── Private helpers ────────────────────────────────────────────────────────

    private static byte[] Cfg_Prt_Uart1() => Generate(0x06, 0x00,
    [
        0x01,                               // portID = 1 (UART1)
        0x00,                               // reserved0
        0x00, 0x00,                         // txReady
        0xD0, 0x08, 0x00, 0x00,            // mode: 8 data bits, no parity, 1 stop
        0x00, 0x08, 0x07, 0x00,            // baudRate = 460800 (0x00070800 LE)
        0x23, 0x00,                         // inProtoMask = 0x0023 (UBX|NMEA|RTCM3)
        0x23, 0x00,                         // outProtoMask = 0x0023
        0x00, 0x00,                         // flags
        0x00, 0x00                          // reserved5
    ]);

    private static byte[] Cfg_Prt_Usb() => Generate(0x06, 0x00,
    [
        0x03,                               // portID = 3 (USB)
        0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x23, 0x00,                         // inProtoMask = 0x0023
        0x23, 0x00,                         // outProtoMask = 0x0023
        0x00, 0x00,
        0x00, 0x00
    ]);

    private static byte[] BuildTmode3SurveyIn(uint minDurSec, uint accLimit)
    {
        // CFG-TMODE3 payload, 40 bytes
        // Offset 2-3: flags = 1 (Survey-In mode)
        // Offset 24-27: svinMinDur (uint32 LE, seconds)
        // Offset 28-31: svinAccLimit (uint32 LE, 0.1mm units)
        var p = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), 1);          // flags = surveyIn
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(24), minDurSec);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(28), accLimit);
        return p;
    }

    private static byte[] BuildTmode3FixedLla(double lat, double lng, double altM)
    {
        // flags = 256 (LLA) | 2 (Fixed) = 258
        // ecefXorLat = lat * 1e7 (integer degrees * 1e7)
        // ecefYorLon = lng * 1e7
        // ecefZorAlt = alt * 100 (cm)
        // High-precision extension bytes capture the fractional part
        var p = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), 258); // flags

        double latScaled = lat * 1e7;
        double lngScaled = lng * 1e7;
        double altCm     = altM * 100.0;

        int latInt = (int)latScaled;
        int lngInt = (int)lngScaled;
        int altInt = (int)altCm;

        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4),  latInt);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(8),  lngInt);
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(12), altInt);

        // HP extensions: fractional remainder in 0.01-unit steps
        p[16] = (byte)((sbyte)Math.Round((latScaled - latInt) * 100));
        p[17] = (byte)((sbyte)Math.Round((lngScaled - lngInt) * 100));
        p[18] = (byte)((sbyte)Math.Round((altCm     - altInt) * 100));

        return p;
    }
}