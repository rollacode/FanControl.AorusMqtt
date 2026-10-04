namespace FanControlBridge;

public record HardwareSensorReading(string Id, string HardwareId, string Name, string Kind, double? Value);
public record HardwareTelemetry(DateTimeOffset SampledAt, HardwareSensorReading[] Sensors, string State);

public static class HardwareTelemetryReader
{
    public static HardwareTelemetry Read()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControlMqtt", "hardware-telemetry.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 128 * 1024) throw new IOException();
            var reading = Storage.Read<HardwareTelemetry>(path, new(default, [], "Unavailable"));
            if (DateTimeOffset.UtcNow - reading.SampledAt > TimeSpan.FromSeconds(15) || reading.SampledAt > DateTimeOffset.UtcNow.AddSeconds(5))
                return new(reading.SampledAt, [], "Stale hardware telemetry; values unavailable");
            return reading;
        }
        catch { return new(default, [], "Hardware sensor helper not connected"); }
    }

    public static TemperatureReading Temperature(HardwareTelemetry reading, bool package)
    {
        var candidates = reading.Sensors.Where(s => s.Kind == "Temperature" && (package
            ? CpuHardware(s.HardwareId) && s.Name == "CPU Package"
            : !CpuHardware(s.HardwareId) && s.Name == "CPU")).ToArray();
        if (candidates.Length != 1 || candidates[0].Value is not double value || value is < 0 or > 130)
            return new(null, package ? "CPU Package" : "Motherboard CPU", null, reading.State);
        return new(value, candidates[0].Id + " (LibreHardwareMonitor)", reading.SampledAt, "Measured; separate sensor source");
    }
    private static bool CpuHardware(string id) => id.StartsWith("/intelcpu/", StringComparison.Ordinal) || id.StartsWith("/amdcpu/", StringComparison.Ordinal);
}
