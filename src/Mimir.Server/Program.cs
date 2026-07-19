using Mimir.Server.Components;
using Mimir.Server.Configuration;
using Mimir.Server.Health;
using Mimir.Server.Models;
using Mimir.Server.Modules;
using Mimir.Server.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMimirOptions(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMimirHealth();
builder.Services.AddMimirStorage(builder.Configuration);
builder.Services.AddMimirModelClients();
builder.Services.AddMimirModules(builder.Configuration);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Spec §11: localhost only, on the configured port. Under Compose the container listens on 8080
// and the host publishes 6464 instead (spec §12), so an explicit ASPNETCORE_URLS wins.
// No HTTPS and no auth: localhost is the trust boundary in v1 (spec §12).
if (string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"]))
{
    var port = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>()?.Port
        ?? new ServerOptions().Port;
    builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapMimirModules();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
