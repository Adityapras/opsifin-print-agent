using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Hub;

internal sealed class PrintSettingsJsonConverter : JsonConverter<PrintSettings>
{
    private const string DefaultEngine = "pdfium";
    private const int DefaultCopies = 1;
    private const string DefaultScale = "noscale";

    public override bool HandleNull => true;

    public override PrintSettings Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return Defaults("print settings were null; safe defaults were used");
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return Defaults("print settings were not an object; safe defaults were used");
        }

        var engine = DefaultEngine;
        var copies = DefaultCopies;
        var duplex = false;
        string? tray = null;
        var scale = DefaultScale;
        var warnings = new List<string>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                reader.Skip();
                continue;
            }

            var propertyName = reader.GetString();
            if (!reader.Read())
            {
                throw new JsonException("The print settings object ended before a property value was read.");
            }

            switch (propertyName?.ToLowerInvariant())
            {
                case "engine":
                    engine = ReadEngine(ref reader, warnings);
                    break;
                case "copies":
                    copies = ReadCopies(ref reader, warnings);
                    break;
                case "duplex":
                    duplex = ReadDuplex(ref reader, warnings);
                    break;
                case "tray":
                    tray = ReadTray(ref reader, warnings);
                    break;
                case "scale":
                    scale = ReadScale(ref reader, warnings);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new PrintSettings
        {
            Engine = engine,
            Copies = copies,
            Duplex = duplex,
            Tray = tray,
            Scale = scale,
            NormalizationWarnings = warnings,
        };
    }

    public override void Write(Utf8JsonWriter writer, PrintSettings value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("engine", value.Engine);
        writer.WriteNumber("copies", value.Copies);
        writer.WriteBoolean("duplex", value.Duplex);
        if (value.Tray is null)
        {
            writer.WriteNull("tray");
        }
        else
        {
            writer.WriteString("tray", value.Tray);
        }

        writer.WriteString("scale", value.Scale);
        writer.WriteEndObject();
    }

    private static PrintSettings Defaults(string warning) => new()
    {
        NormalizationWarnings = [warning],
    };

    private static string ReadEngine(ref Utf8JsonReader reader, List<string> warnings)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (value is not null
            && (value.Equals("pdfium", StringComparison.OrdinalIgnoreCase)
                || value.Equals("sumatra", StringComparison.OrdinalIgnoreCase)))
        {
            return value.ToLowerInvariant();
        }

        reader.Skip();
        warnings.Add("engine was invalid; defaulted to pdfium");
        return DefaultEngine;
    }

    private static int ReadCopies(ref Utf8JsonReader reader, List<string> warnings)
    {
        if (reader.TokenType == JsonTokenType.Number
            && reader.TryGetInt32(out var value)
            && value is >= 1 and <= 99)
        {
            return value;
        }

        reader.Skip();
        warnings.Add("copies was invalid; defaulted to 1");
        return DefaultCopies;
    }

    private static bool ReadDuplex(ref Utf8JsonReader reader, List<string> warnings)
    {
        if (reader.TokenType is JsonTokenType.True or JsonTokenType.False)
        {
            return reader.GetBoolean();
        }

        reader.Skip();
        warnings.Add("duplex was invalid; defaulted to false");
        return false;
    }

    private static string? ReadTray(ref Utf8JsonReader reader, List<string> warnings)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        reader.Skip();
        warnings.Add("tray was invalid; the printer default will be used");
        return null;
    }

    private static string ReadScale(ref Utf8JsonReader reader, List<string> warnings)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        if (value is not null
            && (value.Equals("noscale", StringComparison.OrdinalIgnoreCase)
                || value.Equals("fit", StringComparison.OrdinalIgnoreCase)))
        {
            return value.ToLowerInvariant();
        }

        reader.Skip();
        warnings.Add("scale was invalid; defaulted to noscale");
        return DefaultScale;
    }
}
