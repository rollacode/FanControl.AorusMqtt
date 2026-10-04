using System.Text.Json.Nodes;

namespace FanControlBridge;

public record FanCurveAssignment(string ControlId, string PerformanceCurve, string NightCurve, string? BalancedCurve = null);

public static class CurveAssignments
{
    // Prepare two complete native configs without touching the running instance.
    // Only explicitly listed channels change; curves, enabled flags, pumps and GPU remain intact.
    public static string Build(string nativeConfig, string profile, IReadOnlyList<FanCurveAssignment> assignments)
    {
        profile = profile switch { "Performance" => "Normal", "Night" => "Silent", _ => profile };
        if (profile is not ("Normal" or "Silent" or "Balanced")) throw new InvalidDataException("Unknown curve profile");
        if (nativeConfig.Length > 4 * 1024 * 1024 || assignments.Count is < 1 or > 32)
            throw new InvalidDataException("Configuration or assignment count invalid");
        var root = JsonNode.Parse(nativeConfig)?.AsObject() ?? throw new InvalidDataException("Missing native configuration");
        var native = root["FanControl"]?.AsObject() ?? throw new InvalidDataException("Missing FanControl configuration");
        var controls = native["Controls"]?.AsArray() ?? throw new InvalidDataException("Missing controls");
        var curves = native["FanCurves"]?.AsArray() ?? throw new InvalidDataException("Missing curves");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            if (string.IsNullOrWhiteSpace(assignment.ControlId) || !seen.Add(assignment.ControlId))
                throw new InvalidDataException("Duplicate or missing channel identity");
            if (assignment.ControlId.StartsWith("NVApiWrapper/", StringComparison.Ordinal))
                throw new InvalidDataException("Native GPU channel excluded from this cooling profile");
            var matches = controls.Where(c => c?["Identifier"]?.GetValue<string>() == assignment.ControlId).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Channel missing or ambiguous");
            // Validate both names now: no partially usable Normal/Silent pair.
            foreach (var name in new[] { assignment.PerformanceCurve, assignment.NightCurve })
                if (string.IsNullOrWhiteSpace(name) || curves.Count(c => c?["Name"]?.GetValue<string>() == name) != 1)
                    throw new InvalidDataException("Curve missing or ambiguous");
            var selected = profile switch { "Normal" => assignment.PerformanceCurve, "Silent" => assignment.NightCurve, _ => assignment.BalancedCurve };
            if (string.IsNullOrWhiteSpace(selected) || curves.Count(c => c?["Name"]?.GetValue<string>() == selected) != 1)
                throw new InvalidDataException("Selected mode curve missing or ambiguous");
            matches[0]!["SelectedFanCurve"] = new JsonObject { ["Name"] = selected };
        }
        return root.ToJsonString(Storage.Json);
    }
}
