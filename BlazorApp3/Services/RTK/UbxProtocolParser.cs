// UbxProtocolParser.cs
// Complete rewrite — verified against:
//   • ArduPilot RTCMParser.cpp (Files.md lines 21879–21975)
//   • u-blox M8 Interface Description UBX-13003221
//   • Mission Planner ConfigSerialInjectGPS / ubx_m8p.cs (Files.md)
//
// Key fixes vs previous version:
//   FIX-1: RtcmStreamParser.Feed() always calls Reset() before returning —
//           regardless of CRC pass or fail. The ArduPilot C++ reference
//           (RTCMParser::addByte) returns true/false and leaves reset to caller;
//           our C# version calls Reset() internally so the caller never needs to.
//   FIX-2: DispatchByte() in the service layer handles the IsIdle transition.
//   FIX-3: NAV-SAT version check accepts 0 and 1 (old M8P fw reports version=0).
//   FIX-4: UbxStreamParser exposes IsIdle for symmetrical recovery in service.
//   FIX-5: MonVerInfo fully parsed — firmware version + FWVER/PROTVER strings.

using System;
using System.Collections.Generic;
using System.Text;
using BlazorApp3.Models;

namespace BlazorApp3.Services;

// ═══════════════════════════════════════════════════════════════════════════════
// RTCM 3 Stream Parser
// Reference: ArduPilot RTCMParser.cpp (Files.md 21879-21975)
//            RTCM 10403.3 §4.1 frame structure
//
// Frame layout: [0xD3][10-bit reserved + 10-bit len (2 bytes)][payload N bytes][CRC 3 bytes]
// MessageId   : bits 12..1 of first 12 bits of payload = (payload[0]<<4)|(payload[1]>>4)
// CRC-24Q     : covers preamble + 2 length bytes + payload
// ═══════════════════════════════════════════════════════════════════════════════
public sealed class RtcmStreamParser
{
    private enum State { WaitPreamble, ReadLength, ReadMessage, ReadCrc }

    private const byte   Preamble       = 0xD3;
    private const int    HeaderSize     = 3;          // preamble + 2 length bytes
    private const int    MaxPayload     = 1023;
    private const int    CrcSize        = 3;
    private const int    BufSize        = HeaderSize + MaxPayload + CrcSize;

    private readonly byte[] _buf      = new byte[BufSize];
    private readonly byte[] _lenBytes = new byte[2];
    private readonly byte[] _crcBytes = new byte[CrcSize];

    private State _state          = State.WaitPreamble;
    private int   _bytesRead      = 0;    // bytes written into _buf (header + payload)
    private int   _lenBytesRead   = 0;
    private int   _crcBytesRead   = 0;
    private int   _messageLength  = 0;

    // ── Public properties ─────────────────────────────────────────────────────

    /// True when parser is idle (WaitPreamble). Used by DispatchByte to detect
    /// self-reset after a CRC failure.
    public bool IsIdle => _state == State.WaitPreamble;

    public long TotalBytes { get; private set; }

    /// 12-bit RTCM message type. Valid only when Feed() just returned true.
    public int MessageId
    {
        get
        {
            if (_messageLength < 2) return 0;
            // Payload starts at _buf[HeaderSize]
            return ((_buf[3] << 4) | (_buf[4] >> 4)) & 0x0FFF;
        }
    }

    /// Complete frame including preamble, length, payload, and CRC.
    /// Valid only when Feed() just returned true.
    public ReadOnlySpan<byte> FrameWithCrc =>
        _buf.AsSpan(0, HeaderSize + _messageLength + CrcSize);

