using BlazorApp3.Components;
using Microsoft.AspNetCore.Components.Server;

var builder = WebApplication.CreateBuilder(args);

// 1. Configure Logging to ensure your Console.WriteLine and _logger calls are visible
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// 2. Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// 3. Register MavlinkService as a Singleton first [cite: 137]
// This ensures the UI and Background worker share the EXACT same memory instance.
builder.Services.AddSingleton<MavlinkService>();

// 4. Start the Singleton as a Background Service [cite: 137]
builder.Services.AddHostedService(sp => sp.GetRequiredService<MavlinkService>());

// 5. Increase Circuit Options (Optional: helps with high-frequency telemetry)
builder.Services.Configure<CircuitOptions>(options => {
    options.DetailedErrors = true;
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