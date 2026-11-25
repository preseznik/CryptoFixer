using CryptoFixer.Core.Registry;
using CryptoFixer.Core.Remote;
using CryptoFixer.Core.Schannel;
using Microsoft.Win32;

namespace CryptoFixer.Tests;

public class CipherSuiteTests
{
    [Theory]
    [InlineData("TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384", "AES 256/256", "SHA384", "ECDH")]
    [InlineData("TLS_ECDHE_ECDSA_WITH_AES_128_CBC_SHA256_P256", "AES 128/128", "SHA256", "ECDH")]
    [InlineData("TLS_DHE_RSA_WITH_AES_128_CBC_SHA", "AES 128/128", "SHA", "Diffie-Hellman")]
    [InlineData("TLS_RSA_WITH_3DES_EDE_CBC_SHA", "Triple DES 168", "SHA", "PKCS")]
    [InlineData("TLS_RSA_WITH_RC4_128_MD5", "RC4 128/128", "MD5", "PKCS")]
    [InlineData("TLS_RSA_EXPORT_WITH_RC4_40_MD5", "RC4 40/128", "MD5", "PKCS")]
    [InlineData("TLS_RSA_EXPORT1024_WITH_RC4_56_SHA", "RC4 56/128", "SHA", "PKCS")]
    [InlineData("TLS_RSA_WITH_DES_CBC_SHA", "DES 56/56", "SHA", "PKCS")]
    [InlineData("TLS_RSA_WITH_NULL_SHA256", "NULL", "SHA256", "PKCS")]
    [InlineData("TLS_PSK_WITH_AES_128_GCM_SHA256", "AES 128/128", "SHA256", null)]
    [InlineData("TLS_AES_256_GCM_SHA384", "AES 256/256", "SHA384", null)]
    public void Suites_map_to_exactly_one_cipher_hash_and_key_exchange(string suite, string cipher, string hash, string? keyExchange)
    {
        string[] Used(AlgorithmCategory category) =>
            AlgorithmCatalog.In(category).Where(a => CipherSuiteMap.Uses(suite, a)).Select(a => a.KeyName).ToArray();

        var ciphers = Used(AlgorithmCategory.Cipher);
        string[] expectedCiphers = cipher == "Triple DES 168" ? ["Triple DES 168", "Triple DES 168/168"] : [cipher];
        string[] expectedKeyExchange = keyExchange is null ? [] : [keyExchange];
        Assert.Equal(expectedCiphers, ciphers);
        Assert.Equal([hash], Used(AlgorithmCategory.Hash));
        Assert.Equal(expectedKeyExchange, Used(AlgorithmCategory.KeyExchange));
    }

    [Fact]
    public void Tls13_suites_are_recognised()
    {
        Assert.True(CipherSuiteMap.IsTls13("TLS_AES_128_GCM_SHA256"));
        Assert.True(CipherSuiteMap.IsTls13("TLS_CHACHA20_POLY1305_SHA256"));
        Assert.False(CipherSuiteMap.IsTls13("TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256"));
    }

    [Fact]
    public void Group_policy_comma_list_wins_over_the_local_multi_string()
    {
        var registry = new InMemoryRegistry();
        registry.Set(ActiveCipherSuites.LocalKey, "Functions",
            new RegistryValueData(RegistryValueKind.MultiString, new[] { "TLS_AES_256_GCM_SHA384", " ", "TLS_RSA_WITH_AES_256_CBC_SHA" }));

        var local = ActiveCipherSuites.Read(registry);
        Assert.Equal(CipherSuiteSource.Local, local.Source);
        Assert.Equal(["TLS_AES_256_GCM_SHA384", "TLS_RSA_WITH_AES_256_CBC_SHA"], local.Names);

        registry.Set(ActiveCipherSuites.PolicyKey, "Functions", new RegistryValueData(RegistryValueKind.String, ""));
        Assert.Equal(CipherSuiteSource.Local, ActiveCipherSuites.Read(registry).Source); // an empty policy value is ignored

        registry.Set(ActiveCipherSuites.PolicyKey, "Functions",
            new RegistryValueData(RegistryValueKind.String, "TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256, TLS_RSA_WITH_3DES_EDE_CBC_SHA,,"));
        var policy = ActiveCipherSuites.Read(registry);

        Assert.Equal(CipherSuiteSource.GroupPolicy, policy.Source);
        Assert.Equal(["TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256", "TLS_RSA_WITH_3DES_EDE_CBC_SHA"], policy.Names);
        Assert.Equal(["TLS_RSA_WITH_3DES_EDE_CBC_SHA"], policy.Using(AlgorithmCatalog.TripleDes168));
        Assert.Empty(policy.Using(AlgorithmCatalog.Aes256));
    }

    [Fact]
    public void Missing_or_unreadable_lists_are_unavailable()
    {
        Assert.Equal(ActiveCipherSuites.Unavailable, ActiveCipherSuites.Read(new InMemoryRegistry()));

        var provider = new FakeStdRegProv();
        provider.Failures["EnumValues"] = 5;
        var denied = ActiveCipherSuites.Read(new CimRegistryAccess(provider, "srv01", readOnly: true));

        Assert.False(denied.IsAvailable);
        Assert.Empty(denied.Using(AlgorithmCatalog.Aes128));
    }
}
