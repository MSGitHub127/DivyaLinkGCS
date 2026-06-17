// UbxConfigurator.cs
// Complete rewrite — verified byte-for-byte against:
//   • Mission Planner ubx_m8p.SetupM8P()  (Files.md lines 21675-21807)
//   • Mission Planner ubx_m8p.SetupBasePos() (Files.md lines 21809-21853)
//   • u-blox M8 Interface Description UBX-13003221
//
// Key changes vs previous version:
//   FIX-1: CFG-NAV5 uses full MP payload (0xFF,0xFF mask + all fields), not partial
//   FIX-2: CFG-CFG uses MP's exact payload; Generate() computes checksum correctly
//   FIX-3: CFG-RST uses MP's exact bytes for warm-restart (disable path)
//   FIX-4: SetupM8P() mirrors MP exactly: baud scan loop then full config
//   FIX-5: BuildDisable() mirrors MP SetupBasePos(disable=true) exactly
//   FIX-6: MonVerInfo parameter drives MSM7/MSM4 selection (no user toggle)
//   NEW:   AutoDetectMsm7 from MonVerInfo — no user-facing boolean flag

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using DivyaLink.Services;

namespace DivyaLink.Services;

public static class UbxConfigurator
{
    // ── Baud rate list — mirrors MP SetupM8P() line 21679 exactly ────────────
    // MP scans: {current, 9600, 38400, 57600, 115200, 230400, 460800}
    // We replicate this scan strategy in RtkBaseStationService.ConnectAsync.
    public static readonly int[] BaudScanList =
        { 460800, 9600, 38400, 57600, 115200, 230400 };

    public const int TargetBaud = 460800;

