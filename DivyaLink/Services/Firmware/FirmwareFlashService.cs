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
    /// Connects to the serial bootloader, identifies the board ID/Rev/Chip, and closes the connection.
    /// Tries multiple baud rates for maximum compatibility.
    /// </summary>
    public async Task<DetectedBoardInfo?> DetectBoardAsync(string portName, int baudRate = 115200)
    {
        try
        {
            return await Task.Run(() =>
            {
                // Define baud rates to try in order (highest first for best performance)
                int[] baudRatesToTry = new[] { 115200, 57600, 38400 };

                // Wait for port to exist (up to 5 seconds)
                bool portExists = false;
                for (int i = 0; i < 50; i++)
                {
                    if (System.IO.Ports.SerialPort.GetPortNames().Contains(portName))
                    {
                        portExists = true;
                        break;
                    }
                    Thread.Sleep(100);
                }
                if (!portExists) return null;

                // Try each baud rate in order
                foreach (int targetBaudRate in baudRatesToTry)
                {
                    var client = new Px4BootloaderClient(portName, targetBaudRate);
                    try
                    {
                        bool synced = client.TrySyncInstance(attempts: 15);
                        if (!synced)
                        {
                            client.Dispose();

                            // Send 1200 baud reset to trigger bootloader mode
                            Trigger1200BaudReset(portName);

                            // Wait for port to disconnect (indicating reset has started)
                            bool portDisconnected = false;
                            for (int i = 0; i < 60; i++) // 6 seconds max
                            {
                                if (!System.IO.Ports.SerialPort.GetPortNames().Contains(portName))
                                {
                                    portDisconnected = true;
                                    break;
                                }
                                Thread.Sleep(100);
                            }

                            // Wait for port to reconnect (indicating bootloader is ready)
                            bool portReconnected = false;
                            for (int i = 0; i < 100; i++) // 10 seconds max
                            {
                                if (System.IO.Ports.SerialPort.GetPortNames().Contains(portName))
                                {
                                    portReconnected = true;
                                    break;
                                }
                                Thread.Sleep(100);
                            }

                            if (!portReconnected)
                            {
                                client.Dispose();
                                continue; // Try next baud rate
                            }

                            // Give bootloader extra time to initialize after reconnect
                            Thread.Sleep(3000); // Additional 3 seconds for bootloader to be ready

                            client = new Px4BootloaderClient(portName, targetBaudRate);
                            if (!client.TrySyncInstance(attempts: 120))
                            {
                                client.Dispose();
                                continue; // Try next baud rate
                            }

                            client.Identify();
                            return new DetectedBoardInfo(client.BoardType, client.BoardRev, client.ChipDescription);
                        }
                        else
                        {
                            // Sync succeeded without reset
                            client.Identify();
                            return new DetectedBoardInfo(client.BoardType, client.BoardRev, client.ChipDescription);
                        }
                    }
                    finally
                    {
                        client.Dispose();
                    }
                }

                // If we get here, no baud rate worked
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
    /// Tries multiple baud rates for maximum compatibility with different USB cables and hardware.
    /// </summary>
    public async Task<bool> FlashAsync(
        string portName,
        string firmwarePath,
        FirmwareFamily targetFamily,
        int baudRate = 115200,
        bool forceUpload = false,
        bool forceBoardMatch = false)
    {
        // Define baud rates to try in order (highest first for best performance)
        int[] baudRatesToTry = new[] { 115200, 57600, 38400 };

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

        try
        {
            ValidateExtension(firmwarePath, targetFamily);
            Report(FlashState.Identifying, 1, "Loading firmware file...");
            var image = await Task.Run(() => FirmwareImage.ParseFile(firmwarePath), ct);

            Report(FlashState.WaitingForBootloader, 2, "Waiting for bootloader... Plug in or power cycle the FC USB.");

            await Task.Run(() =>
            {
                // 1. Wait for port to appear in system (up to 20 seconds)
                bool portExists = false;
                for (int i = 0; i < 200 && !ct.IsCancellationRequested; i++)
                {
                    if (System.IO.Ports.SerialPort.GetPortNames().Contains(portName))
                    {
                        portExists = true;
                        break;
                    }
                    Thread.Sleep(100);
                }

                if (!portExists)
                    throw new TimeoutException($"Serial port {portName} not found.");

                // Try each baud rate in order
                bool flashSuccessful = false;
                Px4BootloaderClient? successfulClient = null;

                foreach (int targetBaudRate in baudRatesToTry)
                {
                    if (ct.IsCancellationRequested) return;

                    Report(FlashState.WaitingForBootloader, 3, $"Attempting connection at {targetBaudRate} baud...");

                    // First, try to sync without reset (quick attempt)
                    var client = new Px4BootloaderClient(portName, targetBaudRate);
                    try
                    {
                        client.OnLog += msg => Report(FlashState.Programming, -1, msg);
                        client.OnProgress += pct =>
                        {
                            if (pct >= 0) Report(FlashState.Programming, pct, "Uploading...");
                        };

                        bool synced = client.TrySyncInstance(attempts: 20);
                        if (synced)
                        {
                            // Sync succeeded without reset - proceed with flashing
                            Report(FlashState.Identifying, 4, $"Bootloader detected at {targetBaudRate} baud. Reading board info...");
                            client.Identify();
                            Report(FlashState.Erasing, 5, $"Board {client.BoardType} — erasing...");
                            client.Upload(image, skipSameCheck: forceUpload, forceBoardMatch: forceBoardMatch);
                            flashSuccessful = true;
                            successfulClient = client;
                            break; // Success - exit baud rate loop
                        }
                    }
                    catch (Exception ex)
                    {
                        // Sync failed, continue to reset attempt
                        client.Dispose();
                        if (ct.IsCancellationRequested) return;
                        Report(FlashState.WaitingForBootloader, 6, $"Initial sync failed at {targetBaudRate} baud. Trying with bootloader reset...");
                    }
                    finally
                    {
                        if (!flashSuccessful) client.Dispose();
                    }

                    if (flashSuccessful) break; // Exit if we succeeded

                    // If we get here, initial sync failed. Try with bootloader reset.
                    client.Dispose(); // Ensure clean state

                    Report(FlashState.WaitingForBootloader, 7, "Sending bootloader reboot signal (1200 bps reset)...");
                    Trigger1200BaudReset(portName);

                    // Wait for port to disconnect (indicating reset has started)
                    Report(FlashState.WaitingForBootloader, 8, "Waiting for bootloader reset...");
                    bool portDisconnected = false;
                    for (int i = 0; i < 60 && !ct.IsCancellationRequested; i++) // 6 seconds max
                    {
                        if (!System.IO.Ports.SerialPort.GetPortNames().Contains(portName))
                        {
                            portDisconnected = true;
                            break;
                        }
                        Thread.Sleep(100);
                    }

                    // Wait for port to reconnect (indicating bootloader is ready)
                    Report(FlashState.WaitingForBootloader, 9, "Waiting for bootloader to appear...");
                    bool portReconnected = false;
                    for (int i = 0; i <= 100 && !ct.IsCancellationRequested; i++) // 10 seconds max
                    {
                        if (System.IO.Ports.SerialPort.GetPortNames().Contains(portName))
                        {
                            portReconnected = true;
                            break;
                        }
                        Thread.Sleep(100);
                    }

                    if (!portReconnected)
                    {
                        Report(FlashState.WaitingForBootloader, 10, "Bootloader did not appear after reset.");
                        continue; // Try next baud rate
                    }

                    // Give bootloader extra time to initialize after reconnect
                    Report(FlashState.WaitingForBootloader, 11, "Bootloader detected. Waiting for initialization...");
                    Thread.Sleep(3000); // Additional 3 seconds for bootloader to be ready

                    // Now try to sync at this baud rate with more attempts
                    Report(FlashState.WaitingForBootloader, 12, $"Bootloader detected. Attempting to sync at {targetBaudRate} baud...");
                    client = new Px4BootloaderClient(portName, targetBaudRate);
                    try
                    {
                        client.OnLog += msg => Report(FlashState.Programming, -1, msg);
                        client.OnProgress += pct =>
                        {
                            if (pct >= 0) Report(FlashState.Programming, pct, "Uploading...");
                        };

                        bool synced = client.TrySyncInstance(attempts: 100);
                        if (synced)
                        {
                            // Sync succeeded with reset - proceed with flashing
                            Report(FlashState.Identifying, 13, $"Bootloader detected at {targetBaudRate} baud after reset. Reading board info...");
                            client.Identify();
                            Report(FlashState.Erasing, 14, $"Board {client.BoardType} — erasing...");
                            client.Upload(image, skipSameCheck: forceUpload, forceBoardMatch: forceBoardMatch);
                            flashSuccessful = true;
                            successfulClient = client;
                            break; // Success - exit baud rate loop
                        }
                    }
                    catch (Exception ex)
                    {
                        // Sync failed even with reset
                        client.Dispose();
                        if (ct.IsCancellationRequested) return;
                        Report(FlashState.WaitingForBootloader, 15, $"Sync failed at {targetBaudRate} baud even after reset. Trying next baud rate...");
                    }
                    finally
                    {
                        if (!flashSuccessful) client.Dispose();
                    }
                }

                if (!flashSuccessful || successfulClient == null)
                {
                    throw new TimeoutException("Could not establish bootloader communication at any supported baud rate. Please try unplugging and replugging the USB cable, using a different USB port or cable, or check if the board requires a bootloader button to be held during connection.");
                }

                // If we got here, flashing was successful and successfulClient holds the client
                // The client was already disposed in the Upload method, so we don't need to do anything else
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
                _cts?.Dispose();
                _cts = null;
            }
        }
    }

    private async Task<bool> FlashSingleBaudAsync(
        string portName,
        string firmwarePath,
        FirmwareFamily targetFamily,
        int baudRate = 115200,
        bool forceUpload = false,
        bool forceBoardMatch = false)
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

        try
        {
            ValidateExtension(firmwarePath, targetFamily);
            Report(FlashState.Identifying, 1, "Loading firmware file...");
            var image = await Task.Run(() => FirmwareImage.ParseFile(firmwarePath), ct);

            Report(FlashState.WaitingForBootloader, 2, "Waiting for bootloader... Plug in or power cycle the FC USB.");

            await Task.Run(() =>
            {
                // 1. Wait for port to appear in system (up to 20 seconds)
                bool portExists = false;
                for (int i = 0; i < 200 && !ct.IsCancellationRequested; i++)
                {
                    if (System.IO.Ports.SerialPort.GetPortNames().Contains(portName))
                    {
                        portExists = true;
                        break;
                    }
                    Thread.Sleep(100);
                }

                if (!portExists)
                    throw new TimeoutException($"Serial port {portName} not found.");

                // 2. Open the port and try to sync
                var client = new Px4BootloaderClient(portName, baudRate);
                try
                {
                    client.OnLog += msg => Report(FlashState.Programming, -1, msg);
                    client.OnProgress += pct =>
                    {
                        if (pct >= 0) Report(FlashState.Programming, pct, "Uploading...");
                    };

                    // 3. Try to sync inside the open port
                    bool synced = client.TrySyncInstance(attempts: 60);
                    if (!synced)
                    {
                        client.Dispose();

                        Report(FlashState.WaitingForBootloader, 3, "Sending bootloader reboot signal (1200 bps reset)...");
                        Trigger1200BaudReset(portName);

                        // Wait for port to disconnect (indicating reset has started)
                        Report(FlashState.WaitingForBootloader, 4, "Waiting for bootloader reset...");
                        bool portDisconnected = false;
                        for (int i = 0; i < 60 && !ct.IsCancellationRequested; i++) // 6 seconds max
                        {
                            if (!System.IO.Ports.SerialPort.GetPortNames().Contains(portName))
                            {
                                portDisconnected = true;
                                break;
                            }
                            Thread.Sleep(100);
                        }

                        // Wait for port to reconnect (indicating bootloader is ready)
                        Report(FlashState.WaitingForBootloader, 6, "Waiting for bootloader to appear...");
                        bool portReconnected = false;
                        for (int i = 0; i < 100 && !ct.IsCancellationRequested; i++) // 10 seconds max
                        {
                            if (System.IO.Ports.SerialPort.GetPortNames().Contains(portName))
                            {
                                portReconnected = true;
                                break;
                            }
                            Thread.Sleep(100);
                        }

                        if (!portReconnected)
                        {
                            throw new TimeoutException("Bootloader did not appear after reset. Please unplug and replug the USB cable.");
                        }

                        // Give bootloader extra time to initialize after reconnect
                        Report(FlashState.WaitingForBootloader, 7, "Bootloader detected. Waiting for initialization...");
                        Thread.Sleep(3000); // Additional 3 seconds for bootloader to be ready

                        Report(FlashState.WaitingForBootloader, 8, "Bootloader detected. Attempting to sync...");
                        client = new Px4BootloaderClient(portName, baudRate);
                        client.OnLog += msg => Report(FlashState.Programming, -1, msg);
                        client.OnProgress += pct =>
                        {
                            if (pct >= 0) Report(FlashState.Programming, pct, "Uploading...");
                        };

                        // Try to sync with increased attempts
                        if (!client.TrySyncInstance(attempts: 300))
                        {
                            throw new TimeoutException("Could not sync with bootloader after extended attempts. The bootloader may be unresponsive. Please try unplugging and replugging the USB cable, or check if the board requires a bootloader button to be held during connection.");
                        }
                    }

                    Report(FlashState.Identifying, 5, "Bootloader detected. Reading board info...");
                    client.Identify();

                    Report(FlashState.Erasing, 7, $"Board {client.BoardType} — erasing...");
                    client.Upload(image, skipSameCheck: forceUpload, forceBoardMatch: forceBoardMatch);
                }
                finally
                {
                    client.Dispose();
                }
            }, ct);

            Report(FlashState.Complete, 100, $"{targetFamily} firmware uploaded successfully. Rebooting...");
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
