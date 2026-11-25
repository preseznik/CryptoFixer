namespace CryptoFixer.Core.Schannel;

/// <summary>The SCHANNEL sub key an algorithm lives under.</summary>
public enum AlgorithmCategory
{
    /// <summary>SCHANNEL\Ciphers (bulk encryption: DES, 3DES, RC2, RC4, NULL, AES).</summary>
    Cipher,

    /// <summary>SCHANNEL\Hashes (MD5, SHA, SHA256, SHA384, SHA512).</summary>
    Hash,

    /// <summary>SCHANNEL\KeyExchangeAlgorithms (PKCS, Diffie-Hellman, ECDH).</summary>
    KeyExchange,
}

/// <summary>A cipher, hash or key exchange algorithm that Schannel can switch off with an Enabled DWORD.</summary>
/// <param name="KeyName">Registry key name, e.g. "RC4 128/128". It may contain '/', which is part of the name, not a path separator.</param>
/// <param name="IsDeprecated">True for weak or broken algorithms (DES, 3DES, RC2, RC4, NULL, MD5).</param>
/// <param name="IsDocumented">False when Microsoft doesn't document the key (AES, SHA-2 and ECDH keys used by common tools).</param>
public sealed record SchannelAlgorithm(string KeyName, AlgorithmCategory Category, bool IsDeprecated, bool IsDocumented, string Description)
{
    public string DisplayName => KeyName;

    public override string ToString() => KeyName;
}

public static class AlgorithmCatalog
{
    public static readonly SchannelAlgorithm Null = new("NULL", AlgorithmCategory.Cipher, true, true,
        "No encryption. Only used when an application explicitly asks for a NULL cipher suite.");

    public static readonly SchannelAlgorithm Des56 = new("DES 56/56", AlgorithmCategory.Cipher, true, true,
        "Single DES (56-bit key). Broken; its cipher suites are not enabled by default.");

    public static readonly SchannelAlgorithm Rc2_40 = new("RC2 40/128", AlgorithmCategory.Cipher, true, true,
        "40-bit export RC2. Legacy key: Windows 7 and later have no RC2 cipher suites.");

    public static readonly SchannelAlgorithm Rc2_56 = new("RC2 56/128", AlgorithmCategory.Cipher, true, true,
        "56-bit RC2. Legacy key: Windows 7 and later have no RC2 cipher suites.");

    public static readonly SchannelAlgorithm Rc2_128 = new("RC2 128/128", AlgorithmCategory.Cipher, true, true,
        "128-bit RC2. Legacy key: Windows 7 and later have no RC2 cipher suites.");

    public static readonly SchannelAlgorithm Rc4_40 = new("RC4 40/128", AlgorithmCategory.Cipher, true, true,
        "40-bit export RC4 (RFC 7465 prohibits RC4). Its suites are not enabled by default.");

    public static readonly SchannelAlgorithm Rc4_56 = new("RC4 56/128", AlgorithmCategory.Cipher, true, true,
        "56-bit export RC4 (RFC 7465 prohibits RC4). Its suites are not enabled by default.");

    public static readonly SchannelAlgorithm Rc4_64 = new("RC4 64/128", AlgorithmCategory.Cipher, true, true,
        "64-bit RC4 (RFC 7465 prohibits RC4). Legacy key: no current cipher suite uses it.");

    public static readonly SchannelAlgorithm Rc4_128 = new("RC4 128/128", AlgorithmCategory.Cipher, true, true,
        "128-bit RC4 (RFC 7465 prohibits RC4). Enabled by default before Windows 10 1709 / Server 2019.");

    public static readonly SchannelAlgorithm TripleDes168 = new("Triple DES 168", AlgorithmCategory.Cipher, true, true,
        "3DES (Sweet32). Enabled by default up to Windows Server 2022; off by default from Windows 11 22H2 / Server 2025.");

    public static readonly SchannelAlgorithm TripleDes168Legacy = new("Triple DES 168/168", AlgorithmCategory.Cipher, true, true,
        "Name of the 3DES key before Windows Vista. Newer Windows reads \"Triple DES 168\"; set both to be safe.");

    public static readonly SchannelAlgorithm Aes128 = new("AES 128/128", AlgorithmCategory.Cipher, false, false,
        "AES with a 128-bit key (GCM and CBC suites). Key not documented by Microsoft; used by common tools.");

    public static readonly SchannelAlgorithm Aes256 = new("AES 256/256", AlgorithmCategory.Cipher, false, false,
        "AES with a 256-bit key (GCM and CBC suites). Key not documented by Microsoft; used by common tools.");

