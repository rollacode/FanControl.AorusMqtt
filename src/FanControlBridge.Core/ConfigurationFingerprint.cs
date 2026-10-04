using System.Security.Cryptography;
using System.Text.Json;

namespace FanControlBridge;

public static class ConfigurationFingerprint
{
    public static bool NativeConfigurationAvailable(Profile profile)
    {
        try
        {
            if (!File.Exists(profile.ConfigPath) || new FileInfo(profile.ConfigPath).Length > 4 * 1024 * 1024) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(profile.ConfigPath));
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("FanControl", out var config)
                && config.ValueKind == JsonValueKind.Object
                && config.TryGetProperty("Controls", out var controls) && controls.ValueKind == JsonValueKind.Array
                && config.TryGetProperty("FanCurves", out var curves) && curves.ValueKind == JsonValueKind.Array;
        }
        catch { return false; }
    }
    public static string Canonical(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, document.RootElement);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length) throw new InvalidDataException("Duplicate configuration property");
                foreach (var property in properties) { writer.WritePropertyName(property.Name); Write(writer, property.Value); }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in element.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); break;
            case JsonValueKind.Number:
                if (element.TryGetDecimal(out var number)) writer.WriteRawValue(number.ToString("G29", System.Globalization.CultureInfo.InvariantCulture));
                else writer.WriteNumberValue(element.GetDouble());
                break;
            default: element.WriteTo(writer); break;
        }
    }
    public static bool Matches(Profile profile)
    {
        var bytes = File.ReadAllBytes(profile.ConfigPath);
        if (bytes.Length > 4 * 1024 * 1024) return false;
        if (profile.Sha256.Length > 0 && string.Equals(profile.Sha256, Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase)) return true;
        return profile.CanonicalSha256.Length > 0 && string.Equals(profile.CanonicalSha256, Canonical(File.ReadAllText(profile.ConfigPath)), StringComparison.OrdinalIgnoreCase);
    }
}
