using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FanControlBridge;

public record TemperatureReading(double? Celsius, string Source, DateTimeOffset? SampledAt, string State);
public record Telemetry(TemperatureReading CpuPackage, TemperatureReading BoardCpu, TemperatureReading Gpu, TemperatureReading Coolant, WaterforceReading Waterforce, HardwareTelemetry? Motherboard = null);
public record ConfigurationCheck(string Name, bool Exists, bool MatchesValidation, string State);
public record DiagnosticSnapshot(BridgeState Bridge, Telemetry Telemetry, string[] DetectedVendorWriters, bool RuntimeRunning,
    bool LiveControlEnabled, ConfigurationCheck[] Configurations, string AppliedState, string MqttConnectionState, bool ControlReady);

public sealed class Diagnostics(Controller controller, Settings settings, Func<HardwareTelemetry>? readHardware = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource stop = new();
    private Task? polling;
    private TemperatureReading gpu = Missing("NVIDIA temperature.gpu", "Not sampled yet");
    private WaterforceReading waterforce = new(null, null, null, null, "Not sampled yet");
    private readonly string snapshotPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControlMqtt", "diagnostic-snapshot.json");
    public Func<string> MqttState { private get; set; } = () => "Disabled";
    private static TemperatureReading Missing(string source, string state) => new(null, source, null, state);
    public void Start() => polling ??= Task.Run(PollAsync);
    private async Task PollAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            gpu = await SampleGpuAsync(stop.Token);
            if (!stop.IsCancellationRequested) waterforce = await Task.Run(WaterforceReadback.Read);
            // Same-user local read-only receipt also works across Windows integrity levels.
            // Never includes MQTT credentials or any writable command endpoint.
            try { Storage.Write(snapshotPath, new DiagnosticReceipt(DateTimeOffset.UtcNow, await SnapshotAsync())); } catch (IOException) { }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stop.Token); } catch (OperationCanceledException) { break; }
        }
    }
    private static async Task<TemperatureReading> SampleGpuAsync(CancellationToken cancellation)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvidia-smi.exe");
        if (!File.Exists(path)) return Missing("NVIDIA temperature.gpu", "NVIDIA read-only query unavailable");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var process = new Process { StartInfo = new(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.ArgumentList.Add("--query-gpu=temperature.gpu");
        process.StartInfo.ArgumentList.Add("--format=csv,noheader,nounits");
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = (await output).Trim(); await error;
            if (process.ExitCode == 0 && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 130)
                return new(value, "NVIDIA temperature.gpu (read-only nvidia-smi)", DateTimeOffset.UtcNow, "Measured; GPU fan settings untouched");
            return Missing("NVIDIA temperature.gpu", "Query failed or ambiguous GPU result");
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            return Missing("NVIDIA temperature.gpu", "Query timed out or unavailable");
        }
    }
    public Telemetry ReadTelemetry()
    {
        var reading = gpu;
        if (reading.SampledAt is not null && DateTimeOffset.UtcNow - reading.SampledAt > TimeSpan.FromSeconds(15))
            reading = reading with { Celsius = null, State = "Stale; last value not shown as current" };
        var cooler = waterforce;
        if (cooler.SampledAt is not null && DateTimeOffset.UtcNow - cooler.SampledAt > TimeSpan.FromSeconds(15))
            cooler = cooler with { FanRpm = null, PumpRpm = null, CandidateLiquidCelsius = null, State = "Stale; last values not current" };
        var hardware = (readHardware ?? HardwareTelemetryReader.Read)();
        return new(HardwareTelemetryReader.Temperature(hardware, true), HardwareTelemetryReader.Temperature(hardware, false), reading,
            Missing("Waterforce liquid sensor", "HID coolant interpretation not independently validated"), cooler, hardware);
    }
    private static bool Running(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try { return processes.Length > 0; } finally { foreach (var process in processes) process.Dispose(); }
    }
    public async Task<DiagnosticSnapshot> SnapshotAsync()
    {
        var checks = settings.Profiles.Select(p =>
        {
            try
            {
                if (!File.Exists(p.ConfigPath)) return new ConfigurationCheck(p.Name, false, false, "Saved Fan Control config missing");
                if (new FileInfo(p.ConfigPath).Length > 4 * 1024 * 1024) return new(p.Name, true, false, "Config exceeds limit");
                var match = ConfigurationFingerprint.NativeConfigurationAvailable(p);
                return new(p.Name, true, match, match ? "Saved native configuration available; user edits supported" : "Invalid native configuration");
            }
            catch { return new ConfigurationCheck(p.Name, false, false, "Config could not be inspected"); }
        }).ToArray();
        if (!string.IsNullOrWhiteSpace(settings.RuntimePath))
            await controller.ObserveRuntimeAsync(FanControlRuntime.ObserveConfiguration(settings));
        var bridge = await controller.StatusAsync();
        var writers = new[] { "AsusFanControlService", "GCC", "AorusLcdService" }.Where(Running).ToArray();
        var runtimeRunning = Running("FanControl");
        var liveEnabled = settings.LiveHandoverValidated && File.Exists(settings.HandoverEvidencePath);
        var ready = liveEnabled && runtimeRunning && writers.Length == 0
            && checks.Select(c => c.Name).Order().SequenceEqual(new[] { "Balanced", "Night", "Performance" }) && checks.All(c => c.MatchesValidation)
            && !bridge.Pending && bridge.ObservedProfile is not null && bridge.ObservedAt is not null && DateTimeOffset.UtcNow - bridge.ObservedAt < TimeSpan.FromMinutes(2);
        return new(bridge, ReadTelemetry(), writers, runtimeRunning, liveEnabled, checks,
            bridge.ObservedProfile is null ? "Native active configuration unavailable" : "Native loaded configuration observed; individual physical outputs monitored separately", MqttState(), ready);
    }
    public async ValueTask DisposeAsync()
    { stop.Cancel(); if (polling is not null) await polling; stop.Dispose(); }
}

public record DiagnosticReceipt(DateTimeOffset SampledAt, DiagnosticSnapshot Snapshot);

// Local diagnostic access uses Windows identity, never an unauthenticated TCP backdoor.
// Fixed read-only operation; it cannot change profiles, settings, tokens or hardware.
public sealed class DiagnosticPipe(Diagnostics diagnostics, string? pipeName = null) : IAsyncDisposable
{
    public static string Name => "FanControlMqtt.Diagnostics." + WindowsIdentity.GetCurrent().User!.Value;
    private readonly CancellationTokenSource stop = new();
    private Task? serving;
    public void Start() => serving ??= Task.Run(ServeAsync);
    private async Task ServeAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(pipeName ?? Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var command = new byte[7]; var received = 0;
                while (received < command.Length)
                {
                    var count = await pipe.ReadAsync(command.AsMemory(received), timeout.Token);
                    if (count == 0) break; received += count;
                }
                if (received != 7 || Encoding.ASCII.GetString(command) != "status\n") continue;
                await JsonSerializer.SerializeAsync(pipe, await diagnostics.SnapshotAsync(), Storage.Json, timeout.Token);
                await pipe.FlushAsync(timeout.Token);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }
    public async ValueTask DisposeAsync()
    { stop.Cancel(); if (serving is not null) await serving; stop.Dispose(); }
}
