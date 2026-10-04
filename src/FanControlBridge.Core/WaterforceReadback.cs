using HidSharp;

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
    public static WaterforceReading Read()
    {
        try
        {
            var devices = DeviceList.Local.GetHidDevices(0x1044, 0x7A4D).ToArray();
            if (devices.Length != 1) return new(null, null, null, null, "Expected exactly one Waterforce HID device");
            var device = devices[0]; var inputLength = device.GetMaxInputReportLength(); var outputLength = device.GetMaxOutputReportLength();
            if (inputLength is < 16 or > 1024 || outputLength is < 2 or > 1024)
                return new(null, null, null, null, "Unexpected HID report lengths");
            using var stream = device.Open(); stream.ReadTimeout = 1500; stream.WriteTimeout = 500;
            var request = new byte[outputLength]; request[0] = 0x99; request[1] = 0xDA;
            stream.Write(request); // Status request only. Never E5, E6, B6 or calibration.
            var response = new byte[inputLength]; var received = stream.Read(response, 0, response.Length);
            return Parse(response.AsSpan(0, received));
        }
        catch { return new(null, null, null, null, "Status query unavailable or invalid; no stale values retained"); }
    }
}
