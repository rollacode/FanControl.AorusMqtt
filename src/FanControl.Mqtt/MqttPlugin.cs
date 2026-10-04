using FanControlBridge;
using FanControl.Plugins;
using LibreHardwareMonitor.Hardware;
using System.Security.Principal;

namespace FanControl.Mqtt;

public sealed class MqttPlugin(IPluginLogger logger) : IPlugin2
{
    public string Name => "Fan Control MQTT — Performance / Balanced / Night";
    private Mutex? singleton;
    private Diagnostics? diagnostics;
    private DiagnosticPipe? pipe;
    private MqttBridge? mqtt;
    private Computer? computer;
    private CancellationTokenSource? stop;
    private Task? polling;
    private CpuPackageSensor? cpuSensor;
    private HardwareTelemetry hardware = new(default, [], "Not sampled");
    public void Initialize()
    {
        Close();
        if (WindowsIdentity.GetCurrent().IsSystem) { logger.Log("Fan Control MQTT requires the owner's interactive Fan Control process; SYSTEM credentials are not provisioned."); return; }
        singleton = new Mutex(true, "Local\\FanControlMqtt-" + Environment.UserName, out var created);
        if (!created) { singleton.Dispose(); singleton = null; logger.Log("Fan Control standalone bridge still running; MQTT plugin remains inactive to avoid duplicate clients."); return; }
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControlMqtt");
            var settings = Storage.ReadSettings(Path.Combine(directory, "settings.json"));
            settings = settings with { RuntimePath = Environment.ProcessPath ?? settings.RuntimePath };
            var controller = new Controller(Path.Combine(directory, "state.json"), settings, new FanControlRuntime(settings));
            computer = new Computer { IsCpuEnabled = true, IsMotherboardEnabled = true }; computer.Open();
            stop = new(); var cancellation = stop.Token;
            polling = Task.Run(async () =>
            {
                while (!cancellation.IsCancellationRequested)
                {
                    try
                    {
                        var values = new List<HardwareSensorReading>();
                        foreach (var item in computer.Hardware) Sample(item, values);
                        Volatile.Write(ref hardware, new(DateTimeOffset.UtcNow, values.ToArray(), "Measured by Fan Control plugin; no controls written"));
                        Storage.Write(Path.Combine(directory, "cpu-package-sample.json"), HardwareTelemetryReader.Temperature(Volatile.Read(ref hardware), true));
                    }
                    catch { Volatile.Write(ref hardware, new(DateTimeOffset.UtcNow, [], "Hardware reading unavailable")); }
                    try { await Task.Delay(2000, cancellation); } catch (OperationCanceledException) { break; }
                }
            });
            diagnostics = new(controller, settings, () => Volatile.Read(ref hardware)); diagnostics.Start();
            pipe = new(diagnostics); pipe.Start();
            mqtt = new(settings.Mqtt, new(Path.Combine(directory, "mqtt-credentials.dpapi")), controller, diagnostics);
            diagnostics.MqttState = () => mqtt.ConnectionState;
            mqtt.Start();
            logger.Log("Fan Control MQTT plugin started. Broker settings/DPAPI preserved; live hardware handover remains gated.");
        }
        catch { logger.Log("Fan Control MQTT plugin initialization failed; no secrets logged."); Close(); }
    }
    public void Load(IPluginSensorsContainer container)
    {
        // Fan Control requires each active plugin to register a sensor. Publish the
        // real CPU Package sample already used by MQTT, never connection state as a temperature.
        if (computer is not null)
            container.TempSensors.Add(cpuSensor = new CpuPackageSensor(() => Volatile.Read(ref hardware)));
    }
    public void Update() => cpuSensor?.Update();
    public void Close()
    {
        cpuSensor = null;
        // Host calls Close on its UI thread. Run asynchronous shutdown outside its
        // synchronization context before synchronously waiting for completion.
        Task.Run(async () =>
        {
            if (mqtt is not null) await mqtt.DisposeAsync();
            if (pipe is not null) await pipe.DisposeAsync();
            if (diagnostics is not null) await diagnostics.DisposeAsync();
        }).GetAwaiter().GetResult();
        mqtt = null; pipe = null; diagnostics = null;
        stop?.Cancel(); polling?.GetAwaiter().GetResult(); polling = null; stop?.Dispose(); stop = null;
        computer?.Close(); computer = null;
        singleton?.Dispose(); singleton = null;
    }
    private static void Sample(IHardware item, List<HardwareSensorReading> values)
    {
        item.Update();
        foreach (var sensor in item.Sensors.Where(s => s.SensorType is SensorType.Temperature or SensorType.Fan or SensorType.Control or SensorType.Load or SensorType.Power))
            values.Add(new(sensor.Identifier.ToString(), item.Identifier.ToString(), sensor.Name, sensor.SensorType.ToString(), sensor.Value));
        foreach (var child in item.SubHardware) Sample(child, values);
    }
}

internal sealed class CpuPackageSensor(Func<HardwareTelemetry> read) : IPluginSensor
{
    public string Id => "FanControlBridge/CPU Package";
    public string Name => "CPU Package — MQTT bridge source";
    public float? Value { get; private set; }
    public void Update()
    {
        var sample = read();
        var age = DateTimeOffset.UtcNow - sample.SampledAt;
        var reading = HardwareTelemetryReader.Temperature(sample, true);
        Value = age >= TimeSpan.FromSeconds(-5) && age <= TimeSpan.FromSeconds(15)
            && reading.Celsius is double value ? (float)value : null;
    }
}