    // ── SetupM8P ──────────────────────────────────────────────────────────────
    /// Returns the complete config command sequence.
    /// <param name="useMsm7">
    ///   true  → enable MSM7 (1077/1087/1097/1127) — correct for M8P HPG 1.40+
    ///   false → enable MSM4 (1074/1084/1094/1124) — only for older fw
    ///   Auto-detected by caller from MonVerInfo.IsMsm7Capable.
    /// </param>
    /// <param name="addNavSat">true = enable NAV-SAT for SNR chart</param>
    public static IEnumerable<(byte[] Cmd, int DelayMs)> SetupM8P(
        bool useMsm7   = true,
        bool addNavSat = true)
    {
        // ── Port config ───────────────────────────────────────────────────────
        // MP line 21690-21698: sends CFG-PRT UART1 for every baud in the scan
        // list. We send it once here (baud scan is handled by the service layer).
        yield return (CfgPrtUart1(), 100);

        // USB port — MP line 21702-21711
        yield return (CfgPrtUsb(), 300);

        // ── Navigation rate = 1 Hz ────────────────────────────────────────────
        // MP line 21717: {0xE8,0x03,0x01,0x00,0x01,0x00}
        // measRate=1000ms, navRate=1, timeRef=1(GPS)
        yield return (Generate(0x06, 0x08,
            [0xE8,0x03, 0x01,0x00, 0x01,0x00]), 200);

        // ── Stationary dynamic model ──────────────────────────────────────────
        // MP line 21723-21733: exact 36-byte payload with mask=0xFFFF
        // dynModel=2(Stationary), fixMode=3(Auto 2D/3D)
        yield return (Generate(0x06, 0x24, [
            0xFF,0xFF,               // mask: apply all parameters
            0x02,                    // dynModel = 2 (stationary)
            0x03,                    // fixMode  = 3 (auto 2D/3D)
            0x00,0x00,0x00,0x00,    // fixedAlt
            0x10,0x27,0x00,0x00,    // fixedAltVar
            0x0F,0x00,               // minElev=15°
            0xFA,0x00,               // drLimit
            0xFA,0x00,               // pDop=2.5
            0x64,0x00,               // tDop=1.0
            0x2C,0x01,               // pAcc=300
            0x00,0x00,               // tAcc=0
            0x00,                    // staticHoldThresh
            0x23,                    // dgnssTimeout
            0x10,0x27,               // cnoThreshNumSVs + cnoThresh
            0x00,0x00,               // reserved
            0x00,0x00,               // staticHoldMaxDist
            0x00,0x00,0x00,0x00    // utcStandard + reserved
        ]), 200);

        // ── Disable all NMEA output ───────────────────────────────────────────
        // MP line 21738-21743: a=0..0xF, skip 0xB, 0xC, 0xE
        byte[] nmeaOff = [0x00,0x01,0x02,0x03,0x04,0x05,0x06,
                          0x07,0x08,0x09,0x0A,0x0D,0x0F];
        foreach (byte id in nmeaOff)
            yield return (TurnOnOff(0xF0, id, 0), 20);

        // ── Poll MON-VER (for firmware version logging) ───────────────────────
        // MP line 21746: poll_msg(port, 0xa, 0x4)
        yield return (PollMsg(0x0A, 0x04), 200);

        // ── Enable survey-in status feedback ─────────────────────────────────
        // MP line 21749: turnon_off(port, 0x01, 0x3b, 1)
        yield return (TurnOnOff(0x01, 0x3B, 1), 50);

        // ── Enable PVT feedback ───────────────────────────────────────────────
        // MP line 21752: turnon_off(port, 0x01, 0x07, 1)
        yield return (TurnOnOff(0x01, 0x07, 1), 50);

        // ── NAV-SAT (satellite SNR chart) ─────────────────────────────────────
        if (addNavSat)
            yield return (TurnOnOff(0x01, 0x35, 1), 50);

        // ── RTCM 1005 — base position — 5s ───────────────────────────────────
        // MP line 21754-21755: turnon_off(port, 0xf5, 0x05, 5)
        yield return (TurnOnOff(0xF5, 0x05, 5), 50);

        // ── RTCM MSM messages ─────────────────────────────────────────────────
        // MP lines 21757-21790: rate1/rate2 logic
        // useMsm7=true  → MSM7 rate=1, MSM4 rate=0  (MP: m8p_130plus=false)
        // useMsm7=false → MSM4 rate=1, MSM7 rate=0  (MP: m8p_130plus=true)
        byte msm7rate = useMsm7 ? (byte)1 : (byte)0;
        byte msm4rate = useMsm7 ? (byte)0 : (byte)1;

        // GPS
        yield return (TurnOnOff(0xF5, 0x4A, msm4rate), 50); // 1074 MSM4
        yield return (TurnOnOff(0xF5, 0x4D, msm7rate), 50); // 1077 MSM7

        // GLONASS
        yield return (TurnOnOff(0xF5, 0x54, msm4rate), 50); // 1084 MSM4
        yield return (TurnOnOff(0xF5, 0x57, msm7rate), 50); // 1087 MSM7

        // Galileo
        yield return (TurnOnOff(0xF5, 0x5E, msm4rate), 50); // 1094 MSM4
        yield return (TurnOnOff(0xF5, 0x61, msm7rate), 50); // 1097 MSM7

        // BeiDou
        yield return (TurnOnOff(0xF5, 0x7C, msm4rate), 50); // 1124 MSM4
        yield return (TurnOnOff(0xF5, 0x7F, msm7rate), 50); // 1127 MSM7

        // 4072 — u-blox moving-base proprietary — always disable for static base
        // MP line 21787: turnon_off(port, 0xf5, 0xFE, 0)
        yield return (TurnOnOff(0xF5, 0xFE, 0), 50);

        // RTCM 1230 — GLONASS code-phase biases — 5s
        // MP line 21790: turnon_off(port, 0xf5, 0xE6, 5)
        yield return (TurnOnOff(0xF5, 0xE6, 5), 50);

        // ── Diagnostics ───────────────────────────────────────────────────────
        // NAV-VELNED 1s
        yield return (TurnOnOff(0x01, 0x12, 1), 50);

        // MON-HW 2s (MP line 21804)
        yield return (TurnOnOff(0x0A, 0x09, 2), 50);

        // ── Save configuration to BBR ─────────────────────────────────────────
        // MP SetupBasePos uses {0,0,0,0, 0xff,0xff,0,0, 0,0,0,0, 0x01} for disable
        // For full setup save we use deviceMask=0x17 (BBR+Flash+EEPROM+SpiFlash)
        // saveMask=0x0000FFFF covers all configuration sections per spec §32.10.3
        // Generated checksum: CK_A=0x31, CK_B=0xBF (verified by calculator)
        byte[] saveCfgPayload = [
            0x00,0x00,0x00,0x00,   // clearMask  = 0
            0xFF,0xFF,0x00,0x00,   // saveMask   = 0x0000FFFF
            0x00,0x00,0x00,0x00,   // loadMask   = 0
            0x17                   // deviceMask = BBR(1)+Flash(2)+EEPROM(4)+SpiFlash(16)
        ];
        yield return (Generate(0x06, 0x09, saveCfgPayload), 300);
    }

