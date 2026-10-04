using FanControl.GigabyteWaterforce;
using FanControl.GigabyteWaterforce.Sensors;

var assertions = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); assertions++; }
var transport = new FakeTransport();
using var device = new GigabyteWaterforceDevice(() => transport);
Check(device.Connect() && device.Update(), "Valid status transaction");
Check(device.Status.FanRpm == 1320 && device.Status.PumpRpm == 2183, "Actual little-endian layout");
foreach (var value in new[] { 0f, 100f, float.NaN }) { try { device.SetFanDuty(value); } catch (InvalidOperationException) { } catch (ArgumentOutOfRangeException) { } }
device.ResetFan(); device.ResetPump();
Check(transport.Writes.Count == 1 && transport.Writes.All(p => p[0] == 0x99 && p[1] == 0xDA), "No control/pump/calibration writes");
transport.InvalidHeader = true;
Check(!device.Update() && device.Status.FanRpm is null && device.Status.PumpRpm is null, "Invalid echo clears stale measurements");
transport.InvalidHeader = false; transport.Fail = true;
Check(!device.Update() && device.Status.FanRpm is null, "Transport failure exposes unavailable");
device.Dispose(); Check(device.Status.FanRpm is null, "Close clears current measurements");
Check(GigabyteWaterforceDevice.DutyToRpm(0) == 400 && GigabyteWaterforceDevice.DutyToRpm(100) == 2500, "Calibration cannot stop fans or exceed bounded maximum");
Check(GigabyteWaterforceDevice.DutyToRpm(16) == 400 && GigabyteWaterforceDevice.DutyToRpm(24) == 600 && GigabyteWaterforceDevice.DutyToRpm(32) == 800, "Native low requests map to verified 400/600/800RPM targets");
var lowTransport = new FakeTransport();
using (var lowDevice = new GigabyteWaterforceDevice(() => lowTransport))
{
    Check(lowDevice.Connect(), "Low RPM fake transport connects");
    lowDevice.ArmFanControl(); lowDevice.SetFanDuty(16); lowDevice.Update();
    Check(lowDevice.VerifyRequestedFanCurve() && lowTransport.Curve[5] == 1 && lowTransport.Curve[6] == 144, "400RPM curve is encoded and confirmed on radiator channel");
    lowDevice.ResetFan();
    using var recapture = new GigabyteWaterforceDevice(() => lowTransport);
    lowTransport.Curve = GigabyteWaterforceDevice.FixedFanCurve(400); lowTransport.Curve[1] = 0xD9;
    Check(recapture.Connect(), "Low curve recapture connects");
    recapture.ArmFanControl(); recapture.SetFanDuty(32); recapture.Update(); recapture.ResetFan();
    Check(lowTransport.Curve[5] == 1 && lowTransport.Curve[6] == 144, "An original 400RPM curve can be captured and restored");
}
var controlledTransport = new FakeTransport();
using var controlled = new GigabyteWaterforceDevice(() => controlledTransport);
Check(controlled.Connect(), "Control test connected to fake device");
var backup = controlled.ArmFanControl();
Check(controlled.CoolingCommandsSent == 0 && backup[1] == 0xD9, "Arming only reads model/mode/curve, never writes cooling");
controlled.SetFanDuty(88); Check(controlled.Update(), "Queued radiator command is applied off UI callback");
Check(controlled.VerifyRequestedFanCurve(), "Target curve and active mode confirmed by independent device readback");
Check(controlledTransport.Drains >= 5, "Readback queries discard queued replies before sending the new request");
var written = controlledTransport.Writes.Single(p => p[1] == 0xE6);
Check(written[2] == 1 && written[3] == 1 && written[5] == 8 && written[6] == 152, "2200 RPM big endian goes to radiator channel only");
controlled.ResetFan();
Check(!controlled.FanControlEnabled && controlledTransport.Curve.AsSpan(0,16).SequenceEqual(backup), "Reset restores full original curve and verifies native readback");
Check(controlledTransport.Writes.All(p => p[1] is 0xDA or 0xDE or 0xDD or 0xD9 or 0xE6), "No pump/mode/commit command exists in radiator path");
var modeTransport = new FakeTransport { FanMode = 0 };
using var modeDevice = new GigabyteWaterforceDevice(() => modeTransport);
Check(modeDevice.Connect(), "Mode test fake connected");
modeDevice.ArmFanControl(allowModeChange: true);
Check(modeTransport.Writes.All(p => p[1] != 0xE5), "Capture persists original state before any mode mutation");
modeDevice.SwitchFanToCustomForApprovedTrial();
Check(modeTransport.FanMode == 1 && modeTransport.Writes.Where(p => p[1] == 0xE5).All(p => p[2] == 1), "Custom mode changes radiator channel only");
modeDevice.ResetFan();
Check(modeTransport.FanMode == 0 && !modeDevice.FanControlEnabled, "Original mode restored with original curve");
var sensorTransport = new FakeTransport();
using var sensorDevice = new GigabyteWaterforceDevice(() => sensorTransport);
Check(sensorDevice.Connect(), "Curve forwarding fake device connected");
var sensorBackup = Path.Combine(Path.GetTempPath(), "WaterforceSensorTest-" + Guid.NewGuid());
try
{
    var sensor = new FanControlSensor(sensorDevice, sensorBackup);
    sensor.Set(48); sensor.ApplyNativeCommand(); sensorDevice.Update();
    Check(sensorDevice.VerifyRequestedFanCurve() && sensorTransport.Curve[5] == 4 && sensorTransport.Curve[6] == 176, "Native 48 percent forwards 1200RPM without CPU file or 85C override");
    sensor.Update(); sensor.ApplyNativeCommand(); sensorDevice.Update();
    Check(sensorDevice.VerifyRequestedFanCurve() && sensorTransport.Curve[5] == 4 && sensorTransport.Curve[6] == 176, "Unchanged native curve command remains unchanged without a maximum-speed override");
    sensor.Reset(); sensor.ApplyNativeCommand();
    Check(!sensorDevice.FanControlEnabled && sensorTransport.Curve.AsSpan(0,16).SequenceEqual(Convert.FromHexString("99D901013003C643055B5308626309C4")), "Native Reset restores original device curve without a fixed maximum command");
}
finally { if (Directory.Exists(sensorBackup)) Directory.Delete(sensorBackup, true); }
Console.WriteLine($"PASS: {assertions} Waterforce transport assertions; fake HID only, no live writes.");

