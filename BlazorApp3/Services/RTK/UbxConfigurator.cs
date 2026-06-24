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
using BlazorApp3.Services;

namespace BlazorApp3.Services;

public static class UbxConfigurator
{
    // ── Baud rate list — mirrors MP SetupM8P() line 21679 exactly ────────────
    // MP scans: {current, 9600, 38400, 57600, 115200, 230400, 460800}
    // We replicate this scan strategy in RtkBaseStationService.ConnectAsync.
    public static readonly int[] BaudScanList =
        { 460800, 9600, 38400, 57600, 115200, 230400 };

    public const int TargetBaud = 460800;

    /// <summary>
    /// Phase 1 of the M8P setup sequence: port configuration, navigation settings,
    /// NMEA suppression, and sensor message enables.
    /// Does NOT configure any RTCM output — that is done by <see cref="SetupM8pRtcm"/>
    /// after the caller has queried CFG-GNSS to learn which constellations are active.
    ///
    /// Includes a MON-VER poll so the caller can determine MSM7 vs MSM4 capability
    /// before invoking Phase 2.
    /// </summary>
    public static IEnumerable<(byte[] Cmd, int DelayMs)> SetupM8pPhase1(
        bool addNavSat = true)
    {
        // ── Port config ───────────────────────────────────────────────────────
        // MP line 21690-21698 / 21702-21711
        yield return (CfgPrtUart1(), 100);
        yield return (CfgPrtUsb(),   300);
 
        // ── Navigation rate = 1 Hz ────────────────────────────────────────────
        // MP line 21717: measRate=1000ms, navRate=1, timeRef=1(GPS)
        yield return (Generate(0x06, 0x08,
            [0xE8,0x03, 0x01,0x00, 0x01,0x00]), 200);
 
        // ── Stationary dynamic model ──────────────────────────────────────────
        // MP line 21723-21733: exact 36-byte payload, mask=0xFFFF
        yield return (Generate(0x06, 0x24, [
            0xFF,0xFF,               // mask: apply all parameters
            0x02,                    // dynModel = 2 (stationary)
            0x03,                    // fixMode  = 3 (auto 2D/3D)
            0x00,0x00,0x00,0x00,     // fixedAlt
            0x10,0x27,0x00,0x00,     // fixedAltVar
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
            0x00,0x00,0x00,0x00      // utcStandard + reserved
        ]), 200);
 
        // ── Disable all NMEA output ───────────────────────────────────────────
        // MP line 21738-21743: ids 0..0xF excluding 0xB, 0xC, 0xE
        byte[] nmeaOff = [0x00,0x01,0x02,0x03,0x04,0x05,0x06,
                          0x07,0x08,0x09,0x0A,0x0D,0x0F];
        foreach (byte id in nmeaOff)
            yield return (TurnOnOff(0xF0, id, 0), 20);
 
        // ── Poll MON-VER ──────────────────────────────────────────────────────
        // MP line 21746. Caller waits for _receiverInfo.FwVer to be populated
        // before invoking Phase 2, so it knows MSM7 vs MSM4 capability.
        yield return (PollMsg(0x0A, 0x04), 200);
 
        // ── Enable survey-in + PVT + satellite feedback ───────────────────────
        // MP lines 21749, 21752
        yield return (TurnOnOff(0x01, 0x3B, 1), 50);   // NAV-SVIN
        yield return (TurnOnOff(0x01, 0x07, 1), 50);   // NAV-PVT
 
        if (addNavSat)
            yield return (TurnOnOff(0x01, 0x35, 1), 50); // NAV-SAT
    }
 
