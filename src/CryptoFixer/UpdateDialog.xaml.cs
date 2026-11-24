using System.Diagnostics;
using System.Windows;
using CryptoFixer.Services;

namespace CryptoFixer;

public partial class UpdateDialog : Window
{
    private readonly UpdateOffer _offer;

    public UpdateDialog(UpdateOffer offer)
    {
        InitializeComponent();
        _offer = offer;
        var release = offer.Release;

        TitleText.Text = $"CryptoFixer {release.Version} is available";
        SubtitleText.Text = release.PublishedAt is { } published
            ? $"You have version {offer.CurrentVersion}. Released {published.ToLocalTime():d}."
            : $"You have version {offer.CurrentVersion}.";
        NotesBox.Text = string.IsNullOrWhiteSpace(release.Notes) ? "No release notes were published for this version." : release.Notes.Trim();
        ReleaseLinkText.Text = $"View {release.DisplayName} on GitHub";
        ReleaseLink.ToolTip = offer.ReleasePage.AbsoluteUri;

        if (offer.InstallBlocker is { } blocker)
        {
            ExplanationText.Text = "Download the new CryptoFixer.exe from the release page and replace this copy with it. Keep CryptoFixer.presets.json and CryptoFixer.settings.json next to it.";
            BlockerText.Text = $"Automatic update isn't available: {blocker}";
            BlockerPanel.Visibility = Visibility.Visible;
            PrimaryButton.Content = "Open download page";
        }
        else
        {
            var size = offer.DownloadSize is > 0 ? $" ({offer.DownloadSize.Value / (1024.0 * 1024.0):0.0} MB)" : string.Empty;
            ExplanationText.Text =
                $"Update now downloads the new CryptoFixer.exe{size} from GitHub, checks it against the SHA-256 checksum published with the release, " +
                "replaces this CryptoFixer.exe and restarts CryptoFixer. Your presets and settings files are kept.";
            PrimaryButton.Content = "Update now";
            if (offer.PendingWarning is { } pending)
            {
                PendingText.Text = pending;
                PendingPanel.Visibility = Visibility.Visible;
            }
        }

        Loaded += (_, _) => PrimaryButton.Focus();
    }

    public UpdateDecision Decision { get; private set; } = UpdateDecision.NotNow;

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        Decision = _offer.InstallBlocker is null ? UpdateDecision.Install : UpdateDecision.OpenReleasePage;
        DialogResult = true;
    }

    private void OnReleaseLinkClick(object sender, RoutedEventArgs e)
    {
        if (_offer.ReleasePage.Scheme == Uri.UriSchemeHttps)
        {
            Process.Start(new ProcessStartInfo(_offer.ReleasePage.AbsoluteUri) { UseShellExecute = true });
        }
    }
}