sealed class FakeTransport : IWaterforceStatusTransport
{
    public int InputLength => 256;
    public int OutputLength => 256;
    public bool InvalidHeader; public bool Fail;
    public List<byte[]> Writes = [];
    public byte[] Curve = Convert.FromHexString("99D901013003C643055B5308626309C4");
    private byte lastCommand;
    public byte FanMode = 1;
    public int Drains;
    public void DrainPending() => Drains++;
    public void Write(byte[] report) { if (Fail) throw new IOException(); Writes.Add(report.ToArray()); lastCommand = report[1]; if (lastCommand == 0xE5 && report[2] == 1) FanMode = report[3]; if (lastCommand == 0xE6) { Curve = report.AsSpan(0,16).ToArray(); Curve[1] = 0xD9; } }
    public int Read(byte[] report)
    {
        if (lastCommand == 0xD9) { Curve.CopyTo(report, 0); return 256; }
        report[0] = 0x99; report[1] = InvalidHeader ? (byte)0xE6 : lastCommand;
        if (lastCommand == 0xDE) { report[2] = 2; return 256; }
        if (lastCommand == 0xDD) { report[2] = FanMode; return 256; }
        report[2] = 0x28; report[3] = 0x05; report[5] = 0x87; report[6] = 0x08; report[13] = 41;
        return 256;
    }
    public void Dispose() { }
}
