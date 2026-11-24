using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CryptoFixer.Core.Updates;

namespace CryptoFixer.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData(" V1.2 ", "1.2.0")]
    [InlineData("1.2.3-beta.1", "1.2.3-beta.1")]
    [InlineData("1.2.3+build.7", "1.2.3")]
    [InlineData("v10.20.30-rc-1+sha", "10.20.30-rc-1")]
    public void Parses_release_tags(string text, string expected)
    {
        Assert.True(AppVersion.TryParse(text, out var version));
        Assert.Equal(expected, version.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v")]
    [InlineData("1")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("1.-2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("1.2.3-be ta")]
    [InlineData("latest")]
    public void Rejects_non_versions(string? text) => Assert.False(AppVersion.TryParse(text, out _));

    [Fact]
    public void Orders_by_semantic_version_precedence()
    {
        string[] ascending =
        [
            "0.9.0", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11",
            "1.0.0-rc.1", "1.0.0", "1.0.1", "1.9.0", "1.10.0", "2.0.0",
        ];

        var versions = ascending.Select(AppVersion.Parse).ToList();
        for (var i = 1; i < versions.Count; i++)
        {
            Assert.True(versions[i - 1] < versions[i], $"{versions[i - 1]} should sort before {versions[i]}");
            Assert.True(versions[i] > versions[i - 1]);
        }

        Assert.Equal(versions, versions.AsEnumerable().Reverse().Order().ToList());
        Assert.Equal(AppVersion.Parse("v1.0.0"), AppVersion.Parse("1.0.0+other"));
    }

    [Fact]
    public void Converts_assembly_versions()
    {
        Assert.Equal(new AppVersion(1, 2, 3), AppVersion.FromVersion(new Version(1, 2, 3, 4)));
        Assert.Equal(new AppVersion(1, 2, 0), AppVersion.FromVersion(new Version(1, 2)));
    }
}

public class ReleaseInfoTests
{
    [Fact]
    public void Parses_github_release_json()
    {
        var release = ReleaseInfo.Parse("""
            {
              "url": "https://api.github.com/repos/preseznik/CryptoFixer/releases/1",
              "html_url": "https://github.com/preseznik/CryptoFixer/releases/tag/v1.2.0",
              "tag_name": "v1.2.0",
              "name": "CryptoFixer 1.2.0",
              "draft": false,
              "prerelease": false,
              "published_at": "2026-03-01T10:15:00Z",
              "body": "  - Faster\r\n- Better  ",
              "assets": [
                { "name": "CryptoFixer-1.2.0-win-x64.zip", "size": 100, "browser_download_url": "https://github.com/d/CryptoFixer-1.2.0-win-x64.zip" },
                { "name": "CryptoFixer-win-x64.exe", "size": 200, "browser_download_url": "https://github.com/d/CryptoFixer-win-x64.exe" },
                { "name": "CryptoFixer-win-arm64.exe", "size": 300, "browser_download_url": "http://insecure.example/CryptoFixer-win-arm64.exe" },
                { "name": "CryptoFixer-1.2.0-win-x64.sha256", "size": 10, "browser_download_url": "https://github.com/d/CryptoFixer-1.2.0-win-x64.sha256" },
                { "name": "", "browser_download_url": "https://github.com/d/empty" }
              ]
            }
            """);

        Assert.Equal("v1.2.0", release.TagName);
        Assert.Equal(new AppVersion(1, 2, 0), release.Version);
        Assert.Equal("CryptoFixer 1.2.0", release.DisplayName);
        Assert.Equal("- Faster\r\n- Better", release.Notes);
        Assert.Equal("https://github.com/preseznik/CryptoFixer/releases/tag/v1.2.0", release.PageUrl?.ToString());
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 10, 15, 0, TimeSpan.Zero), release.PublishedAt);
        Assert.False(release.IsDraft);
        Assert.False(release.IsPrerelease);

        // The http asset and the nameless one are dropped.
        Assert.Equal(3, release.Assets.Count);
        Assert.Equal(200, release.FindExecutable("win-x64")?.Size);
        Assert.Null(release.FindExecutable("win-arm64"));
        Assert.Equal("CryptoFixer-1.2.0-win-x64.sha256", release.FindChecksums("win-x64")?.Name);
        Assert.Equal("CryptoFixer-1.2.0-win-x64.sha256", release.FindChecksums("win-arm64")?.Name);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "name": "no tag" }""")]
    [InlineData("""{ "tag_name": 5 }""")]
    public void Rejects_invalid_release_json(string json) => Assert.Throws<FormatException>(() => ReleaseInfo.Parse(json));

    [Fact]
    public void Maps_process_architecture_to_asset_runtime()
    {
        Assert.Equal("win-x64", ReleaseInfo.RuntimeIdFor(Architecture.X64));
        Assert.Equal("win-arm64", ReleaseInfo.RuntimeIdFor(Architecture.Arm64));
        Assert.Null(ReleaseInfo.RuntimeIdFor(Architecture.Wasm));
        Assert.Equal("CryptoFixer-win-x64.exe", ReleaseInfo.ExecutableAssetName("win-x64"));
    }
}

public class ChecksumFileTests
{
    private const string HashA = "3e62794f0000000000000000000000000000000000000000000000000000aa61";
    private const string HashB = "05BE26F60000000000000000000000000000000000000000000000000000C238";

    [Fact]
    public void Parses_sha256sum_output()
    {
        var text = $"{HashA}  CryptoFixer-1.0.0-win-x64.zip\r\n{HashB} *CryptoFixer-win-x64.exe\n\n# comment\n{HashA}  ./sub\\CryptoFixer.exe\nnot a line\n";

        var entries = ChecksumFile.Parse(text);

        Assert.Equal(3, entries.Count);
        Assert.Equal(HashB.ToLowerInvariant(), ChecksumFile.FindHash(text, "CryptoFixer-win-x64.exe"));
        Assert.Equal(HashA, entries["sub/CryptoFixer.exe"]);
        Assert.Null(ChecksumFile.FindHash(text, "CryptoFixer.exe"));
    }

    [Fact]
    public void Computes_file_hashes()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "abc");
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ChecksumFile.ComputeSha256(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class CommandLineTests
{
    [Fact]
    public void Joined_arguments_parse_back_unchanged()
    {
        string[] arguments = ["--read-only", "/readonly", "two words", "", "say \"hi\"", @"C:\Program Files\x\", @"a\\b", @"back\\""slash", "tab\there"];

        var parsed = ParseWithWindows("CryptoFixer.exe " + CommandLine.Join(arguments));

        Assert.Equal(arguments, parsed.Skip(1));
        Assert.Equal("--read-only", CommandLine.Quote("--read-only"));
    }

    private static string[] ParseWithWindows(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        try
        {
            return Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray();
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}

public class UpdateClientTests
{
    private static readonly AppVersion Current = new(1, 0, 0);

    [Fact]
    public async Task Reports_a_newer_release()
    {
        var handler = FakeHttpHandler.Json(HttpStatusCode.OK, ReleaseJson("v1.1.0"));
        using var client = new UpdateClient(Current, handler);

        var result = await client.CheckAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(new AppVersion(1, 1, 0), result.Release?.Version);
        Assert.Contains("1.1.0 is available", result.Message);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://api.github.com/repos/preseznik/CryptoFixer/releases/latest", request.RequestUri?.ToString());
        Assert.Equal("CryptoFixer/1.0.0", request.Headers.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());
        Assert.Null(request.Headers.Authorization);
    }

    [Theory]
    [InlineData("v1.0.0", UpdateCheckStatus.UpToDate)]
    [InlineData("v0.9.0", UpdateCheckStatus.UpToDate)]
    [InlineData("v1.1.0-beta.1", UpdateCheckStatus.UpToDate)]
    [InlineData("nightly", UpdateCheckStatus.Failed)]
    public async Task Offers_only_newer_stable_versions(string tag, UpdateCheckStatus expected)
    {
        using var client = new UpdateClient(Current, FakeHttpHandler.Json(HttpStatusCode.OK, ReleaseJson(tag)));
        Assert.Equal(expected, (await client.CheckAsync()).Status);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Ignores_drafts_and_prereleases(bool draft, bool prerelease)
    {
        using var client = new UpdateClient(Current, FakeHttpHandler.Json(HttpStatusCode.OK, ReleaseJson("v2.0.0", draft, prerelease)));
        var result = await client.CheckAsync();
        Assert.Equal(UpdateCheckStatus.NoRelease, result.Status);
        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public async Task Maps_http_failures_to_quiet_results()
    {
        async Task<UpdateCheckResult> Check(HttpResponseMessage response)
        {
            using var client = new UpdateClient(Current, new FakeHttpHandler((_, _) => Task.FromResult(response)));
            return await client.CheckAsync();
        }

        Assert.Equal(UpdateCheckStatus.NoRelease, (await Check(new HttpResponseMessage(HttpStatusCode.NotFound))).Status);

        var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
        limited.Headers.Add("X-RateLimit-Remaining", "0");
        limited.Headers.Add("X-RateLimit-Reset", "1767225600");
        var rateLimited = await Check(limited);
        Assert.Equal(UpdateCheckStatus.RateLimited, rateLimited.Status);
        Assert.Contains("Try again after", rateLimited.Message);

        Assert.Equal(UpdateCheckStatus.RateLimited, (await Check(new HttpResponseMessage(HttpStatusCode.TooManyRequests))).Status);
        Assert.Equal(UpdateCheckStatus.Failed, (await Check(new HttpResponseMessage(HttpStatusCode.Forbidden))).Status);

        var serverError = await Check(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        Assert.Equal(UpdateCheckStatus.Failed, serverError.Status);
        Assert.Contains("500", serverError.Message);

        Assert.Equal(UpdateCheckStatus.Failed, (await Check(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") })).Status);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, "offline")]
    [InlineData(HttpRequestError.SecureConnectionError, "TLS 1.2")]
    public async Task Reports_network_errors_without_throwing(HttpRequestError error, string expectedText)
    {
        var handler = new FakeHttpHandler((_, _) => throw new HttpRequestException(error, "boom"));
        using var client = new UpdateClient(Current, handler);

        var result = await client.CheckAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains(expectedText, result.Message);
    }

    [Fact]
    public async Task Gives_up_when_github_does_not_answer()
    {
        var handler = new FakeHttpHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        });
        using var client = new UpdateClient(Current, handler) { RequestTimeout = TimeSpan.FromMilliseconds(100) };

        var result = await client.CheckAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("didn't answer", result.Message);
    }

    [Fact]
    public async Task Downloads_and_verifies_an_asset()
    {
        using var folder = new TempFolder();
        var payload = RandomBytes(700_000);
        var release = Release("v1.1.0");
        using var client = new UpdateClient(Current, FakeHttpHandler.Assets(payload, Sha256(payload)));
        var reports = new List<DownloadProgress>();

        var path = await client.DownloadVerifiedAsync(
            release.FindExecutable("win-x64")!, release.FindChecksums("win-x64")!, folder.Path, new SyncProgress<DownloadProgress>(reports.Add));

        Assert.Equal(Path.Combine(folder.Path, "CryptoFixer-win-x64.exe"), path);
        Assert.Equal(payload, File.ReadAllBytes(path));
        Assert.Equal([Path.GetFileName(path)], Directory.GetFiles(folder.Path).Select(Path.GetFileName));
        Assert.Equal(payload.Length, reports[^1].BytesReceived);
        Assert.Equal(1.0, reports[^1].Fraction);
        Assert.True(reports.Count > 2);
    }

    [Fact]
    public async Task Discards_a_download_whose_hash_does_not_match()
    {
        using var folder = new TempFolder();
        var payload = RandomBytes(5000);
        var release = Release("v1.1.0");
        using var client = new UpdateClient(Current, FakeHttpHandler.Assets(payload, Sha256(RandomBytes(10))));

        var error = await Assert.ThrowsAsync<UpdateException>(() =>
            client.DownloadVerifiedAsync(release.FindExecutable("win-x64")!, release.FindChecksums("win-x64")!, folder.Path));

        Assert.Contains("doesn't match its published SHA-256 checksum", error.Message);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task Refuses_a_download_without_a_checksum_entry()
    {
        using var folder = new TempFolder();
        var payload = RandomBytes(100);
        var release = Release("v1.1.0");
        var handler = FakeHttpHandler.Assets(payload, Sha256(payload), checksumFileName: "CryptoFixer.exe");
        using var client = new UpdateClient(Current, handler);

        var error = await Assert.ThrowsAsync<UpdateException>(() =>
            client.DownloadVerifiedAsync(release.FindExecutable("win-x64")!, release.FindChecksums("win-x64")!, folder.Path));

        Assert.Contains("doesn't list a SHA-256 checksum for CryptoFixer-win-x64.exe", error.Message);
        Assert.Empty(Directory.GetFiles(folder.Path));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith(".exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reports_a_missing_asset()
    {
        using var folder = new TempFolder();
        var release = Release("v1.1.0");
        var handler = new FakeHttpHandler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith(".sha256", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{new string('a', 64)}  CryptoFixer-win-x64.exe") }
            : new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var client = new UpdateClient(Current, handler);

        var error = await Assert.ThrowsAsync<UpdateException>(() =>
            client.DownloadVerifiedAsync(release.FindExecutable("win-x64")!, release.FindChecksums("win-x64")!, folder.Path));

        Assert.Contains("404", error.Message);
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    internal static string ReleaseJson(string tag, bool draft = false, bool prerelease = false) => JsonSerializer.Serialize(new
    {
        tag_name = tag,
        name = $"CryptoFixer {tag}",
        html_url = $"https://github.com/preseznik/CryptoFixer/releases/tag/{tag}",
        draft,
        prerelease,
        body = "Release notes",
        assets = new[]
        {
            new { name = "CryptoFixer-win-x64.exe", size = 0, browser_download_url = $"https://github.com/preseznik/CryptoFixer/releases/download/{tag}/CryptoFixer-win-x64.exe" },
            new { name = "CryptoFixer-win-x64.sha256", size = 0, browser_download_url = $"https://github.com/preseznik/CryptoFixer/releases/download/{tag}/CryptoFixer-win-x64.sha256" },
        },
    });

    internal static ReleaseInfo Release(string tag) => ReleaseInfo.Parse(ReleaseJson(tag));

    internal static byte[] RandomBytes(int length) => RandomNumberGenerator.GetBytes(length);

    internal static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>Unlike <see cref="Progress{T}"/>, reports synchronously so the test sees every report.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

public class ExecutableReplacerTests
{
    [Fact]
    public void Swaps_the_executable_and_keeps_other_files()
    {
        using var folder = new TempFolder();
        var exe = folder.Write("CryptoFixer.exe", "old version");
        folder.Write("CryptoFixer.presets.json", "presets");
        folder.Write("CryptoFixer.settings.json", "settings");
        var download = folder.Write(Path.Combine("download", "CryptoFixer-win-x64.exe"), "new version");

        ExecutableReplacer.Replace(exe, download);

        Assert.Equal("new version", File.ReadAllText(exe));
        Assert.Equal("old version", File.ReadAllText(ExecutableReplacer.OldPath(exe)));
        Assert.False(File.Exists(ExecutableReplacer.NewPath(exe)));
        Assert.Equal("presets", File.ReadAllText(Path.Combine(folder.Path, "CryptoFixer.presets.json")));
        Assert.Equal("settings", File.ReadAllText(Path.Combine(folder.Path, "CryptoFixer.settings.json")));
        Assert.Equal(
            ["CryptoFixer.exe", "CryptoFixer.exe.old", "CryptoFixer.presets.json", "CryptoFixer.settings.json"],
            Directory.GetFiles(folder.Path).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Leaves_the_executable_alone_when_it_cannot_be_moved()
    {
        using var folder = new TempFolder();
        var exe = folder.Write("CryptoFixer.exe", "old version");
        var download = folder.Write(Path.Combine("download", "new.exe"), "new version");

        UpdateException error;
        using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            error = Assert.Throws<UpdateException>(() => ExecutableReplacer.Replace(exe, download));
        }

        Assert.Contains("Couldn't move CryptoFixer.exe aside", error.Message);
        Assert.Equal("old version", File.ReadAllText(exe));
        Assert.False(File.Exists(ExecutableReplacer.OldPath(exe)));
        Assert.False(File.Exists(ExecutableReplacer.NewPath(exe)));
    }

    [Fact]
    public void Restores_the_previous_executable_when_the_new_one_cannot_be_placed()
    {
        using var folder = new TempFolder();
        var exe = folder.Write("CryptoFixer.exe", "old version");
        var download = folder.Write(Path.Combine("download", "new.exe"), "new version");
        var moves = 0;

        var error = Assert.Throws<UpdateException>(() => ExecutableReplacer.Replace(exe, download, (from, to) =>
        {
            if (++moves == 2)
            {
                throw new IOException("disk on fire");
            }

            File.Move(from, to);
        }));

        Assert.Contains("disk on fire", error.Message);
        Assert.Contains("previous version was kept", error.Message);
        Assert.Equal("old version", File.ReadAllText(exe));
        Assert.Equal(["CryptoFixer.exe"], Directory.GetFiles(folder.Path).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Cleans_up_after_an_update_once_the_old_process_has_exited()
    {
        using var folder = new TempFolder();
        var exe = folder.Write("CryptoFixer.exe", "new version");
        Assert.Null(ExecutableReplacer.FindReplacedVersion(exe));

        // A real executable with a version resource stands in for the replaced CryptoFixer.exe.
        var testAssembly = typeof(ExecutableReplacerTests).Assembly.Location;
        File.Copy(testAssembly, ExecutableReplacer.OldPath(exe));
        folder.Write("CryptoFixer.exe.new", "half-finished swap");
        Assert.Equal(System.Diagnostics.FileVersionInfo.GetVersionInfo(testAssembly).ProductVersion, ExecutableReplacer.FindReplacedVersion(exe));

        // While the previous process still runs, its executable can't be deleted.
        var locked = new FileStream(ExecutableReplacer.OldPath(exe), FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.False(ExecutableReplacer.TryDeleteLeftovers(exe));
        var cleanup = ExecutableReplacer.DeleteLeftoversAsync(exe, TimeSpan.FromSeconds(10));
        await Task.Delay(700);
        Assert.False(cleanup.IsCompleted);
        locked.Dispose();

        Assert.True(await cleanup);
        Assert.Equal(["CryptoFixer.exe"], Directory.GetFiles(folder.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void Detects_a_folder_that_is_not_writable()
    {
        Assert.Null(ExecutableReplacer.FindWriteBlocker(Path.GetTempPath()));
        var missing = Path.Combine(Path.GetTempPath(), "CryptoFixerTests-missing-" + Guid.NewGuid().ToString("N"));
        Assert.Contains("can't write to its folder", ExecutableReplacer.FindWriteBlocker(missing));
    }
}

public class UpdateInstallerTests
{
    [Fact]
    public async Task Installs_a_verified_release()
    {
        using var folder = new TempFolder();
        var exe = folder.Write(Path.Combine("app", "CryptoFixer.exe"), "old version");
        folder.Write(Path.Combine("app", "CryptoFixer.presets.json"), "presets");
        var payload = UpdateClientTests.RandomBytes(300_000);
        var downloads = Path.Combine(folder.Path, "downloads");
        using var client = new UpdateClient(new AppVersion(1, 0, 0), FakeHttpHandler.Assets(payload, UpdateClientTests.Sha256(payload)));
        var installer = new UpdateInstaller(client, exe, "win-x64", downloads, _ => "1.1.0");
        var release = UpdateClientTests.Release("v1.1.0");

        Assert.Null(installer.FindBlocker(release));
        await installer.InstallAsync(release);

        Assert.Equal(payload, File.ReadAllBytes(exe));
        Assert.Equal("old version", File.ReadAllText(ExecutableReplacer.OldPath(exe)));
        Assert.Equal("presets", File.ReadAllText(Path.Combine(folder.Path, "app", "CryptoFixer.presets.json")));
        Assert.False(Directory.Exists(downloads));
    }

    [Fact]
    public async Task Refuses_a_download_that_reports_another_version()
    {
        using var folder = new TempFolder();
        var exe = folder.Write("CryptoFixer.exe", "old version");
        var payload = UpdateClientTests.RandomBytes(1000);
        using var client = new UpdateClient(new AppVersion(1, 0, 0), FakeHttpHandler.Assets(payload, UpdateClientTests.Sha256(payload)));
        var installer = new UpdateInstaller(client, exe, "win-x64", Path.Combine(folder.Path, "downloads"), _ => "1.0.0");

        var error = await Assert.ThrowsAsync<UpdateException>(() => installer.InstallAsync(UpdateClientTests.Release("v1.1.0")));

        Assert.Contains("reports version 1.0.0 instead of 1.1.0", error.Message);
        Assert.Equal("old version", File.ReadAllText(exe));
        Assert.Equal(["CryptoFixer.exe"], Directory.GetFiles(folder.Path).Select(Path.GetFileName));
        Assert.False(Directory.Exists(Path.Combine(folder.Path, "downloads")));
    }

    [Fact]
    public void Explains_releases_it_cannot_install()
    {
        using var folder = new TempFolder();
        var exe = folder.Write("CryptoFixer.exe", "old");
        using var client = new UpdateClient(new AppVersion(1, 0, 0), new FakeHttpHandler((_, _) => throw new InvalidOperationException("no requests expected")));
        var installer = new UpdateInstaller(client, exe, "win-arm64", Path.Combine(folder.Path, "downloads"));

        Assert.Contains("no CryptoFixer-win-arm64.exe download", installer.FindBlocker(UpdateClientTests.Release("v1.1.0")));
        Assert.Contains("no .sha256 checksum file", installer.FindBlocker(UpdateClientTests.Release("v1.1.0") with
        {
            Assets = [new ReleaseAsset("CryptoFixer-win-arm64.exe", new Uri("https://example.com/a.exe"), 1)],
        }));
    }
}

internal sealed class FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public static FakeHttpHandler Json(HttpStatusCode status, string json) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") }));

    /// <summary>Serves the executable and a checksum file listing <paramref name="hash"/> for <paramref name="checksumFileName"/>.</summary>
    public static FakeHttpHandler Assets(byte[] executable, string hash, string checksumFileName = "CryptoFixer-win-x64.exe") =>
        new((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var response = path.EndsWith(".sha256", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{hash}  {checksumFileName}\n{new string('0', 64)}  other.zip\n") }
                : path.EndsWith("/CryptoFixer-win-x64.exe", StringComparison.Ordinal)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(executable) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return respond(request, cancellationToken);
    }
}

internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CryptoFixerTests-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string Write(string relativePath, string content)
    {
        var path = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