    // ── Feed ─────────────────────────────────────────────────────────────────
    /// Feed one byte. Returns true when a CRC-valid frame is complete.
    /// Returns false on CRC failure OR when the frame is still accumulating.
    ///
    /// CRITICAL CONTRACT (FIX-1):
    ///   This method calls Reset() internally before returning from ReadCrc,
    ///   regardless of CRC outcome. The caller (DispatchByte) must check
    ///   IsIdle to distinguish "CRC failed, reset done" from "still reading".
    ///   This mirrors ArduPilot's RTCMParser::addByte contract.
    public bool Feed(byte b)
    {
        switch (_state)
        {
            case State.WaitPreamble:
                if (b == Preamble)
                {
                    _buf[0]        = b;
                    _bytesRead     = 1;
                    _lenBytesRead  = 0;
                    _state         = State.ReadLength;
                }
                break;

            case State.ReadLength:
                _lenBytes[_lenBytesRead] = b;
                _buf[_bytesRead++]       = b;
                _lenBytesRead++;

                if (_lenBytesRead == 2)
                {
                    _messageLength = ((_lenBytes[0] & 0x03) << 8) | _lenBytes[1];

                    if (_messageLength == 0 || _messageLength > MaxPayload)
                    {
                        Reset(); // invalid length — resync
                        break;
                    }
                    _state = State.ReadMessage;
                }
                break;

            case State.ReadMessage:
                // Guard: never write past the payload area of _buf
                if (_bytesRead < HeaderSize + _messageLength)
                {
                    _buf[_bytesRead++] = b;
                }

                if (_bytesRead >= HeaderSize + _messageLength)
                {
                    _state        = State.ReadCrc;
                    _crcBytesRead = 0;
                }
                break;

            case State.ReadCrc:
                _crcBytes[_crcBytesRead++] = b;

                if (_crcBytesRead == CrcSize)
                {
                    // Copy CRC bytes into tail of _buf so FrameWithCrc spans one array
                    int crcOffset = HeaderSize + _messageLength;
                    _buf[crcOffset]     = _crcBytes[0];
                    _buf[crcOffset + 1] = _crcBytes[1];
                    _buf[crcOffset + 2] = _crcBytes[2];

                    bool valid = ValidateCrc();
                    if (valid) TotalBytes += HeaderSize + _messageLength + CrcSize;

                    // FIX-1: ALWAYS reset after CRC evaluation.
                    // Leaving state=ReadCrc with _crcBytesRead=3 causes
                    // IndexOutOfRangeException on the next byte. This was the
                    // root cause of the entire RTK injection failure.
                    Reset();

                    return valid;
                }
                break;
        }
        return false;
    }

    public void Reset()
    {
        _state        = State.WaitPreamble;
        _bytesRead    = 0;
        _lenBytesRead = 0;
        _crcBytesRead = 0;
        _messageLength= 0;
    }

    // ── CRC-24Q ───────────────────────────────────────────────────────────────
    // Reference: RTKLIB rtkcmn.c rtk_crc24q(), ArduPilot RTCMParser::crc24q()
    // Polynomial: 0x1864CFB  (POLYCRC24Q in rtkcmn.c line 130)
    private bool ValidateCrc()
    {
        int   dataLen  = HeaderSize + _messageLength;
        uint  computed = Crc24Q(_buf, dataLen);
        uint  received = ((uint)_crcBytes[0] << 16)
                       | ((uint)_crcBytes[1] <<  8)
                       |  (uint)_crcBytes[2];
        return computed == received;
    }

