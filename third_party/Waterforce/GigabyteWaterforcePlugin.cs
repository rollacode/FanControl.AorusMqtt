using FanControl.Plugins;
using FanControl.GigabyteWaterforce.Sensors;
using System.Text.Json;

namespace FanControl.GigabyteWaterforce;

public sealed class GigabyteWaterforcePlugin : IPlugin2
{
    private readonly IPluginLogger logger;
    private readonly string receiptDirectory;
    public GigabyteWaterforcePlugin(IPluginLogger logger) : this(logger, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControlMqtt")) { }
    internal GigabyteWaterforcePlugin(IPluginLogger logger, string receiptDirectory)
    { this.logger = logger; this.receiptDirectory = receiptDirectory; }
    public string Name => "AORUS Waterforce X360";
    private GigabyteWaterforceDevice? device;
    private CancellationTokenSource? stop;
    private Task? polling;
    private IPluginSensor[] sensors = [];
    private FanControlSensor? radiator;
    public void Initialize()
    {
        Close(); device = new();
        if (!device.Connect()) return;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControlMqtt");
        try
        {
            using var permission = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "waterforce-control.json")));
            using var trial = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "waterforce-radiator-2500-pass.json")));
            if (permission.RootElement.GetProperty("enabled").GetBoolean()
                && trial.RootElement.GetProperty("requestedCurveReadbackConfirmed").GetBoolean()
                && trial.RootElement.GetProperty("originalCurveReadbackRestored").GetBoolean()) radiator = new(device);
        }
        catch { radiator = null; }
        stop = new(); var cancellation = stop.Token; var currentDevice = device;
        polling = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                try { radiator?.ApplyNativeCommand(); currentDevice.Update(); }
                catch { try { currentDevice.ResetFan(); } catch { } }
                try
                {
                    var receiptPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControlMqtt", "waterforce-plugin-status.json");
                    Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
                    File.WriteAllText(receiptPath + ".new", JsonSerializer.Serialize(new { plugin = Name, hostProcessId = Environment.ProcessId, sampledAt = DateTimeOffset.UtcNow,
                        status = currentDevice.Status, hardwareControlEnabled = currentDevice.FanControlEnabled, coolingCommandsSent = currentDevice.CoolingCommandsSent }));
                    File.Move(receiptPath + ".new", receiptPath, true);
                }
                catch (IOException) { }
                try { await Task.Delay(1000, cancellation); } catch (OperationCanceledException) { break; }
            }
        });
    }
    public void Load(IPluginSensorsContainer container)
    {
        if (device is null) return;
        sensors = [new FanSpeedSensor(device), new PumpSpeedSensor(device), new LiquidTemperatureSensor(device)];
        container.FanSensors.Add(sensors[0]);
        container.FanSensors.Add(sensors[1]);
        container.TempSensors.Add(sensors[2]);
        if (radiator is not null) container.ControlSensors.Add(radiator);
    }
    public void Update() { foreach (var sensor in sensors) sensor.Update(); radiator?.Update(); } // Snapshot reads only, no HID I/O.
    public void Close()
    {
        sensors = [];
        var closingDevice = device;
        string? error = null;
        try
        {
            stop?.Cancel();
            try { polling?.GetAwaiter().GetResult(); }
            catch (Exception ex) { error = ex.Message; }
            try { closingDevice?.Dispose(); }
            catch (Exception ex) { error = ex.Message; }
        }
        finally
        {
            stop?.Dispose(); stop = null; polling = null;
            radiator = null; device = null;
        }
        // A restoration error must remain visible, but must not abort the host's
        // backend reload and leave every native sensor/control missing.
        if (error is not null)
        {
            try { logger.Log("Waterforce original-state restoration unconfirmed during Close: " + error); }
            catch { /* A logging failure must not abort host reload either. */ }
        }
        if (closingDevice is not null)
        {
            try
            {
                var path = Path.Combine(receiptDirectory, "waterforce-last-close.json");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(new { sampledAt = DateTimeOffset.UtcNow, originalStateRestorationConfirmed = error is null, error }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
