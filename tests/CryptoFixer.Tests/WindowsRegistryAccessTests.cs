using CryptoFixer.Core.Registry;
using Microsoft.Win32;

namespace CryptoFixer.Tests;

/// <summary>
/// Exercises the real registry wrapper against a throwaway HKCU key that is deleted afterwards.
/// HKLM is never written by any test.
/// </summary>
public sealed class WindowsRegistryAccessTests : IDisposable
{
    private readonly string _sandbox = $@"Software\CryptoFixerTests\{Guid.NewGuid():N}";

    [Fact]
    public void Writes_reads_and_deletes_dword_values_in_a_sandbox_key()
    {
        var registry = new WindowsRegistryAccess(RegistryHive.CurrentUser, _sandbox);

        Assert.False(registry.KeyExists(@"A\B"));
        registry.SetDWord(@"A\B", "Enabled", 0xFFFFFFFF);

        Assert.True(registry.KeyExists(@"A\B"));
        Assert.Equal(["B"], registry.GetSubKeyNames("A"));
        Assert.Equal(0xFFFFFFFFu, registry.GetValue(@"A\B", "Enabled")!.AsUInt32());
        Assert.Equal(RegistryValueKind.DWord, registry.GetValue(@"A\B", "Enabled")!.Kind);
        Assert.StartsWith(@"HKEY_CURRENT_USER\Software\CryptoFixerTests\", registry.RootName);

        Assert.True(registry.DeleteValue(@"A\B", "Enabled"));
        Assert.False(registry.DeleteValue(@"A\B", "Enabled"));
        Assert.Null(registry.GetValue(@"A\B", "Enabled"));
    }

    [Fact]
    public void Read_only_instance_refuses_writes()
    {
        var registry = new WindowsRegistryAccess(RegistryHive.CurrentUser, _sandbox, readOnly: true);

        Assert.Throws<InvalidOperationException>(() => registry.SetDWord("X", "Enabled", 1));
        Assert.Throws<InvalidOperationException>(() => registry.DeleteValue("X", "Enabled"));
        Assert.False(registry.KeyExists("X"));
    }

    [Fact]
    public void Slash_in_a_cipher_key_name_creates_one_key_not_a_subkey_path()
    {
        var registry = new WindowsRegistryAccess(RegistryHive.CurrentUser, _sandbox);
        var store = new CryptoFixer.Core.Schannel.SchannelProtocolStore(registry);

        store.Apply(
        [
            new CryptoFixer.Core.Schannel.AlgorithmChange(CryptoFixer.Core.Schannel.AlgorithmCatalog.Rc4_128, CryptoFixer.Core.Schannel.ConfiguredSetting.Disabled),
            new CryptoFixer.Core.Schannel.AlgorithmChange(CryptoFixer.Core.Schannel.AlgorithmCatalog.Aes256, CryptoFixer.Core.Schannel.ConfiguredSetting.Enabled),
        ]);

        using var ciphers = Registry.CurrentUser.OpenSubKey($@"{_sandbox}\SYSTEM\CurrentControlSet\Control\SecurityProviders\SCHANNEL\Ciphers");
        Assert.NotNull(ciphers);
        Assert.Equal(["AES 256/256", "RC4 128/128"], ciphers.GetSubKeyNames().Order());
        using var rc4 = ciphers.OpenSubKey("RC4 128/128");
        Assert.Equal(0, rc4!.GetValue("Enabled"));
        Assert.Empty(rc4.GetSubKeyNames());
        Assert.Null(ciphers.OpenSubKey("RC4 128"));

        var states = store.ReadAllAlgorithms(20348);
        Assert.Equal(0u, states.Single(s => s.Algorithm == CryptoFixer.Core.Schannel.AlgorithmCatalog.Rc4_128).Enabled);
        Assert.Equal(0xFFFFFFFFu, states.Single(s => s.Algorithm == CryptoFixer.Core.Schannel.AlgorithmCatalog.Aes256).Enabled);
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_sandbox, throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(@"Software\CryptoFixerTests", writable: true);
        if (parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
        {
            Registry.CurrentUser.DeleteSubKey(@"Software\CryptoFixerTests", throwOnMissingSubKey: false);
        }
    }
}
