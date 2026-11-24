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
}
