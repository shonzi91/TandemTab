using FinApp.Shared.UI.Services;
using Microsoft.Extensions.Logging;

namespace FinApp.App.Maui;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
			});

		builder.Services.AddMauiBlazorWebView();

		// FinApp client, talking to the sync server. Debug builds hit a local dev server;
		// Release builds (what ships to phones) hit production.
#if DEBUG
		const string serverUrl = "http://localhost:5179";
#else
		const string serverUrl = "https://tandemtab.com";
#endif
		builder.Services.AddSingleton(new ClientOptions { BaseUrl = serverUrl });
		builder.Services.AddSingleton(_ => new HttpClient { BaseAddress = new Uri(serverUrl) });
		builder.Services.AddSingleton<FinAppApiClient>();
		builder.Services.AddSingleton<ITokenStore, MauiTokenStore>();
		builder.Services.AddSingleton<Localizer>();
		builder.Services.AddSingleton<AuthState>();
		builder.Services.AddSingleton<SyncClient>();
		builder.Services.AddSingleton<BudgetingState>();

#if DEBUG
		builder.Services.AddBlazorWebViewDeveloperTools();
		builder.Logging.AddDebug();
#endif

		return builder.Build();
	}
}
