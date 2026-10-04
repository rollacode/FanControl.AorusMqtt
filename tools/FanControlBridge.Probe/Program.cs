using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using FanControlBridge;

if (args.Length == 1 && args[0] == "--record-native-fingerprints")
{
    var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControlMqtt", "settings.json");
    var settings = Storage.ReadSettings(settingsPath);
    if (!settings.Profiles.Select(p => p.Name).Order().SequenceEqual(new[] { "Balanced", "Night", "Performance" })) throw new InvalidDataException("Expected three native modes");
    var profiles = settings.Profiles.Select(p => p with { Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p.ConfigPath))), CanonicalSha256 = ConfigurationFingerprint.Canonical(File.ReadAllText(p.ConfigPath)) }).ToArray();
    Storage.Write(settingsPath, settings with { Profiles = profiles });
    Console.WriteLine("Recorded three native configuration fingerprints; no cooling command sent.");
    return;
}

if (args.Length == 2 && args[0] == "--prepare-curve-profiles")
{
    try
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControlMqtt");
        var settingsPath = Path.Combine(directory, "settings.json");
        var settings = Storage.ReadSettings(settingsPath);
        var basePath = Path.GetFullPath(args[1]);
        if (!File.Exists(basePath) || new FileInfo(basePath).Length > 4 * 1024 * 1024) throw new InvalidDataException("Native base config unavailable or oversized");
        var original = await File.ReadAllTextAsync(basePath);
        // Validate both complete outputs before writing anything. Preparation never calls Fan Control.
        var prepared = new[] { "Performance", "Balanced", "Night" }.Select(name => (Name: name, Json: CurveAssignments.Build(original, name, settings.CurveAssignments))).ToArray();
        var bundle = Path.Combine(directory, "profiles", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bundle);
        var profiles = new List<Profile>();
        foreach (var item in prepared)
        {
            var path = Path.Combine(bundle, item.Name + "-" + Path.GetFileName(bundle) + ".json");
            await File.WriteAllTextAsync(path, item.Json);
            profiles.Add(new(item.Name, path, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path)))));
        }
        Storage.Write(Path.Combine(directory, "settings-before-curve-preparation.json"), settings);
        Storage.Write(settingsPath, settings with { Profiles = profiles.ToArray(), LiveHandoverValidated = false, HandoverEvidencePath = "" });
        Console.WriteLine("Performance/Balanced/Night profiles prepared. No runtime command sent; live handover validation remains required.");
    }
    catch (Exception ex) { Console.Error.WriteLine("Curve preparation failed: " + ex.Message); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 1 && args[0] == "--waterforce-config-read")
{
    try
    {
        var devices = HidSharp.DeviceList.Local.GetHidDevices(0x1044, 0x7A4D).ToArray();
        if (devices.Length != 1) throw new InvalidOperationException("Expected exactly one Waterforce HID");
        using var stream = devices[0].Open(); stream.ReadTimeout = 1000; stream.WriteTimeout = 500;
        var inputLength = devices[0].GetMaxInputReportLength(); var outputLength = devices[0].GetMaxOutputReportLength();
        if (inputLength is < 16 or > 1024 || outputLength is < 3 or > 1024) throw new InvalidDataException();
        byte[] Query(byte command, byte channel = 0)
        {
            var request = new byte[outputLength]; request[0] = 0x99; request[1] = command; request[2] = channel;
            stream.Write(request);
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var response = new byte[inputLength]; var count = stream.Read(response, 0, response.Length);
                if (count >= 16 && response[0] == 0x99 && response[1] == command) return response;
            }
            throw new InvalidDataException("Configuration response not matched");
        }
        var model = Query(0xDE); var mode = Query(0xDD); var fanCurve = Query(0xD9, 1); var pumpCurve = Query(0xD9, 2);
        object[] Points(byte[] response) => Enumerable.Range(0, 4).Select(i => (object)new { celsius = response[4 + i * 3], rpm = response[5 + i * 3] << 8 | response[6 + i * 3] }).ToArray();
        var backup = new { sampledAt = DateTimeOffset.UtcNow, operation = "Read-only DE/DD/D9 queries; no E5/E6/B6", modelCode = model[2], fanModeCode = mode[2], pumpModeCode = mode[3],
            fanCurve = Points(fanCurve), pumpCurve = Points(pumpCurve), rawModel = Convert.ToHexString(model.AsSpan(0,16)), rawMode = Convert.ToHexString(mode.AsSpan(0,16)),
            rawFanCurve = Convert.ToHexString(fanCurve.AsSpan(0,16)), rawPumpCurve = Convert.ToHexString(pumpCurve.AsSpan(0,16)), interpretationVerified = false };
        var backupPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControlMqtt", "waterforce-config-readback.json");
        Storage.Write(backupPath, backup);
        Console.WriteLine(JsonSerializer.Serialize(backup, Storage.Json));
    }
    catch (Exception ex) { Console.Error.WriteLine("Read-only configuration query failed: " + ex.Message); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 2 && args[0] == "--provision-mqtt-stdin")
{
    // For an approved local vault-helper pipeline; the secret is never an argument or output.
    try
    {
        var input = new char[4097]; var count = 0;
        while (count < input.Length)
        { var read = await Console.In.ReadAsync(input.AsMemory(count)); if (read == 0) break; count += read; }
        if (count == 0 || count > 4096) throw new InvalidDataException();
        var password = new string(input, 0, count).TrimEnd('\r', '\n'); Array.Clear(input);
        if (password.Length == 0) throw new InvalidDataException();
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControlMqtt");
        var username = args[1];
        if (string.IsNullOrWhiteSpace(username) || username.Length > 256 || username.Any(char.IsControl)) throw new InvalidDataException();
        new MqttCredentialStore(Path.Combine(directory, "mqtt-credentials.dpapi")).Save(username, password);
        Console.WriteLine("MQTT credentials stored with CurrentUser DPAPI; secret not displayed.");
    }
    catch { Console.Error.WriteLine("MQTT credential provisioning failed; secret not displayed."); Environment.ExitCode = 1; }
    return;
}

