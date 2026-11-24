namespace CryptoFixer.Core.Remote;

/// <summary>How CryptoFixer talks to a remote computer.</summary>
public enum RemoteProtocol
{
    /// <summary>CIM over WinRM/WS-Management on HTTP (port 5985). Traffic is still encrypted by Kerberos/NTLM.</summary>
    WsMan,

    /// <summary>CIM over WinRM/WS-Management on HTTPS (port 5986). Needs an HTTPS listener and a trusted certificate on the target.</summary>
    WsManHttps,

    /// <summary>CIM over DCOM/RPC (the classic WMI transport).</summary>
    Dcom,
}

/// <summary>Credentials for a remote computer. Passwords are only ever kept encrypted (see <see cref="ISecretProtector"/>).</summary>
public sealed record MachineCredential
{
    public static MachineCredential CurrentAccount { get; } = new();

    /// <summary>DOMAIN\user, user@domain or a local user name. Null or empty means "use the current Windows account".</summary>
    public string? UserName { get; init; }

    /// <summary>The password encrypted with <see cref="ISecretProtector"/>, or null when the password isn't saved.</summary>
    public byte[]? ProtectedPassword { get; init; }

    public bool UsesCurrentAccount => string.IsNullOrWhiteSpace(UserName);

    public bool HasSavedPassword => !UsesCurrentAccount && ProtectedPassword is { Length: > 0 };

    public string Summary => UsesCurrentAccount ? "Current Windows account" : UserName!.Trim();

    public static MachineCredential ForUser(string userName, byte[]? protectedPassword = null) =>
        new() { UserName = userName.Trim(), ProtectedPassword = protectedPassword is { Length: > 0 } ? protectedPassword : null };

    /// <summary>Splits DOMAIN\user into its parts. user@domain (UPN) and plain names keep an empty domain.</summary>
    public static (string Domain, string User) SplitUserName(string userName)
    {
        ArgumentNullException.ThrowIfNull(userName);
        var trimmed = userName.Trim();
        var slash = trimmed.IndexOf('\\', StringComparison.Ordinal);
        return slash > 0 ? (trimmed[..slash], trimmed[(slash + 1)..]) : (string.Empty, trimmed.TrimStart('\\'));
    }
}

/// <summary>A remote computer in the machine list.</summary>
/// <param name="Name">Display name; unique in the list (case-insensitive).</param>
/// <param name="Host">Host name, FQDN, IPv4 or IPv6 address used to connect.</param>
public sealed record MachineEntry(string Name, string Host, RemoteProtocol Protocol, MachineCredential Credential)
{
    public const int MaxNameLength = 64;

    public string ProtocolLabel => ProtocolDisplayName(Protocol);

    public static string ProtocolDisplayName(RemoteProtocol protocol) => protocol switch
    {
        RemoteProtocol.WsManHttps => "WinRM HTTPS",
        RemoteProtocol.Dcom => "DCOM",
        _ => "WinRM",
    };

    /// <summary>Returns an error message for unusable input, or null when the entry can be saved.</summary>
    /// <param name="existingNames">Names already in the list.</param>
    /// <param name="originalName">The entry's current name when editing, so keeping the name isn't reported as a duplicate.</param>
    public static string? Validate(string? name, string? host, string? userName, bool useCurrentAccount, IEnumerable<string> existingNames, string? originalName = null)
    {
        ArgumentNullException.ThrowIfNull(existingNames);

        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a display name.";
        }

        name = name.Trim();
        if (name.Length > MaxNameLength)
        {
            return $"Use at most {MaxNameLength} characters for the name.";
        }

        if (string.Equals(name, MachineListFile.ThisComputerName, StringComparison.OrdinalIgnoreCase))
        {
            return $"\"{MachineListFile.ThisComputerName}\" is reserved for the local computer.";
        }

        var keepsName = originalName is not null && string.Equals(originalName, name, StringComparison.OrdinalIgnoreCase);
        if (!keepsName && existingNames.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
        {
            return "Another machine already uses that name.";
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            return "Enter a host name or IP address.";
        }

        if (!HostAddress.IsValid(host))
        {
            return "That isn't a valid host name or IP address (no spaces, ports or URLs).";
        }

        if (!useCurrentAccount && string.IsNullOrWhiteSpace(userName))
        {
            return "Enter a user name, or choose the current Windows account.";
        }

        return null;
    }
}