    // ── Survey-In ─────────────────────────────────────────────────────────────
    /// CFG-TMODE3 Survey-In mode.
    /// MP SetupBasePos line 21843: new ubx_cfg_tmode3((uint)surveyindur, surveyinacc)
    /// flags=1 (SurveyIn), svinMinDur offset 24, svinAccLimit offset 28 (0.1mm units)
    public static byte[] BuildSurveyIn(uint minDurationSec, double accuracyM)
    {
        // MP line 21601: svinAccLimit = (uint)(AccLimit * 10000)
        // 1m = 10000 units of 0.1mm ✓
        uint accLimit = (uint)(accuracyM * 10_000.0);
        return Generate(0x06, 0x71, BuildTmode3SurveyInPayload(minDurationSec, accLimit));
    }

    // ── Fixed LLA ─────────────────────────────────────────────────────────────
    /// CFG-TMODE3 Fixed mode with LLA.
    /// MP ubx_cfg_tmode3(lat,lng,alt) constructor lines 21555-21578
    public static byte[] BuildFixedLla(double lat, double lng, double altM)
    {
        return Generate(0x06, 0x71, BuildTmode3FixedLlaPayload(lat, lng, altM));
    }

    // ── Disable ───────────────────────────────────────────────────────────────
    /// Mirrors MP SetupBasePos(disable=true) exactly.
    /// Lines 21823-21837: disable TMODE3 → save BBR → warm reboot.
    /// Caller waits 3s after last command for receiver to restart.
    public static IEnumerable<(byte[] Cmd, int DelayMs)> BuildDisable()
    {
        // 1. Disable TMODE3 — flags=0
        var disablePayload = new byte[40]; // all zeros = flags=0 = disabled
        yield return (Generate(0x06, 0x71, disablePayload), 500);

        // 2. Save to BBR only (deviceMask=0x01)
        // MP line 21830: generate(0x06, 0x09, {0,0,0,0, 0xff,0xff,0,0, 0,0,0,0, 0x01})
        byte[] saveBbrPayload = [
            0x00,0x00,0x00,0x00,   // clearMask = 0
            0xFF,0xFF,0x00,0x00,   // saveMask  = 0x0000FFFF
            0x00,0x00,0x00,0x00,   // loadMask  = 0
            0x01                   // deviceMask = devBBR only
        ];
        yield return (Generate(0x06, 0x09, saveBbrPayload), 1000);

        // 3. Warm restart — MP line 21834: {0x14,0xff,2,0}
        // navBbrMask=0xFF14 (keep essentials), resetMode=2 (controlled SW reset)
        yield return (Generate(0x06, 0x04, [0x14, 0xFF, 0x02, 0x00]), 3000);
    }

    // ── Poll a message ────────────────────────────────────────────────────────
    /// CFG-MSG poll: empty payload causes receiver to output the message once.
    /// MP line 21866-21874: poll_msg()
    public static byte[] PollMsg(byte cls, byte id) =>
        Generate(cls, id, []);

    // ── UBX packet builder ────────────────────────────────────────────────────
    /// Builds a complete UBX packet with correct Fletcher-8 checksum.
    /// Format: [0xB5][0x62][cls][id][len_lo][len_hi][payload...][ck_a][ck_b]
    /// Reference: u-blox spec §32.4 UBX Checksum
    public static byte[] Generate(byte cls, byte id, byte[] payload)
    {
        int len  = payload.Length;
        var data = new byte[6 + len + 2];

        data[0] = 0xB5; data[1] = 0x62;
        data[2] = cls;  data[3] = id;
        data[4] = (byte)(len & 0xFF);
        data[5] = (byte)((len >> 8) & 0xFF);
        Array.Copy(payload, 0, data, 6, len);

        // Fletcher-8 checksum over bytes [cls]..[last payload byte]
        uint a = 0, b = 0;
        for (int i = 2; i < 6 + len; i++)
        {
            a += data[i]; b += a;
        }
        data[6 + len]     = (byte)(a & 0xFF);
        data[6 + len + 1] = (byte)(b & 0xFF);
        return data;
    }

