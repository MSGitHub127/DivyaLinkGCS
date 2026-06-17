using System.IO.Ports;

namespace DivyaLink.Services.Firmware.Flash;

/// <summary>
/// PX4-compatible serial bootloader client (also used by ArduPilot on Pixhawk-class boards).
/// Ported from Mission Planner px4uploader / PX4 px4_uploader.py.
/// </summary>
public sealed class Px4BootloaderClient : IDisposable
{
    public enum Code : byte
    {
        OK = 0x10,
        FAILED = 0x11,
        INSYNC = 0x12,
        INVALID = 0x13,
        EOC = 0x20,
        GET_SYNC = 0x21,
        GET_DEVICE = 0x22,
        CHIP_ERASE = 0x23,
        CHIP_VERIFY = 0x24,
        PROG_MULTI = 0x27,
        READ_MULTI = 0x28,
        GET_CRC = 0x29,
        GET_CHIP = 0x2C,
        GET_CHIP_DES = 0x2E,
        REBOOT = 0x30,
        EXTF_ERASE = 0x34,
        EXTF_PROG_MULTI = 0x35,
        EXTF_GET_CRC = 0x37
    }

    public enum Info : byte
    {
        BL_REV = 1,
        BOARD_ID = 2,
        BOARD_REV = 3,
        FLASH_SIZE = 4,
        EXTF_SIZE = 6
    }

    private const byte BlRevMin = 2;
    private const byte BlRevMax = 20;
    private const byte ProgMultiMax = 252;
    private const byte ReadMultiMax = 252;

    private readonly SerialPort _port;

    public int BoardType { get; private set; }
    public int BoardRev { get; private set; }
    public int FlashMaxSize { get; private set; }
    public int ExtFlashMaxSize { get; private set; }
    public int BootloaderRev { get; private set; }
    public string ChipDescription { get; private set; } = "";

    public event Action<string>? OnLog;
    public event Action<double>? OnProgress;

    public Px4BootloaderClient(string portName, int baudRate = 115200)
    {
        _port = new SerialPort(portName, baudRate)
        {
            ReadTimeout = 2000,
            WriteTimeout = 2000,
            DtrEnable = true,
            RtsEnable = true
        };
        _port.Open();
    }

    public static bool TrySync(string portName, int baudRate = 115200, int attempts = 60)
    {
        using var port = new SerialPort(portName, baudRate)
        {
            ReadTimeout = 100,
            WriteTimeout = 100,
            DtrEnable = true,
            RtsEnable = true
        };
        port.Open();

        for (int i = 0; i < attempts; i++)
        {
            try
            {
                port.DiscardInBuffer();
                port.BaseStream.Flush();
                port.Write([(byte)Code.GET_SYNC, (byte)Code.EOC], 0, 2);
                if (port.ReadByte() == (byte)Code.INSYNC && port.ReadByte() == (byte)Code.OK)
                    return true;
            }
            catch { /* bootloader not ready yet */ }
            Thread.Sleep(100);
        }
        return false;
    }

    public bool TrySyncInstance(int attempts = 30)
    {
        var oldTimeout = _port.ReadTimeout;
        _port.ReadTimeout = 200; // Increased timeout for better reliability
        try
        {
            for (int i = 0; i < attempts; i++)
            {
                try
                {
                    _port.DiscardInBuffer();
                    _port.BaseStream.Flush();
                    Send([(byte)Code.GET_SYNC, (byte)Code.EOC]);

                    int first = _port.ReadByte();
                    if (first == (byte)Code.INSYNC)
                    {
                        int second = _port.ReadByte();
                        if (second == (byte)Code.OK)
                            return true;
                    }
                }
                catch
                {
                    // Ignore serial read timeout/errors
                }
                Thread.Sleep(100);
            }
            return false;
        }
        finally
        {
            _port.ReadTimeout = oldTimeout;
        }
    }

    public void Identify()
    {
        _port.DiscardInBuffer();
        Sync();
        BootloaderRev = GetInfo(Info.BL_REV);
        if (BootloaderRev < BlRevMin || BootloaderRev > BlRevMax)
            throw new InvalidOperationException($"Bootloader protocol {BootloaderRev} not supported");

        BoardType = GetInfo(Info.BOARD_ID);
        BoardRev = GetInfo(Info.BOARD_REV);
        FlashMaxSize = GetInfo(Info.FLASH_SIZE);

        if (BootloaderRev >= 5)
        {
            try { ChipDescription = GetChipDescription(); } catch { Sync(); }
            try { ExtFlashMaxSize = GetInfo(Info.EXTF_SIZE); } catch { Sync(); }
        }

        Log($"Board {BoardType} rev {BoardRev}, BL {BootloaderRev}, flash {FlashMaxSize}, ext {ExtFlashMaxSize}");
    }

