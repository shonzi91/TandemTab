using FinApp.App.Web;
using FinApp.Shared.UI.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Where the FinApp sync server lives. Read from wwwroot/appsettings[.Development].json ("ApiBaseUrl").
// When unset, fall back to this app's own origin — the one-origin deployment where the server hosts
// both the API and these static files. Local cross-origin dev sets it in appsettings.Development.json.
var apiBaseUrl = builder.Configuration["ApiBaseUrl"];
if (string.IsNullOrWhiteSpace(apiBaseUrl))
    apiBaseUrl = builder.HostEnvironment.BaseAddress;

builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(apiBaseUrl) });
builder.Services.AddSingleton(new ClientOptions { BaseUrl = apiBaseUrl });
builder.Services.AddScoped<FinAppApiClient>();
builder.Services.AddScoped<ITokenStore, WebTokenStore>();
builder.Services.AddScoped<Localizer>();
builder.Services.AddScoped<AuthState>();
builder.Services.AddScoped<SyncClient>();
builder.Services.AddScoped<BudgetingState>();
builder.Services.AddScoped<ClientErrorReporter>();
builder.Services.AddScoped<PlanGate>();          // P4: gates that stay inert until the monetization flag lifts
builder.Services.AddScoped<AssistantResolver>(); // R3: masks a question against this account before it is sent

// Error reporting (OPEN-BETA B1). An unhandled Blazor render exception surfaces as a Critical log from
// WebAssemblyRenderer — exactly what BUG-1 produced — so forwarding Error/Critical logs catches that whole class
// of failure. The provider resolves the reporter lazily because logging is configured before the container is
// built; anything logged before then simply has nowhere to go yet, which is fine.
IServiceProvider? services = null;
builder.Logging.AddProvider(new ClientErrorLoggerProvider(() =>
    services?.GetService(typeof(ClientErrorReporter)) as ClientErrorReporter));

var host = builder.Build();
services = host.Services;

await host.RunAsync();
