using System.Text.Json;

namespace FanControlBridge;

public record WaterforceReading(int? FanRpm, int? PumpRpm, double? CandidateLiquidCelsius, DateTimeOffset? SampledAt, string State);
public static class WaterforceReadback
{
    public static WaterforceReading Parse(ReadOnlySpan<byte> response)
    {
        if (response.Length < 15 || response[0] != 0x99 || response[1] != 0xDA)
            throw new InvalidDataException("Status header mismatch");
        var fan = response[2] | response[3] << 8; var pump = response[5] | response[6] << 8;
        if (fan > 6000 || pump > 6000 || response[13] > 100 || response[14] > 9)
            throw new InvalidDataException("Implausible status data");
        return new(fan, pump, response[13] + response[14] / 10d, DateTimeOffset.UtcNow,
            "Measured 99 DA packet; coolant meaning unverified; no cooling control commands");
    }
    public static WaterforceReading Read(string? receiptPath = null)
    {
        try
        {
            // One HID owner: consume the radiator plugin's sample instead of opening
            // a second handle that can interfere with mode/curve response matching.
            receiptPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControlMqtt", "waterforce-plugin-status.json");
            if (!File.Exists(receiptPath) || new FileInfo(receiptPath).Length > 128 * 1024) throw new IOException();
            using var document = JsonDocument.Parse(File.ReadAllText(receiptPath));
            var root = document.RootElement;
            if (root.GetProperty("hostProcessId").GetInt32() != Environment.ProcessId) throw new InvalidDataException();
            var status = root.GetProperty("status");
            var sampledAt = status.GetProperty("SampledAt").GetDateTimeOffset();
            var age = DateTimeOffset.UtcNow - sampledAt;
            if (age > TimeSpan.FromSeconds(5) || age < TimeSpan.FromSeconds(-5)) throw new InvalidDataException();
            var fan = status.GetProperty("FanRpm").GetInt32();
            var pump = status.GetProperty("PumpRpm").GetInt32();
            var candidate = status.GetProperty("CandidateLiquidTemperature").GetDouble();
            if (fan is < 0 or > 6000 || pump is < 0 or > 6000 || !double.IsFinite(candidate) || candidate is < 0 or > 100) throw new InvalidDataException();
            return new(fan, pump, candidate, sampledAt, "Measured by Waterforce plugin; single HID owner; coolant unverified");
        }
        catch { return new(null, null, null, null, "Status query unavailable or invalid; no stale values retained"); }
    }
}
