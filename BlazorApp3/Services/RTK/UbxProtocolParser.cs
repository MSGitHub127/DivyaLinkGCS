// Services/UbxProtocolParser.cs
// UBX binary protocol stream parser for u-blox M8P / F9P receivers.
//
// PACKET WIRE FORMAT:
//   [0xB5][0x62][CLASS][ID][LEN_LSB][LEN_MSB][PAYLOAD × LEN][CK_A][CK_B]
//   Checksum = Fletcher-8 over bytes CLASS..PAYLOAD (offset 2..5+LEN-1)
//
// NAV-SAT vs NAV-SVINFO structural divergence:
//   NAV-SVINFO (0x01/0x30) — LEGACY, 12-byte SV block:
//     [chn:u8][svid:u8][flags:u8][quality:u8][cno:u8][elev:i8][azim:i16][prRes:i32]
//     prRes is 4 bytes → total 12
//   NAV-SAT (0x01/0x35) — MODERN, 12-byte SV block:
//     [gnssId:u8][svId:u8][cno:u8][elev:i8][azim:i16][prRes:i16][flags:u32]
//     prRes is 2 bytes, flags expands to 4 bytes → total 12
//   If you use SVINFO layout to parse NAV-SAT, every field after byte 6
//   is silently corrupted — flags bleed into the next satellite's header.

using System.Buffers.Binary;
using BlazorApp3.Models;

namespace BlazorApp3.Services;

// ── UBX stream state machine ──────────────────────────────────────────────────

/// <summary>
/// Byte-at-a-time UBX frame parser. Feed every byte from the serial stream.
/// When <see cref="Feed"/> returns a positive value, a complete, checksum-valid
/// UBX frame is available via <see cref="Class"/>, <see cref="SubClass"/>,
/// and <see cref="Payload"/>.
/// </summary>
public sealed class UbxStreamParser
{
    // UBX frames: 2 preamble + 1 class + 1 id + 2 length + payload + 2 checksum
    private const int MaxPayloadLength = 8192;

    private enum State { Preamble1, Preamble2, Class, SubClass, Len1, Len2, Payload, Ck1, Ck2 }

    private State  _state   = State.Preamble1;
    private byte   _class;
    private byte   _subclass;
    private int    _payloadLen;
    private int    _payloadRead;
    private byte   _ck1Expected;
    private byte   _ck2Expected;

    // Full frame buffer: [B5][62][CLASS][ID][LEN_LSB][LEN_MSB][PAYLOAD...]
    private readonly byte[] _buf = new byte[6 + MaxPayloadLength];

    public byte Class    => _class;
    public byte SubClass => _subclass;

    /// <summary>Zero-allocation view of the current payload (valid only immediately after Feed returns > 0).</summary>
    public ReadOnlySpan<byte> Payload => _buf.AsSpan(6, _payloadLen);

    /// <summary>
    /// Feed one byte from the serial stream.
    /// Returns the UBX message ID (msgId = class<<8|subclass) when a valid frame completes,
    /// otherwise 0. Returns -1 if the preamble was partially matched but failed.
    /// </summary>
    public int Feed(byte b)
    {
        switch (_state)
        {
            case State.Preamble1:
                if (b == 0xB5) _state = State.Preamble2;
                break;

            case State.Preamble2:
                _state = b == 0x62 ? State.Class : State.Preamble1;
                break;

            case State.Class:
                _class = b;
                _state = State.SubClass;
                break;

            case State.SubClass:
                _subclass = b;
                _state = State.Len1;
                break;

            case State.Len1:
                _payloadLen = b;
                _state = State.Len2;
                break;

            case State.Len2:
                _payloadLen |= b << 8;
                _payloadRead = 0;
                if (_payloadLen > MaxPayloadLength) { Reset(); break; }
                // Pre-write class/id/len so checksum covers them
                _buf[2] = _class; _buf[3] = _subclass;
                _buf[4] = (byte)(_payloadLen & 0xFF); _buf[5] = (byte)(_payloadLen >> 8);
                ComputeChecksum(out _ck1Expected, out _ck2Expected, false);
                _state = _payloadLen > 0 ? State.Payload : State.Ck1;
                break;

            case State.Payload:
                _buf[6 + _payloadRead++] = b;
                if (_payloadRead == _payloadLen)
                {
                    ComputeChecksum(out _ck1Expected, out _ck2Expected, true);
                    _state = State.Ck1;
                }
                break;

            case State.Ck1:
                if (b != _ck1Expected) { Reset(); return -1; }
                _state = State.Ck2;
                break;

            case State.Ck2:
                Reset(toIdle: true);
                if (b == _ck2Expected)
                    return (_class << 8) | _subclass;
                return -1;
        }
        return 0;
    }

