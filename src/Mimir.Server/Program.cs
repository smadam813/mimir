using Mimir.Server.Components;
using Mimir.Server.Configuration;
using Mimir.Server.Health;
using Mimir.Server.Models;
using Mimir.Server.Modules;
using Mimir.Server.Storage;
using Mimir.Server.Ui;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMimirOptions(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMimirHealth();
builder.Services.AddMimirStorage(builder.Configuration);
builder.Services.AddMimirModelClients();
builder.Services.AddMimirModules(builder.Configuration);
builder.Services.AddMimirUi();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

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
