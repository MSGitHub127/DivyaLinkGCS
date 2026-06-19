using DivyaLink.Services.Firmware.Flash;

namespace DivyaLink.Services.Firmware;

public enum FlashState
{
    Idle,
    WaitingForBootloader,
    Identifying,
    Erasing,
    Programming,
    Verifying,
    Complete,
    Error
}

public sealed record FlashProgressEvent(FlashState State, double Percent, string Message);

/// <summary>
/// Flashes ArduPilot (.apj) or PX4 (.px4) firmware via the PX4-compatible serial bootloader.
/// </summary>
public sealed class FirmwareFlashService
{
    private readonly ILogger<FirmwareFlashService> _log;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    public FlashState State { get; private set; } = FlashState.Idle;
    public string? LastError { get; private set; }
    public bool IsFlashing { get; private set; }

    public event Action<FlashProgressEvent>? OnProgress;

    public FirmwareFlashService(ILogger<FirmwareFlashService> log) => _log = log;

    public void Cancel()
    {
        lock (_lock) { _cts?.Cancel(); }
    }

    public sealed record DetectedBoardInfo(int BoardId, int BoardRev, string ChipDescription);

    public static void Trigger1200BaudReset(string portName)
    {
        try
        {
            using var port = new System.IO.Ports.SerialPort(portName, 1200);
            port.Open();
            port.DtrEnable = true;
            port.RtsEnable = true;
            port.Close();
        }
        catch
        {
            // Ignore if port cannot be opened
        }
    }