    public void Upload(FirmwareImage fw, bool skipSameCheck = false, bool forceBoardMatch = false)
    {
        if (!forceBoardMatch && BoardType != fw.BoardId && !(BoardType == 33 && fw.BoardId == 9))
        {
            string? expectedName = FirmwareDownloadService.SupportedBoards
                .FirstOrDefault(b => b.BoardId == fw.BoardId)?.Name;
            string? detectedName = FirmwareDownloadService.SupportedBoards
                .FirstOrDefault(b => b.BoardId == BoardType)?.Name;

            throw new InvalidOperationException(
                $"Board mismatch: Firmware is for Board ID {fw.BoardId}" +
                (expectedName != null ? $" ({expectedName})" : "") +
                $", but detected board is Board ID {BoardType}" +
                (detectedName != null ? $" ({detectedName})" : "") +
                ". Please verify you have selected the correct board model.");
        }

        if (FlashMaxSize > 0 && fw.ImageSize > FlashMaxSize)
            throw new InvalidOperationException("Firmware image too large for board flash");

        if (!skipSameCheck && BootloaderRev >= 3 && fw.ImageSize > 0)
            CheckSameFirmware(fw);

        if (fw.ImageSize > 0)
        {
            Log("Erasing flash...");
            Erase();
            OnProgress?.Invoke(5);
        }

        if (fw.ExtImageSize > 0)
        {
            Log("Erasing external flash...");
            EraseExternal(fw.ExtImageSize);
            Log("Programming external flash...");
            ProgramExternal(fw.ExtImageBytes);
            Log("Verifying external flash...");
            VerifyExternal(fw);
            OnProgress?.Invoke(40);
        }

        if (fw.ImageSize > 0)
        {
            Log("Programming firmware...");
            Program(fw.ImageBytes);
            Log("Verifying firmware...");
            if (BootloaderRev == 2) VerifyV2(fw.ImageBytes);
            else VerifyV3(fw);
        }

        Log("Rebooting...");
        Reboot();
        OnProgress?.Invoke(100);
    }

    private void CheckSameFirmware(FirmwareImage fw)
    {
        var expected = fw.ComputeCrc(FlashMaxSize);
        Send([(byte)Code.GET_CRC, (byte)Code.EOC]);
        var report = RecvInt();
        ExpectSync();
        if (expected == report)
            throw new InvalidOperationException("Board already has this firmware (CRC match). Upload skipped.");
    }

    private void Erase()
    {
        Sync();
        GetInfo(Info.BL_REV);
        Send([(byte)Code.CHIP_ERASE, (byte)Code.EOC]);
        WaitForBytes(1, 30);
        ExpectSync();
    }

    private void EraseExternal(int size)
    {
        Sync();
        GetInfo(Info.BL_REV);
        var sizeBytes = BitConverter.GetBytes(size);
        Send([(byte)Code.EXTF_ERASE, sizeBytes[0], sizeBytes[1], sizeBytes[2], sizeBytes[3], (byte)Code.EOC]);
        ExpectSync();
    }

    private void Program(byte[] image)
    {
        var chunks = Split(image, ProgMultiMax);
        for (int i = 0; i < chunks.Count; i++)
        {
            Send([(byte)Code.PROG_MULTI, (byte)chunks[i].Length]);
            Send(chunks[i]);
            Send([(byte)Code.EOC]);
            ExpectSync();
            OnProgress?.Invoke(10 + (i + 1) * 70.0 / chunks.Count);
        }
    }

    private void ProgramExternal(byte[] image)
    {
        var chunks = Split(image, ProgMultiMax);
        foreach (var chunk in chunks)
        {
            Send([(byte)Code.EXTF_PROG_MULTI, (byte)chunk.Length]);
            Send(chunk);
            Send([(byte)Code.EOC]);
            ExpectSync();
        }
    }

