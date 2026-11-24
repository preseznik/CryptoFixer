using System.Text.Json;

namespace CryptoFixer.Core.Remote;

/// <param name="Error">Set when the file exists but couldn't be read or isn't valid JSON; <see cref="Machines"/> is empty then.</param>
/// <param name="Warnings">Problems found in an otherwise valid file (skipped entries, ignored values).</param>
/// <param name="SkippedEntries">Entries that couldn't be used and would be lost if the list were saved as is.</param>
public sealed record MachineListLoadResult(IReadOnlyList<MachineEntry> Machines, IReadOnlyList<string> Warnings, string? Error, bool FileExists, int SkippedEntries = 0)
{
    public static MachineListLoadResult Missing { get; } = new([], [], null, false);

    /// <summary>True when saving would drop content the user may want to recover from the current file.</summary>
    public bool KeepCopyBeforeSaving => Error is not null || SkippedEntries > 0;
}

/// <summary>
/// Reads and writes the portable machine list (<see cref="FileName"/>) next to the executable:
/// <code>
/// {
///   "formatVersion": 1,
///   "machines": [
///     { "name": "web01", "host": "10.0.0.5", "protocol": "wsman", "credential": "current" },
///     { "name": "db01", "host": "db01.contoso.com", "protocol": "wsman-https",
///       "credential": { "userName": "CONTOSO\\admin", "protectedPassword": "AQAAANCMnd8B..." } }
///   ]
/// }
/// </code>
/// Passwords are only stored DPAPI-encrypted (<see cref="DpapiSecretProtector"/>), never in plain text.
/// </summary>
public static class MachineListFile
{
    public const string FileName = "CryptoFixer.machines.json";
    public const int FormatVersion = 1;

    /// <summary>Display name of the always-present local entry; not stored in the file.</summary>
    public const string ThisComputerName = "This computer";

    private const string HelpText =
        "CryptoFixer remote machines. protocol: \"wsman\" (WinRM HTTP 5985), \"wsman-https\" (WinRM HTTPS 5986) or \"dcom\". " +
        "credential: \"current\" or { \"userName\": \"DOMAIN\\\\user\" } (asks for the password) plus an optional \"protectedPassword\" " +
        "that only the Windows user who saved it can decrypt, on the same computer.";

    public static string DefaultPath(string baseDirectory) => Path.Combine(baseDirectory, FileName);

