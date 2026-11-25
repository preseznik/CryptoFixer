using System.Globalization;
using System.Text.Json;
using CryptoFixer.Core.Schannel;

namespace CryptoFixer.Core.Presets;

/// <param name="Error">Set when the file exists but couldn't be read or isn't valid JSON; <see cref="Presets"/> is empty then.</param>
/// <param name="Warnings">Entries that were skipped while reading an otherwise valid file.</param>
public sealed record PresetFileLoadResult(IReadOnlyList<ProtocolPreset> Presets, IReadOnlyList<string> Warnings, string? Error, bool FileExists)
{
    public static PresetFileLoadResult Missing { get; } = new([], [], null, false);
}

/// <summary>
/// Reads and writes the portable presets file (<see cref="FileName"/>), a human-editable JSON file that lives next to the executable:
/// <code>
/// {
///   "formatVersion": 2,
///   "presets": [
///     {
///       "name": "Web servers",
///       "description": "optional",
///       "protocols": {
///         "TLS 1.0": "disabled",                                  // both sides
///         "TLS 1.3": { "server": "enabled", "client": "osDefault" }
///       },
///       "ciphers": { "Triple DES 168": "disabled", "AES 256/256": "enabled" },
///       "hashes": { "MD5": "disabled" },
///       "keyExchange": { "Diffie-Hellman": "osDefault" }
///     }
///   ]
/// }
/// </code>
/// Values are "enabled", "disabled" or "osDefault" (remove the registry values). Protocols and algorithms that are left out are
/// not changed. Version 1 files (protocols only) are read unchanged. Comments and trailing commas are accepted when reading;
/// they are not preserved when the app saves the file.
/// </summary>
public static class PresetFile
{
    public const string FileName = "CryptoFixer.presets.json";

    /// <summary>The newest format this app reads and writes. Files without algorithm sections are still written as version 1.</summary>
    public const int FormatVersion = 2;
    public const int MaxNameLength = 80;

    public const string EnabledToken = "enabled";
    public const string DisabledToken = "disabled";
    public const string OsDefaultToken = "osDefault";

    private const string HelpText =
        "CryptoFixer presets. Values: \"enabled\", \"disabled\" or \"osDefault\" (removes the registry values). " +
        "Use one value for both sides or { \"server\": ..., \"client\": ... }. The optional \"ciphers\", \"hashes\" and " +
        "\"keyExchange\" sections use SCHANNEL key names, e.g. \"Triple DES 168\". Entries that are left out are not changed.";

    /// <summary>JSON section name for each algorithm category.</summary>
    public static string SectionName(AlgorithmCategory category) => category switch
    {
        AlgorithmCategory.Cipher => "ciphers",
        AlgorithmCategory.Hash => "hashes",
        AlgorithmCategory.KeyExchange => "keyExchange",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    private static readonly AlgorithmCategory[] Categories = [AlgorithmCategory.Cipher, AlgorithmCategory.Hash, AlgorithmCategory.KeyExchange];

    public static string DefaultPath(string baseDirectory) => Path.Combine(baseDirectory, FileName);

    public static PresetFileLoadResult Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            return PresetFileLoadResult.Missing;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PresetFileLoadResult([], [], $"Couldn't read {Path.GetFileName(path)}: {ex.Message}", true);
        }

