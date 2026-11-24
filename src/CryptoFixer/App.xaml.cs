using System.IO;
using System.Windows;
using System.Windows.Threading;
using CryptoFixer.Core.Presets;
using CryptoFixer.Core.Registry;
using CryptoFixer.Core.Schannel;
using CryptoFixer.Core.Settings;
using CryptoFixer.Services;
using CryptoFixer.ViewModels;
using Microsoft.Win32;

namespace CryptoFixer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var system = new SystemActions();
        var forceReadOnly = e.Args.Any(a =>
            a.Equals("--read-only", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("/readonly", StringComparison.OrdinalIgnoreCase));
        var isElevated = system.IsElevated;
        var canWrite = isElevated && !forceReadOnly;
        var readOnlyReason = forceReadOnly
            ? "Started with --read-only."
            : isElevated ? string.Empty : "Not running as Administrator.";

        // In read-only mode the registry wrapper itself refuses writes, independent of the UI state.
        var registry = new WindowsRegistryAccess(RegistryHive.LocalMachine, readOnly: !canWrite);
        var os = WindowsVersionInfo.Read(registry, Environment.OSVersion.Version.Build);
        var backupFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CryptoFixer", "Backups");

        // Presets and settings live next to the executable (also for single-file publish) so they travel with a portable copy.
        var presetsPath = PresetFile.DefaultPath(AppContext.BaseDirectory);
        var settingsPath = AppSettingsFile.DefaultPath(AppContext.BaseDirectory);

        var window = new MainWindow();
        MainWindow = window;
        window.DataContext = new MainViewModel(
            new SchannelProtocolStore(registry), os, canWrite, readOnlyReason, backupFolder, presetsPath, settingsPath, new DialogService(), system);
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "CryptoFixer - unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
