using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FanControlBridge;

public record CpuPowerValues(Guid Scheme, uint Boost, uint Maximum, uint? MaximumClass1);
public interface ICpuPowerSettings
{
    CpuPowerValues ReadActive();
    void Write(CpuPowerValues values);
}
public record CpuPerformanceState(DateTimeOffset SampledAt, string? Profile, uint? Boost, uint? Maximum, string State);

// Only called by the plugin's CPU worker. Native callbacks queue curve values.
public sealed class CpuPerformancePolicy(ICpuPowerSettings power, string backupPath)
{
    private readonly Dictionary<Guid, CpuPowerValues> originals = Storage.Read(backupPath, Array.Empty<CpuPowerValues>()).ToDictionary(p => p.Scheme);
    public CpuPerformanceState Apply(string? profile, float? requested)
    {
        if (requested is float invalid && (!float.IsFinite(invalid) || invalid is < 0 or > 100)) throw new ArgumentOutOfRangeException(nameof(requested));
        if (profile is not ("Night" or "Performance" or "Balanced"))
        {
            var unchanged = power.ReadActive();
            return new(DateTimeOffset.UtcNow, null, unchanged.Boost, unchanged.Maximum, "Native profile temporarily unavailable; current CPU policy unchanged");
        }
        if (profile != "Night")
        {
            Restore();
            var normal = power.ReadActive();
            return new(DateTimeOffset.UtcNow, profile, normal.Boost, normal.Maximum, "Original CPU policy restored; Night restriction inactive");
        }
        var current = power.ReadActive();
        if (!originals.ContainsKey(current.Scheme))
        {
            Storage.Write(backupPath, originals.Values.Append(current).ToArray()); // Persist before the first policy write.
            originals.Add(current.Scheme, current);
        }
        // Night is the trigger. A disabled/missing native slider releases only
        // its additional percentage cap, never the mode's boost restriction.
        var maximum = (uint)Math.Round(requested ?? 100);
        var target = current with { Boost = 0, Maximum = maximum, MaximumClass1 = current.MaximumClass1 is null ? null : maximum };
        if (current != target) power.Write(target);
        var actual = power.ReadActive();
        if (actual != target) throw new InvalidOperationException("CPU power-policy readback does not match the requested Night limit");
        return new(DateTimeOffset.UtcNow, profile, actual.Boost, actual.Maximum, "Night: boost disabled; Windows maximum-performance policy read back; temperature ceiling requires load validation");
    }
    public void Restore()
    {
        foreach (var original in originals.Values.ToArray())
        {
            power.Write(original);
            originals.Remove(original.Scheme);
            Storage.Write(backupPath, originals.Values.ToArray());
        }
    }
}

public sealed class WindowsCpuPowerSettings : ICpuPowerSettings
{
    private static Guid processor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static Guid boost = new("be337238-0d82-4146-a960-4f3749d470c7");
    private static Guid maximum = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static Guid maximumClass1 = new("bc5038f7-23e0-4960-96da-33abaf5935ed");
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid group, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    private static void Check(uint code) { if (code != 0) throw new Win32Exception((int)code); }
    private static Guid Active()
    {
        Check(PowerGetActiveScheme(IntPtr.Zero, out var pointer));
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { LocalFree(pointer); }
    }
    private static uint Read(Guid scheme, Guid setting) { Check(PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref processor, ref setting, out var value)); return value; }
    public CpuPowerValues ReadActive()
    {
        var scheme = Active();
        var class1Code = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref processor, ref maximumClass1, out var class1);
        if (class1Code is not (0 or 2 or 1168)) Check(class1Code);
        return new(scheme, Read(scheme, boost), Read(scheme, maximum), class1Code == 0 ? class1 : null);
    }
    public void Write(CpuPowerValues values)
    {
        var scheme = values.Scheme;
        void WriteIndex(Guid setting, uint value) => Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref processor, ref setting, value));
        WriteIndex(boost, values.Boost); WriteIndex(maximum, values.Maximum);
        if (values.MaximumClass1 is uint class1) WriteIndex(maximumClass1, class1);
        // Re-apply only the currently active plan. Never select a different owner plan.
        if (Active() == scheme) Check(PowerSetActiveScheme(IntPtr.Zero, ref scheme));
        if (Read(scheme, boost) != values.Boost || Read(scheme, maximum) != values.Maximum
            || (values.MaximumClass1 is uint expected && Read(scheme, maximumClass1) != expected))
            throw new InvalidOperationException("CPU policy restoration/application readback mismatch");
    }
}
