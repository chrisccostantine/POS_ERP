using System.Windows;

namespace Scalora.Desktop;

public partial class LoginWindow : Window
{
    private readonly ApiClient _api;
    private readonly SessionStore _sessions;
    public UserSession? Session { get; private set; }

    public LoginWindow(ApiClient api, SessionStore sessions) { InitializeComponent(); _api = api; _sessions = sessions; }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = false; ErrorText.Text = "";
        try
        {
            Session = await _api.LoginAsync(EmailBox.Text.Trim(), PasswordBox.Password);
            _sessions.Save(Session); DialogResult = true;
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; }
        finally { LoginButton.IsEnabled = true; }
    }
}