    /// <summary>Reset parser state. Called by the multi-protocol mux when RTCM is detected.</summary>
    public void Reset() => Reset(toIdle: false);

    private void Reset(bool toIdle)
{
    _state = State.Preamble1;

    _class = 0;
    _subclass = 0;

    _payloadLen = 0;
    _payloadRead = 0;

    _ck1Expected = 0;
    _ck2Expected = 0;
}

    // Fletcher-8 checksum over [CLASS..end-of-payload] (buf[2..5+payloadLen-1])
    private void ComputeChecksum(out byte a, out byte b, bool includePayload)
    {
        uint ca = 0, cb = 0;
        int end = includePayload ? 6 + _payloadLen : 6;
        for (int i = 2; i < end; i++) { ca += _buf[i]; cb += ca; }
        a = (byte)(ca & 0xFF); b = (byte)(cb & 0xFF);
    }
}

// ── NAV-SAT parser ────────────────────────────────────────────────────────────

/// <summary>
/// Parses UBX-NAV-SAT (class=0x01, id=0x35) payload into <see cref="SatelliteInfo"/> list.
///
/// MESSAGE STRUCTURE (from u-blox Interface Description):
///
///   HEADER — 8 bytes:
///   ┌─────────┬──────────┬─────────┬──────────┐
///   │ iTOW    │ version  │ numSvs  │ reserved1│
///   │ u32 (4) │ u8  (1)  │ u8  (1) │ u8[2](2) │
///   └─────────┴──────────┴─────────┴──────────┘
///
///   REPEATING BLOCK — 12 bytes × numSvs:
///   ┌────────┬──────┬─────┬──────┬──────┬───────┬───────────────────────────┐
///   │ gnssId │ svId │ cno │ elev │ azim │ prRes │ flags                     │
///   │ u8 (1) │ u8(1)│ u8(1)│i8(1)│i16(2)│i16(2) │ u32 (4)                 │
///   │ off=0  │ off=1│ off=2│off=3│off=4 │ off=6  │ off=8                   │
///   └────────┴──────┴─────┴──────┴──────┴───────┴───────────────────────────┘
///
///   flags bit layout (u32):
///     bits  0-2 : qualityInd   (0=none … 7=code+carrier locked)
///     bit   3   : svUsed       (1 = SV contributing to navigation)  ← ACTIVE
///     bits  4-5 : health       (0=unknown, 1=healthy, 2=unhealthy)
///     bits  6-7 : diffCorr     (differential corrections applied)
///     bits  8-9 : smoothed
///     bits 10-12: orbitSource  (0=no orbit, 1=ephemeris, 2=almanac…)
///     bit  13   : ephAvail
///     bit  14   : almAvail
///     bit  15   : anoAvail
///     bit  16   : aopAvail
///     bits 17   : reserved
///     bit  18   : sbasCorrUsed
///     bit  19   : rtcmCorrUsed
///     bit  20   : slasCorrUsed
///     bit  21   : spartnCorrUsed
///     bit  22   : prCorrUsed
///     bit  23   : crCorrUsed
///     bit  24   : doCorrUsed
///
///   gnssId → display prefix:
///     0 = GPS     → "G"
///     1 = SBAS    → "S"
///     2 = Galileo → "E"
///     3 = BeiDou  → "B"
///     5 = QZSS    → "Q"
///     6 = GLONASS → "R"
/// </summary>
public static class UbxNavSatParser
{
    private const int HeaderSize = 8;
    private const int BlockSize  = 12;  // MUST be 12 — do NOT use SVINFO's layout