    public static readonly SchannelAlgorithm Md5 = new("MD5", AlgorithmCategory.Hash, true, true,
        "MD5. Used by the *_MD5 cipher suites (none enabled by default since Windows 10 1709) and older handshakes.");

    public static readonly SchannelAlgorithm Sha1 = new("SHA", AlgorithmCategory.Hash, false, true,
        "SHA-1. Used by the *_SHA cipher suites (e.g. ECDHE_RSA_WITH_AES_256_CBC_SHA) and TLS 1.0 / 1.1.");

    public static readonly SchannelAlgorithm Sha256 = new("SHA256", AlgorithmCategory.Hash, false, false,
        "SHA-256. Used by the *_SHA256 cipher suites. Key not documented by Microsoft.");

    public static readonly SchannelAlgorithm Sha384 = new("SHA384", AlgorithmCategory.Hash, false, false,
        "SHA-384. Used by the *_SHA384 cipher suites. Key not documented by Microsoft.");

    public static readonly SchannelAlgorithm Sha512 = new("SHA512", AlgorithmCategory.Hash, false, false,
        "SHA-512. No TLS cipher suite uses it; used for signatures. Key not documented by Microsoft.");

    public static readonly SchannelAlgorithm Pkcs = new("PKCS", AlgorithmCategory.KeyExchange, false, true,
        "RSA key exchange (TLS_RSA_WITH_* suites, no forward secrecy). Disabling it can also break RSA certificates.");

    public static readonly SchannelAlgorithm DiffieHellman = new("Diffie-Hellman", AlgorithmCategory.KeyExchange, false, true,
        "Finite-field DHE key exchange (TLS_DHE_* suites). Minimum key sizes are read-only here.");

    public static readonly SchannelAlgorithm Ecdh = new("ECDH", AlgorithmCategory.KeyExchange, false, false,
        "Elliptic-curve ECDHE key exchange (TLS_ECDHE_* suites, preferred). Key not documented by Microsoft.");

    /// <summary>Ciphers from weakest to strongest; this is also the default "catalog" sort order.</summary>
    public static IReadOnlyList<SchannelAlgorithm> Ciphers { get; } =
    [
        Null,
        Des56,
        Rc2_40,
        Rc2_56,
        Rc2_128,
        Rc4_40,
        Rc4_56,
        Rc4_64,
        Rc4_128,
        TripleDes168,
        TripleDes168Legacy,
        Aes128,
        Aes256,
    ];

    public static IReadOnlyList<SchannelAlgorithm> Hashes { get; } = [Md5, Sha1, Sha256, Sha384, Sha512];

    public static IReadOnlyList<SchannelAlgorithm> KeyExchanges { get; } = [Pkcs, DiffieHellman, Ecdh];

    public static IReadOnlyList<SchannelAlgorithm> All { get; } = [.. Ciphers, .. Hashes, .. KeyExchanges];

    public static IReadOnlyList<SchannelAlgorithm> In(AlgorithmCategory category) => category switch
    {
        AlgorithmCategory.Cipher => Ciphers,
        AlgorithmCategory.Hash => Hashes,
        AlgorithmCategory.KeyExchange => KeyExchanges,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    public static SchannelAlgorithm? Find(AlgorithmCategory category, string keyName) =>
        In(category).FirstOrDefault(a => string.Equals(a.KeyName, keyName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Position in <see cref="All"/>; used to keep a stable catalog order.</summary>
    public static int IndexOf(SchannelAlgorithm algorithm)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (All[i] == algorithm)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Registry folder name under SCHANNEL.</summary>
    public static string FolderName(AlgorithmCategory category) => category switch
    {
        AlgorithmCategory.Cipher => "Ciphers",
        AlgorithmCategory.Hash => "Hashes",
        AlgorithmCategory.KeyExchange => "KeyExchangeAlgorithms",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    /// <summary>Singular label for messages, e.g. "cipher".</summary>
    public static string Noun(AlgorithmCategory category) => category switch
    {
        AlgorithmCategory.Cipher => "cipher",
        AlgorithmCategory.Hash => "hash",
        AlgorithmCategory.KeyExchange => "key exchange algorithm",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    /// <summary>Plural label for headings, e.g. "Ciphers".</summary>
    public static string Title(AlgorithmCategory category) => category switch
    {
        AlgorithmCategory.Cipher => "Ciphers",
        AlgorithmCategory.Hash => "Hashes",
        AlgorithmCategory.KeyExchange => "Key exchange",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };
}
