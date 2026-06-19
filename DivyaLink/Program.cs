using DivyaLink.Components;
using Microsoft.AspNetCore.Components.Server;
using System.Diagnostics;
using System.IO;
using DivyaLink.Services;
using DivyaLink.Services.Firmware;

using System.Runtime.InteropServices;

// 1. Locate the embedded MediaMTX executable
string serverFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MediaServer");
string binaryName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "mediamtx.exe" : "mediamtx";
string mtxExePath = Path.Combine(serverFolder, binaryName);

if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && File.Exists(mtxExePath))
{
    try
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "chmod",
            Arguments = $"+x \"{mtxExePath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        })?.WaitForExit();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[DivyaLink] Failed to set executable permissions on MediaMTX: {ex.Message}");
    }
}

var mtxProcess = new Process
{
    StartInfo = new ProcessStartInfo
    {
        FileName = mtxExePath,
        WorkingDirectory = serverFolder,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    }
};

bool mtxStarted = false;
try
{
    Console.WriteLine("[DivyaLink] Booting internal video server...");
    bool started = mtxProcess.Start();
    if (started)
    {
        mtxStarted = true;
        mtxProcess.BeginOutputReadLine();
        mtxProcess.BeginErrorReadLine();
        mtxProcess.OutputDataReceived += (sender, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine($"[MediaMTX] {e.Data}"); };
        mtxProcess.ErrorDataReceived  += (sender, e) => { if (!string.IsNullOrEmpty(e.Data)) Console.WriteLine($"[MediaMTX] {e.Data}"); };
        Console.WriteLine("[DivyaLink] Video server started (PID {0})", mtxProcess.Id);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"[DivyaLink] Failed to start video server: {ex.Message}");
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});

// Logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// Razor + Blazor
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// ── NEW: MVC Controllers (needed for AirspaceController REST endpoint) ────────
builder.Services.AddControllers();
// ─────────────────────────────────────────────────────────────────────────────

// Firmware + vehicle profile services
builder.Services.AddHttpClient("Px4MetadataClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DivyaLink-GCS/1.0");
});
builder.Services.AddHttpClient("FirmwareDownloaderClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(180); // Longer timeout for larger manifest files
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DivyaLink-GCS/1.0");
});
builder.Services.AddSingleton<Px4ParameterMetadataService>(sp =>
{
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("Px4MetadataClient");
    return new Px4ParameterMetadataService(http, sp.GetRequiredService<ILogger<Px4ParameterMetadataService>>());
});
builder.Services.AddSingleton<ArduPilotFirmwareAdapter>();
builder.Services.AddSingleton<Px4FirmwareAdapter>();
builder.Services.AddSingleton<FirmwareProfileService>();
builder.Services.AddSingleton<FirmwareFlashService>();
builder.Services.AddSingleton<FirmwareDownloadService>();
builder.Services.AddSingleton<VehicleProfileService>();

builder.Services.AddSingleton<OverlayService>();
builder.Services.AddSingleton<LicenseService>();

// MAVLink
builder.Services.AddSingleton<MavlinkService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MavlinkService>());

// Parameter services
builder.Services.AddHttpClient("ArduPilotClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DivyaLink-GCS/1.0");
});
builder.Services.AddSingleton<ParameterMetadataService>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var client = httpClientFactory.CreateClient("ArduPilotClient");
    var logger = sp.GetRequiredService<ILogger<ParameterMetadataService>>();
    return new ParameterMetadataService(client, logger);
});
builder.Services.AddSingleton<ParameterManager>(sp =>
{
    var mavlink  = sp.GetRequiredService<MavlinkService>();
    var metadata = sp.GetRequiredService<ParameterMetadataService>();
    var firmware = sp.GetRequiredService<FirmwareProfileService>();
    var vehicle  = sp.GetRequiredService<VehicleProfileService>();
    var logger   = sp.GetRequiredService<ILogger<ParameterManager>>();
    var manager  = new ParameterManager(mavlink, metadata, firmware, vehicle, logger);
    mavlink.ParameterManager = manager;
    return manager;
});

builder.Services.AddSingleton<NtripService>();
builder.Services.AddSingleton<RtkBaseStationService>();

// ── DGCA Airspace services ────────────────────────────────────────────────────
builder.Services.AddMemoryCache();

builder.Services.AddHttpClient("DgcaClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("DivyaLink-GCS/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});

