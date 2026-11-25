using CryptoFixer.Core.Schannel;

namespace CryptoFixer.Tests;

public class ChangeAdvisorTests
{
    private const int Server2019 = 17763;
    private const int Server2022 = 20348;

    private static IReadOnlyList<ProtocolRoleState> Read(InMemoryRegistry registry, int build) =>
        new SchannelProtocolStore(registry).ReadAll(build);

    [Fact]
    public void Warns_when_no_modern_tls_would_remain_for_a_role()
    {
        var states = Read(new InMemoryRegistry(), Server2019); // TLS 1.3 unsupported here

        var warnings = ChangeAdvisor.GetWarnings(states,
            [new PendingChange(ProtocolCatalog.Tls12, ProtocolRole.Server, ConfiguredSetting.Disabled)]);

        Assert.Contains(warnings, w => w.Contains("No TLS 1.2 or TLS 1.3", StringComparison.Ordinal) && w.Contains("Server", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("Client role", StringComparison.Ordinal));
    }

    [Fact]
    public void Does_not_warn_when_tls13_remains_enabled()
    {
        var states = Read(new InMemoryRegistry(), Server2022);

        var warnings = ChangeAdvisor.GetWarnings(states,
            [new PendingChange(ProtocolCatalog.Tls12, ProtocolRole.Server, ConfiguredSetting.Disabled)]);

        Assert.Empty(warnings);
    }

    [Fact]
    public void Warns_about_enabling_deprecated_or_unsupported_protocols_and_disabling_legacy_tls()
    {
        var states = Read(new InMemoryRegistry(), Server2019);

        var warnings = ChangeAdvisor.GetWarnings(states,
        [
            new PendingChange(ProtocolCatalog.Ssl30, ProtocolRole.Server, ConfiguredSetting.Enabled),
            new PendingChange(ProtocolCatalog.Tls13, ProtocolRole.Server, ConfiguredSetting.Enabled),
            new PendingChange(ProtocolCatalog.Tls10, ProtocolRole.Client, ConfiguredSetting.Disabled),
        ]);

        Assert.Contains(warnings, w => w.StartsWith("Enabling SSL 3.0", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("TLS 1.3 (Server) is not supported", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.StartsWith("Disabling TLS 1.0/1.1", StringComparison.Ordinal));
    }

    private static IReadOnlyList<AlgorithmState> Algorithms(int build = Server2022) =>
        new SchannelProtocolStore(new InMemoryRegistry()).ReadAllAlgorithms(build);

    [Fact]
    public void Disabling_weak_ciphers_and_keeping_aes_has_no_algorithm_warnings()
    {
        var changes = AlgorithmCatalog.Ciphers
            .Select(a => new AlgorithmChange(a, a.IsDeprecated ? ConfiguredSetting.Disabled : ConfiguredSetting.Enabled))
            .ToList();

        Assert.Empty(ChangeAdvisor.GetWarnings(Algorithms(), changes));
        Assert.Empty(ChangeAdvisor.GetWarnings(Algorithms(), Array.Empty<AlgorithmChange>()));
    }

    [Fact]
    public void Warns_about_enabling_deprecated_ciphers_and_disabling_both_aes_keys()
    {
        var warnings = ChangeAdvisor.GetWarnings(Algorithms(),
        [
            new AlgorithmChange(AlgorithmCatalog.Rc4_128, ConfiguredSetting.Enabled),
            new AlgorithmChange(AlgorithmCatalog.TripleDes168, ConfiguredSetting.Enabled),
            new AlgorithmChange(AlgorithmCatalog.Aes128, ConfiguredSetting.Disabled),
            new AlgorithmChange(AlgorithmCatalog.Aes256, ConfiguredSetting.Disabled),
        ]);

        Assert.Contains("Enabling RC4 128/128, Triple DES 168 weakens security: they are deprecated.", warnings);
        Assert.Contains(warnings, w => w.StartsWith("Both AES ciphers would be disabled", StringComparison.Ordinal));
        Assert.Empty(ChangeAdvisor.GetWarnings(Algorithms(), [new AlgorithmChange(AlgorithmCatalog.Aes128, ConfiguredSetting.Disabled)]));
    }

    [Fact]
    public void Warns_about_disabling_sha1_pkcs_ecdh_or_all_key_exchange()
    {
        static IReadOnlyList<string> Warn(params SchannelAlgorithm[] disabled) =>
            ChangeAdvisor.GetWarnings(Algorithms(), disabled.Select(a => new AlgorithmChange(a, ConfiguredSetting.Disabled)).ToList());

        Assert.Contains(Warn(AlgorithmCatalog.Sha1), w => w.StartsWith("Disabling SHA (SHA-1)", StringComparison.Ordinal));
        Assert.Contains(Warn(AlgorithmCatalog.Pkcs), w => w.StartsWith("Disabling PKCS", StringComparison.Ordinal));
        Assert.Contains(Warn(AlgorithmCatalog.Ecdh), w => w.StartsWith("Disabling ECDH", StringComparison.Ordinal));
        Assert.Empty(Warn(AlgorithmCatalog.Md5, AlgorithmCatalog.DiffieHellman));

        var none = Warn([.. AlgorithmCatalog.KeyExchanges]);
        Assert.Contains(none, w => w.StartsWith("No key exchange algorithm", StringComparison.Ordinal));
        Assert.DoesNotContain(none, w => w.StartsWith("Disabling ECDH", StringComparison.Ordinal));
    }
}
