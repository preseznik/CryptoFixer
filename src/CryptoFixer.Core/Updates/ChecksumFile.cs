using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CryptoFixer.Core.Updates;

/// <summary>Reads checksum files in the <c>sha256sum</c> format: one <c>&lt;64 hex digits&gt;  &lt;file name&gt;</c> line per file.</summary>
public static partial class ChecksumFile
{
    /// <summary>Maps each listed file name (with '/' separators) to its lowercase SHA-256 hash; later duplicates win.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var match = LinePattern().Match(line.Trim());
            if (match.Success)
            {
                var name = match.Groups["name"].Value.Trim().Replace('\\', '/');
                if (name.StartsWith("./", StringComparison.Ordinal))
                {
                    name = name[2..];
                }

                entries[name] = match.Groups["hash"].Value.ToLowerInvariant();
            }
        }

        return entries;
    }

    /// <summary>The hash listed for exactly <paramref name="fileName"/>, or null.</summary>
    public static string? FindHash(string text, string fileName) =>
        Parse(text).TryGetValue(fileName, out var hash) ? hash : null;

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    [GeneratedRegex(@"^(?<hash>[0-9a-fA-F]{64})\s+\*?(?<name>.+)$")]
    private static partial Regex LinePattern();
}
