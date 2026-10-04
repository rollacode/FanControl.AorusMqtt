using System.Diagnostics;
using FanControlBridge;
using FanControl.GigabyteWaterforce;

if (args.Length != 1 || args[0] != "--validate-and-enable")
{
    Console.Error.WriteLine("Explicit --validate-and-enable required. Stop Fan Control and competing cooler writers first. This tests the X360 radiator at 800RPM for eight seconds, restores its original state, and enables control only on success. No pump writes.");
    return 1;
}
var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControlMqtt");
Directory.CreateDirectory(directory);
var settingsPath = Path.Combine(directory, "settings.json");
var settings = Storage.ReadSettings(settingsPath);
if (!File.Exists(settingsPath) || !File.Exists(settings.RuntimePath)
    || Path.GetFileName(settings.RuntimePath) != "FanControl.exe"
    || !settings.Profiles.Select(p => p.Name).Order().SequenceEqual(new[] { "Balanced", "Night", "Performance" })
    || settings.Profiles.Any(p => !ConfigurationFingerprint.NativeConfigurationAvailable(p)))
{
    Console.Error.WriteLine("Configure the runtime and three valid native profiles before validation.");
    return 1;
}
foreach (var name in new[] { "FanControl", "GCC", "GBT_Cooler", "AsusFanControlService", "AorusLcdService" })
{
    var processes = Process.GetProcessesByName(name);
    try { if (processes.Length != 0) { Console.Error.WriteLine("A cooling host or competing writer is running; no device command sent."); return 1; } }
    finally { foreach (var process in processes) process.Dispose(); }
}
var samples = new List<WaterforceStatus>();
bool armed = false, restored = false, confirmed = false;
string? error = null;
using var device = new GigabyteWaterforceDevice();
try
{
    if (!device.Connect() || !device.Update()) throw new IOException("Fresh X360 status unavailable");
    samples.Add(device.Status);
    var original = device.ArmFanControl(allowModeChange:true); armed = true;
    Storage.Write(Path.Combine(directory, "waterforce-before-validation.json"), new { sampledAt=DateTimeOffset.UtcNow, rawOriginalCurve=Convert.ToHexString(original), originalFanMode=device.OriginalFanMode });
    device.SwitchFanToCustomForApprovedTrial();
    device.SetFanDuty(32);
    for(int i=0;i<8;i++)
    {
        if (!device.Update() || device.Status.FanRpm is not float rpm || rpm < 100) throw new IOException("Fresh running-radiator status unavailable during test");
        samples.Add(device.Status);
        await Task.Delay(1000);
    }
    confirmed = device.VerifyRequestedFanCurve();
    if (!confirmed || !samples.Skip(1).TakeLast(3).Any(s => s.FanRpm is float rpm && Math.Abs(rpm-800)<=200))
        throw new IOException("800RPM curve/readback or measured response not confirmed");
}
catch(Exception ex) { error=ex.Message; }
finally
{
    if(armed) { try { device.ResetFan(); restored=true; } catch(Exception ex) { error="Original-state restoration unconfirmed: "+ex.Message; } }
    // Legacy filename retained for plugin compatibility; requestedRpm records the real target.
    Storage.Write(Path.Combine(directory,"waterforce-radiator-2500-pass.json"), new { sampledAt=DateTimeOffset.UtcNow, requestedRpm=800, requestedCurveReadbackConfirmed=confirmed, originalCurveReadbackRestored=restored, samples, error });
}
if(error is not null || !restored || !confirmed)
{
    Console.Error.WriteLine(error ?? "Validation incomplete; control not enabled.");
    return 1;
}
var evidencePath=Path.Combine(directory,"native-mqtt-handover.json");
Storage.Write(evidencePath,new { sampledAt=DateTimeOffset.UtcNow, requestedRpm=800, requestedCurveReadbackConfirmed=confirmed, originalCurveReadbackRestored=restored });
Storage.Write(Path.Combine(directory,"waterforce-control.json"),new { enabled=true });
Storage.Write(settingsPath,settings with { LiveHandoverValidated=true, HandoverEvidencePath=evidencePath });
Console.WriteLine("PASS: radiator response/readback and original-state restoration confirmed; local control enabled. Start Fan Control as the same user. Pump commands: 0.");
return 0;