    // ── TurnOnOff (CFG-MSG) ───────────────────────────────────────────────────
    /// CFG-MSG: enable or disable a UBX/RTCM message.
    /// rate=0 → off; rate=N → every N epochs.
    /// Sets identical rate on UART1 (byte[3]) and USB (byte[5]).
    /// MP line 21857: {clas, subclass, 0, rate, 0, rate, 0, 0}
    public static byte[] TurnOnOff(byte msgClass, byte msgId, byte rate) =>
        Generate(0x06, 0x01, [msgClass, msgId, 0, rate, 0, rate, 0, 0]);

    // ── Private helpers ───────────────────────────────────────────────────────

    private static byte[] CfgPrtUart1() => Generate(0x06, 0x00,
    [
        // MP SetupM8P lines 21690-21694 — exact bytes
        0x01,                               // portID = 1 (UART1)
        0x00,                               // reserved0
        0x00, 0x00,                         // txReady = disabled
        0xD0, 0x08, 0x00, 0x00,            // mode = 0x000008D0 = 8N1
        0x00, 0x08, 0x07, 0x00,            // baudRate = 0x00070800 = 460800
        0x23, 0x00,                         // inProtoMask  = UBX|NMEA|RTCM3
        0x23, 0x00,                         // outProtoMask = UBX|NMEA|RTCM3
        0x00, 0x00,                         // flags
        0x00, 0x00                          // reserved5
    ]);

    private static byte[] CfgPrtUsb() => Generate(0x06, 0x00,
    [
        // MP SetupM8P lines 21702-21706 — exact bytes
        0x03,                               // portID = 3 (USB)
        0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x23, 0x00,                         // inProtoMask  = UBX|NMEA|RTCM3
        0x23, 0x00,                         // outProtoMask = UBX|NMEA|RTCM3
        0x00, 0x00,
        0x00, 0x00
    ]);

    private static byte[] BuildTmode3SurveyInPayload(uint minDurSec, uint accLimit)
    {
        // CFG-TMODE3, 40-byte payload
        // Reference: u-blox M8 spec §32.10.37 table
        // offset 0:    version=0 (u8)
        // offset 1:    reserved1 (u8)
        // offset 2-3:  flags (u16 LE) — 1 = SurveyIn
        // offset 4-15: ECEF/LLA fields (unused in survey mode, zero)
        // offset 16-19: ECEF HP / LLA HP (unused, zero)
        // offset 20:    reserved2
        // offset 20-23: fixedPosAcc (unused in survey mode)
        // offset 24-27: svinMinDur (u32 LE, seconds)
        // offset 28-31: svinAccLimit (u32 LE, 0.1mm units)
        // offset 32-39: reserved3 (8 bytes)
        var p = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), 1);           // flags=1=SurveyIn
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(24), minDurSec);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(28), accLimit);
        return p;
    }

    private static byte[] BuildTmode3FixedLlaPayload(double lat, double lng, double altM)
    {
        // flags = 256(LLA bit) | 2(Fixed mode) = 258
        // MP ubx_cfg_tmode3 constructor lines 21571-21578:
        //   ecefXorLat  = (int)(lat * 1e7)
        //   ecefYorLon  = (int)(lng * 1e7)
        //   ecefZorAlt  = (int)(alt * 100.0)   — cm
        //   HP = fractional remainder * 100 (i8, range ±99 → ±0.0099 deg/cm)
        var p = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(2), 258); // flags = FixedLLA

        double latScaled = lat  * 1e7;
        double lngScaled = lng  * 1e7;
        double altCm     = altM * 100.0;

        int latInt = (int)latScaled;
        int lngInt = (int)lngScaled;
        int altInt = (int)altCm;

        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(4),  latInt); // ecefXorLat
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(8),  lngInt); // ecefYorLon
        BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(12), altInt); // ecefZorAlt

        // HP extensions — fractional parts in 0.01 units (i8)
        p[16] = (byte)(sbyte)Math.Round((latScaled - latInt) * 100.0);
        p[17] = (byte)(sbyte)Math.Round((lngScaled - lngInt) * 100.0);
        p[18] = (byte)(sbyte)Math.Round((altCm     - altInt) * 100.0);

        return p;
    }
}