using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace CryptoFixer.Core.Updates;

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    NoRelease,
    RateLimited,
    Failed,
}

/// <param name="Message">A one-line summary suitable for the status bar.</param>
/// <param name="Release">The latest published release, when one was found.</param>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, string Message, ReleaseInfo? Release = null)
{
    public bool IsUpdateAvailable => Status == UpdateCheckStatus.UpdateAvailable && Release is not null;
}

public sealed record DownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Fraction => TotalBytes > 0 ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1) : null;
}

/// <summary>An update step failed; <see cref="Exception.Message"/> is written for the user.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Talks to the public GitHub releases of the CryptoFixer repository without authentication.
/// Uses the system proxy settings; every request has a timeout so a check can't hang.
/// </summary>
public sealed class UpdateClient : IDisposable
{
    public const string DefaultRepository = "preseznik/CryptoFixer";

    private const long MaxChecksumFileBytes = 64 * 1024;
    private const long MaxDownloadBytes = 512L * 1024 * 1024;

    private readonly HttpClient _http;

    public UpdateClient(AppVersion currentVersion, HttpMessageHandler? handler = null, string repository = DefaultRepository)
    {
        CurrentVersion = currentVersion;
        Repository = repository;
        _http = new HttpClient(handler ?? CreateDefaultHandler(), disposeHandler: true)
        {
            // Timeouts are applied per request (see RequestTimeout and StallTimeout) so large downloads aren't cut off.
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = 8 * 1024 * 1024,
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CryptoFixer", currentVersion.ToString()));
    }

    public AppVersion CurrentVersion { get; }

    public string Repository { get; }

    public Uri LatestReleaseApiUrl => new($"https://api.github.com/repos/{Repository}/releases/latest");

    public Uri ReleasesPageUrl => new($"https://github.com/{Repository}/releases");

    /// <summary>Limit for the release check and the checksum download.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>A download is abandoned when no data arrives for this long.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Uses the system proxy (with the signed-in user's credentials for authenticating proxies).</summary>
    public static HttpMessageHandler CreateDefaultHandler() => new SocketsHttpHandler
    {
        UseProxy = true,
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };

    /// <summary>Never throws for network or server problems; those are reported in the result. Throws only when <paramref name="cancellationToken"/> is cancelled.</summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(UpdateCheckStatus.NoRelease, "No CryptoFixer release has been published yet.");
            }

            if (DescribeRateLimit(response) is { } limit)
            {
                return new UpdateCheckResult(UpdateCheckStatus.RateLimited, limit);
            }

            if (!response.IsSuccessStatusCode)
            {
                return Failed($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            ReleaseInfo release;
            try
            {
                release = ReleaseInfo.Parse(json);
            }
            catch (FormatException)
            {
                return Failed("the release information from GitHub couldn't be read");
            }

            return Evaluate(release);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed($"GitHub didn't answer within {RequestTimeout.TotalSeconds:0} seconds");
        }
        catch (HttpRequestException ex)
        {
            return Failed(DescribeNetworkError(ex));
        }
    }

    /// <summary>Compares the latest published release with <see cref="CurrentVersion"/>. Drafts and prereleases are never offered.</summary>
    public UpdateCheckResult Evaluate(ReleaseInfo release)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (release.IsDraft || release.IsPrerelease)
        {
            return new UpdateCheckResult(UpdateCheckStatus.NoRelease, "No CryptoFixer release has been published yet (only drafts or prereleases).");
        }

        if (release.Version is not { } latest)
        {
            return Failed($"the latest release tag \"{release.TagName}\" isn't a version number");
        }

