using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CryptoFixer.Core;

/// <summary>Shared helpers for the human-editable JSON files that live next to the executable.</summary>
internal static class PortableJsonFile
{
    public static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static readonly JsonWriterOptions WriteOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriteOptions))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + Environment.NewLine;
    }

    /// <summary>Writes via a temporary file so an interrupted save can't leave a half-written file behind.</summary>
    public static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    /// <summary>Case-insensitive property lookup; the last duplicate wins.</summary>
    public static JsonElement? Property(JsonElement element, string name)
    {
        JsonElement? found = null;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                found = property.Value;
            }
        }

        return found;
    }
}
