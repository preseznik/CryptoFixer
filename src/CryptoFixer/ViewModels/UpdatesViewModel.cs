using System.Windows.Input;
using CryptoFixer.Core.Updates;
using CryptoFixer.Services;

namespace CryptoFixer.ViewModels;

/// <summary>Everything the update feature needs, prepared at startup.</summary>
/// <param name="ExePath">The running executable; null when it couldn't be determined.</param>
/// <param name="Installer">Null when this copy can't replace itself; <paramref name="InstallUnavailableReason"/> says why.</param>
/// <param name="RelaunchArguments">Command line for starting the new version, so it runs in the same mode.</param>
/// <param name="UpdatedFromMessage">Set on the first start after an update.</param>
public sealed record UpdateSetup(
    UpdateClient Client,
    string? ExePath,
    UpdateInstaller? Installer,
    string? InstallUnavailableReason,
    string RelaunchArguments,
    string? UpdatedFromMessage = null);

/// <summary>The parts of the main window the update feature talks to.</summary>
public interface IUpdateHost
{
    /// <summary>A warning that restarting discards staged changes, or null when there are none.</summary>
    string? PendingChangesWarning { get; }

    void ReportStatus(string message);

    /// <summary>Called right before the app exits so the new version can start; closing must not prompt any more.</summary>
    void PrepareForRestart();
}

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Failed,
}

public sealed class UpdatesViewModel : ObservableObject
{
    private readonly UpdateSetup _setup;
    private readonly IDialogService _dialogs;
    private readonly ISystemActions _system;
    private readonly IUpdateHost _host;
    private UpdateState _state = UpdateState.Idle;
    private string _statusText = string.Empty;
    private string? _statusDetail;
    private ReleaseInfo? _availableRelease;
    private bool _bannerDismissed;
    private double _downloadPercent;
    private bool _isProgressIndeterminate = true;
    private string _downloadText = string.Empty;
    private CancellationTokenSource? _downloadCancellation;

    public UpdatesViewModel(UpdateSetup setup, IDialogService dialogs, ISystemActions system, IUpdateHost host)
    {
        _setup = setup;
        _dialogs = dialogs;
        _system = system;
        _host = host;

        CheckNowCommand = new RelayCommand(() => _ = CheckAsync(userInitiated: true), () => !IsBusy);
        InstallCommand = new RelayCommand(() => _ = OfferInstallAsync(), () => AvailableRelease is not null && !IsBusy);
        OpenReleaseNotesCommand = new RelayCommand(() => OpenUrl(AvailableRelease?.PageUrl ?? ReleasesPage));
        OpenReleasesPageCommand = new RelayCommand(() => OpenUrl(ReleasesPage));
        DismissCommand = new RelayCommand(() =>
        {
            _bannerDismissed = true;
            OnPropertyChanged(nameof(IsBannerVisible));
        });
        CancelDownloadCommand = new RelayCommand(() => _downloadCancellation?.Cancel(), () => IsDownloading);
    }

    public AppVersion CurrentVersion => _setup.Client.CurrentVersion;

    public string VersionText => $"Version {CurrentVersion}";

    public Uri ReleasesPage => _setup.Client.ReleasesPageUrl;

