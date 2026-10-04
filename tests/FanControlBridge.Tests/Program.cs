using System.Net;


using FanControlBridge;




using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Net.Sockets;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Server;
using MQTTnet.Protocol;

internal static class Program
{
    private static int assertions;
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); assertions++; }
    private static async Task Reject(Func<Task> action, string name) { try { await action(); } catch (InvalidOperationException) { assertions++; return; } throw new Exception(name); }
    [STAThread]
    private static void Main() => Run().GetAwaiter().GetResult();
    private static async Task Run()
    {
        var folder = Path.Combine(Path.GetTempPath(), "FanControlBridgeTests-" + Guid.NewGuid()); Directory.CreateDirectory(folder);
        try
        {
            var settings = new Settings();
            Check(ConfigurationFingerprint.Canonical("{\"b\":[1.0,2],\"a\":true}") == ConfigurationFingerprint.Canonical("{ \"a\":true,\"b\":[1,2.0] }"), "Native formatting, object order and numeric spelling do not change validation");
            Check(ConfigurationFingerprint.Canonical("{\"duty\":40}") != ConfigurationFingerprint.Canonical("{\"duty\":41}") && ConfigurationFingerprint.Canonical("[1,2]") != ConfigurationFingerprint.Canonical("[2,1]"), "Real curve changes and array ordering still invalidate native configuration");
            var editablePath = Path.Combine(folder, "Night.json");
            File.WriteAllText(editablePath, "{\"FanControl\":{\"Controls\":[],\"FanCurves\":[{\"Name\":\"CPU\",\"Points\":[\"85,100\"]}]}}");
            var editableProfile = new Profile("Night", editablePath, "OLD_HASH");
            Check(ConfigurationFingerprint.NativeConfigurationAvailable(editableProfile), "Owner-edited native preset remains selectable without an obsolete fingerprint");
            File.WriteAllText(editablePath, "{\"FanControl\":{\"Controls\":[],\"FanCurves\":[{\"Name\":\"CPU\",\"Points\":[\"80,100\"]}]}}");
            Check(ConfigurationFingerprint.NativeConfigurationAvailable(editableProfile), "Saving changed curve points keeps the existing native preset selectable");
            File.WriteAllText(editablePath, "{\"other\":true}");
            Check(!ConfigurationFingerprint.NativeConfigurationAvailable(editableProfile), "Invalid or non-native preset stays rejected");
            Check(FanControlRuntime.TitleMatchesConfiguration("Fan Control V282 (Performance-unique.json)", "Performance-unique.json")
                && !FanControlRuntime.TitleMatchesConfiguration("Fan Control V282 (userConfig.json)", "Performance-unique.json")
                && !FanControlRuntime.TitleMatchesConfiguration("Requested Performance-unique.json", "Performance-unique.json"), "Native observation requires exact loaded configuration title, not requested text");
            var curveFixture = """
                {"FanControl":{"Controls":[
                    {"Identifier":"board/cpu-fan","Enable":true,"SelectedFanCurve":{"Name":"CPU-Performance"}},
                    {"Identifier":"NVApiWrapper/gpu/control/0","Enable":true,"SelectedFanCurve":{"Name":"GPU-fixed"}},
                    {"Identifier":"waterforce/pump","Enable":false,"SelectedFanCurve":null}],
                    "FanCurves":[{"Name":"CPU-Performance","Points":["80,100"]},{"Name":"CPU-Silent","Points":["90,100"]},{"Name":"GPU-fixed","Points":["50,20"]}]},"Unrelated":{"Keep":17}}
                """;
            FanCurveAssignment[] assignments = [new("board/cpu-fan", "CPU-Performance", "CPU-Silent")];
            var silentConfig = System.Text.Json.Nodes.JsonNode.Parse(CurveAssignments.Build(curveFixture, "Silent", assignments))!;
            var originalConfig = System.Text.Json.Nodes.JsonNode.Parse(curveFixture)!;
            Check(silentConfig["FanControl"]!["Controls"]![0]!["SelectedFanCurve"]!["Name"]!.GetValue<string>() == "CPU-Silent", "Silent assigns explicit CPU curve");
            Check(System.Text.Json.Nodes.JsonNode.DeepEquals(silentConfig["FanControl"]!["Controls"]![1], originalConfig["FanControl"]!["Controls"]![1])
                && System.Text.Json.Nodes.JsonNode.DeepEquals(silentConfig["FanControl"]!["Controls"]![2], originalConfig["FanControl"]!["Controls"]![2])
                && System.Text.Json.Nodes.JsonNode.DeepEquals(silentConfig["FanControl"]!["FanCurves"], originalConfig["FanControl"]!["FanCurves"])
                && silentConfig["Unrelated"]!["Keep"]!.GetValue<int>() == 17, "Profile preparation preserves GPU, pump, curve points and unrelated settings");
            Check(System.Text.Json.Nodes.JsonNode.DeepEquals(originalConfig, System.Text.Json.Nodes.JsonNode.Parse(CurveAssignments.Build(silentConfig.ToJsonString(), "Normal", assignments))), "Normal restores exact original assignments");
            var balancedFixture = System.Text.Json.Nodes.JsonNode.Parse(curveFixture)!;
            balancedFixture["FanControl"]!["FanCurves"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse("{\"Name\":\"CPU-Balanced\",\"Points\":[\"85,100\"]}"));
            var balancedConfig = System.Text.Json.Nodes.JsonNode.Parse(CurveAssignments.Build(balancedFixture.ToJsonString(), "Balanced", [assignments[0] with { BalancedCurve = "CPU-Balanced" }]))!;
            Check(balancedConfig["FanControl"]!["Controls"]![0]!["SelectedFanCurve"]!["Name"]!.GetValue<string>() == "CPU-Balanced"
                && System.Text.Json.Nodes.JsonNode.DeepEquals(balancedConfig["FanControl"]!["Controls"]![1], originalConfig["FanControl"]!["Controls"]![1]), "Balanced maps only its explicit channel, preserving GPU");
            foreach (var invalidMapping in new[] {
                new FanCurveAssignment[] { new("board/cpu-fan", "CPU-Performance", "missing") },
                new FanCurveAssignment[] { assignments[0], assignments[0] },
                new FanCurveAssignment[] { new("unknown", "CPU-Performance", "CPU-Silent") },
                new FanCurveAssignment[] { new("NVApiWrapper/gpu/control/0", "CPU-Performance", "CPU-Silent") } })
            {
                var rejectedMapping = false;
                try { CurveAssignments.Build(curveFixture, "Silent", invalidMapping); } catch (InvalidDataException) { rejectedMapping = true; }
                Check(rejectedMapping, "Invalid mapping fails before any native config can be applied");
            }
            Check(MqttBridge.ValidCommand("fancontrol/custom/profile/set", "Night", "fancontrol/custom") && !MqttBridge.ValidCommand("fancontrol/aorus/profile/set", "Night", "fancontrol/custom"), "Configured prefix isolates commands");
            Check(!MqttBridge.ValidRoot("fancontrol/+") && !MqttBridge.ValidRoot("fancontrol/#") && !MqttBridge.ValidRoot(""), "MQTT prefix cannot inject subscriptions");
            var hardware = new HardwareTelemetry(DateTimeOffset.UtcNow, [
                new("/intelcpu/0/temperature/1", "/intelcpu/0", "CPU Package", "Temperature", 61),
                new("/lpc/testboard/temperature/0", "/lpc/testboard", "CPU", "Temperature", 48)
            ], "Measured");
            Check(HardwareTelemetryReader.Temperature(hardware, true).Celsius == 61 && HardwareTelemetryReader.Temperature(hardware, false).Celsius == 48, "CPU package and board CPU remain distinct");
            Check(HardwareTelemetryReader.Temperature(hardware with { Sensors = [hardware.Sensors[0] with { Value = null }] }, true).Celsius is null, "Missing CPU measurement stays unavailable");
            Check(HardwareTelemetryReader.Temperature(hardware with { Sensors = [hardware.Sensors[0], hardware.Sensors[0]] }, true).Celsius is null, "Ambiguous CPU source rejected");
            Check(HardwareTelemetryReader.Temperature(hardware with { Sensors = [hardware.Sensors[0] with { Value = 200 }] }, true).Celsius is null, "Implausible CPU reading rejected");
            var samplePacket = new byte[15]; samplePacket[0] = 0x99; samplePacket[1] = 0xDA;
            samplePacket[2] = 0x28; samplePacket[3] = 0x05; samplePacket[5] = 0x87; samplePacket[6] = 0x08; samplePacket[13] = 41;
            var coolerReading = WaterforceReadback.Parse(samplePacket);
            var receiptPath = Path.Combine(folder, "waterforce-status.json");
            void WriteReceipt(int pid, DateTimeOffset at) => File.WriteAllText(receiptPath, JsonSerializer.Serialize(new {
                hostProcessId = pid, status = new { SampledAt = at, FanRpm = 479, PumpRpm = 2183, CandidateLiquidTemperature = 41.2 } }));
            WriteReceipt(Environment.ProcessId, DateTimeOffset.UtcNow);
            Check(WaterforceReadback.Read(receiptPath).FanRpm == 479, "MQTT consumes same-host plugin sample without a second HID owner");
            WriteReceipt(-1, DateTimeOffset.UtcNow);
            Check(WaterforceReadback.Read(receiptPath).FanRpm is null, "Another host's receipt is not current telemetry");
            WriteReceipt(Environment.ProcessId, DateTimeOffset.UtcNow.AddSeconds(-10));
            Check(WaterforceReadback.Read(receiptPath).FanRpm is null, "Stale plugin receipt clears telemetry");
            File.WriteAllText(receiptPath, "{}");
            Check(WaterforceReadback.Read(receiptPath).FanRpm is null, "Incomplete plugin receipt cannot fabricate readings");
            Check(coolerReading.FanRpm == 1320 && coolerReading.PumpRpm == 2183 && coolerReading.CandidateLiquidCelsius == 41, "Waterforce status offsets and LE RPM parsing");
            samplePacket[1] = 0xE6;
            var invalidRejected = false; try { WaterforceReadback.Parse(samplePacket); } catch (InvalidDataException) { invalidRejected = true; }
            Check(invalidRejected, "Non-status response not accepted as telemetry");
            var runtime = new FakeRuntime();
            var statePath = Path.Combine(folder, "state.json");
            var controller = new Controller(statePath, settings, runtime);
            await controller.CommandAsync("select", "Night");
            var state = await controller.StatusAsync();
            Check(state.ObservedProfile == "Night" && !state.NightActive && state.PreviousProfile is null, "Night is an ordinary mode with no override");
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => controller.CommandAsync("select", "Night")));
            Check(runtime.Calls == 1, "Duplicate observed mode selections are idempotent");
            controller = new Controller(statePath, settings, runtime);
            state = await controller.StatusAsync();
            Check(state.RequestedProfile == "Night" && state.ObservedProfile is null && !state.NightActive && state.PreviousProfile is null, "Restart preserves requested mode but never restores an override or invents observation");
            await controller.CommandAsync("select", "Balanced");
            Check((await controller.StatusAsync()).ObservedProfile == "Balanced", "Balanced can directly replace Night");
            await Reject(() => controller.CommandAsync("enter-night"), "Legacy Night toggle removed");
            await Reject(() => controller.CommandAsync("select", "../../anything"), "Only configured profiles allowed");
            runtime.Fail = true; await controller.CommandAsync("select", "Performance");
            Check((await controller.StatusAsync()).Pending, "Uncertain failure retained for reconciliation");
            await Reject(() => controller.CommandAsync("select", "Night"), "Failure blocks further submissions");
            controller = new Controller(statePath, settings, runtime);
            Check((await controller.StatusAsync()).Pending, "Uncertain failure survives restart");
            await controller.ConfirmObservedAsync("Night"); runtime.Fail = false; runtime.Unknown = true;
            await controller.CommandAsync("select", "Balanced");
            Check((await controller.StatusAsync()).Pending && (await controller.StatusAsync()).ObservedProfile is null, "CLI acceptance is not reported as actual mode");
            await controller.ConfirmObservedAsync("Balanced"); runtime.Unknown = false;
            await Task.WhenAll(Enumerable.Range(0, 6).Select(i => controller.CommandAsync("select", i % 2 == 0 ? "Performance" : "Night")));
            Check(runtime.MaxConcurrent == 1, "Mode commands serialized");
            var live = new FanControlRuntime(settings);
            Check(!(await live.SubmitAsync(new("Performance"))).Accepted, "Live writer locked by default");
            var lockedController = new Controller(Path.Combine(folder, "locked-state.json"), settings, live);
            await lockedController.ObserveRuntimeAsync("Balanced");
            Check((await lockedController.StatusAsync()).ObservedProfile == "Balanced" && runtime.Calls > 0, "Read-only native observation reconciles manual mode without submitting cooling commands");
            await lockedController.ObserveRuntimeAsync(null);
            Check((await lockedController.StatusAsync()).ObservedProfile is null, "Lost native observation clears current mode instead of preserving stale requested state");
            await lockedController.ConfirmObservedAsync("Performance");
            var rejectedNight = await lockedController.CommandAsync("select", "Night");
            Check(!rejectedNight.Pending && rejectedNight.ObservedProfile == "Performance", "Definite hardware lock preserves last actual observed mode");
            await using var diagnostics = new Diagnostics(controller, settings, () => new(default, [], "Test sensor disconnected"));
            var pipeName = "FanControlBridgeTest-" + Guid.NewGuid();
            await using var diagnosticPipe = new DiagnosticPipe(diagnostics, pipeName); diagnosticPipe.Start();
            using (var pipeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(pipeTimeout.Token); await pipe.WriteAsync(Encoding.ASCII.GetBytes("status\n"), pipeTimeout.Token);
                var snapshot = await JsonSerializer.DeserializeAsync<DiagnosticSnapshot>(pipe, Storage.Json, pipeTimeout.Token);
                Check(snapshot is not null && snapshot.Telemetry.CpuPackage.Celsius is null && !snapshot.LiveControlEnabled, "Local identity-protected diagnostics reports actual limitations");
            }
            var beforeInvalidPipe = runtime.Calls;
            using (var pipeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(pipeTimeout.Token); await pipe.WriteAsync(Encoding.ASCII.GetBytes("select\n"), pipeTimeout.Token);
                var buffer = new byte[16]; Check(await pipe.ReadAsync(buffer, pipeTimeout.Token) == 0 && runtime.Calls == beforeInvalidPipe, "Diagnostic pipe cannot mutate profiles");
            }
            await TestMqtt(folder, controller, diagnostics, runtime);
            Console.WriteLine($"PASS: {assertions} assertions; mock cooling, real loopback MQTT, DPAPI, protected diagnostics. No hardware writes.");
        }
        finally { Directory.Delete(folder, true); }
    }
    private static async Task WaitFor(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await condition()) await Task.Delay(20, timeout.Token);
    }
    private static async Task TestMqtt(string folder, Controller controller, Diagnostics diagnostics, FakeRuntime runtime)
    {
        var credentialPath = Path.Combine(folder, "mqtt.dpapi"); var credentialStore = new MqttCredentialStore(credentialPath);
        var brokerPassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        credentialStore.Save("mqtt-test-user", brokerPassword);
        Check(credentialStore.Read()?.Password == brokerPassword && !Encoding.UTF8.GetString(File.ReadAllBytes(credentialPath)).Contains(brokerPassword), "MQTT credentials protected at rest");
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var factory = new MqttFactory();
        using var broker = factory.CreateMqttServer(new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointBoundIPAddress(IPAddress.Loopback).WithDefaultEndpointPort(port).Build());
        broker.ValidatingConnectionAsync += args =>
        { if (args.UserName != "mqtt-test-user" || args.Password != brokerPassword) args.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword; return Task.CompletedTask; };
        await broker.StartAsync();
        using var publisher = factory.CreateMqttClient();
        await publisher.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).WithCredentials("mqtt-test-user", brokerPassword).Build());
        using var unauthorized = factory.CreateMqttClient();
        var authRejected = false;
        try { await unauthorized.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", port).Build()); } catch { authRejected = true; }
        Check(authRejected && !unauthorized.IsConnected, "MQTT broker rejects missing credentials");
        Check(!MqttBridge.ValidCommand("fancontrol/aorus/shell", "bad") && !MqttBridge.ValidCommand("fancontrol/aorus/profile/set", "arbitrary")
            && MqttBridge.ValidCommand("fancontrol/aorus/profile/set", "Night") && MqttBridge.ValidCommand("fancontrol/aorus/profile/set", "Balanced"), "MQTT fixed profile allowlist includes named modes");
        Check(!MqttBridge.ValidCommand(MqttBridge.Root + "/night/set", "ON"), "MQTT exposes no Night toggle");
        await controller.ConfirmObservedAsync("Performance");
        await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(MqttBridge.Root + "/profile/set").WithPayload("Night").WithRetainFlag().Build());
        await using var mqtt = new MqttBridge(new MqttSettings { Enabled = true, Host = "127.0.0.1", Port = port, UseTls = false, AllowPlaintextPrivateBroker = true }, credentialStore, controller, diagnostics);
        var callsBeforeRetained = runtime.Calls; mqtt.Start();
        await WaitFor(() => Task.FromResult(mqtt.ConnectionState == "Connected")); await Task.Delay(150);
        Check((await controller.StatusAsync()).ObservedProfile == "Performance" && runtime.Calls == callsBeforeRetained, "Retained profile command is never replayed");
        await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(MqttBridge.Root + "/profile/set").WithPayload("Night").Build());
        await WaitFor(async () => (await controller.StatusAsync()).ObservedProfile == "Night");
        Check((await controller.StatusAsync()).PreviousProfile is null && !(await controller.StatusAsync()).NightActive, "MQTT Night directly selects profile without override");
        var callsBeforeDuplicate = runtime.Calls;
        await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(MqttBridge.Root + "/profile/set").WithPayload("Night").Build()); await Task.Delay(100);
        Check(runtime.Calls == callsBeforeDuplicate, "Duplicate MQTT selection does not resubmit observed mode");
        foreach (var profile in new[] { "Balanced", "Performance" })
        {
            await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(MqttBridge.Root + "/profile/set").WithPayload(profile).Build());
            await WaitFor(async () => (await controller.StatusAsync()).ObservedProfile == profile);
            Check(runtime.Last == profile, "MQTT directly selects requested daytime mode");
        }
        var beforeOldToggle = runtime.Calls;
        await publisher.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(MqttBridge.Root + "/night/set").WithPayload("ON").Build()); await Task.Delay(100);
        Check(runtime.Calls == beforeOldToggle, "Old Night topic cannot mutate hardware");
        await publisher.DisconnectAsync(); await broker.StopAsync();
    }
    private sealed class FakeRuntime : ICoolingRuntime
    {
        public int Calls; public string? Last; public bool Fail; public bool Unknown; public int MaxConcurrent; private int active;
        public async Task<Submission> SubmitAsync(Profile profile)
        {
            Calls++; Last = profile.Name; var count = Interlocked.Increment(ref active); MaxConcurrent = Math.Max(MaxConcurrent, count);
            try { await Task.Delay(15); return Fail ? new(false, "Injected failure") : new(true, "Mock only", Unknown ? null : profile.Name); }
            finally { Interlocked.Decrement(ref active); }
        }
    }
}
