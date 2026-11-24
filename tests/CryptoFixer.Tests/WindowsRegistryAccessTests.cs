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
