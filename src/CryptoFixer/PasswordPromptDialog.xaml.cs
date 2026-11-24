using System.Windows;
using CryptoFixer.Core.Remote;
using CryptoFixer.Services;

namespace CryptoFixer;

public partial class PasswordPromptDialog : Window
{
    public PasswordPromptDialog(MachineEntry machine, string? reason)
    {
        InitializeComponent();
        Title = $"Password for {machine.Name}";
        PromptText.Text = $"Enter the password of {machine.Credential.UserName} to connect to {machine.Name} ({machine.Host}).";
        if (reason is not null)
        {
            ReasonText.Text = reason;
            ReasonText.Visibility = Visibility.Visible;
        }

        SavePasswordBox.IsChecked = machine.Credential.HasSavedPassword;
        Loaded += (_, _) => PasswordInput.Focus();
    }

    public PasswordPromptResult? Result { get; private set; }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        if (PasswordInput.SecurePassword is not { Length: > 0 } password)
        {
            ErrorText.Text = "Enter the password.";
            ErrorText.Visibility = Visibility.Visible;
            PasswordInput.Focus();
            return;
        }

        password.MakeReadOnly();
        Result = new PasswordPromptResult(password, SavePasswordBox.IsChecked == true);
        PasswordInput.Clear();
        DialogResult = true;
    }
}
