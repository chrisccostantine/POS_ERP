using System.Text.Json;
using System.IO;
using System.Windows;

namespace Scalora.Desktop;
public partial class App : Application
{
    private ApiClient? _api;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var settingsPath = Path.Combine(AppContext.BaseDirectory, "desktopsettings.json");
            using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
            var baseUrl = settings.RootElement.GetProperty("ApiBaseUrl").GetString()
                ?? throw new InvalidOperationException("ApiBaseUrl is missing from desktopsettings.json.");
            _api = new ApiClient(baseUrl);
            var store = new SessionStore();
            var session = store.Load();
            if (session is null)
            {
                var login = new LoginWindow(_api, store);
                if (login.ShowDialog() != true || login.Session is null) { Shutdown(); return; }
                session = login.Session;
            }
            _api.SetSession(session);
            var window = new MainWindow(new MainViewModel(_api, session));
            MainWindow = window; window.Show(); ShutdownMode = ShutdownMode.OnMainWindowClose;
            await ((MainViewModel)window.DataContext).InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Scalora startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e) { _api?.Dispose(); base.OnExit(e); }
}
