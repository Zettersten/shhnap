using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shnapp.Core;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ShnappDocument))]
[JsonSerializable(typeof(SettingsFile))]
internal sealed partial class ShnappJsonContext : JsonSerializerContext;

internal sealed record SettingsFile(int SchemaVersion, ShnappSettings Settings);

internal static class JsonSchema
{
    internal static void ValidateDocument(JsonElement root)
    {
        RequireObject(root, "schemaVersion", "id", "title", "createdAt", "captureKind",
            "pixelWidth", "pixelHeight", "crop", "hasWindowShadow", "annotations");
        RequireVersion(root);

        JsonElement annotations = root.GetProperty("annotations");
        if (annotations.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Document annotations must be an array.");
        }

        foreach (JsonElement annotation in annotations.EnumerateArray())
        {
            RequireObject(annotation, "id", "kind", "start", "end", "strokeArgb", "fillArgb",
                "strokeWidth", "text", "fontFamily", "fontSize", "fontWeight", "italic",
                "stepDiameter", "stepNumber");
            RequireObject(annotation.GetProperty("start"), "x", "y");
            RequireObject(annotation.GetProperty("end"), "x", "y");
        }

        JsonElement crop = root.GetProperty("crop");
        if (crop.ValueKind != JsonValueKind.Null)
        {
            RequireObject(crop, "x", "y", "width", "height");
        }
    }

    internal static void ValidateSettings(JsonElement root)
    {
        RequireObject(root, "schemaVersion", "settings");
        RequireVersion(root);
        RequireObject(root.GetProperty("settings"), "startOnLogin", "theme", "autoCopy", "windowShadow");
    }

    private static void RequireVersion(JsonElement root)
    {
        JsonElement version = root.GetProperty("schemaVersion");
        if (version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out int number) || number != DocumentValidation.SchemaVersion)
        {
            throw new InvalidDataException("The stored schema version is unsupported.");
        }
    }

    private static void RequireObject(JsonElement element, params string[] requiredProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("A stored object has an invalid JSON shape.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new InvalidDataException($"Duplicate JSON property '{property.Name}'.");
            }
        }

        foreach (string property in requiredProperties)
        {
            if (!names.Contains(property))
            {
                throw new InvalidDataException($"Missing JSON property '{property}'.");
            }
        }
    }
}