        return Parse(text);
    }

    public static PresetFileLoadResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (string.IsNullOrWhiteSpace(json))
        {
            return new PresetFileLoadResult([], [], null, true);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, PortableJsonFile.ReadOptions);
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (line {line + 1})" : string.Empty;
            return new PresetFileLoadResult([], [], $"The presets file isn't valid JSON{where}.", true);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new PresetFileLoadResult([], [], "The presets file must contain a JSON object with a \"presets\" list.", true);
            }

            var warnings = new List<string>();
            if (Property(root, "formatVersion") is { ValueKind: JsonValueKind.Number } version
                && version.TryGetInt32(out var v) && v > FormatVersion)
            {
                warnings.Add($"The presets file uses format version {v}; this app understands version {FormatVersion}. Unknown settings are ignored.");
            }

            var list = Property(root, "presets");
            if (list is null || list.Value.ValueKind == JsonValueKind.Null)
            {
                return new PresetFileLoadResult([], warnings, null, true);
            }

            if (list.Value.ValueKind != JsonValueKind.Array)
            {
                return new PresetFileLoadResult([], warnings, "\"presets\" in the presets file must be a list.", true);
            }

            var presets = new List<ProtocolPreset>();
            var index = 0;
            foreach (var item in list.Value.EnumerateArray())
            {
                index++;
                var preset = ReadPreset(item, index, warnings);
                if (preset is null)
                {
                    continue;
                }

                if (BuiltInPresets.IsReservedName(preset.Name))
                {
                    warnings.Add($"Preset \"{preset.Name}\" skipped: the name is used by a built-in preset.");
                }
                else if (presets.Any(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    warnings.Add($"Preset \"{preset.Name}\" skipped: another preset has the same name.");
                }
                else
                {
                    presets.Add(preset);
                }
            }

            return new PresetFileLoadResult(presets, warnings, null, true);
        }
    }

    /// <summary>Serializes user presets (built-in presets are never written).</summary>
    public static string Serialize(IEnumerable<ProtocolPreset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);

        return PortableJsonFile.Write(writer =>
        {
            var userPresets = presets.Where(p => !p.IsBuiltIn).ToList();
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", userPresets.Any(p => p.Algorithms.Count > 0) ? FormatVersion : 1);
            writer.WriteString("help", HelpText);
            writer.WriteStartArray("presets");
            foreach (var preset in userPresets)
            {
                WritePreset(writer, preset);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    /// <summary>Writes the file via a temporary file so an interrupted save can't leave a half-written file behind.</summary>
    public static void Save(string path, IEnumerable<ProtocolPreset> presets)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        PortableJsonFile.WriteAtomically(path, Serialize(presets));
    }

    /// <summary>Renames an unreadable presets file so a new one can be written. Returns the new path.</summary>
    public static string MoveAside(string path, DateTime timestamp)
    {
        var target = $"{path}.invalid-{timestamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        File.Move(path, target, overwrite: true);
        return target;
    }

    /// <summary>Returns an error message for an unusable preset name, or null if the name is fine.</summary>
    public static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a name for the preset.";
        }

        if (name.Trim().Length > MaxNameLength)
        {
            return $"Use at most {MaxNameLength} characters.";
        }

        return BuiltInPresets.IsReservedName(name) ? "That name is used by a built-in preset." : null;
    }

    public static string ToToken(ConfiguredSetting setting) => setting switch
    {
        ConfiguredSetting.Enabled => EnabledToken,
        ConfiguredSetting.Disabled => DisabledToken,
        ConfiguredSetting.NotConfigured => OsDefaultToken,
        _ => throw new ArgumentOutOfRangeException(nameof(setting), setting, "Custom settings can't be stored in a preset."),
    };

    public static ConfiguredSetting? FromToken(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "enabled" or "enable" or "on" => ConfiguredSetting.Enabled,
        "disabled" or "disable" or "off" => ConfiguredSetting.Disabled,
        "osdefault" or "default" or "notconfigured" => ConfiguredSetting.NotConfigured,
        _ => null,
    };

    private static ProtocolPreset? ReadPreset(JsonElement item, int index, List<string> warnings)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            warnings.Add($"Preset #{index} skipped: it isn't a JSON object.");
            return null;
        }

        var name = Property(item, "name") is { ValueKind: JsonValueKind.String } n ? n.GetString()?.Trim() : null;
        if (string.IsNullOrEmpty(name))
        {
            warnings.Add($"Preset #{index} skipped: it has no \"name\".");
            return null;
        }

        if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength];
        }

        var entries = new List<PresetEntry>();
        var protocols = Property(item, "protocols");
        if (protocols is { ValueKind: JsonValueKind.Object } map)
        {
            foreach (var property in map.EnumerateObject())
            {
                ReadProtocol(name, property, entries, warnings);
            }
        }
        else if (protocols is not null && protocols.Value.ValueKind != JsonValueKind.Null)
        {
            warnings.Add($"Preset \"{name}\": \"protocols\" must be an object; no settings were read.");
        }

        var algorithms = new List<AlgorithmPresetEntry>();
        foreach (var category in Categories)
        {
            var section = SectionName(category);
            var value = Property(item, section);
            if (value is { ValueKind: JsonValueKind.Object } algorithmMap)
            {
                foreach (var property in algorithmMap.EnumerateObject())
                {
                    var algorithm = AlgorithmCatalog.Find(category, property.Name.Trim());
                    if (algorithm is null)
                    {
                        warnings.Add($"Preset \"{name}\": unknown {AlgorithmCatalog.Noun(category)} \"{property.Name}\" skipped.");
                    }
                    else if (ReadSetting(name, algorithm.KeyName, property.Value, warnings) is { } setting)
                    {
                        algorithms.Add(new AlgorithmPresetEntry(algorithm, setting));
                    }
                }
            }
            else if (value is not null && value.Value.ValueKind != JsonValueKind.Null)
            {
                warnings.Add($"Preset \"{name}\": \"{section}\" must be an object; it was ignored.");
            }
        }

        if (entries.Count == 0 && algorithms.Count == 0)
        {
            warnings.Add($"Preset \"{name}\" has no protocol or algorithm settings; loading it changes nothing.");
        }

        DateTimeOffset? savedAt = null;
        if (Property(item, "savedAt") is { ValueKind: JsonValueKind.String } s
            && DateTimeOffset.TryParse(s.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            savedAt = parsed;
        }

        return new ProtocolPreset(name, ProtocolPreset.Normalize(entries))
        {
            Description = OptionalString(item, "description"),
            SavedAt = savedAt,
            SavedFrom = OptionalString(item, "savedFrom"),
            Algorithms = ProtocolPreset.Normalize(algorithms),
        };
    }

    private static void ReadProtocol(string presetName, JsonProperty property, List<PresetEntry> entries, List<string> warnings)
    {
        var protocol = ProtocolCatalog.Find(property.Name.Trim());
        if (protocol is null)
        {
            warnings.Add($"Preset \"{presetName}\": unknown protocol \"{property.Name}\" skipped.");
            return;
        }

        switch (property.Value.ValueKind)
        {
            case JsonValueKind.String:
                if (ReadSetting(presetName, protocol.KeyName, property.Value, warnings) is { } both)
                {
                    entries.Add(new PresetEntry(protocol, ProtocolRole.Server, both));
                    entries.Add(new PresetEntry(protocol, ProtocolRole.Client, both));
                }

                break;

            case JsonValueKind.Object:
                foreach (var side in property.Value.EnumerateObject())
                {
                    ProtocolRole? role = side.Name.Trim().ToLowerInvariant() switch
                    {
                        "server" => ProtocolRole.Server,
                        "client" => ProtocolRole.Client,
                        _ => null,
                    };

                    if (role is null)
                    {
                        warnings.Add($"Preset \"{presetName}\": {protocol.KeyName} has unknown side \"{side.Name}\" (use \"server\" or \"client\").");
                    }
                    else if (ReadSetting(presetName, $"{protocol.KeyName} {role}", side.Value, warnings) is { } setting)
                    {
                        entries.Add(new PresetEntry(protocol, role.Value, setting));
                    }
                }

                break;

            default:
                warnings.Add($"Preset \"{presetName}\": {protocol.KeyName} must be a value or {{ \"server\": ..., \"client\": ... }}.");
                break;
        }
    }

    private static ConfiguredSetting? ReadSetting(string presetName, string what, JsonElement value, List<string> warnings)
    {
        var setting = value.ValueKind == JsonValueKind.String ? FromToken(value.GetString()) : null;
        if (setting is null)
        {
            warnings.Add($"Preset \"{presetName}\": {what} has invalid value {value.GetRawText()} " +
                         $"(use \"{EnabledToken}\", \"{DisabledToken}\" or \"{OsDefaultToken}\").");
        }

        return setting;
    }

    private static void WritePreset(Utf8JsonWriter writer, ProtocolPreset preset)
    {
        writer.WriteStartObject();
        writer.WriteString("name", preset.Name);
        if (!string.IsNullOrWhiteSpace(preset.Description))
        {
            writer.WriteString("description", preset.Description);
        }

        if (preset.SavedAt is { } savedAt)
        {
            writer.WriteString("savedAt", savedAt.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(preset.SavedFrom))
        {
            writer.WriteString("savedFrom", preset.SavedFrom);
        }

        writer.WriteStartObject("protocols");
        foreach (var group in ProtocolPreset.Normalize(preset.Entries.Where(e => e.Setting != ConfiguredSetting.Custom)).GroupBy(e => e.Protocol))
        {
            var server = group.FirstOrDefault(e => e.Role == ProtocolRole.Server);
            var client = group.FirstOrDefault(e => e.Role == ProtocolRole.Client);
            if (server is not null && client is not null && server.Setting == client.Setting)
            {
                writer.WriteString(group.Key.KeyName, ToToken(server.Setting));
                continue;
            }

            writer.WriteStartObject(group.Key.KeyName);
            foreach (var entry in group)
            {
                writer.WriteString(entry.Role == ProtocolRole.Server ? "server" : "client", ToToken(entry.Setting));
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();

        var algorithms = ProtocolPreset.Normalize(preset.Algorithms);
        foreach (var category in Categories)
        {
            var inCategory = algorithms.Where(a => a.Algorithm.Category == category).ToList();
            if (inCategory.Count == 0)
            {
                continue;
            }

            writer.WriteStartObject(SectionName(category));
            foreach (var entry in inCategory)
            {
                writer.WriteString(entry.Algorithm.KeyName, ToToken(entry.Setting));
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static string? OptionalString(JsonElement item, string name) =>
        Property(item, name) is { ValueKind: JsonValueKind.String } value && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static JsonElement? Property(JsonElement element, string name) => PortableJsonFile.Property(element, name);
}
