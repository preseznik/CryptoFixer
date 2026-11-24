using Microsoft.Win32;

namespace CryptoFixer.Core.Registry;

/// <summary>A registry value as returned by <see cref="IRegistryAccess"/>. <see cref="Data"/> uses the same
/// CLR types as <see cref="RegistryKey.GetValue(string)"/> (int for DWORD, long for QWORD, string, string[], byte[]).</summary>
public sealed record RegistryValueData(RegistryValueKind Kind, object? Data)
{
    public static RegistryValueData DWord(uint value) => new(RegistryValueKind.DWord, unchecked((int)value));

    /// <summary>Returns the value as an unsigned 32-bit number for DWORD/QWORD values, otherwise null.</summary>
    public uint? AsUInt32() => (Kind, Data) switch
    {
        (RegistryValueKind.DWord, int i) => unchecked((uint)i),
        (RegistryValueKind.QWord, long l) => unchecked((uint)l),
        _ => null,
    };
}

/// <summary>
/// Minimal registry abstraction so the Schannel logic can be unit tested without touching the real registry.
/// All key paths are relative to the root the implementation was created for (e.g. HKEY_LOCAL_MACHINE).
/// </summary>
public interface IRegistryAccess
{
    /// <summary>Full name of the root, used when exporting .reg files (e.g. "HKEY_LOCAL_MACHINE").</summary>
    string RootName { get; }

    bool KeyExists(string keyPath);

    IReadOnlyList<string> GetSubKeyNames(string keyPath);

    IReadOnlyList<string> GetValueNames(string keyPath);

    RegistryValueData? GetValue(string keyPath, string valueName);

    /// <summary>Creates the key (and any missing parents) if needed and writes a REG_DWORD value.</summary>
    void SetDWord(string keyPath, string valueName, uint value);

    /// <summary>Deletes a value. Returns false if the key or value did not exist.</summary>
    bool DeleteValue(string keyPath, string valueName);
}
