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
    public void ReadAllAlgorithms_reads_every_cipher_hash_and_key_exchange_key()
    {
        _registry.SetDWord(SchannelPaths.AlgorithmKey(AlgorithmCatalog.Rc4_128), "Enabled", 0);
        _registry.SetDWord(SchannelPaths.AlgorithmKey(AlgorithmCatalog.DiffieHellman), "Enabled", 1);
        _registry.SetDWord(SchannelPaths.AlgorithmKey(AlgorithmCatalog.DiffieHellman), "ServerMinKeyBitLength", 3072);

        var states = _store.ReadAllAlgorithms(Server2022);

        Assert.Equal(AlgorithmCatalog.All, states.Select(s => s.Algorithm));
        var rc4 = states.Single(s => s.Algorithm == AlgorithmCatalog.Rc4_128);
        Assert.Equal((ConfiguredSetting.Disabled, EffectiveState.Disabled, true), (rc4.Configured, rc4.Effective, rc4.IsExplicitlyConfigured));

        var dh = states.Single(s => s.Algorithm == AlgorithmCatalog.DiffieHellman);
        Assert.Equal(ConfiguredSetting.Enabled, dh.Configured); // Microsoft's own examples use 1, not 0xffffffff
        Assert.Equal("ServerMinKeyBitLength = 3072 (set in registry)", dh.KeySizes[0].Describe());
        Assert.Equal("ClientMinKeyBitLength: 1024 bits (OS default)", dh.KeySizes[1].Describe());

        var aes = states.Single(s => s.Algorithm == AlgorithmCatalog.Aes256);
        Assert.Equal((ConfiguredSetting.NotConfigured, EffectiveState.Enabled), (aes.Configured, aes.Effective));
        Assert.Empty(aes.KeySizes);
    }

    [Theory]
    [InlineData(20348, true)]  // Server 2022
    [InlineData(19044, true)]  // Windows 10 21H2
    [InlineData(19045, false)] // Windows 10 22H2
    [InlineData(22621, false)] // Windows 11 22H2
    [InlineData(26100, false)] // Server 2025
    public void Triple_des_default_depends_on_the_build(int build, bool enabled)
    {
        var state = _store.Read(AlgorithmCatalog.TripleDes168, build);
        var legacy = _store.Read(AlgorithmCatalog.TripleDes168Legacy, build);

        var expected = enabled ? OsDefaultState.Enabled : OsDefaultState.Disabled;
        Assert.Equal(expected, state.OsDefault.State);
        Assert.Equal(expected, legacy.OsDefault.State);
        Assert.False(state.OsDefault.IsInferred);
        Assert.True(legacy.OsDefault.IsInferred);
    }

    [Theory]
    [InlineData(14393, true)]
    [InlineData(15063, true)]
    [InlineData(16299, false)]
    [InlineData(17763, false)]
    public void Rc4_128_default_is_enabled_only_before_windows_10_1709(int build, bool enabled) =>
        Assert.Equal(enabled ? OsDefaultState.Enabled : OsDefaultState.Disabled, AlgorithmDefaults.Get(AlgorithmCatalog.Rc4_128, build).State);

    [Fact]
    public void Algorithm_defaults_mark_undocumented_and_inferred_keys()
    {
        Assert.All(AlgorithmCatalog.All, a => Assert.NotNull(AlgorithmDefaults.Get(a, Server2022)));
        Assert.All(AlgorithmCatalog.All.Where(a => !a.IsDocumented), a => Assert.True(AlgorithmDefaults.Get(a, Server2022).IsInferred));
        Assert.True(AlgorithmDefaults.Get(AlgorithmCatalog.Rc2_128, Server2022).IsInferred);
        Assert.False(AlgorithmDefaults.Get(AlgorithmCatalog.Des56, Server2022).IsInferred);
        Assert.Equal(OsDefaultState.Disabled, AlgorithmDefaults.Get(AlgorithmCatalog.Null, Server2022).State);
        Assert.Equal(OsDefaultState.Enabled, AlgorithmDefaults.Get(AlgorithmCatalog.Md5, Server2022).State);
        Assert.Equal("1024 bits", AlgorithmDefaults.KeySizeValues(AlgorithmCatalog.DiffieHellman, 9600)[0].DefaultText);
        Assert.Equal("2048 bits", AlgorithmDefaults.KeySizeValues(AlgorithmCatalog.DiffieHellman, 10240)[0].DefaultText);
    }

    [Fact]
    public void Apply_algorithm_changes_writes_ffffffff_or_zero_and_reset_deletes_only_Enabled()
    {
        var dhKey = SchannelPaths.AlgorithmKey(AlgorithmCatalog.DiffieHellman);
        _registry.SetDWord(dhKey, "Enabled", 0);
        _registry.SetDWord(dhKey, "ServerMinKeyBitLength", 3072);

        _store.Apply(
        [
            new AlgorithmChange(AlgorithmCatalog.Aes128, ConfiguredSetting.Enabled),
            new AlgorithmChange(AlgorithmCatalog.Rc4_128, ConfiguredSetting.Disabled),
            new AlgorithmChange(AlgorithmCatalog.DiffieHellman, ConfiguredSetting.NotConfigured),
        ]);

        Assert.Equal(0xFFFFFFFFu, _registry.GetValue(SchannelPaths.AlgorithmKey(AlgorithmCatalog.Aes128), "Enabled")!.AsUInt32());
        Assert.Equal(0u, _registry.GetValue(SchannelPaths.AlgorithmKey(AlgorithmCatalog.Rc4_128), "Enabled")!.AsUInt32());
        Assert.Null(_registry.GetValue(dhKey, "Enabled"));
        Assert.Equal(3072u, _registry.GetValue(dhKey, "ServerMinKeyBitLength")!.AsUInt32());
        Assert.Equal(["AES 128/128", "RC4 128/128"], _registry.GetSubKeyNames(SchannelPaths.AlgorithmFolder(AlgorithmCategory.Cipher)).Order());
    }

    [Fact]
    public void Apply_rejects_custom_algorithm_target_without_writing_anything()
    {
        Assert.Throws<ArgumentException>(() => _store.Apply(
        [
            new AlgorithmChange(AlgorithmCatalog.Aes128, ConfiguredSetting.Enabled),
            new AlgorithmChange(AlgorithmCatalog.Md5, ConfiguredSetting.Custom),
        ]));

        Assert.Equal(0, _registry.WriteCount);
    }

    [Fact]
    public void ReadSnapshot_includes_the_active_cipher_suites()
    {
        _registry.Set(ActiveCipherSuites.LocalKey, "Functions",
            new(Microsoft.Win32.RegistryValueKind.MultiString, new[] { "TLS_AES_256_GCM_SHA384", "TLS_RSA_WITH_3DES_EDE_CBC_SHA" }));

        var snapshot = _store.ReadSnapshot(Server2022);

        Assert.Equal(ProtocolCatalog.All.Count * 2, snapshot.Protocols.Count);
        Assert.Equal(AlgorithmCatalog.All.Count, snapshot.Algorithms.Count);
        Assert.Equal(CipherSuiteSource.Local, snapshot.CipherSuites.Source);
        Assert.Equal(["TLS_RSA_WITH_3DES_EDE_CBC_SHA"], snapshot.CipherSuites.Using(AlgorithmCatalog.TripleDes168));
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
