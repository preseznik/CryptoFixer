using System.Security;
using CryptoFixer.Core.Registry;

namespace CryptoFixer.Core.Schannel;

public enum CipherSuiteSource
{
    /// <summary>The "SSL Cipher Suite Order" Group Policy is set; it replaces the local list.</summary>
    GroupPolicy,

    /// <summary>The local list (the Windows default, or as changed by Enable-/Disable-TlsCipherSuite).</summary>
    Local,

    /// <summary>The list could not be read.</summary>
    Unavailable,
}

/// <summary>
/// The cipher suites Schannel offers, read only. Used to show which listed suites depend on a cipher, hash or key
/// exchange algorithm; this tool never edits the suite list.
/// </summary>
public sealed record ActiveCipherSuites(CipherSuiteSource Source, IReadOnlyList<string> Names)
{
    public const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Cryptography\Configuration\SSL\00010002";
    public const string LocalKey = @"SYSTEM\CurrentControlSet\Control\Cryptography\Configuration\Local\SSL\00010002";
    public const string FunctionsValue = "Functions";

    public static ActiveCipherSuites Unavailable { get; } = new(CipherSuiteSource.Unavailable, []);

    public bool IsAvailable => Source != CipherSuiteSource.Unavailable;

    public string SourceText => Source switch
    {
        CipherSuiteSource.GroupPolicy => "Group Policy cipher suite order",
        CipherSuiteSource.Local => "local cipher suite list",
        _ => "cipher suite list (not available)",
    };

    /// <summary>Listed suites that use <paramref name="algorithm"/>, in list order.</summary>
    public IReadOnlyList<string> Using(SchannelAlgorithm algorithm) =>
        Names.Where(name => CipherSuiteMap.Uses(name, algorithm)).ToList();

    /// <summary>Reads the Group Policy list if present, otherwise the local list. Read failures give <see cref="Unavailable"/>.</summary>
    public static ActiveCipherSuites Read(IRegistryAccess registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        try
        {
            if (Parse(registry.GetValue(PolicyKey, FunctionsValue)) is { Count: > 0 } policy)
            {
                return new ActiveCipherSuites(CipherSuiteSource.GroupPolicy, policy);
            }

            return Parse(registry.GetValue(LocalKey, FunctionsValue)) is { } local
                ? new ActiveCipherSuites(CipherSuiteSource.Local, local)
                : Unavailable;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            return Unavailable;
        }
    }

    /// <summary>The policy stores a comma-separated REG_SZ; the local list is a REG_MULTI_SZ.</summary>
    internal static IReadOnlyList<string>? Parse(RegistryValueData? value)
    {
        IEnumerable<string>? parts = value?.Data switch
        {
            string text => text.Split(','),
            string[] items => items,
            _ => null,
        };

        return parts?
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();
    }
}

/// <summary>Maps IANA / Windows cipher suite names to the SCHANNEL algorithm keys they depend on (inferred from the names).</summary>
public static class CipherSuiteMap
{
    /// <summary>TLS 1.3 suites negotiate key exchange separately and don't encode it in the name.</summary>
    public static bool IsTls13(string suite) =>
        suite.StartsWith("TLS_AES_", StringComparison.OrdinalIgnoreCase) ||
        suite.StartsWith("TLS_CHACHA20_", StringComparison.OrdinalIgnoreCase);

    public static bool Uses(string suite, SchannelAlgorithm algorithm)
    {
        ArgumentNullException.ThrowIfNull(suite);
        ArgumentNullException.ThrowIfNull(algorithm);

        var name = suite.ToUpperInvariant();

        // Windows 7 / 8 list ECDHE suites once per curve, e.g. TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA_P256.
        foreach (var curve in (ReadOnlySpan<string>)["_P256", "_P384", "_P521"])
        {
            if (name.EndsWith(curve, StringComparison.Ordinal))
            {
                name = name[..^curve.Length];
                break;
            }
        }

        return algorithm.Category switch
        {
            AlgorithmCategory.Cipher => CipherKey(name) is { } key && Matches(key, algorithm.KeyName),
            AlgorithmCategory.Hash => HashKey(name) == algorithm.KeyName,
            AlgorithmCategory.KeyExchange => KeyExchangeKey(name) == algorithm.KeyName,
            _ => false,
        };
    }

    private static bool Matches(string cipherKey, string algorithmKey) =>
        cipherKey == algorithmKey || (cipherKey == "Triple DES 168" && algorithmKey == "Triple DES 168/168");

    private static string? CipherKey(string name)
    {
        if (name.Contains("3DES", StringComparison.Ordinal) || name.Contains("DES_192_EDE3", StringComparison.Ordinal))
        {
            return "Triple DES 168";
        }

        if (name.Contains("_AES_128_", StringComparison.Ordinal))
        {
            return "AES 128/128";
        }

        if (name.Contains("_AES_256_", StringComparison.Ordinal))
        {
            return "AES 256/256";
        }

        if (name.Contains("_RC4_", StringComparison.Ordinal))
        {
            return name.Contains("40", StringComparison.Ordinal) ? "RC4 40/128"
                : name.Contains("_RC4_56_", StringComparison.Ordinal) ? "RC4 56/128"
                : name.Contains("_RC4_64_", StringComparison.Ordinal) ? "RC4 64/128"
                : "RC4 128/128";
        }

        if (name.Contains("_RC2_", StringComparison.Ordinal))
        {
            return name.Contains("40", StringComparison.Ordinal) ? "RC2 40/128"
                : name.Contains("_56", StringComparison.Ordinal) ? "RC2 56/128"
                : "RC2 128/128";
        }

        if (name.Contains("_WITH_DES_CBC_", StringComparison.Ordinal) || name.Contains("_DES_64_CBC_", StringComparison.Ordinal))
        {
            return "DES 56/56";
        }

        return name.Contains("_WITH_NULL_", StringComparison.Ordinal) ? "NULL" : null;
    }

    private static string? HashKey(string name) => name[(name.LastIndexOf('_') + 1)..] switch
    {
        "MD5" => "MD5",
        "SHA" => "SHA",
        "SHA256" => "SHA256",
        "SHA384" => "SHA384",
        "SHA512" => "SHA512",
        _ => null,
    };

    private static string? KeyExchangeKey(string name)
    {
        if (IsTls13(name))
        {
            return null;
        }

        if (name.Contains("_ECDHE_", StringComparison.Ordinal) || name.Contains("_ECDH_", StringComparison.Ordinal))
        {
            return "ECDH";
        }

        if (name.Contains("_DHE_", StringComparison.Ordinal) || name.Contains("_DH_", StringComparison.Ordinal))
        {
            return "Diffie-Hellman";
        }

        return name.StartsWith("TLS_RSA_", StringComparison.Ordinal) || name.StartsWith("SSL_RSA_", StringComparison.Ordinal)
            ? "PKCS"
            : null;
    }
}
