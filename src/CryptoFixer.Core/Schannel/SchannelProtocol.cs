namespace CryptoFixer.Core.Schannel;

public enum ProtocolRole
{
    Server,
    Client,
}

public enum ProtocolFamily
{
    Legacy,
    Ssl,
    Tls,
    Dtls,
}

/// <param name="KeyName">Registry sub key name under SCHANNEL\Protocols, e.g. "TLS 1.2".</param>
/// <param name="IsDeprecated">True for protocols that are obsolete or deprecated (RFC 6176, RFC 7568, RFC 8996).</param>
public sealed record SchannelProtocol(string KeyName, ProtocolFamily Family, bool IsDeprecated, string Description)
{
    public string DisplayName => KeyName;

    public override string ToString() => KeyName;
}

public static class ProtocolCatalog
{
    public static readonly SchannelProtocol Pct10 = new("PCT 1.0", ProtocolFamily.Legacy, true,
        "Private Communications Technology. Obsolete; not supported by any current Windows version.");

    public static readonly SchannelProtocol MultiProtocolUnifiedHello = new("Multi-Protocol Unified Hello", ProtocolFamily.Legacy, true,
        "SSL 2.0-compatible ClientHello format. Windows servers accept it by default; clients don't send it.");

    public static readonly SchannelProtocol Ssl20 = new("SSL 2.0", ProtocolFamily.Ssl, true,
        "Prohibited (RFC 6176). Removed from Windows 10 1607 / Server 2016 and later.");

    public static readonly SchannelProtocol Ssl30 = new("SSL 3.0", ProtocolFamily.Ssl, true,
        "Deprecated (RFC 7568, POODLE). Disabled by default since Windows 10 1607 / Server 2016.");

    public static readonly SchannelProtocol Tls10 = new("TLS 1.0", ProtocolFamily.Tls, true,
        "Deprecated (RFC 8996). Still enabled by default on current Windows Server releases.");

    public static readonly SchannelProtocol Tls11 = new("TLS 1.1", ProtocolFamily.Tls, true,
        "Deprecated (RFC 8996). Still enabled by default on current Windows Server releases.");

    public static readonly SchannelProtocol Tls12 = new("TLS 1.2", ProtocolFamily.Tls, false,
        "Current baseline protocol. Required by most services (RDP, WinRM, IIS, SQL Server).");

    public static readonly SchannelProtocol Tls13 = new("TLS 1.3", ProtocolFamily.Tls, false,
        "Supported from Windows Server 2022 / Windows 11. Not supported on earlier versions.");

    public static readonly SchannelProtocol Dtls10 = new("DTLS 1.0", ProtocolFamily.Dtls, true,
        "Datagram TLS based on TLS 1.1. Deprecated (RFC 8996).");

    public static readonly SchannelProtocol Dtls12 = new("DTLS 1.2", ProtocolFamily.Dtls, false,
        "Datagram TLS based on TLS 1.2. Supported from Windows 10 1607 / Server 2016.");

    public static IReadOnlyList<SchannelProtocol> All { get; } =
    [
        Pct10,
        MultiProtocolUnifiedHello,
        Ssl20,
        Ssl30,
        Tls10,
        Tls11,
        Tls12,
        Tls13,
        Dtls10,
        Dtls12,
    ];

    public static SchannelProtocol? Find(string keyName) =>
        All.FirstOrDefault(p => string.Equals(p.KeyName, keyName, StringComparison.OrdinalIgnoreCase));
}