    public static List<SatelliteInfo> Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderSize) return [];

        byte version = payload[4];
        // Version must be 1 for NAV-SAT. If future firmware changes this,
        // we must not attempt to parse with old offsets.
        if (version > 1) return [];

        byte numSvs      = payload[5];
        int  expectedLen = HeaderSize + numSvs * BlockSize;
        if (payload.Length < expectedLen) return [];

        var sats = new List<SatelliteInfo>(numSvs);

        for (int i = 0; i < numSvs; i++)
        {
            int base_ = HeaderSize + i * BlockSize;

            // ── Read each field at its EXACT byte offset ─────────────────────
            // Critical: prRes is int16 at offset 6 (NOT int32 at offset 8 like SVINFO).
            // flags is uint32 at offset 8 (NOT starting at offset 12 like SVINFO).
            byte  gnssId = payload[base_ + 0];
            byte  svId   = payload[base_ + 1];
            byte  cno    = payload[base_ + 2];   // dB-Hz: the SNR bar height
            // elev and azim unused in current display but parsed for completeness
            // prRes unused in display
            uint  flags  = BinaryPrimitives.ReadUInt32LittleEndian(
                               payload.Slice(base_ + 8, 4));

            // ── Extract status flags ─────────────────────────────────────────
            bool svUsed = (flags & 0x08u) != 0;   // bit 3 — SV used in nav solution

            // ── Map gnssId to display prefix ─────────────────────────────────
            string prefix = gnssId switch
            {
                0 => "G",   // GPS
                1 => "S",   // SBAS
                2 => "E",   // Galileo
                3 => "B",   // BeiDou
                5 => "Q",   // QZSS
                6 => "R",   // GLONASS
                _ => "?"
            };

            sats.Add(new SatelliteInfo(
                Id:     $"{prefix}{svId:D2}",
                Snr:    cno,
                Active: svUsed,
                GnssId: gnssId,
                SvId:   svId
            ));
        }

        return sats;
    }
}

// ── NAV-SVIN parser ───────────────────────────────────────────────────────────

/// <summary>
/// Parses UBX-NAV-SVIN (class=0x01, id=0x3B) payload into <see cref="NavSvinData"/>.
///
///   Payload offset map (verified against ubx_m8p.cs ubx_nav_svin struct):
///   0:  version  (u8)
///   1-3: reserved1[3]
///   4-7: iTOW    (u32, ms)
///   8-11: dur    (u32, seconds elapsed) ← Survey-In duration display
///   12-15: meanX (i32, ECEF X, cm)
///   16-19: meanY (i32, ECEF Y, cm)
///   20-23: meanZ (i32, ECEF Z, cm)
///   24: meanXHP  (i8, 0.1mm high-precision extension)
///   25: meanYHP  (i8)
///   26: meanZHP  (i8)
///   27: reserved2
///   28-31: meanAcc (u32, 0.1mm units → ÷10000 for metres) ← accuracy display
///   32-35: obs     (u32) ← observation count display
///   36: valid     (u8, 1 = Survey-In converged) ← INJECTION GATE
///   37: active    (u8, 1 = Survey-In running)
///   38-39: reserved3[2]
///   Total payload: 40 bytes
/// </summary>
public readonly struct NavSvinData
{
    public uint   Dur      { get; init; }  // seconds
    public uint   MeanAcc  { get; init; }  // 0.1mm — display as MeanAcc/10000.0 metres
    public uint   Obs      { get; init; }  // observations
    public bool   Valid    { get; init; }  // true = Survey-In complete
    public bool   Active   { get; init; }  // true = Survey-In running
    public int    MeanX    { get; init; }  // ECEF X cm
    public int    MeanY    { get; init; }  // ECEF Y cm
    public int    MeanZ    { get; init; }  // ECEF Z cm
    public sbyte  MeanXHP  { get; init; }  // HP extension 0.1mm
    public sbyte  MeanYHP  { get; init; }
    public sbyte  MeanZHP  { get; init; }

    public double AccuracyM => MeanAcc / 10_000.0;

    public (double X, double Y, double Z) EcefMetres => (
        MeanX / 100.0 + MeanXHP * 0.0001,
        MeanY / 100.0 + MeanYHP * 0.0001,
        MeanZ / 100.0 + MeanZHP * 0.0001
    );
}

public static class UbxNavSvinParser
{
    private const int ExpectedPayloadLen = 40;

    public static NavSvinData? Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < ExpectedPayloadLen) return null;

        return new NavSvinData
        {
            Dur     = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]),
            MeanX   = BinaryPrimitives.ReadInt32LittleEndian(payload[12..]),
            MeanY   = BinaryPrimitives.ReadInt32LittleEndian(payload[16..]),
            MeanZ   = BinaryPrimitives.ReadInt32LittleEndian(payload[20..]),
            MeanXHP = (sbyte)payload[24],
            MeanYHP = (sbyte)payload[25],
            MeanZHP = (sbyte)payload[26],
            MeanAcc = BinaryPrimitives.ReadUInt32LittleEndian(payload[28..]),
            Obs     = BinaryPrimitives.ReadUInt32LittleEndian(payload[32..]),
            Valid   = payload[36] == 1,
            Active  = payload[37] == 1,
        };
    }
}