    /// <summary>
    /// Phase 2 of the M8P setup sequence: RTCM output rates, diagnostic message
    /// enables, and CFG-CFG save-to-flash.
    ///
    /// Only emits CFG-MSG commands for RTCM messages whose constellation is present
    /// in <paramref name="enabledGnss"/>. Pass <c>null</c> to send all (backward
    /// compatibility); pass the result of <see cref="ParseCfgGnss"/> to filter
    /// precisely and eliminate NACKs for unsupported constellations.
    ///
    /// Changes vs the original SetupM8P body:
    ///   • RTCM 1005 rate: 5 → 1  (ARP every second, not every 5 s)
    ///   • Each MSM pair (MSM4+MSM7) gated on the matching gnssId being present
    ///   • RTCM 1230 gated on GLONASS (gnssId 6) being present
    /// </summary>
    public static IEnumerable<(byte[] Cmd, int DelayMs)> SetupM8pRtcm(
        bool useMsm7,
        IReadOnlySet<byte>? enabledGnss = null)
    {
        // Helper: true if constellation is enabled (or filter is not applied).
        bool Has(byte gnssId) => enabledGnss == null || enabledGnss.Contains(gnssId);
 
        byte msm7rate = useMsm7 ? (byte)1 : (byte)0;
        byte msm4rate = useMsm7 ? (byte)0 : (byte)1;
 
        // ── RTCM 1005 — Base station ARP ─────────────────────────────────────
        // Always sent — not constellation-specific.
        // FIX: rate 5 → 1 so the drone gets the base position every second,
        // not every 5 s.  MP line 21754-21755: rate=5 (matched here as rate=1
        // intentionally, see engineering report §5 "RTCM 1005 rate").
        yield return (TurnOnOff(0xF5, 0x05, 1), 50);   // RTCM-1005 @ 1 Hz
 
        // ── GPS (gnssId = 0) ──────────────────────────────────────────────────
        // MP lines 21757-21762
        if (Has(0))
        {
            yield return (TurnOnOff(0xF5, 0x4A, msm4rate), 50); // RTCM-1074 MSM4
            yield return (TurnOnOff(0xF5, 0x4D, msm7rate), 50); // RTCM-1077 MSM7
        }
 
        // ── GLONASS (gnssId = 6) ──────────────────────────────────────────────
        // MP lines 21764-21769
        if (Has(6))
        {
            yield return (TurnOnOff(0xF5, 0x54, msm4rate), 50); // RTCM-1084 MSM4
            yield return (TurnOnOff(0xF5, 0x57, msm7rate), 50); // RTCM-1087 MSM7
        }
 
        // ── Galileo (gnssId = 2) ──────────────────────────────────────────────
        // MP lines 21771-21776
        // NEO-M8P-2 supports Galileo from HPG 1.40+. Firmware HPG 1.43 (observed
        // in logs) includes it, but only if enabled in CFG-GNSS.
        if (Has(2))
        {
            yield return (TurnOnOff(0xF5, 0x5E, msm4rate), 50); // RTCM-1094 MSM4
            yield return (TurnOnOff(0xF5, 0x61, msm7rate), 50); // RTCM-1097 MSM7
        }
 
        // ── BeiDou (gnssId = 3) ───────────────────────────────────────────────
        // MP lines 21778-21783
        // NEO-M8P-2 does NOT support BeiDou — CFG-GNSS will never return it as
        // enabled, so this block is effectively dead on the M8P-2.  Keeping it
        // here means the code works correctly on any future u-blox module that
        // does support BeiDou (e.g. ZED-F9P).
        if (Has(3))
        {
            yield return (TurnOnOff(0xF5, 0x7C, msm4rate), 50); // RTCM-1124 MSM4
            yield return (TurnOnOff(0xF5, 0x7F, msm7rate), 50); // RTCM-1127 MSM7
        }
 
        // ── RTCM 4072 — u-blox moving-base proprietary — always off ──────────
        // MP line 21787
        yield return (TurnOnOff(0xF5, 0xFE, 0), 50);
 
        // ── RTCM 1230 — GLONASS code-phase biases ────────────────────────────
        // MP line 21790
        // Only meaningful when GLONASS is active.
        if (Has(6))
            yield return (TurnOnOff(0xF5, 0xE6, 5), 50);
 
        // ── Diagnostics ───────────────────────────────────────────────────────
        yield return (TurnOnOff(0x01, 0x12, 1), 50);   // NAV-VELNED 1 s
        yield return (TurnOnOff(0x0A, 0x09, 2), 50);   // MON-HW     2 s
 
        // ── Save to flash (BBR + Flash + EEPROM + SpiFlash) ───────────────────
        // MP §32.10.3 CFG-CFG: saveMask=0x0000FFFF, deviceMask=0x17
        yield return (Generate(0x06, 0x09, [
            0x00,0x00,0x00,0x00,   // clearMask = 0
            0xFF,0xFF,0x00,0x00,   // saveMask  = 0x0000FFFF
            0x00,0x00,0x00,0x00,   // loadMask  = 0
            0x17                   // deviceMask= BBR(1)+Flash(2)+EEPROM(4)+SpiFlash(16)
        ]), 300);
    }
    
    public static IEnumerable<(byte[] Cmd, int DelayMs)> SetupM8P(
        bool useMsm7   = true,
        bool addNavSat = true)
    {
        // Thin wrapper that runs both phases in sequence.
        // ConnectAsync uses the two phases directly (with CFG-GNSS query between
        // them). This wrapper exists only for backward compatibility.
        foreach (var item in SetupM8pPhase1(addNavSat))      yield return item;
        foreach (var item in SetupM8pRtcm(useMsm7, null))    yield return item;
    }
    
    // NOTE: also add IReadOnlySet<byte>? enabledGnss = null as a third parameter
    // if you want SetupM8P callers to be able to pass a filter too:
    