    private static uint Crc24Q(byte[] data, int len)
    {
        const uint Poly = 0x1864CFBu;
        uint crc = 0;
        for (int i = 0; i < len; i++)
        {
            crc ^= (uint)(data[i] << 16);
            for (int j = 0; j < 8; j++)
            {
                crc <<= 1;
                if ((crc & 0x1000000u) != 0) crc ^= Poly;
            }
        }
        return crc & 0xFFFFFFu;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// UBX Stream Parser
// Reference: u-blox M8 Interface Description §32.2 UBX Frame Structure
//
// Frame: [0xB5][0x62][CLS][ID][LEN_LO][LEN_HI][payload...][CK_A][CK_B]
// Checksum: Fletcher-8 over bytes [CLS]..[last payload byte]
// ═══════════════════════════════════════════════════════════════════════════════
public sealed class UbxStreamParser
{
    private enum State
    {
        WaitSync1,   // waiting for 0xB5
        WaitSync2,   // waiting for 0x62
        ReadClass,
        ReadId,
        ReadLen1,
        ReadLen2,
        ReadPayload,
        ReadCkA,
        ReadCkB
    }

    private const int MaxPayload = 1024;

    private State    _state       = State.WaitSync1;
    private byte     _cls, _id;
    private int      _payloadLen  = 0;
    private int      _payloadRead = 0;
    private byte     _ckA = 0, _ckB = 0;         // running checksum
    private byte     _ckAExpected, _ckBExpected;
    private readonly byte[] _payload = new byte[MaxPayload];

    // ── Public ────────────────────────────────────────────────────────────────
    public bool IsIdle => _state == State.WaitSync1;

    public byte   Class   => _cls;
    public byte   SubClass=> _id;

    /// Valid only when Feed() returns > 0.
    public ReadOnlySpan<byte> Payload => _payload.AsSpan(0, _payloadLen);

    /// Feed one byte.
    /// Returns  1 = complete frame, checksum OK.
    ///            CALLER MUST call Reset() after reading Class/SubClass/Payload.
    ///            Payload remains valid until Reset() is called.
    /// Returns  0 = frame in progress.
    /// Returns -1 = checksum failed; parser already self-reset, Payload is invalid.
    ///
    /// CONTRACT (fixes the empty-payload bug):
    ///   On return 1, this method does NOT call Reset() internally.
    ///   The caller reads ubx.Class, ubx.SubClass, ubx.Payload, then calls ubx.Reset().
    ///   On return -1 (either checksum byte wrong), Reset() IS called internally
    ///   because there is nothing useful for the caller to read.
    public int Feed(byte b)
    {
        switch (_state)
        {
            case State.WaitSync1:
                if (b == 0xB5) _state = State.WaitSync2;
                break;

            case State.WaitSync2:
                _state = b == 0x62 ? State.ReadClass : State.WaitSync1;
                break;

            case State.ReadClass:
                _cls   = b;
                _ckA   = b; _ckB = b;    // start Fletcher-8 checksum
                _state = State.ReadId;
                break;

            case State.ReadId:
                _id    = b;
                UpdateCk(b);
                _state = State.ReadLen1;
                break;

            case State.ReadLen1:
                _payloadLen = b;          // low byte of length
                UpdateCk(b);
                _state = State.ReadLen2;
                break;

            case State.ReadLen2:
                _payloadLen |= (b << 8);  // high byte of length
                UpdateCk(b);
                _payloadRead = 0;

                if (_payloadLen == 0)
                {
                    _ckAExpected = _ckA;
                    _ckBExpected = _ckB;
                    _state = State.ReadCkA;
                }
                else if (_payloadLen > MaxPayload)
                {
                    Reset();  // reject oversized frame
                }
                else
                {
                    _state = State.ReadPayload;
                }
                break;

            case State.ReadPayload:
                _payload[_payloadRead++] = b;
                UpdateCk(b);
                if (_payloadRead == _payloadLen)
                {
                    _ckAExpected = _ckA;
                    _ckBExpected = _ckB;
                    _state = State.ReadCkA;
                }
                break;

            case State.ReadCkA:
                if (b == _ckAExpected)
                {
                    _state = State.ReadCkB;
                }
                else
                {
                    Reset();   // bad CK_A — discard frame, nothing to read
                    return -1;
                }
                break;

            case State.ReadCkB:
                if (b == _ckBExpected)
                {
                    // BUG-A FIX: do NOT call Reset() here.
                    // _payloadLen, _payload, _cls, _id are all still valid.
                    // The caller (DispatchByte) reads them, THEN calls Reset().
                    // Calling Reset() here zeroed _payloadLen, making ubx.Payload
                    // return an empty span — causing empty MON-VER/NAV-SVIN/NAV-SAT.
                    return 1;   // complete valid frame — caller owns payload until Reset()
                }
                Reset();       // bad CK_B — discard frame, nothing to read
                return -1;
        }
        return 0;
    }

    public void Reset()
    {
        _state       = State.WaitSync1;
        _payloadLen  = 0;
        _payloadRead = 0;
        _ckA = _ckB  = 0;
    }

    private void UpdateCk(byte b)
    {
        _ckA = (byte)((_ckA + b) & 0xFF);
        _ckB = (byte)((_ckB + _ckA) & 0xFF);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// UBX-NAV-SVIN  (0x01 / 0x3B)
// Reference: u-blox M8 spec §32.17.26, table at line 392 of spec MD
// Verified against MP ProcessUBXMessage (Files.md line 30252)
// ═══════════════════════════════════════════════════════════════════════════════
public static class UbxNavSvinParser
{
    // Offsets within payload (0-based, after 6-byte UBX header stripped by parser)
    private const int OFF_VERSION  = 0;   // u8
    private const int OFF_RESERVED = 1;   // 3 bytes
    private const int OFF_ITOW     = 4;   // u32 LE
    private const int OFF_DUR      = 8;   // u32 LE  seconds
    private const int OFF_MEANX    = 12;  // i32 LE  cm
    private const int OFF_MEANY    = 16;  // i32 LE  cm
    private const int OFF_MEANZ    = 20;  // i32 LE  cm
    private const int OFF_MEANXHP  = 24;  // i8   0.1mm
    private const int OFF_MEANYHP  = 25;  // i8   0.1mm
    private const int OFF_MEANZHP  = 26;  // i8   0.1mm
    private const int OFF_RESERVED2= 27;
    private const int OFF_MEANACC  = 28;  // u32 LE  0.1mm
    private const int OFF_OBS      = 32;  // u32 LE
    private const int OFF_VALID    = 36;  // u8  1=valid
    private const int OFF_ACTIVE   = 37;  // u8  1=active
    private const int MIN_LEN      = 40;

    public static NavSvinData? Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < MIN_LEN) return null;

        uint dur     = BitConverter.ToUInt32(payload.Slice(OFF_DUR,  4));
        uint obs     = BitConverter.ToUInt32(payload.Slice(OFF_OBS,  4));
        uint accRaw  = BitConverter.ToUInt32(payload.Slice(OFF_MEANACC, 4));
        bool valid   = payload[OFF_VALID]  == 1;
        bool active  = payload[OFF_ACTIVE] == 1;

        // ECEF in metres: integer part in cm + HP extension in 0.1mm
        double ecefX = ReadEcefM(payload, OFF_MEANX,  OFF_MEANXHP);
        double ecefY = ReadEcefM(payload, OFF_MEANY,  OFF_MEANYHP);
        double ecefZ = ReadEcefM(payload, OFF_MEANZ,  OFF_MEANZHP);

        // meanAcc in metres: raw is 0.1mm units → divide by 10000
        // MP: svin.meanAcc / 10000.0  (Files.md line 30256)
        double accM  = accRaw / 10000.0;

        return new NavSvinData(dur, obs, accM, valid, active, ecefX, ecefY, ecefZ);
    }

    private static double ReadEcefM(ReadOnlySpan<byte> p, int intOff, int hpOff)
    {
        int    intPart = BitConverter.ToInt32(p.Slice(intOff, 4)); // cm
        sbyte  hpPart  = (sbyte)p[hpOff];                          // 0.1mm
        return intPart / 100.0 + hpPart * 0.0001;                  // metres
    }
}

public sealed record NavSvinData(
    uint   DurationSec,
    uint   Observations,
    double AccuracyM,
    bool   Valid,
    bool   Active,
    double EcefX,    // metres
    double EcefY,
    double EcefZ
);

// ═══════════════════════════════════════════════════════════════════════════════
// UBX-NAV-SAT  (0x01 / 0x35)
// Reference: u-blox M8 spec §32.17.20
// ═══════════════════════════════════════════════════════════════════════════════
public static class UbxNavSatParser
{
    private const int HEADER_LEN = 8;   // version(1)+reserved(1)+numSvs(1)+reserved(1)+iTOW(4)?
    // Actual layout: iTOW(4)+version(1)+numSvs(1)+reserved(2) = 8 bytes header
    // Then numSvs × 12-byte blocks
    private const int OFF_ITOW    = 0;
    private const int OFF_VERSION = 4;
    private const int OFF_NUMSVS  = 5;
    private const int BLOCK_SIZE  = 12;

    // Block offsets (within each 12-byte block):
    private const int BLK_GNSSID = 0;  // u8
    private const int BLK_SVID   = 1;  // u8
    private const int BLK_CNO    = 2;  // u8  dBHz (carrier-to-noise)
    private const int BLK_ELEV   = 3;  // i8  degrees
    private const int BLK_AZIM   = 4;  // i16 LE degrees
    private const int BLK_PRRES  = 6;  // i16 LE 0.1m
    private const int BLK_FLAGS  = 8;  // u32 LE

    // flags bit 3 = svUsed (used in navigation solution)
    private const uint FLAG_SV_USED = 0x08;

    public static IReadOnlyList<SatelliteInfo> Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HEADER_LEN) return [];

        byte version = payload[OFF_VERSION];
        // FIX-3: Accept version 0 (M8P fw < 3.01) and version 1.
        // Previous code rejected version != 1, blanking the SNR chart on older fw.
        if (version > 1) return [];

        int  numSvs = payload[OFF_NUMSVS];
        if (payload.Length < HEADER_LEN + numSvs * BLOCK_SIZE) return [];

        var result = new List<SatelliteInfo>(numSvs);
        for (int i = 0; i < numSvs; i++)
        {
            int    blk    = HEADER_LEN + i * BLOCK_SIZE;
            byte   gnssId = payload[blk + BLK_GNSSID];
            byte   svId   = payload[blk + BLK_SVID];
            byte   cno    = payload[blk + BLK_CNO];
            uint   flags  = BitConverter.ToUInt32(payload.Slice(blk + BLK_FLAGS, 4));
            bool   used   = (flags & FLAG_SV_USED) != 0;
            string prefix = SatelliteInfo.GnssPrefix(gnssId);
            string id     = $"{prefix}{svId:D2}";

            result.Add(new SatelliteInfo(id, cno, used, gnssId, svId));
        }
        return result;
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// UBX-MON-VER  (0x0A / 0x04)
// Reference: u-blox M8 spec §32.16.13, lines 355-357 of spec MD
// MP ProcessUBXMessage lines 30290-30301 (Files.md)
//
// Payload layout:
//   [0..29]  swVersion  (30 bytes, null-terminated ASCII)
//   [30..39] hwVersion  (10 bytes, null-terminated ASCII)
//   [40..]   extension strings, each 30 bytes null-terminated, variable count
// ═══════════════════════════════════════════════════════════════════════════════
public static class UbxMonVerParser
{
    public static MonVerInfo Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 40) return MonVerInfo.Unknown;

        string sw = NullTermString(payload.Slice(0, 30));
        string hw = NullTermString(payload.Slice(30, 10));

        var exts = new List<string>();
        for (int i = 40; i + 30 <= payload.Length; i += 30)
            exts.Add(NullTermString(payload.Slice(i, 30)));

        // Parse FWVER and PROTVER from extensions
        string fwVer   = exts.Find(e => e.StartsWith("FWVER="))  ?? "";
        string protVer = exts.Find(e => e.StartsWith("PROTVER="))  ?? "";
        string mod     = exts.Find(e => e.StartsWith("MOD="))    ?? "";

        // Determine if MSM7 is supported.
        // u-blox M8P: HPG 1.40+ supports MSM7 (PROTVER=20.30)
        // u-blox F9P: HPG 1.0+ always supports MSM7
        // Reference: u-blox M8 spec firmware table line 504-509:
        //   HPG 1.40 → PROTVER 20.30
        //   HPG 1.43 → PROTVER 20.30
        bool isMsm7Capable = IsMsm7Supported(fwVer, protVer);
        bool isF9P         = mod.Contains("F9P") || sw.Contains("F9P");

        return new MonVerInfo(sw, hw, mod, fwVer, protVer, [..exts], isMsm7Capable, isF9P);
    }

