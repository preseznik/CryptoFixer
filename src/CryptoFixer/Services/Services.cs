using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using CryptoFixer.Core.Updates;
using Microsoft.Win32;

namespace CryptoFixer.Services;

public sealed record PresetDetails(string Name, string? Description);

/// <param name="InstallBlocker">Why the update can't be installed automatically; the dialog then offers the download page instead.</param>
/// <param name="PendingWarning">Shown when restarting would discard staged changes.</param>
/// <param name="DownloadSize">Size of the executable that would be downloaded, if known.</param>
public sealed record UpdateOffer(AppVersion CurrentVersion, ReleaseInfo Release, Uri ReleasePage, string? InstallBlocker, string? PendingWarning, long? DownloadSize = null);

public enum UpdateDecision
{
    NotNow,
    Install,
    OpenReleasePage,
}

public interface IDialogService
{
    bool Confirm(string title, string message, bool isWarning = false);

    void ShowInfo(string title, string message);

    void ShowError(string title, string message);

    /// <summary>Returns the chosen path, or null when cancelled.</summary>
    string? PickSaveFile(string initialFolder, string defaultFileName);

    /// <summary>Asks for a preset name and description. <paramref name="validateName"/> returns an error message or null.</summary>
    PresetDetails? AskPresetDetails(string suggestedName, string filePath, Func<string, string?> validateName);

    UpdateDecision ShowUpdate(UpdateOffer offer);
}

public interface ISystemActions
{
    bool IsElevated { get; }

    void RestartComputer();

    void OpenFolder(string folder);

    void OpenInTextEditor(string path);

    void OpenUrl(Uri url);

    /// <summary>Starts <paramref name="exePath"/> through the shell, so it runs with the same elevation as this process.</summary>
    void StartProgram(string exePath, string arguments);

    void ExitApplication();
}

public sealed class DialogService : IDialogService
{
    public bool Confirm(string title, string message, bool isWarning = false) =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.YesNo,
            isWarning ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void ShowInfo(string title, string message) =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string title, string message) =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public string? PickSaveFile(string initialFolder, string defaultFileName)
    {
        Directory.CreateDirectory(initialFolder);
        var dialog = new SaveFileDialog
        {
            Title = "Export Schannel protocol backup",
            InitialDirectory = initialFolder,
            FileName = defaultFileName,
            DefaultExt = ".reg",
            Filter = "Registry files (*.reg)|*.reg|All files (*.*)|*.*",
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(Owner()) == true ? dialog.FileName : null;
    }

    public PresetDetails? AskPresetDetails(string suggestedName, string filePath, Func<string, string?> validateName)
    {
        var dialog = new SavePresetDialog(suggestedName, filePath, validateName) { Owner = Owner() };
        return dialog.ShowDialog() == true ? new PresetDetails(dialog.PresetName, dialog.PresetDescription) : null;
    }

    public UpdateDecision ShowUpdate(UpdateOffer offer)
    {
        var dialog = new UpdateDialog(offer) { Owner = Owner() };
        dialog.ShowDialog();
        return dialog.Decision;
    }

    private static Window Owner() => Application.Current.MainWindow;
}

public sealed class SystemActions : ISystemActions
{
    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public void RestartComputer() =>
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 10 /d p:2:4 /c \"CryptoFixer: restarting to apply Schannel protocol changes.\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });

    public void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    public void OpenInTextEditor(string path) =>
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = false });

    public void OpenUrl(Uri url)
    {
        if (url.Scheme == Uri.UriSchemeHttps)
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
    }

    public void StartProgram(string exePath, string arguments) =>
        Process.Start(new ProcessStartInfo(exePath, arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.CurrentDirectory,
        });

    public void ExitApplication() => Application.Current.Shutdown();
}
