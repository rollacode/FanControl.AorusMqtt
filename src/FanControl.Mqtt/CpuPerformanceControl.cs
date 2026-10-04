using FanControl.Plugins;
using FanControlBridge;

namespace FanControl.Mqtt;

internal sealed class CpuPerformanceControl : IPluginControlSensor, IDisposable
{
    public string Id => "CpuPerformance/Limit";
    public string Name => "CPU performance limit — Night only";
    public float? Value => Volatile.Read(ref state).Maximum;
    private readonly object gate = new();
    private float? requested;
    private readonly ManualResetEventSlim stopped = new();
    private readonly CpuPerformancePolicy policy;
    private readonly Settings settings;
    private readonly string receiptPath;
    private readonly IPluginLogger logger;
    private readonly Thread worker;
    private CpuPerformanceState state = new(default, null, null, null, "Not sampled");
    public CpuPerformanceControl(Settings settings, string directory, IPluginLogger logger)
    {
        this.settings = settings; this.logger = logger;
        policy = new(new WindowsCpuPowerSettings(), Path.Combine(directory, "cpu-performance-before-night.json"));
        receiptPath = Path.Combine(directory, "cpu-performance-status.json");
        worker = new Thread(Run) { IsBackground = true, Name = "FanControl CPU performance policy" };
        worker.Start();
    }
    public void Set(float value)
    {
        if (!float.IsFinite(value) || value is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(value));
        lock (gate) requested = value;
    }
    public void Reset() { lock (gate) requested = null; }
    public void Update() { } // Windows API writes never run in the host's sensor callback.
    private void Run()
    {
        string? lastError = null;
        while (!stopped.IsSet)
        {
            try
            {
                var profile = FanControlRuntime.ObserveConfiguration(settings);
                float? command;
                lock (gate) { if (profile is "Performance" or "Balanced") requested = null; command = requested; }
                var actual = policy.Apply(profile, command);
                Volatile.Write(ref state, actual);
                try { Storage.Write(receiptPath, actual); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                lastError = null;
            }
            catch (Exception ex)
            {
                var error = ex.GetType().Name + ": " + ex.Message;
                Volatile.Write(ref state, new(DateTimeOffset.UtcNow, null, null, null, error));
                if (error != lastError) { try { logger.Log("CPU performance policy: " + error); } catch { } lastError = error; }
            }
            stopped.Wait(1000);
        }
    }
    public void Dispose()
    {
        stopped.Set(); worker.Join();
        try { policy.Restore(); Storage.Write(receiptPath, new CpuPerformanceState(DateTimeOffset.UtcNow, null, null, null, "Plugin closed; original CPU policy restored")); }
        catch (Exception ex) { try { logger.Log("CPU policy restore unconfirmed: " + ex.Message); } catch { } }
        stopped.Dispose();
    }
}