    private static bool IsMsm7Supported(string fwVer, string protVer)
    {
        // HPG 1.40 was the first M8P firmware with MSM7 output.
        // Compare version strings numerically.
        if (fwVer.StartsWith("FWVER=HPG "))
        {
            string vstr = fwVer["FWVER=HPG ".Length..].Split(' ')[0]; // e.g. "1.43"
            if (Version.TryParse(vstr, out var v) && v >= new Version(1, 40))
                return true;
        }
        // F9P always supports MSM7
        if (fwVer.StartsWith("FWVER=HPG 1.") && fwVer.Contains("F9"))
            return true;
        // Fallback: PROTVER >= 20.00 → MSM7 capable
        if (protVer.StartsWith("PROTVER="))
        {
            string pstr = protVer["PROTVER=".Length..].Split(' ')[0];
            if (double.TryParse(pstr,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double pv) && pv >= 20.00)
                return true;
        }
        return false; // default conservative: use MSM4
    }

    private static string NullTermString(ReadOnlySpan<byte> s)
    {
        int end = s.IndexOf((byte)0);
        if (end < 0) end = s.Length;
        return Encoding.ASCII.GetString(s.Slice(0, end));
    }
}

public sealed record MonVerInfo(
    string   SwVersion,
    string   HwVersion,
    string   Module,
    string   FwVer,
    string   ProtVer,
    string[] Extensions,
    bool     IsMsm7Capable,
    bool     IsF9P
)
{
    public static MonVerInfo Unknown =>
        new("", "", "", "", "", [], false, false);
}