    public static MachineListLoadResult Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            return MachineListLoadResult.Missing;
        }

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MachineListLoadResult([], [], $"Couldn't read {Path.GetFileName(path)}: {ex.Message}", true);
        }
    }

    public static MachineListLoadResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (string.IsNullOrWhiteSpace(json))
        {
            return new MachineListLoadResult([], [], null, true);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, PortableJsonFile.ReadOptions);
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (line {line + 1})" : string.Empty;
            return new MachineListLoadResult([], [], $"The machine list isn't valid JSON{where}.", true);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new MachineListLoadResult([], [], "The machine list must contain a JSON object.", true);
            }

            var machinesElement = PortableJsonFile.Property(root, "machines");
            if (machinesElement is null || machinesElement.Value.ValueKind == JsonValueKind.Null)
            {
                return new MachineListLoadResult([], [], null, true);
            }

            if (machinesElement.Value.ValueKind != JsonValueKind.Array)
            {
                return new MachineListLoadResult([], [], "\"machines\" must be a JSON array.", true);
            }

            var machines = new List<MachineEntry>();
            var warnings = new List<string>();
            var skipped = 0;
            var index = 0;
            foreach (var element in machinesElement.Value.EnumerateArray())
            {
                index++;
                var entry = ParseEntry(element, index, warnings);
                if (entry is null)
                {
                    skipped++;
                    continue;
                }

                if (machines.Any(m => string.Equals(m.Name, entry.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    warnings.Add($"Machine #{index} (\"{entry.Name}\") was skipped: the name is used more than once.");
                    skipped++;
                    continue;
                }

                machines.Add(entry);
            }

            return new MachineListLoadResult(machines, warnings, null, true, skipped);
        }
    }

    private static MachineEntry? ParseEntry(JsonElement element, int index, List<string> warnings)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            warnings.Add($"Machine #{index} was skipped: it must be a JSON object.");
            return null;
        }

        var name = GetString(element, "name")?.Trim();
        var host = GetString(element, "host")?.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(host))
        {
            warnings.Add($"Machine #{index} was skipped: it needs a \"name\" and a \"host\".");
            return null;
        }

        if (string.Equals(name, ThisComputerName, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Machine #{index} was skipped: \"{ThisComputerName}\" is reserved for the local computer.");
            return null;
        }

        if (!HostAddress.IsValid(host))
        {
            warnings.Add($"Machine \"{name}\": host \"{host}\" doesn't look like a host name or IP address.");
        }

        var protocol = RemoteProtocol.WsMan;
        if (PortableJsonFile.Property(element, "protocol") is { } protocolElement && protocolElement.ValueKind != JsonValueKind.Null)
        {
            if (protocolElement.ValueKind == JsonValueKind.String && ProtocolFromToken(protocolElement.GetString()) is { } parsed)
            {
                protocol = parsed;
            }
            else
            {
                warnings.Add($"Machine \"{name}\": protocol {protocolElement.GetRawText()} isn't valid (use \"wsman\", \"wsman-https\" or \"dcom\"); using \"wsman\".");
            }
        }

        return new MachineEntry(name, host, protocol, ParseCredential(element, name, warnings));
    }

    private static MachineCredential ParseCredential(JsonElement element, string name, List<string> warnings)
    {
        var credential = PortableJsonFile.Property(element, "credential");
        if (credential is null || credential.Value.ValueKind == JsonValueKind.Null)
        {
            return MachineCredential.CurrentAccount;
        }

        var value = credential.Value;
        if (value.ValueKind == JsonValueKind.String)
        {
            if (string.Equals(value.GetString()?.Trim(), "current", StringComparison.OrdinalIgnoreCase))
            {
                return MachineCredential.CurrentAccount;
            }

            warnings.Add($"Machine \"{name}\": credential must be \"current\" or an object with \"userName\"; using the current Windows account.");
            return MachineCredential.CurrentAccount;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            warnings.Add($"Machine \"{name}\": credential must be \"current\" or an object with \"userName\"; using the current Windows account.");
            return MachineCredential.CurrentAccount;
        }

        var userName = GetString(value, "userName")?.Trim();
        if (string.IsNullOrEmpty(userName))
        {
            return MachineCredential.CurrentAccount;
        }

        byte[]? protectedPassword = null;
        if (GetString(value, "protectedPassword") is { Length: > 0 } encoded)
        {
            try
            {
                protectedPassword = Convert.FromBase64String(encoded);
            }
            catch (FormatException)
            {
                warnings.Add($"Machine \"{name}\": the saved password is damaged and will be asked for again.");
            }
        }

        if (PortableJsonFile.Property(value, "password") is not null)
        {
            warnings.Add($"Machine \"{name}\": plain-text \"password\" values are ignored; the password will be asked for and can be saved encrypted.");
        }

        return MachineCredential.ForUser(userName, protectedPassword);
    }

    private static string? GetString(JsonElement element, string name) =>
        PortableJsonFile.Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static string Serialize(IEnumerable<MachineEntry> machines)
    {
        ArgumentNullException.ThrowIfNull(machines);

        return PortableJsonFile.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteString("help", HelpText);
            writer.WriteStartArray("machines");
            foreach (var machine in machines)
            {
                writer.WriteStartObject();
                writer.WriteString("name", machine.Name);
                writer.WriteString("host", machine.Host);
                writer.WriteString("protocol", ToToken(machine.Protocol));
                if (machine.Credential.UsesCurrentAccount)
                {
                    writer.WriteString("credential", "current");
                }
                else
                {
                    writer.WriteStartObject("credential");
                    writer.WriteString("userName", machine.Credential.UserName!.Trim());
                    if (machine.Credential.HasSavedPassword)
                    {
                        writer.WriteString("protectedPassword", Convert.ToBase64String(machine.Credential.ProtectedPassword!));
                    }

                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    public static void Save(string path, IEnumerable<MachineEntry> machines)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        PortableJsonFile.WriteAtomically(path, Serialize(machines));
    }

    public static string ToToken(RemoteProtocol protocol) => protocol switch
    {
        RemoteProtocol.WsManHttps => "wsman-https",
        RemoteProtocol.Dcom => "dcom",
        _ => "wsman",
    };

    public static RemoteProtocol? ProtocolFromToken(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "wsman" or "winrm" or "wsman-http" or "http" => RemoteProtocol.WsMan,
        "wsman-https" or "winrm-https" or "https" => RemoteProtocol.WsManHttps,
        "dcom" or "rpc" => RemoteProtocol.Dcom,
        _ => null,
    };
}
