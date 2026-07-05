using System.Diagnostics;
using System.Windows;

namespace AIUsageMonitor;

public partial class LoginWindow : Window
{
    private readonly ClaudeAuth.PendingLogin _login;

    public LoginWindow()
    {
        InitializeComponent();
        _login = ClaudeAuth.BeginLogin(); // also opens the browser
        Loaded += (_, _) => CodeBox.Focus();
    }

    private void OnReopen(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_login.Url) { UseShellExecute = true });

    private async void OnOk(object sender, RoutedEventArgs e)
    {
        OkButton.IsEnabled = false;
        ErrorText.Text = "";
        try
        {
            await ClaudeAuth.CompleteLoginAsync(_login, CodeBox.Text);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.Message;
            OkButton.IsEnabled = true;
        }
    }
}
