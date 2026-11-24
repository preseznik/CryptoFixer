using CryptoFixer.Core.Registry;
using CryptoFixer.Core.Schannel;
using Microsoft.Win32;

namespace CryptoFixer.Tests;

/// <summary>In-memory <see cref="IRegistryAccess"/> fake. Never touches the real registry.</summary>
public sealed class InMemoryRegistry : IRegistryAccess
{
    private readonly Dictionary<string, Dictionary<string, RegistryValueData>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public string RootName { get; init; } = "HKEY_LOCAL_MACHINE";

    public int WriteCount { get; private set; }

    public void CreateKey(string keyPath)
    {
        var parts = Normalize(keyPath).Split('\\');
        for (var i = 1; i <= parts.Length; i++)
        {
            var path = string.Join('\\', parts.Take(i));
            if (!_keys.ContainsKey(path))
            {
                _keys[path] = new Dictionary<string, RegistryValueData>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void Set(string keyPath, string valueName, RegistryValueData value)
    {
        CreateKey(keyPath);
        _keys[Normalize(keyPath)][valueName] = value;
    }

    public void SetProtocol(SchannelProtocol protocol, ProtocolRole role, uint? enabled, uint? disabledByDefault)
    {
        var key = SchannelPaths.RoleKey(protocol, role);
        CreateKey(key);
        if (enabled is { } e)
        {
            Set(key, SchannelPaths.EnabledValue, RegistryValueData.DWord(e));
        }

        if (disabledByDefault is { } d)
        {
            Set(key, SchannelPaths.DisabledByDefaultValue, RegistryValueData.DWord(d));
        }
    }

    public bool KeyExists(string keyPath) => _keys.ContainsKey(Normalize(keyPath));

    public IReadOnlyList<string> GetSubKeyNames(string keyPath)
    {
        var prefix = Normalize(keyPath) + "\\";
        return _keys.Keys
            .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.IndexOf('\\', prefix.Length) < 0)
            .Select(k => k[prefix.Length..])
            .ToList();
    }

    public IReadOnlyList<string> GetValueNames(string keyPath) =>
        _keys.TryGetValue(Normalize(keyPath), out var values) ? values.Keys.ToList() : [];

    public RegistryValueData? GetValue(string keyPath, string valueName) =>
        _keys.TryGetValue(Normalize(keyPath), out var values) && values.TryGetValue(valueName, out var value) ? value : null;

    public void SetDWord(string keyPath, string valueName, uint value)
    {
        WriteCount++;
        Set(keyPath, valueName, RegistryValueData.DWord(value));
    }

    public bool DeleteValue(string keyPath, string valueName)
    {
        WriteCount++;
        return _keys.TryGetValue(Normalize(keyPath), out var values) && values.Remove(valueName);
    }

    /// <summary>Minimal .reg importer (keys, dword values and deletions) used to verify backups round-trip.</summary>
    public void Import(string regFileContent)
    {
        string? currentKey = null;
        var rootPrefix = RootName + "\\";
        foreach (var raw in regFileContent.Split("\r\n"))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith("Windows Registry Editor", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith('['))
            {
                var full = line[1..^1];
                Assert.StartsWith(rootPrefix, full);
                currentKey = full[rootPrefix.Length..];
                CreateKey(currentKey);
                continue;
            }

            Assert.NotNull(currentKey);
            var eq = line.IndexOf("\"=", StringComparison.Ordinal);
            var name = line[1..eq];
            var data = line[(eq + 2)..];
            if (data == "-")
            {
                _keys[Normalize(currentKey)].Remove(name);
            }
            else if (data.StartsWith("dword:", StringComparison.Ordinal))
            {
                Set(currentKey, name, RegistryValueData.DWord(Convert.ToUInt32(data["dword:".Length..], 16)));
            }
            else if (data.StartsWith('"'))
            {
                Set(currentKey, name, new RegistryValueData(RegistryValueKind.String, data[1..^1]));
            }
            else
            {
                throw new NotSupportedException(line);
            }
        }
    }

    private static string Normalize(string keyPath) => keyPath.Trim('\\');
}
