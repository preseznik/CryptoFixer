namespace CryptoFixer.Core.Schannel;

public enum OsDefaultState
{
    Enabled,
    Disabled,
    NotSupported,
}

/// <summary>
/// Schannel protocol defaults per Windows build, based on Microsoft's "Protocols in TLS/SSL (Schannel SSP)" documentation.
/// These are the states Windows uses when no Enabled/DisabledByDefault value is configured in the registry.
/// </summary>
public static class OsDefaults
{
    public const int Windows7Build = 7600;            // Windows 7 / Server 2008 R2
    public const int Windows8Build = 9200;            // Windows 8 / Server 2012
    public const int Windows10V1607Build = 14393;     // Windows 10 1607 / Server 2016
    public const int WindowsServer2022Build = 20348;  // Windows Server 2022 (Windows 11 starts at 22000)

    public static OsDefaultState Get(SchannelProtocol protocol, ProtocolRole role, int build)
    {
        ArgumentNullException.ThrowIfNull(protocol);

        return protocol.KeyName switch
        {
            "PCT 1.0" => OsDefaultState.NotSupported,

            // Servers accept a unified-format ClientHello on all Windows versions; clients only send it together with SSL 2.0.
            "Multi-Protocol Unified Hello" => role == ProtocolRole.Server ? OsDefaultState.Enabled : OsDefaultState.Disabled,

            "SSL 2.0" when build >= Windows10V1607Build => OsDefaultState.NotSupported,
            "SSL 2.0" when role == ProtocolRole.Client => OsDefaultState.Disabled,
            "SSL 2.0" => build >= Windows8Build ? OsDefaultState.Disabled : OsDefaultState.Enabled,

            "SSL 3.0" => build >= Windows10V1607Build ? OsDefaultState.Disabled : OsDefaultState.Enabled,

            "TLS 1.0" => OsDefaultState.Enabled,

            "TLS 1.1" or "TLS 1.2" => build >= Windows8Build ? OsDefaultState.Enabled : OsDefaultState.Disabled,

            "TLS 1.3" => build >= WindowsServer2022Build ? OsDefaultState.Enabled : OsDefaultState.NotSupported,

            "DTLS 1.0" => build >= Windows7Build ? OsDefaultState.Enabled : OsDefaultState.NotSupported,

            "DTLS 1.2" => build >= Windows10V1607Build ? OsDefaultState.Enabled : OsDefaultState.NotSupported,

            _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol.KeyName, "Unknown Schannel protocol."),
        };
    }

    /// <summary>Human readable name of the Windows release family whose defaults apply to <paramref name="build"/>.</summary>
    public static string DescribeDefaultsFamily(int build) => build switch
    {
        >= WindowsServer2022Build => "Windows Server 2022 / Windows 11 and later",
        >= Windows10V1607Build => "Windows Server 2016 / 2019, Windows 10 1607 and later",
        >= Windows8Build => "Windows Server 2012 / 2012 R2, Windows 8 / 8.1, Windows 10 1507 / 1511",
        >= Windows7Build => "Windows Server 2008 R2 / Windows 7",
        _ => "Windows Server 2008 / Windows Vista",
    };
}
