using BlazorApp3.Components;
using Microsoft.AspNetCore.Components.Server;
using System.Diagnostics;
using System.IO;

// 1. Locate the embedded MediaMTX executable
string serverFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MediaServer");
string mtxExePath = Path.Combine(serverFolder, "mediamtx.exe");

var mtxProcess = new Process
{
    StartInfo = new ProcessStartInfo
    {
        FileName = mtxExePath,
        WorkingDirectory = serverFolder, // Ensures it reads the correct .yml file
        UseShellExecute = false,
        CreateNoWindow = true,           // Hides the black terminal window entirely
        RedirectStandardOutput = true,
        RedirectStandardError = true
    }
};

// 2. Start the server silently
try
{
    Console.WriteLine("[DivyaLink] Booting internal video server...");
    mtxProcess.Start();
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

// 1. Configure Logging to ensure your Console.WriteLine and _logger calls are visible
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// 2. Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// 3. Register MavlinkService as a Singleton first [cite: 137]
// MavlinkService needs IConfiguration
builder.Services.AddSingleton<MavlinkService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MavlinkService>());

// Log TCP configuration on startup
var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Startup");
logger.LogInformation("═══════════════════════════════════════");
logger.LogInformation("  DIVYALINK GROUND CONTROL STATION");
logger.LogInformation("═══════════════════════════════════════");
logger.LogInformation("TCP: {Host}:{Port}",
    builder.Configuration["TcpConnection:DefaultHost"],
    builder.Configuration["TcpConnection:DefaultPort"]);
logger.LogInformation("Video: {Enabled}",
    builder.Configuration["VideoStreaming:Enabled"]);
logger.LogInformation("═══════════════════════════════════════");

//builder.Services.AddHostedService<MediaServerService>();

// 5. Increase Circuit Options (Optional: helps with high-frequency telemetry)
builder.Services.Configure<CircuitOptions>(options => {
    options.DetailedErrors = true;
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(10);
});

// In Program.cs
builder.Services.AddServerSideBlazor().AddHubOptions(options => {
    options.MaximumReceiveMessageSize = 1024 * 128;
    options.HandshakeTimeout = TimeSpan.FromSeconds(60);
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
});


var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapStaticAssets();

// 6. Ensure the App uses InteractiveServer render mode globally
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// 3. Ensure the video server dies when the UI closes to prevent memory leaks
app.Lifetime.ApplicationStopping.Register(() =>
{
    if (!mtxProcess.HasExited)
    {
        Console.WriteLine("[DivyaLink] Shutting down internal video server...");
        mtxProcess.Kill();
    }
});

app.Run();