    /// <summary>
    /// <summary>
    /// Connects to the serial bootloader, identifies the board ID/Rev/Chip, and closes the connection.
    /// </summary>
    public async Task<DetectedBoardInfo?> DetectBoardAsync(string portName, int baudRate = 115200)
    {
        try
        {
            return await Task.Run(() =>
            {
                // 1. Try immediate sync in case it's already in bootloader mode
                try
                {
                    using var client = new Px4BootloaderClient(portName, baudRate);
                    if (client.TrySyncInstance(attempts: 15))
                    {
                        client.Identify();
                        return new DetectedBoardInfo(client.BoardType, client.BoardRev, client.ChipDescription);
                    }
                }
                catch { }

                // 2. Trigger reset and wait for bootloader to initialize
                Trigger1200BaudReset(portName);
                Thread.Sleep(200); // Give OS a moment to begin port disconnection

                // 3. Enter aggressive connection loop (up to 8 seconds)
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (DateTime.UtcNow < deadline)
                {
                    try
                    {
                        var client = new Px4BootloaderClient(portName, baudRate);
                        try
                        {
                            if (client.TrySyncInstance(attempts: 15))
                            {
                                client.Identify();
                                return new DetectedBoardInfo(client.BoardType, client.BoardRev, client.ChipDescription);
                            }
                        }
                        finally
                        {
                            client.Dispose();
                        }
                    }
                    catch
                    {
                        // Port is detaching/re-attaching, or sync failed. Retry.
                    }
                    Thread.Sleep(100);
                }

                return null;
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Firmware] Failed to auto-detect board on {Port}", portName);
            return null;
        }
    }

    /// <summary>
    /// Upload firmware to the flight controller. Disconnect MAVLink before calling.
    /// User must put the board in bootloader mode (unplug/replug or Force Bootloader).
    /// </summary>
    public async Task<bool> FlashAsync(
        string portName,
        string firmwarePath,
        FirmwareFamily targetFamily,
        int baudRate = 115200,
        bool forceUpload = false)
    {
        lock (_lock)
        {
            if (IsFlashing)
            {
                LastError = "A flash operation is already in progress.";
                return false;
            }
            IsFlashing = true;
            _cts = new CancellationTokenSource();
        }

        var ct = _cts!.Token;
        Px4BootloaderClient? successfulClient = null;

        try
        {
            ValidateExtension(firmwarePath, targetFamily);
            Report(FlashState.Identifying, 1, "Loading firmware file...");
            var image = await Task.Run(() => FirmwareImage.ParseFile(firmwarePath), ct);

            Report(FlashState.WaitingForBootloader, 2, "Waiting for bootloader... Connecting...");

            bool flashSuccessful = false;

            await Task.Run(() =>
            {
                // 1. Try immediate sync (in case the board is already in bootloader mode)
                try
                {
                    var client = new Px4BootloaderClient(portName, baudRate);
                    client.OnLog += msg => Report(FlashState.Programming, -1, msg);
                    client.OnProgress += pct =>
                    {
                        if (pct >= 0) Report(FlashState.Programming, pct, "Uploading...");
                    };

                    if (client.TrySyncInstance(attempts: 15))
                    {
                        Report(FlashState.Identifying, 4, "Bootloader detected. Reading board info...");
                        client.Identify();
                        Report(FlashState.Erasing, 5, $"Board {client.BoardType} — erasing...");
                        client.Upload(image, skipSameCheck: forceUpload);
                        flashSuccessful = true;
                        successfulClient = client;
                    }
                    else
                    {
                        client.Dispose();
                    }
                }
                catch { }

                if (flashSuccessful) return;

                // 2. Trigger reset since immediate sync failed
                Report(FlashState.WaitingForBootloader, 6, "Sending bootloader reboot signal...");
                Trigger1200BaudReset(portName);
                Thread.Sleep(200); // Give OS a moment to begin port disconnection

                // 3. Loop attempting to open the port and sync aggressively (up to 12 seconds)
                var deadline = DateTime.UtcNow.AddSeconds(12);
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    Px4BootloaderClient? client = null;
                    try
                    {
                        client = new Px4BootloaderClient(portName, baudRate);
                        client.OnLog += msg => Report(FlashState.Programming, -1, msg);
                        client.OnProgress += pct =>
                        {
                            if (pct >= 0) Report(FlashState.Programming, pct, "Uploading...");
                        };

                        if (client.TrySyncInstance(attempts: 15))
                        {
                            Report(FlashState.Identifying, 13, "Bootloader detected. Reading board info...");
                            client.Identify();
                            Report(FlashState.Erasing, 14, $"Board {client.BoardType} — erasing...");
                            client.Upload(image, skipSameCheck: forceUpload);
                            flashSuccessful = true;
                            successfulClient = client;
                            break;
                        }
                    }
                    catch
                    {
                        // Open failed or sync failed
                    }
                    finally
                    {
                        if (!flashSuccessful) client?.Dispose();
                    }

                    Thread.Sleep(100);
                }

                if (!flashSuccessful || successfulClient == null)
                {
                    throw new TimeoutException("Could not establish bootloader communication. Please try unplugging and replugging the USB cable, or holding the bootloader button during power up.");
                }

                Report(FlashState.Complete, 100, $"{targetFamily} firmware uploaded successfully. Rebooting...");
            }, ct);

            return true;
        }
        catch (OperationCanceledException)
        {
            LastError = "Flash cancelled.";
            Report(FlashState.Error, 0, LastError);
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.LogError(ex, "[Firmware] Flash failed");
            Report(FlashState.Error, 0, ex.Message);
            return false;
        }
        finally
        {
            lock (_lock)
            {
                IsFlashing = false;
                successfulClient?.Dispose();
                _cts?.Dispose();
                _cts = null;
            }
        }
    }

    private static void ValidateExtension(string path, FirmwareFamily family)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var expected = family switch
        {
            FirmwareFamily.ArduPilot => ".apj",
            FirmwareFamily.PX4 => ".px4",
            _ => throw new ArgumentException("Select ArduPilot or PX4 firmware target.")
        };
        if (ext != expected)
            throw new ArgumentException($"Expected a {expected} file for {family}, got {ext}");
    }

    private void Report(FlashState state, double percent, string message)
    {
        State = state;
        OnProgress?.Invoke(new FlashProgressEvent(state, percent, message));
    }
}
