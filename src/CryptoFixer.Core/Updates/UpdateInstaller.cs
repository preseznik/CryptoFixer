using System.Diagnostics;

namespace CryptoFixer.Core.Updates;

/// <summary>Downloads a release's executable, verifies it and swaps it in for the running executable.</summary>
public sealed class UpdateInstaller
{
    private readonly UpdateClient _client;
    private readonly Func<string, string?> _readProductVersion;

    /// <param name="exePath">The executable to replace (the running CryptoFixer.exe).</param>
    /// <param name="runtimeId">Selects the release asset, e.g. win-x64.</param>
    /// <param name="downloadRoot">Each install downloads into a new subfolder here, which is deleted afterwards.</param>
    /// <param name="readProductVersion">Reads a file's product version; defaults to the Win32 version resource.</param>
    public UpdateInstaller(UpdateClient client, string exePath, string runtimeId, string downloadRoot, Func<string, string?>? readProductVersion = null)
    {
        _client = client;
        ExePath = exePath;
        RuntimeId = runtimeId;
        DownloadRoot = downloadRoot;
        _readProductVersion = readProductVersion ?? (path => FileVersionInfo.GetVersionInfo(path).ProductVersion);
    }

    public string ExePath { get; }

    public string RuntimeId { get; }

    public string DownloadRoot { get; }

    /// <summary>Returns why <paramref name="release"/> can't be installed automatically, or null when it can.</summary>
    public string? FindBlocker(ReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (release.Version is null)
        {
            return $"The release tag \"{release.TagName}\" isn't a version number.";
        }

        if (release.FindExecutable(RuntimeId) is null)
        {
            return $"The release has no {ReleaseInfo.ExecutableAssetName(RuntimeId)} download for this computer.";
        }

        if (release.FindChecksums(RuntimeId) is null)
        {
            return "The release has no .sha256 checksum file, so a download couldn't be verified.";
        }

        return ExecutableReplacer.FindWriteBlocker(Path.GetDirectoryName(Path.GetFullPath(ExePath))!);
    }

    /// <exception cref="UpdateException">Nothing was replaced (or the previous version was restored).</exception>
    /// <exception cref="OperationCanceledException">Cancelled before the executable was replaced.</exception>
    public async Task InstallAsync(ReleaseInfo release, IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (FindBlocker(release) is { } blocker)
        {
            throw new UpdateException(blocker);
        }

        var executable = release.FindExecutable(RuntimeId)!;
        var checksums = release.FindChecksums(RuntimeId)!;
        var folder = Path.Combine(DownloadRoot, Guid.NewGuid().ToString("N"));
        try
        {
            var file = await _client.DownloadVerifiedAsync(executable, checksums, folder, progress, cancellationToken).ConfigureAwait(false);

            // The hash proves the file is the one published with the release; the version check guards against a
            // mislabelled upload, which would otherwise offer the same "update" on every start.
            var reported = _readProductVersion(file);
            if (!AppVersion.TryParse(reported, out var version) || version != release.Version)
            {
                throw new UpdateException(
                    $"The downloaded {executable.Name} reports version {reported ?? "(none)"} instead of {release.Version}, so it wasn't installed.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => ExecutableReplacer.Replace(ExePath, file), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFolder(folder);
            TryDeleteFolder(DownloadRoot, onlyIfEmpty: true);
        }
    }

    private static void TryDeleteFolder(string folder, bool onlyIfEmpty = false)
    {
        try
        {
            if (Directory.Exists(folder) && (!onlyIfEmpty || !Directory.EnumerateFileSystemEntries(folder).Any()))
            {
                Directory.Delete(folder, recursive: !onlyIfEmpty);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
