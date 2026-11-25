namespace CryptoFixer.Core.Schannel;

/// <summary>What Windows does with an algorithm when its Enabled value is not configured.</summary>
/// <param name="IsInferred">True when the default is derived (e.g. from cipher suite tables) rather than stated by Microsoft.</param>
/// <param name="Note">Short explanation shown in tooltips.</param>
public sealed record AlgorithmOsDefault(OsDefaultState State, bool IsInferred, string Note);

/// <summary>A read-only key size value of a key exchange algorithm (e.g. Diffie-Hellman ServerMinKeyBitLength).</summary>
/// <param name="Value">The configured value, or null when Windows uses <paramref name="DefaultText"/>.</param>
public sealed record KeySizeSetting(string ValueName, uint? Value, string DefaultText)
{
    public string Describe() => Value is { } v ? $"{ValueName} = {v} (set in registry)" : $"{ValueName}: {DefaultText} (OS default)";
}

/// <summary>
/// Cipher, hash and key exchange defaults per Windows build.
/// <para>
/// Ciphers: KB 245030 says an unconfigured cipher key doesn't block the cipher, so whether it is used depends on the
/// cipher suites Windows enables by default. The defaults below follow Microsoft's "TLS Cipher Suites in Windows ..." tables:
/// "Enabled" means at least one suite using the cipher is enabled by default on that build.
/// </para>
/// <para>Hashes and key exchange algorithms default to Enabled (0xffffffff) on every build.</para>
/// </summary>
public static class AlgorithmDefaults
{
    public const int Windows10V1507Build = 10240;
    public const int Windows10V1709Build = 16299;    // RC4 suites off by default (also Server 2019 = 17763)
    public const int Windows10V22H2Build = 19045;    // 3DES moved to "not enabled by default"
    public const int Windows11V22H2Build = 22621;    // 3DES off by default (also Server 2022 23H2 = 25398, Server 2025 = 26100)

    public const string DhServerMinKeyBitLength = "ServerMinKeyBitLength";
    public const string ClientMinKeyBitLength = "ClientMinKeyBitLength";
    public const string ClientMaxKeyBitLength = "ClientMaxKeyBitLength";

    private const string Undocumented = " Microsoft doesn't document this key.";

