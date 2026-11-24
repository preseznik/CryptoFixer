using Microsoft.Win32;

namespace CryptoFixer.Core.Registry;

/// <summary><see cref="IRegistryAccess"/> backed by the real Windows registry (always the 64-bit view).</summary>
public sealed class WindowsRegistryAccess : IRegistryAccess
{
    private readonly RegistryHive _hive;
    private readonly string _basePath;
    private readonly bool _readOnly;

    /// <param name="hive">Registry hive to operate on.</param>
    /// <param name="basePath">Optional sub key that all paths are relative to (used by tests to sandbox under HKCU).</param>
    /// <param name="readOnly">When true, every write attempt throws, regardless of process privileges.</param>
    public WindowsRegistryAccess(RegistryHive hive, string basePath = "", bool readOnly = false)
    {
        _hive = hive;
        _basePath = basePath.Trim('\\');
        _readOnly = readOnly;
    }

    public bool IsReadOnly => _readOnly;

    public string RootName => _basePath.Length == 0 ? HiveName(_hive) : $"{HiveName(_hive)}\\{_basePath}";

    public bool KeyExists(string keyPath)
    {
        using var key = OpenKey(keyPath, writable: false);
        return key is not null;
    }

    public IReadOnlyList<string> GetSubKeyNames(string keyPath)
    {
        using var key = OpenKey(keyPath, writable: false);
        return key?.GetSubKeyNames() ?? [];
    }

    public IReadOnlyList<string> GetValueNames(string keyPath)
    {
        using var key = OpenKey(keyPath, writable: false);
        return key?.GetValueNames() ?? [];
    }

    public RegistryValueData? GetValue(string keyPath, string valueName)
    {
        using var key = OpenKey(keyPath, writable: false);
        if (key is null || !key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var data = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return new RegistryValueData(key.GetValueKind(valueName), data);
    }

    public void SetDWord(string keyPath, string valueName, uint value)
    {
        EnsureWritable();
        using var root = RegistryKey.OpenBaseKey(_hive, RegistryView.Registry64);
        using var key = root.CreateSubKey(FullPath(keyPath), writable: true)
            ?? throw new InvalidOperationException($"Could not create registry key {RootName}\\{keyPath}.");
        key.SetValue(valueName, unchecked((int)value), RegistryValueKind.DWord);
    }

    public bool DeleteValue(string keyPath, string valueName)
    {
        EnsureWritable();
        using var key = OpenKey(keyPath, writable: true);
        if (key is null || !key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        key.DeleteValue(valueName, throwOnMissingValue: false);
        return true;
    }

    private RegistryKey? OpenKey(string keyPath, bool writable)
    {
        using var root = RegistryKey.OpenBaseKey(_hive, RegistryView.Registry64);
        return root.OpenSubKey(FullPath(keyPath), writable);
    }

    private string FullPath(string keyPath)
    {
        keyPath = keyPath.Trim('\\');
        if (_basePath.Length == 0)
        {
            return keyPath;
        }

        return keyPath.Length == 0 ? _basePath : $"{_basePath}\\{keyPath}";
    }

    private void EnsureWritable()
    {
        if (_readOnly)
        {
            throw new InvalidOperationException("The registry is opened in read-only mode.");
        }
    }

    private static string HiveName(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
        RegistryHive.ClassesRoot => "HKEY_CLASSES_ROOT",
        RegistryHive.Users => "HKEY_USERS",
        RegistryHive.CurrentConfig => "HKEY_CURRENT_CONFIG",
        RegistryHive.PerformanceData => "HKEY_PERFORMANCE_DATA",
        _ => hive.ToString(),
    };
}
