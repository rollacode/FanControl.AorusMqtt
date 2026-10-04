using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;

namespace FanControlBridge;

public sealed class FanControlRuntime(Settings settings) : ICoolingRuntime
{
    private delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr count, StringBuilder text, uint flags, uint timeout, out IntPtr result);
    public static string? ObserveConfiguration(Settings settings)
    {
        var running = Process.GetProcessesByName("FanControl");
        try
        {
            if (running.Length != 1 || !string.Equals(running[0].MainModule?.FileName, Path.GetFullPath(settings.RuntimePath), StringComparison.OrdinalIgnoreCase)) return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var pid);
                if (pid == running[0].Id)
                {
                    var title = new StringBuilder(512);
                    // Same-process GetWindowText can synchronously wait on the UI thread.
                    // Close joins diagnostic workers on that thread; bound this title query.
                    if (running[0].Id == Environment.ProcessId)
                    {
                        if (SendMessageTimeout(window, 0x000D, (IntPtr)title.Capacity, title, 2, 100, out _) == IntPtr.Zero) return true;
                    }
                    else GetWindowText(window, title, title.Capacity);
                    foreach (var profile in settings.Profiles)
                        if (TitleMatchesConfiguration(title.ToString(), profile.ConfigPath)) names.Add(profile.Name);
                }
                return true;
            }, IntPtr.Zero);
            return names.Count == 1 ? names.Single() : null;
        }
        catch { return null; }
        finally { foreach (var process in running) process.Dispose(); }
    }
    public const string HardwareLockMessage = "Live control locked: approved handover and safety validation required";
    public static bool TitleMatchesConfiguration(string title, string path) =>
        title.StartsWith("Fan Control V", StringComparison.Ordinal) && title.EndsWith(" (" + Path.GetFileName(path) + ")", StringComparison.OrdinalIgnoreCase);
    private static Submission Rejected(string message) => new(false, message, MayHaveApplied: false);
    public async Task<Submission> SubmitAsync(Profile profile)
    {
        if (!settings.LiveHandoverValidated || !File.Exists(settings.HandoverEvidencePath))
            return Rejected(HardwareLockMessage);
        foreach (var writerName in new[] { "AsusFanControlService", "GCC", "AorusLcdService" })
        {
            var competing = Process.GetProcessesByName(writerName);
            try { if (competing.Length != 0) return Rejected("Competing vendor cooling writer detected; handover required"); }
            finally { foreach (var process in competing) process.Dispose(); }
        }
        if (!File.Exists(settings.RuntimePath) || Path.GetFileName(settings.RuntimePath) != "FanControl.exe")
            return Rejected("Configured Fan Control executable unavailable");
        if (!File.Exists(profile.ConfigPath) || new FileInfo(profile.ConfigPath).Length > 4 * 1024 * 1024)
            return Rejected("Validated configuration unavailable or oversized");
        if (!ConfigurationFingerprint.NativeConfigurationAvailable(profile))
            return Rejected("Saved native Fan Control configuration unavailable or invalid");
        if (settings.Profiles.Count(p => string.Equals(Path.GetFileName(p.ConfigPath), Path.GetFileName(profile.ConfigPath), StringComparison.OrdinalIgnoreCase)) != 1)
            return Rejected("Configuration basenames must be unique for native observation");
        var running = Process.GetProcessesByName("FanControl");
        try
        {
            if (running.Length != 1 || !string.Equals(running[0].MainModule?.FileName, Path.GetFullPath(settings.RuntimePath), StringComparison.OrdinalIgnoreCase))
                return Rejected("Exactly one matching Fan Control instance must already be running; bridge will not launch a writer");
        }
        finally { foreach (var process in running) process.Dispose(); }
        var info = new ProcessStartInfo(settings.RuntimePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(settings.RuntimePath)! };
        info.ArgumentList.Add("-c"); info.ArgumentList.Add(Path.GetFullPath(profile.ConfigPath));
        using var sender = Process.Start(info) ?? throw new InvalidOperationException();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await sender.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { return new(false, "CLI did not finish within ten seconds; application unknown. No cooling process was killed."); }
        if (sender.ExitCode != 0) return new(false, "CLI returned failure; application unknown");
        // OS window metadata observes the runtime independently of the requested command.
        // It acknowledges the loaded configuration only, never all physical fan outputs.
        var until = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < until)
        {
                if (ObserveConfiguration(settings) == profile.Name)
                    return new(true, "Loaded Fan Control configuration observed in native window title; individual hardware outputs remain separately monitored",
                        profile.Name, ObservationCode: "runtime_config_observed");
            await Task.Delay(200);
        }
        return new(true, "CLI accepted configuration; native observation unavailable. Applied configuration and hardware outputs remain unverified");
    }
}
