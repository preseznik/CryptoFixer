using System.Text.Json;

namespace CryptoFixer.Core.Settings;

/// <summary>How the protocol table is laid out.</summary>
public enum TableDisplayMode
{
    /// <summary>The full table: descriptions, setting, effective state and source on separate lines.</summary>
    Standard,

    /// <summary>The same table with one tight line per protocol.</summary>
    Compact,

    /// <summary>One list item per protocol with the server and client settings inline.</summary>
    List,
}

public sealed record AppSettings
{
    public static AppSettings Default { get; } = new();

    public TableDisplayMode DisplayMode { get; init; } = TableDisplayMode.Standard;

    /// <summary>Look for a newer GitHub release in the background when the app starts.</summary>
    public bool CheckForUpdates { get; init; } = true;
}

/// <param name="Error">Set when the file exists but couldn't be read or isn't valid JSON; <see cref="Settings"/> holds the defaults then.</param>
/// <param name="Warnings">Values that were ignored while reading an otherwise valid file.</param>
public sealed record AppSettingsLoadResult(AppSettings Settings, IReadOnlyList<string> Warnings, string? Error, bool FileExists)
{
    public static AppSettingsLoadResult Missing { get; } = new(AppSettings.Default, [], null, false);
}

/// <summary>
/// Reads and writes the portable settings file (<see cref="FileName"/>) next to the executable:
/// <code>
/// { "formatVersion": 1, "displayMode": "compact", "checkForUpdates": true }
/// </code>
/// Missing, unknown or invalid values fall back to the defaults, so a damaged file never stops the app from starting.
/// </summary>
public static class AppSettingsFile
{
    public const string FileName = "CryptoFixer.settings.json";
    public const int FormatVersion = 1;

    private const string HelpText =
        "CryptoFixer settings. displayMode: \"standard\", \"compact\" or \"list\". checkForUpdates: true or false (look for a new release at startup).";

    public static string DefaultPath(string baseDirectory) => Path.Combine(baseDirectory, FileName);

    public static AppSettingsLoadResult Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (!File.Exists(path))
        {
            return AppSettingsLoadResult.Missing;
        }

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AppSettingsLoadResult(AppSettings.Default, [], $"Couldn't read {Path.GetFileName(path)}: {ex.Message}", true);
        }
    }

    public static AppSettingsLoadResult Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (string.IsNullOrWhiteSpace(json))
        {
            return new AppSettingsLoadResult(AppSettings.Default, [], null, true);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, PortableJsonFile.ReadOptions);
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (line {line + 1})" : string.Empty;
            return new AppSettingsLoadResult(AppSettings.Default, [], $"The settings file isn't valid JSON{where}.", true);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new AppSettingsLoadResult(AppSettings.Default, [], "The settings file must contain a JSON object.", true);
            }

            var warnings = new List<string>();
            var settings = AppSettings.Default;
            if (PortableJsonFile.Property(document.RootElement, "displayMode") is { } value)
            {
                if (value.ValueKind == JsonValueKind.String && FromToken(value.GetString()) is { } mode)
                {
                    settings = settings with { DisplayMode = mode };
                }
                else
                {
                    warnings.Add($"displayMode {value.GetRawText()} isn't valid (use \"standard\", \"compact\" or \"list\"); using \"standard\".");
                }
            }

            if (PortableJsonFile.Property(document.RootElement, "checkForUpdates") is { } check)
            {
                if (check.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    settings = settings with { CheckForUpdates = check.GetBoolean() };
                }
                else
                {
                    warnings.Add($"checkForUpdates {check.GetRawText()} isn't valid (use true or false); using true.");
                }
            }

            return new AppSettingsLoadResult(settings, warnings, null, true);
        }
    }

    public static string Serialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return PortableJsonFile.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", FormatVersion);
            writer.WriteString("help", HelpText);
            writer.WriteString("displayMode", ToToken(settings.DisplayMode));
            writer.WriteBoolean("checkForUpdates", settings.CheckForUpdates);
            writer.WriteEndObject();
        });
    }

    public static void Save(string path, AppSettings settings)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        PortableJsonFile.WriteAtomically(path, Serialize(settings));
    }

    public static string ToToken(TableDisplayMode mode) => mode switch
    {
        TableDisplayMode.Compact => "compact",
        TableDisplayMode.List => "list",
        _ => "standard",
    };

    public static TableDisplayMode? FromToken(string? token) => token?.Trim().ToLowerInvariant() switch
    {
        "standard" or "current" or "table" or "default" => TableDisplayMode.Standard,
        "compact" => TableDisplayMode.Compact,
        "list" or "cards" => TableDisplayMode.List,
        _ => null,
    };
}