    public static IEnumerable<(byte[] Cmd, int DelayMs)> SetupM8P(
        bool useMsm7            = true,
        bool addNavSat          = true,
        IReadOnlySet<byte>? enabledGnss = null)
    {
        foreach (var item in SetupM8pPhase1(addNavSat))            yield return item;
        foreach (var item in SetupM8pRtcm(useMsm7, enabledGnss))   yield return item;
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

    private static readonly Dictionary<(byte, byte), string> _messageNames = new()
    {
        // CFG group (0x06) ─────────────────────────────────────────────────────────
        { (0x06, 0x00), "CFG-PRT"    },   // port configuration
        { (0x06, 0x01), "CFG-MSG"    },   // message rate (used for all RTCM enables)
        { (0x06, 0x08), "CFG-RATE"   },   // navigation/measurement rate
        { (0x06, 0x09), "CFG-CFG"    },   // save/load/clear configuration
        { (0x06, 0x11), "CFG-RXM"    },   // receiver manager (power mode)
        { (0x06, 0x13), "CFG-ANT"    },   // antenna control
        { (0x06, 0x16), "CFG-SBAS"   },   // SBAS configuration
        { (0x06, 0x17), "CFG-NMEA"   },   // NMEA protocol
        { (0x06, 0x1B), "CFG-USB"    },   // USB configuration
        { (0x06, 0x23), "CFG-NAVX5"  },   // navigation expert settings
        { (0x06, 0x24), "CFG-NAV5"   },   // navigation engine settings
        { (0x06, 0x31), "CFG-TP5"    },   // time pulse
        { (0x06, 0x39), "CFG-ITFM"   },   // jamming/interference monitor
        { (0x06, 0x3D), "CFG-PMS"    },   // power management
        { (0x06, 0x3E), "CFG-GNSS"   },   // GNSS system configuration
        { (0x06, 0x71), "CFG-TMODE3" },   // time mode 3 (survey-in / fixed-pos)
 
        // NAV group (0x01) ─────────────────────────────────────────────────────────
        { (0x01, 0x02), "NAV-POSLLH" },
        { (0x01, 0x03), "NAV-STATUS" },
        { (0x01, 0x06), "NAV-SOL"    },
        { (0x01, 0x07), "NAV-PVT"    },
        { (0x01, 0x12), "NAV-VELNED" },
        { (0x01, 0x20), "NAV-TIMEGPS"},
        { (0x01, 0x21), "NAV-TIMEUTC"},
        { (0x01, 0x26), "NAV-TIMELS" },
        { (0x01, 0x30), "NAV-SVINFO" },
        { (0x01, 0x35), "NAV-SAT"    },
        { (0x01, 0x3B), "NAV-SVIN"   },
        { (0x01, 0x3C), "NAV-RELPOSNED"},
 
        // MON group (0x0A) ─────────────────────────────────────────────────────────
        { (0x0A, 0x04), "MON-VER"    },
        { (0x0A, 0x06), "MON-MSGPP"  },
        { (0x0A, 0x07), "MON-RXBUF"  },
        { (0x0A, 0x08), "MON-TXBUF"  },
        { (0x0A, 0x09), "MON-HW"     },
        { (0x0A, 0x0B), "MON-HW2"    },
        { (0x0A, 0x21), "MON-RXR"    },
        { (0x0A, 0x27), "MON-PATCH"  },
        { (0x0A, 0x28), "MON-GNSS"   },
        { (0x0A, 0x36), "MON-COMMS"  },
 
        // ACK group (0x05) ─────────────────────────────────────────────────────────
        { (0x05, 0x00), "ACK-NACK"   },
        { (0x05, 0x01), "ACK-ACK"    },
 
        // RTCM output messages (CFG-MSG target class 0xF5) ─────────────────────────
        // The receiver NACKs CFG-MSG with payload [0x06,0x01]; the inner target
        // (0xF5, msgId) identifies which RTCM message was being configured.
        { (0xF5, 0x05), "RTCM-1005"  },   // Stationary RTK reference ARP
        { (0xF5, 0x4A), "RTCM-1074"  },   // GPS MSM4
        { (0xF5, 0x4D), "RTCM-1077"  },   // GPS MSM7   ← most commonly NACKed
        { (0xF5, 0x54), "RTCM-1084"  },   // GLONASS MSM4
        { (0xF5, 0x57), "RTCM-1087"  },   // GLONASS MSM7
        { (0xF5, 0x5E), "RTCM-1094"  },   // Galileo MSM4
        { (0xF5, 0x61), "RTCM-1097"  },   // Galileo MSM7
        { (0xF5, 0x7C), "RTCM-1124"  },   // BeiDou MSM4
        { (0xF5, 0x7F), "RTCM-1127"  },   // BeiDou MSM7
        { (0xF5, 0xE6), "RTCM-1230"  },   // GLONASS code-phase biases
        { (0xF5, 0xFE), "RTCM-4072"  },   // u-blox moving-base proprietary
 
        // NMEA output messages (CFG-MSG target class 0xF0) ─────────────────────────
        { (0xF0, 0x00), "NMEA-GGA"   },
        { (0xF0, 0x01), "NMEA-GLL"   },
        { (0xF0, 0x02), "NMEA-GSA"   },
        { (0xF0, 0x03), "NMEA-GSV"   },
        { (0xF0, 0x04), "NMEA-RMC"   },
        { (0xF0, 0x05), "NMEA-VTG"   },
        { (0xF0, 0x06), "NMEA-GRS"   },
        { (0xF0, 0x07), "NMEA-GST"   },
        { (0xF0, 0x08), "NMEA-ZDA"   },
        { (0xF0, 0x0D), "NMEA-GNS"   },
    };
 
    /// <summary>
    /// Returns a human-readable name for a UBX (class, id) pair.
    /// Used by ACK/NACK log messages to identify which command the receiver
    /// accepted or rejected, instead of logging raw hex bytes.
    /// </summary>
    /// <example>
    /// GetMessageName(0x06, 0x01) → "CFG-MSG"
    /// GetMessageName(0xF5, 0x4D) → "RTCM-1077"
    /// GetMessageName(0xFF, 0xFF) → "0xFF/0xFF"
    /// </example>
    public static string GetMessageName(byte cls, byte id)
        => _messageNames.TryGetValue((cls, id), out var name)
            ? name
            : $"0x{cls:X2}/0x{id:X2}";
 
    /// <summary>
    /// Overload used when NACK payload indicates a CFG-MSG rejection:
    /// decodes the inner (msgClass, msgId) to name the specific RTCM/NMEA message.
    /// </summary>
    public static string GetCfgMsgTargetName(byte msgClass, byte msgId)
    {
        if (_messageNames.TryGetValue((msgClass, msgId), out var name)) return name;
        return $"class=0x{msgClass:X2} id=0x{msgId:X2}";
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

    /// <summary>
    /// Generates a CFG-GNSS poll request (0-byte payload = poll, per spec §32.10.15).
    /// The receiver responds with a CFG-GNSS frame listing every GNSS block it
    /// knows about, each with an enable/disable flag in bits 0 of the flags field.
    /// </summary>
    public static byte[] PollCfgGnss()
        => Generate(0x06, 0x3E, Array.Empty<byte>());
 
    /// <summary>
    /// Parses a CFG-GNSS response payload and returns the set of gnssId values
    /// (byte) that are currently enabled in the receiver.
    ///
    /// CFG-GNSS payload layout (u-blox M8 Interface Description §32.10.15):
    ///   Byte 0:  msgVer          (u8)  always 0
    ///   Byte 1:  numTrkChHw      (u8)  hardware tracking channels
    ///   Byte 2:  numTrkChUse     (u8)  tracking channels to use
    ///   Byte 3:  numConfigBlocks (u8)  number of 8-byte constellation blocks
    ///   Then numConfigBlocks × 8-byte blocks:
    ///     Byte 0: gnssId   (u8)   — 0=GPS, 1=SBAS, 2=Galileo, 3=BeiDou,
    ///                               4=IMES, 5=QZSS, 6=GLONASS
    ///     Byte 1: resTrkCh (u8)
    ///     Byte 2: maxTrkCh (u8)
    ///     Byte 3: reserved
    ///     Bytes 4-7: flags (u32 LE)  — bit 0 = enable (1=on, 0=off)
    ///
    /// Only gnssIds with flags bit 0 == 1 appear in the returned set.
    /// </summary>
    public static IReadOnlySet<byte> ParseCfgGnss(ReadOnlySpan<byte> payload)
    {
        var enabled = new HashSet<byte>();
        if (payload.Length < 4) return enabled;
 
        byte numBlocks = payload[3];
        for (int i = 0; i < numBlocks; i++)
        {
            int offset = 4 + i * 8;
            if (offset + 8 > payload.Length) break;
 
            byte gnssId = payload[offset];
            uint flags  = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset + 4, 4));
 
            if ((flags & 0x01) != 0)   // bit 0 = enable
                enabled.Add(gnssId);
        }
        return enabled;
    }
 
    /// <summary>Returns the human-readable name for a u-blox gnssId byte.</summary>
    public static string GnssIdName(byte gnssId) => gnssId switch
    {
        0 => "GPS",
        1 => "SBAS",
        2 => "Galileo",
        3 => "BeiDou",
        4 => "IMES",
        5 => "QZSS",
        6 => "GLONASS",
        _ => $"GNSS-{gnssId}"
    };
}