        if (latest > CurrentVersion && !latest.IsPrerelease)
        {
            return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable,
                $"CryptoFixer {latest} is available (you have {CurrentVersion}).", release);
        }

        var message = latest == CurrentVersion
            ? $"CryptoFixer {CurrentVersion} is the latest version."
            : $"CryptoFixer {CurrentVersion} is up to date (latest release: {latest}).";
        return new UpdateCheckResult(UpdateCheckStatus.UpToDate, message, release);
    }

    /// <summary>
    /// Downloads <paramref name="asset"/> into <paramref name="destinationFolder"/> and checks its SHA-256 hash against the entry for
    /// <paramref name="asset"/> in <paramref name="checksums"/>. Returns the verified file; a file that fails verification is deleted.
    /// </summary>
    /// <exception cref="UpdateException">The download failed or couldn't be verified.</exception>
    public async Task<string> DownloadVerifiedAsync(
        ReleaseAsset asset,
        ReleaseAsset checksums,
        string destinationFolder,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(checksums);

        var checksumText = await DownloadTextAsync(checksums, cancellationToken).ConfigureAwait(false);
        var expected = ChecksumFile.FindHash(checksumText, asset.Name)
            ?? throw new UpdateException($"{checksums.Name} doesn't list a SHA-256 checksum for {asset.Name}, so the download can't be verified.");

        Directory.CreateDirectory(destinationFolder);
        var target = Path.Combine(destinationFolder, Path.GetFileName(asset.Name));
        var partial = target + ".partial";
        try
        {
            var actual = await DownloadToFileAsync(asset, partial, progress, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new UpdateException(
                    $"The downloaded {asset.Name} doesn't match its published SHA-256 checksum, so it was discarded.{Environment.NewLine}" +
                    $"Expected {expected}{Environment.NewLine}Received {actual}");
            }

            File.Move(partial, target, overwrite: true);
            return target;
        }
        finally
        {
            TryDelete(partial);
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<string> DownloadTextAsync(ReleaseAsset asset, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            EnsureDownloadSucceeded(response, asset);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxChecksumFileBytes)
                {
                    throw new UpdateException($"{asset.Name} is unexpectedly large; it isn't a checksum file.");
                }
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException($"Downloading {asset.Name} timed out.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"Downloading {asset.Name} failed: {DescribeNetworkError(ex)}.", ex);
        }
        catch (HttpIOException ex)
        {
            throw new UpdateException($"Downloading {asset.Name} was interrupted: {ex.Message}", ex);
        }
    }

    /// <summary>Streams the asset to <paramref name="path"/> and returns its lowercase SHA-256 hash.</summary>
    private async Task<string> DownloadToFileAsync(ReleaseAsset asset, string path, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(StallTimeout);
        try
        {
            using var response = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
            EnsureDownloadSucceeded(response, asset);

            var total = response.Content.Headers.ContentLength ?? (asset.Size > 0 ? asset.Size : null);
            if (total > MaxDownloadBytes)
            {
                throw new UpdateException($"{asset.Name} is unexpectedly large ({total:N0} bytes).");
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long received = 0, reported = 0;
            progress?.Report(new DownloadProgress(0, total));
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                var read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                received += read;
                if (received > MaxDownloadBytes)
                {
                    throw new UpdateException($"{asset.Name} is unexpectedly large.");
                }

                if (received - reported >= 256 * 1024)
                {
                    reported = received;
                    progress?.Report(new DownloadProgress(received, total));
                }
            }

            progress?.Report(new DownloadProgress(received, total));
            if (total is { } expectedLength && received != expectedLength)
            {
                throw new UpdateException($"The download of {asset.Name} ended early ({received:N0} of {expectedLength:N0} bytes).");
            }

            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException($"Downloading {asset.Name} stalled for {StallTimeout.TotalSeconds:0} seconds and was stopped.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateException($"Downloading {asset.Name} failed: {DescribeNetworkError(ex)}.", ex);
        }
        catch (HttpIOException ex)
        {
            throw new UpdateException($"Downloading {asset.Name} was interrupted: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new UpdateException($"Saving {asset.Name} to {Path.GetDirectoryName(path)} failed: {ex.Message}", ex);
        }
    }

    private static void EnsureDownloadSucceeded(HttpResponseMessage response, ReleaseAsset asset)
    {
        if (DescribeRateLimit(response) is { } limit)
        {
            throw new UpdateException(limit);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase} when downloading {asset.Name}.");
        }
    }

    /// <summary>GitHub allows 60 unauthenticated API requests per hour and address.</summary>
    private static string? DescribeRateLimit(HttpResponseMessage response)
    {
        var limited = response.StatusCode == HttpStatusCode.TooManyRequests ||
            (response.StatusCode == HttpStatusCode.Forbidden && Header(response, "X-RateLimit-Remaining") == "0");
        if (!limited)
        {
            return null;
        }

        var resetAt = long.TryParse(Header(response, "X-RateLimit-Reset"), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? $" after {DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime():HH:mm}"
            : " later";
        return $"GitHub's limit for update checks from this network was reached. Try again{resetAt}.";
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static string DescribeNetworkError(HttpRequestException ex) => ex.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => "github.com couldn't be found (is this computer offline?)",
        HttpRequestError.ConnectionError => "couldn't connect to GitHub (offline or blocked by a firewall?)",
        HttpRequestError.SecureConnectionError =>
            "a secure connection to GitHub couldn't be established (GitHub requires TLS 1.2 or TLS 1.3 to be enabled for clients)",
        HttpRequestError.ProxyTunnelError => "the proxy server refused the connection to GitHub",
        _ => ex.Message.TrimEnd('.'),
    };

    private static UpdateCheckResult Failed(string reason) =>
        new(UpdateCheckStatus.Failed, $"Couldn't check for updates: {reason}.");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
