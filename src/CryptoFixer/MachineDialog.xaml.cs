using System.Windows;
using CryptoFixer.Core.Remote;
using CryptoFixer.Services;
using CryptoFixer.ViewModels;

namespace CryptoFixer;

public partial class MachineDialog : Window
{
    private readonly Func<MachineDialogResult, string?> _validate;

    public MachineDialog(MachineEntry? existing, string filePath, Func<MachineDialogResult, string?> validate)
    {
        InitializeComponent();
        _validate = validate;

        Title = existing is null ? "Add remote machine" : $"Edit {existing.Name}";
        SaveButton.Content = existing is null ? "Add machine" : "Save";
        ProtocolBox.ItemsSource = new[]
        {
            new Choice<RemoteProtocol>(RemoteProtocol.WsMan, "WinRM (HTTP, port 5985) - recommended"),
            new Choice<RemoteProtocol>(RemoteProtocol.WsManHttps, "WinRM over HTTPS (port 5986)"),
            new Choice<RemoteProtocol>(RemoteProtocol.Dcom, "DCOM / RPC (port 135 and dynamic ports)"),
        };
        ProtocolBox.SelectedValue = existing?.Protocol ?? RemoteProtocol.WsMan;

        HostBox.Text = existing?.Host ?? string.Empty;
        NameBox.Text = existing?.Name ?? string.Empty;

        var credential = existing?.Credential ?? MachineCredential.CurrentAccount;
        if (credential.UsesCurrentAccount)
        {
            CurrentAccountRadio.IsChecked = true;
        }
        else
        {
            OtherAccountRadio.IsChecked = true;
            UserBox.Text = credential.UserName;
        }

        KeepPasswordHint.Visibility = credential.HasSavedPassword ? Visibility.Visible : Visibility.Collapsed;
        SavePasswordBox.IsChecked = existing is null || credential.HasSavedPassword;
        FileText.Text = $"Stored in {filePath}";
        UpdateAccountFields();

        Loaded += (_, _) =>
        {
            HostBox.Focus();
            HostBox.SelectAll();
        };
    }

    public MachineDialogResult? Result { get; private set; }

    private void OnCredentialModeChanged(object sender, RoutedEventArgs e) => UpdateAccountFields();

    private void UpdateAccountFields()
    {
        if (AccountFields is not null)
        {
            AccountFields.IsEnabled = OtherAccountRadio.IsChecked == true;
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var useCurrentAccount = CurrentAccountRadio.IsChecked == true;
        var host = HostBox.Text.Trim();
        var name = string.IsNullOrWhiteSpace(NameBox.Text) ? host : NameBox.Text.Trim();
        var password = !useCurrentAccount && PasswordInput.SecurePassword is { Length: > 0 } typed ? typed : null;

        var result = new MachineDialogResult(
            name,
            host,
            ProtocolBox.SelectedValue is RemoteProtocol protocol ? protocol : RemoteProtocol.WsMan,
            useCurrentAccount,
            useCurrentAccount ? null : UserBox.Text.Trim(),
            password,
            !useCurrentAccount && SavePasswordBox.IsChecked == true);

        var error = _validate(result);
        if (error is not null)
        {
            password?.Dispose();
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        password?.MakeReadOnly();
        Result = result;
        PasswordInput.Clear();
        DialogResult = true;
    }
}
