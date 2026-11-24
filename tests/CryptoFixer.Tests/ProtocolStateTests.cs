using CryptoFixer.Core.Schannel;

namespace CryptoFixer.Tests;

public class ProtocolStateTests
{
    [Theory]
    [InlineData(null, null, ConfiguredSetting.NotConfigured)]
    [InlineData(1u, 0u, ConfiguredSetting.Enabled)]
    [InlineData(0xFFFFFFFFu, null, ConfiguredSetting.Enabled)]
    [InlineData(0u, 1u, ConfiguredSetting.Disabled)]
    [InlineData(0u, null, ConfiguredSetting.Disabled)]
    [InlineData(1u, 1u, ConfiguredSetting.Custom)]
    [InlineData(null, 1u, ConfiguredSetting.Custom)]
    public void Classify_maps_registry_values_to_setting(uint? enabled, uint? disabledByDefault, ConfiguredSetting expected) =>
        Assert.Equal(expected, ProtocolStateEvaluator.Classify(new ProtocolRegistryValues(enabled, disabledByDefault)));

    [Theory]
    [InlineData(null, null, OsDefaultState.Enabled, EffectiveState.Enabled)]
    [InlineData(null, null, OsDefaultState.Disabled, EffectiveState.Disabled)]
    [InlineData(0u, null, OsDefaultState.Enabled, EffectiveState.Disabled)]
    [InlineData(1u, null, OsDefaultState.Disabled, EffectiveState.Enabled)]
    [InlineData(1u, 1u, OsDefaultState.Enabled, EffectiveState.EnabledOnRequestOnly)]
    [InlineData(null, 1u, OsDefaultState.Enabled, EffectiveState.EnabledOnRequestOnly)]
    [InlineData(1u, 0u, OsDefaultState.NotSupported, EffectiveState.NotSupported)]
    public void Evaluate_combines_registry_values_with_os_default(uint? enabled, uint? disabledByDefault, OsDefaultState osDefault, EffectiveState expected) =>
        Assert.Equal(expected, ProtocolStateEvaluator.Evaluate(new ProtocolRegistryValues(enabled, disabledByDefault), osDefault));

    [Theory]
    [InlineData("TLS 1.3", 17763, OsDefaultState.NotSupported)]  // Server 2019
    [InlineData("TLS 1.3", 20348, OsDefaultState.Enabled)]       // Server 2022
    [InlineData("SSL 2.0", 14393, OsDefaultState.NotSupported)]  // Server 2016
    [InlineData("SSL 3.0", 14393, OsDefaultState.Disabled)]
    [InlineData("SSL 3.0", 9600, OsDefaultState.Enabled)]        // Server 2012 R2
    [InlineData("TLS 1.0", 26100, OsDefaultState.Enabled)]       // Server 2025
    [InlineData("TLS 1.2", 7601, OsDefaultState.Disabled)]       // Server 2008 R2
    [InlineData("TLS 1.2", 9200, OsDefaultState.Enabled)]
    [InlineData("DTLS 1.2", 9600, OsDefaultState.NotSupported)]
    [InlineData("DTLS 1.2", 14393, OsDefaultState.Enabled)]
    [InlineData("PCT 1.0", 26100, OsDefaultState.NotSupported)]
    public void OsDefaults_follow_documented_matrix(string protocol, int build, OsDefaultState expected) =>
        Assert.Equal(expected, OsDefaults.Get(ProtocolCatalog.Find(protocol)!, ProtocolRole.Server, build));

    [Fact]
    public void Ssl20_client_was_disabled_by_default_even_when_server_was_enabled()
    {
        Assert.Equal(OsDefaultState.Enabled, OsDefaults.Get(ProtocolCatalog.Ssl20, ProtocolRole.Server, 7601));
        Assert.Equal(OsDefaultState.Disabled, OsDefaults.Get(ProtocolCatalog.Ssl20, ProtocolRole.Client, 7601));
    }
}