// ═══════════════════════════════════════════════════════════════════════════════
// UBX-MON-HW  (0x0A / 0x09)
// Reference: u-blox M8 spec §32.16.4
// MP ProcessUBXMessage line 30305-30307 (Files.md)
// ═══════════════════════════════════════════════════════════════════════════════
public static class UbxMonHwParser
{
    private const int OFF_PIN_SEL   = 0;   // u32 LE
    private const int OFF_PIN_BANK  = 4;   // u32 LE
    private const int OFF_PIN_DIR   = 8;   // u32 LE
    private const int OFF_PIN_VAL   = 12;  // u32 LE
    private const int OFF_NOISE     = 16;  // u16 LE
    private const int OFF_AGC_CNT   = 18;  // u16 LE  0-8191
    private const int OFF_AGC_STATE = 20;  // u8
    private const int OFF_ANT_STAT  = 21;  // u8
    private const int OFF_ANT_PWR   = 22;  // u8
    private const int OFF_FLAGS     = 23;  // u8  bits1:0 = jammingState
    private const int OFF_USED_MASK = 24;  // u32
    private const int OFF_VP        = 28;  // 17 bytes
    private const int OFF_JAM_IND   = 45;  // u8  jam indicator 0-255
    private const int MIN_LEN       = 60;