// ── RTCM3 stream parser (port of RTCMParser.cc) ───────────────────────────────

/// <summary>
/// Byte-at-a-time RTCM3 frame parser. Feed one byte at a time.
/// When <see cref="Feed"/> returns true, a complete CRC-validated frame is available
/// via <see cref="Packet"/> and <see cref="MessageId"/>.
///
/// Frame layout:
///   [0xD3][LEN_HI:6reserved+2MSB][LEN_LO:8LSB][PAYLOAD×LEN][CRC24Q:3]
/// </summary>
public sealed class RtcmStreamParser
{
    private const byte Preamble         = 0xD3;
    private const int  MaxPayloadLength = 1023;
    private const int  HeaderSize       = 3;    // preamble + 2 length bytes
    private const int  CrcSize          = 3;

    private enum State { WaitPreamble, ReadLength, ReadMessage, ReadCrc }

    private State _state          = State.WaitPreamble;
    private int   _messageLength;
    private int   _bytesRead;
    private int   _lengthBytesRead;
    private int   _crcBytesRead;
    public bool IsIdle => _state == State.WaitPreamble;

    // Buffer holds: [preamble][len1][len2][payload...]
    private readonly byte[] _buf      = new byte[HeaderSize + MaxPayloadLength];
    private readonly byte[] _lenBytes = new byte[2];
    private readonly byte[] _crcBytes = new byte[3];

    /// <summary>Full frame including preamble, length bytes, payload (excludes CRC — validated internally).</summary>
    public ReadOnlySpan<byte> Frame   => _buf.AsSpan(0, HeaderSize + _messageLength);

    /// <summary>Complete frame including trailing CRC bytes (needed for injection into GPS module).</summary>
    public byte[] FrameWithCrc
    {
        get
        {
            var f = new byte[HeaderSize + _messageLength + CrcSize];
            Frame.CopyTo(f);
            f[HeaderSize + _messageLength + 0] = _crcBytes[0];
            f[HeaderSize + _messageLength + 1] = _crcBytes[1];
            f[HeaderSize + _messageLength + 2] = _crcBytes[2];
            return f;
        }
    }

    public int TotalBytes  => HeaderSize + _messageLength + CrcSize;
    public int MessageId
{
    get
    {
        if (_messageLength < 2)
            return 0;

        byte b0 = _buf[3];
        byte b1 = _buf[4];

        return ((b0 << 4) | (b1 >> 4)) & 0x0FFF;
    }
}

    public bool Feed(byte b)
{
    switch (_state)
    {
        case State.WaitPreamble:
            if (b == Preamble)
            {
                _buf[0] = b;
                _bytesRead = 1;
                _lengthBytesRead = 0;
                _state = State.ReadLength;
            }
            break;

        case State.ReadLength:
            _lenBytes[_lengthBytesRead] = b;
            _buf[_bytesRead++] = b;

            if (++_lengthBytesRead == 2)
            {
                _messageLength =
                    ((_lenBytes[0] & 0x03) << 8) |
                    _lenBytes[1];

                if (_messageLength <= 0 ||
                    _messageLength > MaxPayloadLength)
                {
                    Reset();
                    return false;
                }

                _state = State.ReadMessage;
            }
            break;

        case State.ReadMessage:

            // protect against buffer overflow
            if (_bytesRead >= HeaderSize + _messageLength)
            {
                _state = State.ReadCrc;
                _crcBytesRead = 0;
                break;
            }

            _buf[_bytesRead++] = b;

            if ((_bytesRead - HeaderSize) == _messageLength)
            {
                _state = State.ReadCrc;
                _crcBytesRead = 0;
            }
            break;

        case State.ReadCrc:
            _crcBytes[_crcBytesRead++] = b;

            if (_crcBytesRead == CrcSize)
            {
                bool ok = ValidateCrc();
                _state = State.WaitPreamble;
                return ok;
            }
            break;
    }

    return false;
}

    public void Reset()
{
    _state = State.WaitPreamble;
    //_messageLength = 0;

    _bytesRead = 0;
    _lengthBytesRead = 0;
    _crcBytesRead = 0;
}

    private bool ValidateCrc()
    {
        if (_messageLength == 0 || _bytesRead < HeaderSize + _messageLength) return false;
        uint computed = Crc24Q(_buf, HeaderSize + _messageLength);
        uint received = ((uint)_crcBytes[0] << 16) | ((uint)_crcBytes[1] << 8) | _crcBytes[2];
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
        return crc & 0xFFFFFF;
    }
}