    private void VerifyV2(byte[] image)
    {
        Send([(byte)Code.CHIP_VERIFY, (byte)Code.EOC]);
        ExpectSync();
        foreach (var chunk in Split(image, ReadMultiMax))
        {
            Send([(byte)Code.READ_MULTI, (byte)chunk.Length, (byte)Code.EOC]);
            var read = Recv(chunk.Length);
            if (!chunk.SequenceEqual(read))
                throw new InvalidOperationException("Firmware verification failed (read-back mismatch)");
            ExpectSync();
        }
    }

    private void VerifyV3(FirmwareImage fw)
    {
        var expected = fw.ComputeCrc(FlashMaxSize);
        Send([(byte)Code.GET_CRC, (byte)Code.EOC]);
        var report = RecvInt();
        ExpectSync();
        if (expected != report)
            throw new InvalidOperationException($"CRC mismatch: expected 0x{expected:X8}, got 0x{report:X8}");
    }

    private void VerifyExternal(FirmwareImage fw)
    {
        var expected = fw.ComputeExtCrc(fw.ExtImageSize);
        var sizeBytes = BitConverter.GetBytes(fw.ExtImageSize);
        Send([(byte)Code.EXTF_GET_CRC, sizeBytes[0], sizeBytes[1], sizeBytes[2], sizeBytes[3], (byte)Code.EOC]);
        WaitForBytes(4, 30);
        var report = RecvInt();
        ExpectSync();
        if (expected != report)
            throw new InvalidOperationException($"External CRC mismatch: expected 0x{expected:X8}, got 0x{report:X8}");
    }

    private void Sync()
    {
        _port.BaseStream.Flush();
        Send([(byte)Code.GET_SYNC, (byte)Code.EOC]);
        ExpectSync();
    }

    private int GetInfo(Info param)
    {
        Send([(byte)Code.GET_DEVICE, (byte)param, (byte)Code.EOC]);
        var val = RecvInt();
        ExpectSync();
        return val;
    }

    private string GetChipDescription()
    {
        Send([(byte)Code.GET_CHIP_DES, (byte)Code.EOC]);
        var len = RecvInt();
        if (len <= 0) { ExpectSync(); return ""; }
        var bytes = Recv(len);
        ExpectSync();
        return System.Text.Encoding.ASCII.GetString(bytes);
    }

    private void Reboot()
    {
        try
        {
            Send([(byte)Code.REBOOT, (byte)Code.EOC]);
            _port.DiscardInBuffer();
        }
        catch { /* port may close on reboot */ }
    }

    private void Send(byte[] data) => _port.Write(data, 0, data.Length);

    private byte[] Recv(int count)
    {
        var buf = new byte[count];
        int pos = 0;
        while (pos < count)
            pos += _port.Read(buf, pos, count - pos);
        return buf;
    }

    private int RecvInt() => BitConverter.ToInt32(Recv(4), 0);

    private void ExpectSync()
    {
        _port.BaseStream.Flush();
        var deadline = DateTime.UtcNow.AddMilliseconds(_port.ReadTimeout);
        while (_port.BytesToRead == 0)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Bootloader response timeout");
            Thread.Sleep(1);
        }

        var c = (Code)_port.ReadByte();
        if (c != Code.INSYNC) throw new InvalidOperationException($"Expected INSYNC, got 0x{(byte)c:X2}");
        c = (Code)_port.ReadByte();
        if (c == Code.INVALID || c == Code.FAILED)
            throw new InvalidOperationException($"Bootloader error: 0x{(byte)c:X2}");
        if (c != Code.OK)
            throw new InvalidOperationException($"Expected OK, got 0x{(byte)c:X2}");
    }

    private bool WaitForBytes(int count, int timeoutSec)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSec);
        while (DateTime.UtcNow < deadline)
        {
            _port.BaseStream.Flush();
            if (_port.BytesToRead >= count) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    private static List<byte[]> Split(byte[] data, int chunkSize)
    {
        var result = new List<byte[]>();
        for (int i = 0; i < data.Length;)
        {
            var size = Math.Min(chunkSize, data.Length - i);
            var chunk = new byte[size];
            Array.Copy(data, i, chunk, 0, size);
            result.Add(chunk);
            i += size;
        }
        return result;
    }

    private void Log(string msg) => OnLog?.Invoke(msg);

    public void Dispose()
    {
        try { if (_port.IsOpen) _port.Close(); } catch { }
        _port.Dispose();
    }
}
