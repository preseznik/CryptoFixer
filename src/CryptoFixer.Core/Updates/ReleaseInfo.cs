using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace CryptoFixer.Core.Updates;

public sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size);

/// <summary>The parts of a GitHub release (REST API <c>/repos/{owner}/{repo}/releases/latest</c>) that the updater uses.</summary>
public sealed record ReleaseInfo
{
    public required string TagName { get; init; }

    public string? Name { get; init; }

    public string Notes { get; init; } = string.Empty;

    public Uri? PageUrl { get; init; }

    public bool IsDraft { get; init; }

    public bool IsPrerelease { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];

    /// <summary>The version in <see cref="TagName"/>, or null when the tag isn't a version number.</summary>
    public AppVersion? Version => AppVersion.TryParse(TagName, out var version) ? version : null;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? TagName : Name.Trim();

    /// <summary>The standalone executable uploaded to each release. Unversioned, so a self-updated copy keeps a sensible name.</summary>
    public static string ExecutableAssetName(string runtimeId) => $"CryptoFixer-{runtimeId}.exe";

    /// <summary>The .NET runtime identifier of the running process, matching the release asset names, or null if unsupported.</summary>
    public static string? RuntimeIdFor(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "win-x64",
        Architecture.Arm64 => "win-arm64",
        Architecture.X86 => "win-x86",
        _ => null,
    };

    public ReleaseAsset? FindExecutable(string runtimeId) =>
        Assets.FirstOrDefault(a => string.Equals(a.Name, ExecutableAssetName(runtimeId), StringComparison.OrdinalIgnoreCase));

    /// <summary>The checksum file for <paramref name="runtimeId"/>; falls back to any <c>.sha256</c> or <c>SHA256SUMS</c> asset.</summary>
    public ReleaseAsset? FindChecksums(string runtimeId)
    {
        var candidates = Assets
            .Where(a => a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) ||
                        a.Name.StartsWith("SHA256SUMS", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return candidates.FirstOrDefault(a => a.Name.Contains(runtimeId, StringComparison.OrdinalIgnoreCase)) ?? candidates.FirstOrDefault();
    }

    /// <exception cref="FormatException">The JSON isn't a GitHub release object.</exception>
    public static ReleaseInfo Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("The release information isn't a JSON object.");
            }

            var tag = String(root, "tag_name");
            if (string.IsNullOrWhiteSpace(tag))
            {
                throw new FormatException("The release information has no tag_name.");
            }

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in list.EnumerateArray())
                {
                    if (asset.ValueKind == JsonValueKind.Object &&
                        String(asset, "name") is { Length: > 0 } name &&
                        Url(asset, "browser_download_url") is { } url)
                    {
                        var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) ? bytes : 0;
                        assets.Add(new ReleaseAsset(name, url, size));
                    }
                }
            }

            return new ReleaseInfo
            {
                TagName = tag.Trim(),
                Name = String(root, "name"),
                Notes = String(root, "body")?.Trim() ?? string.Empty,
                PageUrl = Url(root, "html_url"),
                IsDraft = Bool(root, "draft"),
                IsPrerelease = Bool(root, "prerelease"),
                PublishedAt = DateTimeOffset.TryParse(String(root, "published_at"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var published) ? published : null,
                Assets = assets,
            };
        }
        catch (JsonException ex)
        {
            throw new FormatException("The release information isn't valid JSON.", ex);
        }
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>Only https URLs are accepted, so a tampered response can't point downloads at plain http.</summary>
    private static Uri? Url(JsonElement element, string name) =>
        Uri.TryCreate(String(element, name), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
}
