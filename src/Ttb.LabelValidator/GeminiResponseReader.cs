using System.Text.Json;

namespace Ttb.LabelValidator;

public static class GeminiResponseReader
{
    static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "observed", "not_found", "unreadable", "conflicting"
    };

    public static LabelExtraction Read(string path)
    {
        JsonDocument envelope;
        try { envelope = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (Exception exception) when (exception is IOException or JsonException)
        { throw new ProviderResponseException("Gemini response is not readable JSON: " + exception.Message); }

        using (envelope)
        using (JsonDocument structured = ExtractStructured(envelope.RootElement))
            return ParseStructured(structured.RootElement);
    }

    public static LabelExtraction ReadEmbedded(JsonElement extraction)
    {
        if (!extraction.TryGetProperty("fields", out _) || !extraction.TryGetProperty("warningFormatting", out _))
            throw new ProviderResponseException("Embedded label extraction is incomplete.");
        return ParseStructured(extraction);
    }

    static JsonDocument ExtractStructured(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("fields", out _))
            return JsonDocument.Parse(root.GetRawText());
        if (root.ValueKind != JsonValueKind.Object)
            throw new ProviderResponseException("Gemini response root must be an object.");
        if (root.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.String && status.GetString() != "completed")
            throw new ProviderResponseException("Gemini response did not complete successfully.");
        if (!root.TryGetProperty("steps", out JsonElement steps) || steps.ValueKind != JsonValueKind.Array)
            throw new ProviderResponseException("Gemini response has no steps array.");
        foreach (JsonElement step in steps.EnumerateArray())
        {
            if (ReadString(step, "type") != "model_output" || !step.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (JsonElement block in content.EnumerateArray())
                if (ReadString(block, "type") == "text" && block.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
                    try { return JsonDocument.Parse(text.GetString()!); }
                    catch (JsonException exception) { throw new ProviderResponseException("Gemini model output is not valid JSON: " + exception.Message); }
        }
        throw new ProviderResponseException("Gemini response has no structured model output.");
    }

    static LabelExtraction ParseStructured(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("fields", out JsonElement fieldsElement) || fieldsElement.ValueKind != JsonValueKind.Object)
            throw new ProviderResponseException("Gemini structured output has no fields object.");
        var fields = new Dictionary<string, LabelField>(StringComparer.Ordinal);
        foreach (JsonProperty property in fieldsElement.EnumerateObject())
            fields[property.Name] = ParseField(property.Name, property.Value);
        if (!root.TryGetProperty("warningFormatting", out JsonElement warning) || warning.ValueKind != JsonValueKind.Object)
            throw new ProviderResponseException("Gemini structured output has no warningFormatting object.");
        string relativeSize = ReadNullableString(warning, "relativeSize") ?? "undetermined";
        if (relativeSize is not ("smaller" or "similar" or "larger" or "undetermined"))
            throw new ProviderResponseException("warningFormatting.relativeSize is invalid.");
        return new LabelExtraction(fields, new WarningFormatting(
            ReadNullableString(warning, "headingText"),
            ReadNullableBoolean(warning, "headingAllCaps"),
            ReadNullableBoolean(warning, "headingBold"),
            relativeSize,
            ReadEvidence(warning),
            ReadNullableString(warning, "notes") ?? ""));
    }

    static LabelField ParseField(string name, JsonElement field)
    {
        if (field.ValueKind != JsonValueKind.Object) throw new ProviderResponseException($"fields.{name} must be an object.");
        string status = ReadNullableString(field, "status") ?? throw new ProviderResponseException($"fields.{name}.status is required.");
        if (!AllowedStatuses.Contains(status)) throw new ProviderResponseException($"fields.{name}.status is invalid.");
        string? raw = ReadNullableString(field, "raw");
        if (status == "observed" && string.IsNullOrWhiteSpace(raw))
            throw new ProviderResponseException($"fields.{name}.raw is required when status is observed.");
        return new LabelField(raw, status, ReadEvidence(field), ReadNullableString(field, "notes") ?? "");
    }

    static List<Evidence> ReadEvidence(JsonElement parent)
    {
        var result = new List<Evidence>();
        if (parent.ValueKind != JsonValueKind.Object)
            throw new ProviderResponseException("Evidence parent must be an object.");
        if (!parent.TryGetProperty("evidence", out JsonElement evidence) || evidence.ValueKind != JsonValueKind.Array)
            throw new ProviderResponseException("evidence must be an array.");
        foreach (JsonElement item in evidence.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new ProviderResponseException("Every evidence item must be an object.");
            string? imageId = ReadNullableString(item, "imageId"), text = ReadNullableString(item, "text");
            if (string.IsNullOrWhiteSpace(imageId) || string.IsNullOrWhiteSpace(text))
                throw new ProviderResponseException("Every evidence item needs nonempty imageId and text values.");
            result.Add(new Evidence(imageId, text));
        }
        return result;
    }

    static string? ReadNullableString(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object)
            throw new ProviderResponseException($"The parent of {name} must be an object.");
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ProviderResponseException($"{name} must be a string or null.");
        return value.GetString();
    }

    static string? ReadString(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static bool? ReadNullableBoolean(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object)
            throw new ProviderResponseException($"The parent of {name} must be an object.");
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ProviderResponseException($"{name} must be true, false, or null.")
        };
    }
}

public sealed class ProviderResponseException(string message) : Exception(message);