if (args.Length == 1 && args[0] == "--waterforce-status")
{
    // One bounded status transaction only. No E5/E6/B6 commands, mode changes or calibration.
    try
    {
        var devices = HidSharp.DeviceList.Local.GetHidDevices(0x1044, 0x7A4D).ToArray();
        if (devices.Length != 1) throw new InvalidOperationException("Expected exactly one Waterforce HID device");
        var device = devices[0]; var inputLength = device.GetMaxInputReportLength(); var outputLength = device.GetMaxOutputReportLength();
        if (inputLength is < 16 or > 1024 || outputLength is < 2 or > 1024) throw new InvalidOperationException("Unexpected report lengths");
        using var stream = device.Open(); stream.ReadTimeout = 1500; stream.WriteTimeout = 500;
        var request = new byte[outputLength]; request[0] = 0x99; request[1] = 0xDA; stream.Write(request);
        var response = new byte[inputLength]; var received = stream.Read(response, 0, response.Length);
        if (received < 15 || response[0] != 0x99 || response[1] != 0xDA) throw new InvalidOperationException("Status header mismatch; no measurements trusted");
        var fanRpm = response[2] | response[3] << 8; var pumpRpm = response[5] | response[6] << 8;
        if (fanRpm > 6000 || pumpRpm > 6000 || response[14] > 9) throw new InvalidOperationException("Implausible status; no measurements trusted");
        Console.WriteLine(JsonSerializer.Serialize(new { device = device.GetFriendlyName(), vendorId = "1044", productId = "7A4D", inputLength, outputLength,
            sampledAt = DateTimeOffset.UtcNow, fanRpm, pumpRpm, rawFanDuty = response[8], rawPumpDuty = response[9],
            candidateLiquidCelsius = response[13] + response[14] / 10d, coolantVerified = false, operation = "Single 99 DA status query; cooling settings untouched" }, Storage.Json));
    }
    catch (Exception ex) { Console.Error.WriteLine("Waterforce read-only probe: " + ex.Message); Environment.ExitCode = 1; }
    return;
}
if (args.Length != 0) { Console.Error.WriteLine("Only status or --waterforce-status supported"); Environment.ExitCode = 1; return; }

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
try
{
    await using var pipe = new NamedPipeClientStream(".", DiagnosticPipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    await pipe.ConnectAsync(timeout.Token);
    await pipe.WriteAsync(Encoding.ASCII.GetBytes("status\n"), timeout.Token);
    await pipe.FlushAsync(timeout.Token);
    var snapshot = await JsonSerializer.DeserializeAsync<DiagnosticSnapshot>(pipe, Storage.Json, timeout.Token);
    Console.WriteLine(JsonSerializer.Serialize(snapshot, Storage.Json));
}
catch
{
    try
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControlMqtt", "diagnostic-snapshot.json");
        if (new FileInfo(path).Length > 128 * 1024) throw new IOException();
        var receipt = Storage.Read<DiagnosticReceipt>(path, null!) ?? throw new IOException();
        if (DateTimeOffset.UtcNow - receipt.SampledAt > TimeSpan.FromSeconds(15) || receipt.SampledAt > DateTimeOffset.UtcNow.AddSeconds(5)) throw new IOException();
        Console.WriteLine(JsonSerializer.Serialize(receipt.Snapshot, Storage.Json));
    }
    catch { Console.Error.WriteLine("Fresh same-user diagnostics unavailable. Start Fan Control as the same Windows user."); Environment.ExitCode = 1; }
}