    public UpdateState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsChecking));
                OnPropertyChanged(nameof(IsDownloading));
                OnPropertyChanged(nameof(IsNotDownloading));
                OnPropertyChanged(nameof(IsBannerVisible));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading;

    public bool IsChecking => State == UpdateState.Checking;

    public bool IsDownloading => State == UpdateState.Downloading;

    public bool IsNotDownloading => !IsDownloading;

    /// <summary>Short text next to the version in the header, e.g. "Up to date".</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Full explanation of <see cref="StatusText"/>, shown as its tooltip.</summary>
    public string? StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    public ReleaseInfo? AvailableRelease
    {
        get => _availableRelease;
        private set
        {
            if (SetProperty(ref _availableRelease, value))
            {
                OnPropertyChanged(nameof(BannerText));
                OnPropertyChanged(nameof(IsBannerVisible));
            }
        }
    }

    public bool IsBannerVisible =>
        AvailableRelease is not null && (State == UpdateState.Downloading || (State == UpdateState.Available && !_bannerDismissed));

    public string BannerText => AvailableRelease is { } release
        ? $"CryptoFixer {release.Version} is available. You have {CurrentVersion}."
        : string.Empty;

    public double DownloadPercent
    {
        get => _downloadPercent;
        private set => SetProperty(ref _downloadPercent, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public string DownloadText
    {
        get => _downloadText;
        private set => SetProperty(ref _downloadText, value);
    }

    public ICommand CheckNowCommand { get; }

    public ICommand InstallCommand { get; }

    public ICommand OpenReleaseNotesCommand { get; }

    public ICommand OpenReleasesPageCommand { get; }

    public ICommand DismissCommand { get; }

    public ICommand CancelDownloadCommand { get; }

    /// <summary>
    /// Asks GitHub for the latest release. Never throws. A background check only updates the header; a check the user asked for
    /// also reports in the status line and offers an available update right away.
    /// </summary>
    public async Task CheckAsync(bool userInitiated)
    {
        if (IsBusy)
        {
            return;
        }

        State = UpdateState.Checking;
        SetStatus("Checking for updates…", $"Asking {_setup.Client.LatestReleaseApiUrl} for the latest release.");
        if (userInitiated)
        {
            _host.ReportStatus("Checking for updates…");
        }

        UpdateCheckResult result;
        try
        {
            result = await _setup.Client.CheckAsync();
        }
        catch (Exception ex)
        {
            result = new UpdateCheckResult(UpdateCheckStatus.Failed, $"The update check failed: {ex.Message}");
        }

        switch (result.Status)
        {
            case UpdateCheckStatus.UpdateAvailable when result.Release is not null:
                if (userInitiated || result.Release.Version != AvailableRelease?.Version)
                {
                    _bannerDismissed = false;
                }

                AvailableRelease = result.Release;
                State = UpdateState.Available;
                SetStatus($"Update available: {result.Release.Version}", result.Message);
                break;

            case UpdateCheckStatus.UpToDate:
                AvailableRelease = null;
                State = UpdateState.UpToDate;
                SetStatus("Up to date", result.Message);
                break;

            case UpdateCheckStatus.NoRelease:
                AvailableRelease = null;
                State = UpdateState.UpToDate;
                SetStatus("No releases yet", result.Message);
                break;

            default:
                // Keep offering an update found earlier; only the header reports the failed check.
                State = AvailableRelease is not null ? UpdateState.Available : UpdateState.Failed;
                SetStatus("Update check failed", result.Message);
                break;
        }

        if (userInitiated)
        {
            _host.ReportStatus(result.Message);
            if (State == UpdateState.Available && result.IsUpdateAvailable)
            {
                await OfferInstallAsync();
            }
        }
    }

    /// <summary>Shows the update dialog and, if the user agrees, downloads, verifies and installs the update, then restarts.</summary>
    public async Task OfferInstallAsync()
    {
        if (AvailableRelease is not { } release || IsBusy)
        {
            return;
        }

        try
        {
            var blocker = _setup.InstallUnavailableReason ?? _setup.Installer?.FindBlocker(release);
            var page = release.PageUrl ?? ReleasesPage;
            var size = _setup.Installer is { } installer ? release.FindExecutable(installer.RuntimeId)?.Size : null;
            var decision = _dialogs.ShowUpdate(new UpdateOffer(CurrentVersion, release, page, blocker, _host.PendingChangesWarning, size));
            switch (decision)
            {
                case UpdateDecision.OpenReleasePage:
                    OpenUrl(page);
                    return;

                case UpdateDecision.Install when blocker is null && _setup.Installer is not null && _setup.ExePath is not null:
                    await InstallAsync(_setup.Installer, _setup.ExePath, release);
                    return;
            }
        }
        catch (Exception ex)
        {
            State = UpdateState.Available;
            _dialogs.ShowError("Update failed", $"{ex.Message}\n\nYou can download the new version from {ReleasesPage}.");
        }
    }

    private async Task InstallAsync(UpdateInstaller installer, string exePath, ReleaseInfo release)
    {
        using var cancellation = new CancellationTokenSource();
        _downloadCancellation = cancellation;
        State = UpdateState.Downloading;
        IsProgressIndeterminate = true;
        DownloadPercent = 0;
        DownloadText = $"Downloading CryptoFixer {release.Version}…";
        _host.ReportStatus(DownloadText);

        // Progress<T> posts to the UI thread because it's created here.
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.Fraction is { } fraction)
            {
                IsProgressIndeterminate = false;
                DownloadPercent = fraction * 100;
                DownloadText = $"Downloading CryptoFixer {release.Version}… {FormatSize(p.BytesReceived)} of {FormatSize(p.TotalBytes!.Value)}";
            }
            else
            {
                DownloadText = $"Downloading CryptoFixer {release.Version}… {FormatSize(p.BytesReceived)}";
            }
        });

        try
        {
            await installer.InstallAsync(release, progress, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            State = UpdateState.Available;
            _host.ReportStatus("Update cancelled. Nothing was changed.");
            return;
        }
        catch (Exception ex)
        {
            State = UpdateState.Available;
            var message = ex is UpdateException ? ex.Message : $"Installing the update failed: {ex.Message}";
            _host.ReportStatus(message);
            _dialogs.ShowError("Update failed", $"{message}\n\nCryptoFixer {CurrentVersion} is still installed. You can also download the new version from {release.PageUrl ?? ReleasesPage}.");
            return;
        }
        finally
        {
            _downloadCancellation = null;
        }

        DownloadPercent = 100;
        IsProgressIndeterminate = false;
        DownloadText = $"CryptoFixer {release.Version} installed. Restarting…";
        _host.ReportStatus(DownloadText);
        _host.PrepareForRestart();

        try
        {
            _system.StartProgram(exePath, _setup.RelaunchArguments);
        }
        catch (Exception ex)
        {
            State = UpdateState.UpToDate;
            AvailableRelease = null;
            SetStatus("Restart needed", $"CryptoFixer {release.Version} is installed; restart CryptoFixer to use it.");
            _host.ReportStatus($"CryptoFixer {release.Version} is installed. Close and start CryptoFixer again to use it.");
            _dialogs.ShowInfo("Update installed",
                $"CryptoFixer {release.Version} was installed, but it couldn't be started automatically ({ex.Message}).\n\nClose this window and start CryptoFixer again.");
            return;
        }

        _system.ExitApplication();
    }

    private void SetStatus(string text, string? detail)
    {
        StatusText = text;
        StatusDetail = detail;
    }

    private void OpenUrl(Uri url)
    {
        try
        {
            _system.OpenUrl(url);
        }
        catch (Exception ex)
        {
            _host.ReportStatus($"Couldn't open {url}: {ex.Message}");
        }
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024.0 * 1024.0):0.0} MB"
        : $"{bytes / 1024.0:0} KB";
}
