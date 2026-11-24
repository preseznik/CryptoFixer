using System.Globalization;
using System.Text;
using CryptoFixer.Core.Registry;
using CryptoFixer.Core.Schannel;
using Microsoft.Win32;

namespace CryptoFixer.Core.Backup;

/// <summary>Creates regedit-compatible (version 5.00) backups of the Schannel protocol settings.</summary>
public static class RegFileExporter
{
    /// <summary>
    /// Exports the whole SCHANNEL\Protocols tree. For every protocol role managed by this tool whose
    /// Enabled/DisabledByDefault value is absent, a deletion line ("Name"=-) is added so that importing the
    /// backup also removes values created later and restores the exact original state.
    /// </summary>
    /// <param name="isRemote">The registry belongs to another computer; the restore note then warns against importing the file locally.</param>
    public static string ExportProtocols(IRegistryAccess registry, DateTimeOffset timestamp, string? machineName = null, bool isRemote = false)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var sb = new StringBuilder();
        sb.Append("Windows Registry Editor Version 5.00\r\n\r\n");
        sb.Append("; CryptoFixer backup of Schannel protocol settings\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"; Created {timestamp:yyyy-MM-dd HH:mm:ss zzz}");
        if (!string.IsNullOrEmpty(machineName))
        {
            sb.Append(CultureInfo.InvariantCulture, $" on {(isRemote ? "remote computer " : string.Empty)}{machineName}");
        }

        sb.Append(isRemote
            ? "\r\n; Restore ON THAT COMPUTER: copy this file there, run 'reg import <this file>' as Administrator, then restart it." +
              "\r\n; Importing it on any other computer changes that computer instead.\r\n\r\n"
            : "\r\n; Restore: run 'reg import <this file>' as Administrator (or double-click it), then restart Windows.\r\n\r\n");

        var managed = ProtocolCatalog.All
            .SelectMany(p => new[] { SchannelPaths.RoleKey(p, ProtocolRole.Server), SchannelPaths.RoleKey(p, ProtocolRole.Client) })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (registry.KeyExists(SchannelPaths.Protocols))
        {
            WriteKeyRecursive(registry, SchannelPaths.Protocols, sb, managed, emitted);
        }
        else
        {
            WriteKeyHeader(registry, SchannelPaths.Protocols, sb);
            sb.Append("\r\n");
        }

        foreach (var key in managed.Where(k => !emitted.Contains(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            WriteKeyHeader(registry, key, sb);
            AppendMissingManagedValueDeletions(registry, key, sb);
            sb.Append("\r\n");
        }

        return sb.ToString();
    }

    /// <summary>Writes the export as UTF-16 LE with BOM, the encoding regedit uses.</summary>
    public static void WriteFile(string path, string content) =>
        File.WriteAllText(path, content, Encoding.Unicode);

    private static void WriteKeyRecursive(IRegistryAccess registry, string keyPath, StringBuilder sb, HashSet<string> managed, HashSet<string> emitted)
    {
        WriteKeyHeader(registry, keyPath, sb);

        foreach (var name in registry.GetValueNames(keyPath).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var value = registry.GetValue(keyPath, name);
            if (value is not null)
            {
                sb.Append(FormatValue(name, value)).Append("\r\n");
            }
        }

        if (managed.Contains(keyPath))
        {
            AppendMissingManagedValueDeletions(registry, keyPath, sb);
            emitted.Add(keyPath);
        }

        sb.Append("\r\n");

        foreach (var sub in registry.GetSubKeyNames(keyPath).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            WriteKeyRecursive(registry, $"{keyPath}\\{sub}", sb, managed, emitted);
        }
    }

    private static void WriteKeyHeader(IRegistryAccess registry, string keyPath, StringBuilder sb) =>
        sb.Append('[').Append(registry.RootName).Append('\\').Append(keyPath).Append("]\r\n");

    private static void AppendMissingManagedValueDeletions(IRegistryAccess registry, string keyPath, StringBuilder sb)
    {
        foreach (var name in new[] { SchannelPaths.EnabledValue, SchannelPaths.DisabledByDefaultValue })
        {
            if (registry.GetValue(keyPath, name) is null)
            {
                sb.Append('"').Append(Escape(name)).Append("\"=-\r\n");
            }
        }
    }

    internal static string FormatValue(string name, RegistryValueData value)
    {
        var lhs = name.Length == 0 ? "@" : $"\"{Escape(name)}\"";
        var rhs = (value.Kind, value.Data) switch
        {
            (RegistryValueKind.DWord, int i) => $"dword:{unchecked((uint)i):x8}",
            (RegistryValueKind.String, string s) => $"\"{Escape(s)}\"",
            (RegistryValueKind.QWord, long l) => "hex(b):" + Hex(BitConverter.GetBytes(l)),
            (RegistryValueKind.ExpandString, string s) => "hex(2):" + Hex(Encoding.Unicode.GetBytes(s + "\0")),
            (RegistryValueKind.MultiString, string[] parts) =>
                "hex(7):" + Hex(Encoding.Unicode.GetBytes(string.Concat(parts.Select(p => p + "\0")) + "\0")),
            (RegistryValueKind.Binary, byte[] bytes) => "hex:" + Hex(bytes),
            (RegistryValueKind.None, byte[] bytes) => "hex(0):" + Hex(bytes),
            _ => throw new NotSupportedException($"Cannot export registry value '{name}' of kind {value.Kind}."),
        };
        return $"{lhs}={rhs}";
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string Hex(byte[] bytes) => string.Join(",", bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
}