    public static MonHwData? Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < MIN_LEN) return null;

        ushort noise     = BitConverter.ToUInt16(payload.Slice(OFF_NOISE,   2));
        ushort agcCnt    = BitConverter.ToUInt16(payload.Slice(OFF_AGC_CNT, 2));
        byte   flags     = payload[OFF_FLAGS];
        byte   jamInd    = payload[OFF_JAM_IND];

        // MP formula: agc% = (agcCnt / 8191.0) * 100, jam% = (jamInd / 256.0) * 100
        double agcPct    = (agcCnt / 8191.0) * 100.0;
        double jamPct    = (jamInd / 256.0)  * 100.0;
        int    jamState  = flags & 0x0C;   // bits 3:2 per spec §32.16.4

        return new MonHwData(noise, agcPct, jamPct, jamState);
    }
}

public sealed record MonHwData(
    ushort NoisePerMs,
    double AgcPct,
    double JamPct,
    int    JamState      // 0=unknown,1=ok,2=warning,3=critical
);

// ═══════════════════════════════════════════════════════════════════════════════
// ECEF → LLA conversion
// Reference: Bowring's method, 10 iterations (same as original DivyaLink)
// ═══════════════════════════════════════════════════════════════════════════════
public static class EcefConverter
{
    private const double A  = 6378137.0;           // WGS-84 semi-major axis (m)
    private const double F  = 1.0 / 298.257223563; // flattening
    private const double B  = A * (1.0 - F);
    private const double E2 = 2 * F - F * F;       // first eccentricity squared
    private const double EP2= (A * A - B * B) / (B * B); // second eccentricity squared

    public static (double LatDeg, double LonDeg, double AltM)
        ToLla(double x, double y, double z)
    {
        double lon     = Math.Atan2(y, x);
        double p       = Math.Sqrt(x * x + y * y);
        double lat     = Math.Atan2(z, p * (1.0 - E2)); // initial estimate
        double N = 0.0;

        for (int i = 0; i < 10; i++)
        {
            double sinLat = Math.Sin(lat);
            N      = A / Math.Sqrt(1.0 - E2 * sinLat * sinLat);
            lat = Math.Atan2(z + E2 * N * sinLat, p);
        }

        double sinL = Math.Sin(lat);
        N    = A / Math.Sqrt(1.0 - E2 * sinL * sinL);
        double alt  = p / Math.Cos(lat) - N;

        return (lat * 180.0 / Math.PI, lon * 180.0 / Math.PI, alt);
    }
}