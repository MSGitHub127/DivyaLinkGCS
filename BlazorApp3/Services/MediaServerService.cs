using System.Diagnostics;
using Microsoft.Extensions.Hosting;

public class MediaServerService : IHostedService, IDisposable
{
    private Process? _mediaProcess;
    private readonly string _mtxPath = "mediamtx.exe"; // Path to your MediaMTX binary

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // 1. Force-kill any existing MediaMTX instances to free up ports
            foreach (var proc in Process.GetProcessesByName("mediamtx"))
            {
                try { proc.Kill(); proc.WaitForExit(1000); } catch { }
            }

            _mediaProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _mtxPath,
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = false, // Set to false to avoid pipe-clogging
                    RedirectStandardError = false
                }
            };

            _mediaProcess.Start();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VIDEO] Startup Error: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[VIDEO] Shutting down MediaMTX...");
        try
        {
            if (_mediaProcess != null && !_mediaProcess.HasExited)
            {
                _mediaProcess.Kill();
            }
        }
        catch { }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _mediaProcess?.Dispose();
    }
}