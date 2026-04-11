using BlazorApp3.Components;
using Microsoft.AspNetCore.Components.Server;

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

app.Run();