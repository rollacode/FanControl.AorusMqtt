using FanControl.Plugins;
using System.Text.Json;

namespace FanControl.GigabyteWaterforce.Sensors;

// Only registered after validated handover. Host callbacks queue intent; HID runs on the polling worker.
public class FanControlSensor : IPluginControlSensor2
{
    private readonly GigabyteWaterforceDevice _device;
    private readonly object gate = new();
    private readonly string backupDirectory;
    private float requestedDuty = 100;
    private bool requested;

    public string  Id                => "GigabyteWaterforce/FanControl";
    public string  Name              => "Waterforce X360 — CPU radiator control";
    public float?  Value             { get; private set; }
    public string? PairedFanSensorId => "GigabyteWaterforce/FanRpm";

    public FanControlSensor(GigabyteWaterforceDevice device, string? backupDirectory = null)
    {
        _device = device;
        this.backupDirectory = backupDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControlMqtt");
    }

    public void Update()
    {
        Value = _device.Status.FanRpm is float rpm ? Math.Clamp(rpm / 25, 0, 100) : null;
    }
    public void Set(float value)
    {
        _ = GigabyteWaterforceDevice.DutyToRpm(value); // Reject NaN and out-of-range input before queueing.
        lock (gate) { requestedDuty = value; requested = true; }
    }
    public void Reset() { lock (gate) { requested = false; requestedDuty = 100; } }
    public void ApplyNativeCommand()
    {
        float duty; bool active;
        lock (gate) { duty = requestedDuty; active = requested; }
        if (!active) { if (_device.FanControlEnabled) _device.ResetFan(); return; }
        if (!_device.FanControlEnabled)
        {
            var backup = _device.ArmFanControl(allowModeChange: true);
            Directory.CreateDirectory(backupDirectory);
            File.WriteAllText(Path.Combine(backupDirectory, "waterforce-fan-before-control.json"), JsonSerializer.Serialize(new { sampledAt = DateTimeOffset.UtcNow, rawOriginalCurve = Convert.ToHexString(backup), originalFanMode = _device.OriginalFanMode }));
            _device.SwitchFanToCustomForApprovedTrial();
        }
        _device.SetFanDuty(duty);
    }
}

public class LiquidTemperatureSensor : IPluginSensor
{
    private readonly GigabyteWaterforceDevice _device;

    public string Id   => "GigabyteWaterforce/LiquidTemp";
    public string Name => "Waterforce — candidate coolant (unverified)";
    public float? Value { get; private set; }

    public LiquidTemperatureSensor(GigabyteWaterforceDevice device) => _device = device;

    public void Update() => Value = _device.Status.CandidateLiquidTemperature;
}

public class FanSpeedSensor : IPluginSensor
{
    private readonly GigabyteWaterforceDevice _device;

    public string Id   => "GigabyteWaterforce/FanRpm";
    public string Name => "Waterforce X360 — radiator fans";
    public float? Value { get; private set; }

    public FanSpeedSensor(GigabyteWaterforceDevice device) => _device = device;

    public void Update() => Value = _device.Status.FanRpm;
}

public class PumpSpeedSensor : IPluginSensor
{
    private readonly GigabyteWaterforceDevice _device;

    public string Id   => "GigabyteWaterforce/PumpRpm";
    public string Name => "Waterforce X360 — pump";
    public float? Value { get; private set; }

    public PumpSpeedSensor(GigabyteWaterforceDevice device) => _device = device;

    public void Update() => Value = _device.Status.PumpRpm;
}
