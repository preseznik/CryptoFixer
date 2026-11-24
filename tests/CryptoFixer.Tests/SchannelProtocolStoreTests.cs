using CryptoFixer.Core.Schannel;

namespace CryptoFixer.Tests;

public class SchannelProtocolStoreTests
{
    private const int Server2022 = 20348;

    private readonly InMemoryRegistry _registry = new();
    private readonly SchannelProtocolStore _store;

    public SchannelProtocolStoreTests() => _store = new SchannelProtocolStore(_registry);

    [Fact]
    public void ReadAll_returns_server_and_client_state_for_every_protocol()
    {
        _registry.SetProtocol(ProtocolCatalog.Tls10, ProtocolRole.Server, 0, 1);

        var states = _store.ReadAll(Server2022);

        Assert.Equal(ProtocolCatalog.All.Count * 2, states.Count);
        var tls10Server = states.Single(s => s.Protocol == ProtocolCatalog.Tls10 && s.Role == ProtocolRole.Server);
        Assert.Equal(ConfiguredSetting.Disabled, tls10Server.Configured);
        Assert.Equal(EffectiveState.Disabled, tls10Server.Effective);
        Assert.True(tls10Server.IsExplicitlyConfigured);

        var tls10Client = states.Single(s => s.Protocol == ProtocolCatalog.Tls10 && s.Role == ProtocolRole.Client);
        Assert.Equal(ConfiguredSetting.NotConfigured, tls10Client.Configured);
        Assert.Equal(EffectiveState.Enabled, tls10Client.Effective);
        Assert.False(tls10Client.IsExplicitlyConfigured);
    }

    [Fact]
    public void Apply_enable_and_disable_write_both_values_to_the_role_key()
    {
        _store.Apply(
        [
            new PendingChange(ProtocolCatalog.Tls12, ProtocolRole.Client, ConfiguredSetting.Enabled),
            new PendingChange(ProtocolCatalog.Tls11, ProtocolRole.Server, ConfiguredSetting.Disabled),
        ]);

        Assert.Equal(new ProtocolRegistryValues(1, 0), _store.ReadValues(ProtocolCatalog.Tls12, ProtocolRole.Client));
        Assert.Equal(new ProtocolRegistryValues(0, 1), _store.ReadValues(ProtocolCatalog.Tls11, ProtocolRole.Server));
        Assert.Equal(ProtocolRegistryValues.None, _store.ReadValues(ProtocolCatalog.Tls11, ProtocolRole.Client));
    }

    [Fact]
    public void Apply_reset_to_os_default_deletes_only_the_managed_values()
    {
        _registry.SetProtocol(ProtocolCatalog.Ssl30, ProtocolRole.Server, 1, 1);
        var key = SchannelPaths.RoleKey(ProtocolCatalog.Ssl30, ProtocolRole.Server);
        _registry.Set(key, "SomethingElse", Core.Registry.RegistryValueData.DWord(7));

        _store.Apply([new PendingChange(ProtocolCatalog.Ssl30, ProtocolRole.Server, ConfiguredSetting.NotConfigured)]);

        var state = _store.Read(ProtocolCatalog.Ssl30, ProtocolRole.Server, Server2022);
        Assert.Equal(ConfiguredSetting.NotConfigured, state.Configured);
        Assert.Equal(EffectiveState.Disabled, state.Effective);
        Assert.Equal(7u, _registry.GetValue(key, "SomethingElse")!.AsUInt32());
    }

    [Fact]
    public void Apply_rejects_custom_target_without_writing_anything()
    {
        Assert.Throws<ArgumentException>(() => _store.Apply(
        [
            new PendingChange(ProtocolCatalog.Tls12, ProtocolRole.Server, ConfiguredSetting.Enabled),
            new PendingChange(ProtocolCatalog.Tls10, ProtocolRole.Server, ConfiguredSetting.Custom),
        ]));

        Assert.Equal(0, _registry.WriteCount);
    }

    [Fact]
    public void WindowsVersionInfo_reports_windows_11_and_server_correctly()
    {
        var client = new InMemoryRegistry();
        client.Set(WindowsVersionInfo.CurrentVersionKey, "ProductName", new(Microsoft.Win32.RegistryValueKind.String, "Windows 10 Pro"));
        client.Set(WindowsVersionInfo.CurrentVersionKey, "CurrentBuildNumber", new(Microsoft.Win32.RegistryValueKind.String, "26200"));
        client.Set(WindowsVersionInfo.CurrentVersionKey, "InstallationType", new(Microsoft.Win32.RegistryValueKind.String, "Client"));
        client.Set(WindowsVersionInfo.CurrentVersionKey, "UBR", Core.Registry.RegistryValueData.DWord(7171));

        var info = WindowsVersionInfo.Read(client, fallbackBuild: 1);
        Assert.Equal("Windows 11 Pro", info.ProductName);
        Assert.Equal(26200, info.Build);
        Assert.Equal(7171, info.Revision);
        Assert.False(info.IsServer);

        var server = new InMemoryRegistry();
        server.Set(WindowsVersionInfo.CurrentVersionKey, "ProductName", new(Microsoft.Win32.RegistryValueKind.String, "Windows Server 2022 Datacenter"));
        server.Set(WindowsVersionInfo.CurrentVersionKey, "InstallationType", new(Microsoft.Win32.RegistryValueKind.String, "Server Core"));

        var serverInfo = WindowsVersionInfo.Read(server, fallbackBuild: 20348);
        Assert.True(serverInfo.IsServer);
        Assert.Equal(20348, serverInfo.Build);
    }
}
