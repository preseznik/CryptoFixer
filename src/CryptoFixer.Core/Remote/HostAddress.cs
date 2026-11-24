using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace CryptoFixer.Core.Remote;

/// <summary>Parsing and ordering of host names and IP addresses.</summary>
public static class HostAddress
{
    public enum Kind
    {
        IPv4,
        IPv6,
        HostName,
    }

    /// <summary>Orders IPv4 addresses numerically (octet by octet), then IPv6 addresses, then host names alphabetically.</summary>
    public static IComparer<string> Comparer { get; } = new HostComparer();

    public static Kind Classify(string host) => Parse(host).Kind;

    public static bool IsIpAddress(string host) => Classify(host) != Kind.HostName;

    /// <summary>True for a plain host name, FQDN, dotted IPv4 or (optionally bracketed) IPv6 address.</summary>
    public static bool IsValid(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var trimmed = host.Trim();
        if (Parse(trimmed).Kind != Kind.HostName)
        {
            return true;
        }

        // Rejects ports, URLs, paths, spaces and things like "1.2.3" that look like a broken IPv4 address.
        return trimmed.Length <= 253
            && trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_')
            && !trimmed.StartsWith('.') && !trimmed.StartsWith('-')
            && !trimmed.Contains("..", StringComparison.Ordinal)
            && !trimmed.All(c => char.IsAsciiDigit(c) || c == '.');
    }

    private readonly record struct ParsedHost(Kind Kind, byte[]? Bytes, long ScopeId);

    private static ParsedHost Parse(string host)
    {
        var trimmed = host.Trim();
        if (TryParseIPv4(trimmed) is { } v4)
        {
            return new ParsedHost(Kind.IPv4, v4, 0);
        }

        var candidate = trimmed.Length > 2 && trimmed[0] == '[' && trimmed[^1] == ']' ? trimmed[1..^1] : trimmed;
        if (candidate.Contains(':', StringComparison.Ordinal)
            && IPAddress.TryParse(candidate, out var address)
            && address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return new ParsedHost(Kind.IPv6, address.GetAddressBytes(), address.ScopeId);
        }

        return new ParsedHost(Kind.HostName, null, 0);
    }

    // IPAddress.TryParse also accepts "1", "1.2" or hex/octal forms, which are almost always typos here.
    private static byte[]? TryParseIPv4(string text)
    {
        var parts = text.Split('.');
        if (parts.Length != 4)
        {
            return null;
        }

        var bytes = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            var part = parts[i];
            if (part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit)
                || !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i]))
            {
                return null;
            }
        }

        return bytes;
    }

    private sealed class HostComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var a = Parse(x);
            var b = Parse(y);
            var byKind = a.Kind.CompareTo(b.Kind);
            if (byKind != 0)
            {
                return byKind;
            }

            if (a.Bytes is not null && b.Bytes is not null)
            {
                var byBytes = a.Bytes.AsSpan().SequenceCompareTo(b.Bytes);
                if (byBytes != 0)
                {
                    return byBytes;
                }

                var byScope = a.ScopeId.CompareTo(b.ScopeId);
                if (byScope != 0)
                {
                    return byScope;
                }
            }

            var byName = StringComparer.OrdinalIgnoreCase.Compare(x.Trim(), y.Trim());
            return byName != 0 ? byName : StringComparer.Ordinal.Compare(x, y);
        }
    }
}

public enum MachineSortOrder
{
    Name,
    Address,
}

/// <summary>Sorting and filtering for the machine list.</summary>
public static class MachineListQuery
{
    public static IEnumerable<MachineEntry> Sort(IEnumerable<MachineEntry> machines, MachineSortOrder order)
    {
        ArgumentNullException.ThrowIfNull(machines);
        return machines.OrderBy(m => m, Comparer(order));
    }

    /// <summary>By name (then address), or by address (IPv4 numerically, then IPv6, then host names; then name).</summary>
    public static IComparer<MachineEntry> Comparer(MachineSortOrder order) =>
        order == MachineSortOrder.Address ? ByAddress : ByName;

    private static readonly IComparer<MachineEntry> ByName = Comparer<MachineEntry>.Create((a, b) =>
    {
        var byName = StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
        return byName != 0 ? byName : HostAddress.Comparer.Compare(a.Host, b.Host);
    });

    private static readonly IComparer<MachineEntry> ByAddress = Comparer<MachineEntry>.Create((a, b) =>
    {
        var byHost = HostAddress.Comparer.Compare(a.Host, b.Host);
        return byHost != 0 ? byHost : StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name);
    });

    /// <summary>True when every whitespace-separated term occurs in the name or host (case-insensitive).</summary>
    public static bool Matches(string name, string host, string? filterText)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(host);

        if (string.IsNullOrWhiteSpace(filterText))
        {
            return true;
        }

        return filterText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .All(term => name.Contains(term, StringComparison.OrdinalIgnoreCase) || host.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public static bool Matches(MachineEntry machine, string? filterText)
    {
        ArgumentNullException.ThrowIfNull(machine);
        return Matches(machine.Name, machine.Host, filterText);
    }
}
