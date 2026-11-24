using System.Diagnostics;

namespace CryptoFixer.Core.Updates;

/// <summary>
/// Replaces the running executable: Windows lets a running .exe be renamed but not overwritten, so the current file is
/// renamed to <c>.old</c> and the new one moved into its place. The <c>.old</c> file is deleted by the next start.
/// Only the executable and its <c>.old</c>/<c>.new</c> siblings are touched; presets and settings stay as they are.
/// </summary>
public static class ExecutableReplacer
{
    public const string OldSuffix = ".old";
    public const string NewSuffix = ".new";

    public static string OldPath(string exePath) => exePath + OldSuffix;

    public static string NewPath(string exePath) => exePath + NewSuffix;

    /// <summary>Returns why files can't be created in <paramref name="folder"/>, or null when it is writable.</summary>
    public static string? FindWriteBlocker(string folder)
    {
        var probe = Path.Combine(folder, $".cryptofixer-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"CryptoFixer can't write to its folder ({folder}), so it can't replace itself. " +
                "Run it as administrator, or download the new version and replace CryptoFixer.exe yourself.";
        }
    }

    /// <exception cref="UpdateException">The swap failed; the original executable is left (or put back) in place.</exception>
    public static void Replace(string exePath, string replacementPath) => Replace(exePath, replacementPath, File.Move);

    internal static void Replace(string exePath, string replacementPath, Action<string, string> move)
    {
        ArgumentException.ThrowIfNullOrEmpty(exePath);
        ArgumentException.ThrowIfNullOrEmpty(replacementPath);

        var folder = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
        if (FindWriteBlocker(folder) is { } blocker)
        {
            throw new UpdateException(blocker);
        }

        var name = Path.GetFileName(exePath);
        var oldPath = OldPath(exePath);
        var newPath = NewPath(exePath);

        // Copy (rather than move) from the download folder so the new file gets the permissions of the executable's folder.
        try
        {
            DeleteIfExists(newPath);
            DeleteIfExists(oldPath);
            File.Copy(replacementPath, newPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(newPath);
            throw new UpdateException($"Couldn't place the new version next to {name}: {ex.Message}", ex);
        }

        try
        {
            move(exePath, oldPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(newPath);
            throw new UpdateException($"Couldn't move {name} aside to make room for the new version: {ex.Message}", ex);
        }

        try
        {
            move(newPath, exePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                move(oldPath, exePath);
            }
            catch (Exception restoreEx) when (restoreEx is IOException or UnauthorizedAccessException)
            {
                throw new UpdateException(
                    $"Couldn't put the new version in place ({ex.Message}), and restoring the previous version failed too ({restoreEx.Message}). " +
                    $"Rename {Path.GetFileName(oldPath)} back to {name} to restore it.", ex);
            }

            TryDelete(newPath);
            throw new UpdateException($"Couldn't put the new version in place: {ex.Message} The previous version was kept.", ex);
        }
    }

    /// <summary>The product version of the executable that an update replaced, or null when no update just happened.</summary>
    public static string? FindReplacedVersion(string exePath)
    {
        var oldPath = OldPath(exePath);
        if (!File.Exists(oldPath))
        {
            return null;
        }

        try
        {
            return FileVersionInfo.GetVersionInfo(oldPath).ProductVersion is { Length: > 0 } version ? version : "an earlier version";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "an earlier version";
        }
    }

    /// <summary>Deletes the <c>.old</c> and <c>.new</c> files left by an update; returns true when none remain.</summary>
    public static bool TryDeleteLeftovers(string exePath) => TryDelete(OldPath(exePath)) & TryDelete(NewPath(exePath));

    /// <summary>
    /// Keeps trying to delete the leftovers for up to <paramref name="timeout"/>: right after an update the previous process
    /// may still be exiting, and its executable can't be deleted until it has.
    /// </summary>
    public static async Task<bool> DeleteLeftoversAsync(string exePath, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (TryDeleteLeftovers(exePath))
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            DeleteIfExists(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
