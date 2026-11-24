using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using CryptoFixer.Core.Presets;
using CryptoFixer.Core.Registry;
using CryptoFixer.Core.Schannel;
using CryptoFixer.Core.Settings;
using CryptoFixer.Core.Updates;
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
            new SchannelProtocolStore(registry), os, canWrite, readOnlyReason, backupFolder, presetsPath, settingsPath, new DialogService(), system,
            CreateUpdateSetup(e.Args));
        window.Show();
    }

    private static UpdateSetup CreateUpdateSetup(string[] args)
    {
        var current = CurrentVersion();
        var client = new UpdateClient(current);
        var exePath = Environment.ProcessPath;
        var runtimeId = ReleaseInfo.RuntimeIdFor(RuntimeInformation.ProcessArchitecture);

        // Only the published single-file exe can swap itself; a development build runs from a folder of DLLs.
#pragma warning disable IL3000
        var isSingleFile = string.IsNullOrEmpty(typeof(App).Assembly.Location);
#pragma warning restore IL3000

        UpdateInstaller? installer = null;
        string? unavailable = null;
        if (!isSingleFile)
        {
            unavailable = "This copy of CryptoFixer isn't the single-file release build, so it can't replace itself. Download the new version from the release page instead.";
        }
        else if (exePath is null)
        {
            unavailable = "The location of CryptoFixer.exe couldn't be determined, so it can't be replaced.";
        }
        else if (runtimeId is null)
        {
            unavailable = $"CryptoFixer releases don't include a build for {RuntimeInformation.ProcessArchitecture} processors.";
        }
        else
        {
            installer = new UpdateInstaller(client, exePath, runtimeId, Path.Combine(Path.GetTempPath(), "CryptoFixer-update"));
        }

        string? updatedFrom = null;
        if (isSingleFile && exePath is not null)
        {
            if (AppVersion.TryParse(ExecutableReplacer.FindReplacedVersion(exePath), out var previous) && previous != current)
            {
                updatedFrom = $"Updated from CryptoFixer {previous} to {current}. Your presets and settings were kept.";
            }

            // The previous version may still be exiting, so its file can take a moment to become deletable.
            _ = ExecutableReplacer.DeleteLeftoversAsync(exePath, TimeSpan.FromSeconds(30));
        }

        return new UpdateSetup(client, exePath, installer, unavailable, CommandLine.Join(args), updatedFrom);
    }

    private static AppVersion CurrentVersion()
    {
        var assembly = typeof(App).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return AppVersion.TryParse(informational, out var version)
            ? version
            : AppVersion.FromVersion(assembly.GetName().Version ?? new Version(0, 0, 0));
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "CryptoFixer - unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