// Scoped: one instance per Blazor circuit / HTTP request
builder.Services.AddScoped<DgcaAirspaceService>();
// ─────────────────────────────────────────────────────────────────────────────

// Startup log
var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Startup");
logger.LogInformation("═══════════════════════════════════════");
logger.LogInformation("  DIVYALINK GROUND CONTROL STATION");
logger.LogInformation("═══════════════════════════════════════");
logger.LogInformation("TCP:   {Host}:{Port}",
    builder.Configuration["TcpConnection:DefaultHost"],
    builder.Configuration["TcpConnection:DefaultPort"]);
logger.LogInformation("Video: {Enabled}", builder.Configuration["VideoStreaming:Enabled"]);
logger.LogInformation("DGCA:  AuthToken={HasToken}",
    string.IsNullOrWhiteSpace(builder.Configuration["DgcaAirspace:AuthToken"]) ? "NOT SET (mock mode)" : "SET");
logger.LogInformation("═══════════════════════════════════════");

// Circuit options
builder.Services.Configure<CircuitOptions>(options =>
{
    options.DetailedErrors = true;
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(10);
});

builder.Services.AddServerSideBlazor().AddHubOptions(options =>
{
    options.MaximumReceiveMessageSize = 1024 * 128;
    options.HandshakeTimeout = TimeSpan.FromSeconds(60);
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
});

var app = builder.Build();



_ = app.Services.GetRequiredService<ParameterManager>();
_ = app.Services.GetRequiredService<VehicleProfileService>();
_ = app.Services.GetRequiredService<NtripService>();
_ = app.Services.GetRequiredService<RtkBaseStationService>();

// Resolve and log license info
var licenseSvc = app.Services.GetRequiredService<LicenseService>();
logger.LogInformation("License:    {Tier} ({Days})", licenseSvc.LicenseTier, licenseSvc.RemainingDaysText);
logger.LogInformation("Hardware ID: {HwId}", licenseSvc.HardwareId);
logger.LogInformation("═══════════════════════════════════════");


if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

// ── NEW: Map the API controller routes ───────────────────────────────────────
app.MapControllers();
// ─────────────────────────────────────────────────────────────────────────────

app.MapStaticAssets();

    // Offline map tile endpoints
    app.MapGet("/offline-tiles/{z}/{x}/{y}.png", async (int z, int x, int y) =>
    {
        var tilePath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "offline-cache", z.ToString(), x.ToString(), $"{y}.png");
        if (File.Exists(tilePath))
        {
            var bytes = await File.ReadAllBytesAsync(tilePath);
            return Results.File(bytes, "image/png");
        }
        return Results.NotFound();
    });
    // Metadata endpoint (optional placeholder)
    app.MapGet("/offline-map-metadata", () =>
    {
        // Return simple JSON metadata about offline map availability
        return Results.Json(new { Available = true });
    });


app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Lifetime.ApplicationStopping.Register(() =>
{
    if (mtxStarted)
    {
        try
        {
            if (!mtxProcess.HasExited)
            {
                Console.WriteLine("[DivyaLink] Shutting down internal video server...");
                mtxProcess.Kill();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DivyaLink] Error killing video server process: {ex.Message}");
        }
    }
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        var address = app.Urls.FirstOrDefault();
        if (string.IsNullOrEmpty(address))
        {
            address = "http://localhost:5006";
        }
        else
        {
            address = address.Replace("+", "localhost")
                             .Replace("*", "localhost")
                             .Replace("0.0.0.0", "localhost")
                             .Replace("[::]", "localhost");
        }

        Console.WriteLine($"[DivyaLink] Launching standalone app window pointing to: {address}");

        bool launched = false;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Common paths for Chrome and Edge executables on Windows
            var browserPaths = new[]
            {
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                "msedge.exe",
                "chrome.exe"
            };

            foreach (var path in browserPaths)
            {
                try
                {
                    if (path.Contains(":") && !File.Exists(path))
                    {
                        continue;
                    }

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        Arguments = $"--app={address}",
                        UseShellExecute = !path.Contains(":")
                    });
                    launched = true;
                    break;
                }
                catch
                {
                    // Try the next browser path
                }
            }
        }

        if (!launched)
        {
            // Fallback: Use default system browser
            Process.Start(new ProcessStartInfo
            {
                FileName = address,
                UseShellExecute = true
            });
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[DivyaLink] Error launching browser window: {ex.Message}");
    }
});

app.Run();