    public static AlgorithmOsDefault Get(SchannelAlgorithm algorithm, int build)
    {
        ArgumentNullException.ThrowIfNull(algorithm);

        return algorithm.KeyName switch
        {
            "NULL" => Off(false, "Off by default. NULL suites are only used when an application explicitly requests them."),
            "DES 56/56" => Off(false, "Its cipher suites (e.g. TLS_RSA_WITH_DES_CBC_SHA) are not enabled by default."),
            "RC2 40/128" or "RC2 56/128" or "RC2 128/128" => Off(true, "Windows 7 and later have no RC2 cipher suites."),
            "RC4 40/128" or "RC4 56/128" => Off(false, "Its export cipher suites are not enabled by default."),
            "RC4 64/128" => Off(true, "No cipher suite uses 64-bit RC4."),
            "RC4 128/128" => build >= Windows10V1709Build
                ? Off(false, "RC4 suites are not enabled by default since Windows 10 1709 / Server 2019.")
                : On(false, "TLS_RSA_WITH_RC4_128_SHA and _MD5 are enabled by default before Windows 10 1709."),
            "Triple DES 168" => TripleDes(build, false, string.Empty),
            "Triple DES 168/168" => TripleDes(build, true, " This is the pre-Vista key name; newer Windows reads \"Triple DES 168\"."),
            "AES 128/128" or "AES 256/256" => On(true, "AES cipher suites are enabled by default." + Undocumented),
            "MD5" => On(false, build >= Windows10V1709Build
                ? "Not blocked by default, although no *_MD5 cipher suite is enabled by default since Windows 10 1709."
                : "Not blocked by default."),
            "SHA" or "PKCS" or "Diffie-Hellman" => On(false, "Not blocked by default (Enabled = 0xffffffff)."),
            "SHA256" or "SHA384" or "SHA512" or "ECDH" => On(true, "Not blocked by default." + Undocumented),
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm.KeyName, "Unknown Schannel algorithm."),
        };
    }

    /// <summary>Key size values that exist for a key exchange algorithm, with their defaults for <paramref name="build"/>.</summary>
    public static IReadOnlyList<(string ValueName, string DefaultText)> KeySizeValues(SchannelAlgorithm algorithm, int build) =>
        algorithm.KeyName switch
        {
            "Diffie-Hellman" =>
            [
                (DhServerMinKeyBitLength, build >= Windows10V1507Build ? "2048 bits" : "1024 bits"),
                (ClientMinKeyBitLength, "1024 bits"),
                (ClientMaxKeyBitLength, "no maximum"),
            ],
            "PKCS" =>
            [
                (ClientMinKeyBitLength, "1024 bits"),
                (ClientMaxKeyBitLength, "no maximum"),
            ],
            _ => [],
        };

    private static AlgorithmOsDefault TripleDes(int build, bool inferred, string suffix)
    {
        var off = build >= Windows11V22H2Build || build == Windows10V22H2Build;
        return off
            ? Off(inferred, "TLS_RSA_WITH_3DES_EDE_CBC_SHA is not enabled by default on Windows 10 22H2, Windows 11 22H2, Server 2022 23H2, Server 2025 and later." + suffix)
            : On(inferred, "TLS_RSA_WITH_3DES_EDE_CBC_SHA is enabled by default on this build." + suffix);
    }

    private static AlgorithmOsDefault On(bool inferred, string note) => new(OsDefaultState.Enabled, inferred, note);

    private static AlgorithmOsDefault Off(bool inferred, string note) => new(OsDefaultState.Disabled, inferred, note);
}

/// <summary>Registry state of one cipher, hash or key exchange algorithm.</summary>
/// <param name="Enabled">The raw Enabled DWORD, or null when the value is not present.</param>
public sealed record AlgorithmState(SchannelAlgorithm Algorithm, uint? Enabled, AlgorithmOsDefault OsDefault)
{
    /// <summary>Value written for <see cref="ConfiguredSetting.Enabled"/> (documented in KB 245030).</summary>
    public const uint EnabledValue = 0xFFFFFFFF;

    /// <summary>Read-only key size values (Diffie-Hellman and PKCS only).</summary>
    public IReadOnlyList<KeySizeSetting> KeySizes { get; init; } = [];

    /// <summary>Any non-zero Enabled counts as enabled (Microsoft itself uses 1 for Diffie-Hellman).</summary>
    public ConfiguredSetting Configured => Enabled switch
    {
        null => ConfiguredSetting.NotConfigured,
        0 => ConfiguredSetting.Disabled,
        _ => ConfiguredSetting.Enabled,
    };

    public EffectiveState Effective =>
        (Enabled is { } e ? e != 0 : OsDefault.State == OsDefaultState.Enabled) ? EffectiveState.Enabled : EffectiveState.Disabled;

    public bool IsExplicitlyConfigured => Enabled is not null;

    public string KeyPath => SchannelPaths.AlgorithmKey(Algorithm);

    /// <summary>Returns the state as it would be after applying <paramref name="target"/>.</summary>
    public AlgorithmState With(ConfiguredSetting target) => target switch
    {
        ConfiguredSetting.NotConfigured => this with { Enabled = null },
        ConfiguredSetting.Enabled => this with { Enabled = EnabledValue },
        ConfiguredSetting.Disabled => this with { Enabled = 0 },
        _ => this,
    };
}

/// <summary>A staged change: set <see cref="Target"/> for one algorithm's Enabled value.</summary>
public sealed record AlgorithmChange(SchannelAlgorithm Algorithm, ConfiguredSetting Target)
{
    public override string ToString() =>
        $"{Algorithm.KeyName} ({AlgorithmCatalog.Noun(Algorithm.Category)}): {PendingChange.Describe(Target)}";
}
