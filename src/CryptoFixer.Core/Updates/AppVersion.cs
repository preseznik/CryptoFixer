using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace CryptoFixer.Core.Updates;

/// <summary>
/// A semantic version (major.minor.patch with an optional prerelease label), as used in release tags such as <c>v1.2.3</c>.
/// Ordering follows the Semantic Versioning 2.0 precedence rules; build metadata (<c>+...</c>) is ignored.
/// </summary>
public sealed record AppVersion(int Major, int Minor, int Patch, string? Prerelease = null) : IComparable<AppVersion>
{
    public bool IsPrerelease => Prerelease is not null;

    public static AppVersion Parse(string text) =>
        TryParse(text, out var version) ? version : throw new FormatException($"\"{text}\" isn't a version number like 1.2.3.");

    /// <summary>Accepts "1.2.3", "v1.2.3", "1.2" (patch 0), "1.2.3-beta.1" and "1.2.3+build".</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, [NotNullWhen(true)] out AppVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var span = text.Trim();
        if (span.StartsWith('v') || span.StartsWith('V'))
        {
            span = span[1..];
        }

        var plus = span.IndexOf('+');
        if (plus >= 0)
        {
            span = span[..plus];
        }

        string? prerelease = null;
        var dash = span.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = span[(dash + 1)..];
            span = span[..dash];
            if (!IsValidPrerelease(prerelease))
            {
                return false;
            }
        }

        var parts = span.Split('.');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new AppVersion(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    /// <summary>Converts an assembly version (major.minor.build.revision); the revision is dropped.</summary>
    public static AppVersion FromVersion(Version version) =>
        new(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));

    public int CompareTo(AppVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var result = Major.CompareTo(other.Major);
        if (result == 0)
        {
            result = Minor.CompareTo(other.Minor);
        }

        if (result == 0)
        {
            result = Patch.CompareTo(other.Patch);
        }

        return result != 0 ? result : ComparePrerelease(Prerelease, other.Prerelease);
    }

    public static bool operator <(AppVersion left, AppVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(AppVersion left, AppVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(AppVersion left, AppVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(AppVersion left, AppVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        Prerelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Prerelease}";

    private static bool IsValidPrerelease(string prerelease) =>
        prerelease.Length > 0 &&
        prerelease.Split('.').All(id => id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));

    /// <summary>A release sorts after its prereleases; prerelease identifiers compare numerically or ordinally, field by field.</summary>
    private static int ComparePrerelease(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return left is null ? (right is null ? 0 : 1) : -1;
        }

        var a = left.Split('.');
        var b = right.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var aNumeric = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var aNumber);
            var bNumeric = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bNumber);
            var result = (aNumeric, bNumeric) switch
            {
                (true, true) => aNumber.CompareTo(bNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(a[i], b[i]),
            };

            if (result != 0)
            {
                return Math.Sign(result);
            }
        }

        return a.Length.CompareTo(b.Length);
    }
}
