using System.Text.Json;

namespace FanControlBridge;

public record Profile(string Name, string ConfigPath = "", string Sha256 = "", string CanonicalSha256 = "");
public record Settings
{
    public string RuntimePath { get; init; } = "";
    public bool LiveHandoverValidated { get; init; }
    public string HandoverEvidencePath { get; init; } = "";
    public Profile[] Profiles { get; init; } = [new("Performance"), new("Balanced"), new("Night")];
    public FanCurveAssignment[] CurveAssignments { get; init; } = [];
    public MqttSettings Mqtt { get; init; } = new();
}
public record MqttSettings
{
    public bool Enabled { get; init; }
    public string Host { get; init; } = "";
    public int Port { get; init; } = 8883;
    public bool UseTls { get; init; } = true;
    public string TlsServerName { get; init; } = "";
    public bool AllowPlaintextPrivateBroker { get; init; }
    public string ClientId { get; init; } = "fancontrol-mqtt";
    public string TopicRoot { get; init; } = MqttBridge.Root;
}
public record BridgeState
{
    public string? ObservedProfile { get; init; }
    public string? RequestedProfile { get; init; }
    public string? PreviousProfile { get; init; }
    public bool NightActive { get; init; }
    public bool Pending { get; init; }
    public string Outcome { get; init; } = "Unconfigured; hardware state unknown";
    public string OutcomeCode { get; init; } = "unknown";
    public string? LastCommand { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
}
public static class Storage
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static T Read<T>(string path, T fallback) => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Empty state") : fallback;
    public static Settings ReadSettings(string path)
    {
        if (!File.Exists(path)) return new();
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException("Empty settings");
        foreach (var oldHttpField in new[] { "bindAddress", "port", "remoteExplicitlyEnabled" }) root.Remove(oldHttpField);
        root.Remove("nightProfile");
        var settings = root.Deserialize<Settings>(Json) ?? throw new InvalidDataException("Empty settings");
        return settings with { Profiles = settings.Profiles.Select(p => p with { Name = p.Name switch { "Normal" => "Performance", "Silent" or "Quiet" => "Night", _ => p.Name } }).ToArray() };
    }
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".new";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, value, Json); stream.Flush(true); }
        File.Move(temp, path, true);
    }
}
