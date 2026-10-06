using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Reflection;

namespace FanControlBridge;

public sealed class FanControlRuntime(Settings settings) : ICoolingRuntime
{
    private static readonly object ipcGate = new();
    private static object? ipcClient;
    // Fan Control supplies this public IPC API. Resolve it from the host rather
    // than redistributing or embedding its proprietary runtime assemblies.
    private static string? ObserveIpcConfiguration(Settings settings)
    {
        try
        {
            object client;
            lock (ipcGate)
            {
                if (ipcClient is null)
                {
                    var assembly = Assembly.Load("FanControl.IPC");
                    ipcClient = assembly.GetType("FanControl.IPC.IPCFactory", true)!.GetMethod("GetFanControlClient", Type.EmptyTypes)!.Invoke(null, null);
                }
                client = ipcClient!;
            }
            var method = client.GetType().GetMethods().Single(m => m.Name == "ListAvailableConfigsAsync" && m.GetParameters().Length == 4);
            var request = Activator.CreateInstance(method.GetParameters()[0].ParameterType);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            // The native named-pipe BlockingUnaryCall can remain in Task.Result
            // after its deadline/cancellation. Own the async call lifetime and
            // independently bound waiting; dispose its pipe on every outcome.
            var call = method.Invoke(client, [request, null, DateTime.UtcNow.AddMilliseconds(500), cancellation.Token]);
            var reply = AwaitNativeResponse(call, TimeSpan.FromMilliseconds(500));
            if (reply is null) return null;
            var current = reply.GetType().GetProperty("CurrentConfig")!.GetValue(reply) as string;
            var folder = reply.GetType().GetProperty("ConfigFolder")!.GetValue(reply) as string;
            return MatchNativeConfiguration(current, folder, settings.Profiles);
        }
        catch { return null; }
    }
    public static bool WaitForNativeResponse(Task response, TimeSpan timeout)
    {
        // WaitAsync abandons its wait on timeout, not the underlying RPC.
        // Observe a later transport fault as well: otherwise the host's global
        // UnobservedTaskException handler shows an error dialog during GC.
        ObserveNativeFailure(response);
        try { response.WaitAsync(timeout).GetAwaiter().GetResult(); return true; }
        catch { return false; }
    }
    private static void ObserveNativeFailure(Task task) =>
        _ = task.ContinueWith(failed => { _ = failed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    public static object? AwaitNativeResponse(object? call, TimeSpan timeout)
    {
        using var lifetime = call as IDisposable;
        if (call?.GetType().GetProperty("ResponseHeadersAsync")?.GetValue(call) is Task headers)
            ObserveNativeFailure(headers);
        if (call?.GetType().GetProperty("ResponseAsync")?.GetValue(call) is not Task response || !WaitForNativeResponse(response, timeout)) return null;
        return response.GetType().GetProperty("Result")?.GetValue(response);
    }
    public static string? MatchNativeConfiguration(string? current, string? folder, Profile[] profiles)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return null;
            var actualPath = Path.GetFullPath(Path.IsPathRooted(current) ? current : Path.Combine(folder, current));
            var matches = profiles.Where(p => p.Name is "Performance" or "Balanced" or "Night")
                .Where(p => !string.IsNullOrWhiteSpace(p.ConfigPath) && string.Equals(Path.GetFullPath(p.ConfigPath), actualPath, StringComparison.OrdinalIgnoreCase)).ToArray();
            return matches.Length == 1 ? matches[0].Name : null;
        }
        catch { return null; }
    }
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
            var nativeProfile = ObserveIpcConfiguration(settings);
            if (nativeProfile is not null) return nativeProfile;
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
        // The native CLI may open another full host when its IPC endpoint is
        // broken. Do not launch a configuration command against that condition.
        if (ObserveIpcConfiguration(settings) is null)
            return Rejected("Fan Control native command transport unavailable; restart the existing host before switching configurations");
        var info = new ProcessStartInfo(settings.RuntimePath) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(settings.RuntimePath)! };
        info.ArgumentList.Add("-c"); info.ArgumentList.Add(Path.GetFullPath(profile.ConfigPath));
        using var sender = Process.Start(info) ?? throw new InvalidOperationException();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await sender.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { return new(false, "CLI did not finish within ten seconds; application unknown. No cooling process was killed."); }
        if (sender.ExitCode != 0) return new(false, "CLI returned failure; application unknown");
        // Native IPC (with window-title fallback) observes the loaded runtime independently of the requested command.
        // It acknowledges the loaded configuration only, never all physical fan outputs.
        var until = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < until)
        {
                if (ObserveConfiguration(settings) == profile.Name)
                    return new(true, "Loaded Fan Control configuration observed from native runtime; individual hardware outputs remain separately monitored",
                        profile.Name, ObservationCode: "runtime_config_observed");
            await Task.Delay(200);
        }
        return new(true, "CLI accepted configuration; native observation unavailable. Applied configuration and hardware outputs remain unverified");
    }
}
