using System.Text;
using CryptoFixer.Core.Backup;
using CryptoFixer.Core.Registry;
using CryptoFixer.Core.Schannel;
using Microsoft.Win32;

namespace CryptoFixer.Tests;

public class RegFileExporterTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Export_writes_regedit_format_with_existing_values()
    {
        var registry = new InMemoryRegistry();
        registry.SetProtocol(ProtocolCatalog.Tls12, ProtocolRole.Client, 0xFFFFFFFF, 0);

        var text = RegFileExporter.Export(registry, Timestamp, "SRV01");

        Assert.StartsWith("Windows Registry Editor Version 5.00\r\n", text);
        Assert.Contains(
            "[HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\SecurityProviders\\SCHANNEL\\Protocols\\TLS 1.2\\Client]\r\n" +
            "\"DisabledByDefault\"=dword:00000000\r\n" +
            "\"Enabled\"=dword:ffffffff\r\n",
            text);
    }

    [Fact]
    public void Importing_backup_restores_original_state_including_removal_of_later_values()
    {
        var registry = new InMemoryRegistry();
        registry.SetProtocol(ProtocolCatalog.Tls10, ProtocolRole.Server, 0, 1);
        registry.SetProtocol(ProtocolCatalog.Ssl30, ProtocolRole.Client, null, 1);
        var store = new SchannelProtocolStore(registry);
        var before = store.ReadAll(20348);

        var backup = RegFileExporter.Export(registry, Timestamp);
        store.Apply(
        [
            new PendingChange(ProtocolCatalog.Tls10, ProtocolRole.Server, ConfiguredSetting.NotConfigured),
            new PendingChange(ProtocolCatalog.Tls11, ProtocolRole.Server, ConfiguredSetting.Disabled),
            new PendingChange(ProtocolCatalog.Ssl30, ProtocolRole.Client, ConfiguredSetting.Enabled),
        ]);
        Assert.NotEqual(before, store.ReadAll(20348));

        registry.Import(backup);

        Assert.Equal(before, store.ReadAll(20348));
    }

    [Fact]
    public void Export_preserves_unrelated_values_in_the_protocols_tree()
    {
        var registry = new InMemoryRegistry();
        var key = SchannelPaths.Protocols + "\\TLS 1.2\\Server";
        registry.Set(key, "Note", new RegistryValueData(RegistryValueKind.String, "a \"quoted\" C:\\path"));

        var text = RegFileExporter.Export(registry, Timestamp);

        Assert.Contains("\"Note\"=\"a \\\"quoted\\\" C:\\\\path\"\r\n", text);
    }

    [Fact]
    public void Export_includes_cipher_hash_and_key_exchange_keys_with_slash_names()
    {
        var registry = new InMemoryRegistry();
        registry.SetDWord(SchannelPaths.AlgorithmKey(AlgorithmCatalog.Rc4_128), "Enabled", 0);
        registry.SetDWord(SchannelPaths.AlgorithmKey(AlgorithmCatalog.DiffieHellman), "ServerMinKeyBitLength", 3072);

        var text = RegFileExporter.Export(registry, Timestamp);

        const string root = "[HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\SecurityProviders\\SCHANNEL\\";
        Assert.Contains(root + "Ciphers\\RC4 128/128]\r\n\"Enabled\"=dword:00000000\r\n", text);
        Assert.Contains(root + "Ciphers\\AES 128/128]\r\n\"Enabled\"=-\r\n", text);
        Assert.Contains(root + "Hashes\\MD5]\r\n\"Enabled\"=-\r\n", text);
        Assert.Contains(root + "KeyExchangeAlgorithms\\Diffie-Hellman]\r\n\"ServerMinKeyBitLength\"=dword:00000c00\r\n\"Enabled\"=-\r\n", text);
        Assert.DoesNotContain("RC4 128]", text);
    }

    [Fact]
    public void Importing_backup_restores_original_algorithm_state()
    {
        var registry = new InMemoryRegistry();
        registry.SetDWord(SchannelPaths.AlgorithmKey(AlgorithmCatalog.TripleDes168), "Enabled", 0);
        registry.SetDWord(SchannelPaths.AlgorithmKey(AlgorithmCatalog.DiffieHellman), "Enabled", 1);
        var store = new SchannelProtocolStore(registry);
        static List<(SchannelAlgorithm, uint?)> Values(SchannelProtocolStore s) =>
            s.ReadAllAlgorithms(20348).Select(a => (a.Algorithm, a.Enabled)).ToList();
        var before = Values(store);

        var backup = RegFileExporter.Export(registry, Timestamp);
        store.Apply(
        [
            new AlgorithmChange(AlgorithmCatalog.TripleDes168, ConfiguredSetting.NotConfigured),
            new AlgorithmChange(AlgorithmCatalog.Rc4_128, ConfiguredSetting.Disabled),
            new AlgorithmChange(AlgorithmCatalog.DiffieHellman, ConfiguredSetting.Disabled),
            new AlgorithmChange(AlgorithmCatalog.Aes256, ConfiguredSetting.Enabled),
        ]);
        Assert.NotEqual(before, Values(store));

        registry.Import(backup);

        Assert.Equal(before, Values(store));
    }

    [Theory]
    [InlineData(RegistryValueKind.QWord, 1L, "\"v\"=hex(b):01,00,00,00,00,00,00,00")]
    [InlineData(RegistryValueKind.ExpandString, "%A%", "\"v\"=hex(2):25,00,41,00,25,00,00,00")]
    public void FormatValue_uses_regedit_hex_encodings(RegistryValueKind kind, object data, string expected) =>
        Assert.Equal(expected, RegFileExporter.FormatValue("v", new RegistryValueData(kind, data)));

    [Fact]
    public void WriteFile_uses_utf16_with_bom_like_regedit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cryptofixer-{Guid.NewGuid():N}.reg");
        try
        {
            RegFileExporter.WriteFile(path, "Windows Registry Editor Version 5.00\r\n");
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes[..2]);
            Assert.Equal("Windows Registry Editor Version 5.00\r\n", Encoding.Unicode.GetString(bytes[2..]));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
