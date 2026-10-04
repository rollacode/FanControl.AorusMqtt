using HidSharp;

namespace FanControl.GigabyteWaterforce;

// Local hardening of attributed upstream status transport. Deployment starts read-only.
// No automatic calibration, E5/B6 pump writes or unverified Reset commands.
public sealed record WaterforceStatus(float? FanRpm, float? PumpRpm, float? FanDuty, float? CandidateLiquidTemperature, DateTimeOffset? SampledAt, string State);
public interface IWaterforceStatusTransport : IDisposable
{
    int InputLength { get; }
    int OutputLength { get; }
    void Write(byte[] report);
    int Read(byte[] report);
    void DrainPending() { }
}
public sealed class GigabyteWaterforceDevice : IDisposable
{
    private IWaterforceStatusTransport? transport;
    private readonly Func<IWaterforceStatusTransport> open;
    private WaterforceStatus status = Missing("Not sampled");
    private readonly object gate = new();
    private byte[]? originalFanCurve;
    private int? targetFanRpm;
    public byte OriginalFanMode { get; private set; }
    private byte originalPumpMode;
    private bool fanModeChanged;
    public bool FanControlEnabled { get { lock (gate) return originalFanCurve is not null; } }
    public int CoolingCommandsSent { get; private set; }
    public string? ControlError { get; private set; }
    public GigabyteWaterforceDevice(Func<IWaterforceStatusTransport>? open = null) => this.open = open ?? OpenHardware;
    public WaterforceStatus Status
    {
        get
        {
            var snapshot = Volatile.Read(ref status);
            return snapshot.SampledAt is not null && DateTimeOffset.UtcNow - snapshot.SampledAt > TimeSpan.FromSeconds(5)
                ? Missing("Stale device status") : snapshot;
        }
    }
    private static WaterforceStatus Missing(string state) => new(null, null, null, null, null, state);
    public bool Connect()
    {
        lock (gate)
        {
            try
            {
                transport?.Dispose(); transport = open();
                if (transport.InputLength is < 16 or > 1024 || transport.OutputLength is < 2 or > 1024) throw new InvalidDataException();
                return true;
            }
            catch { transport?.Dispose(); transport = null; Volatile.Write(ref status, Missing("HID unavailable or report sizes unsupported")); return false; }
        }
    }
    public bool Update()
    {
        lock (gate)
        {
            try
            {
                if (transport is null) return false;
                if (targetFanRpm is int target) WriteFanCurveLocked(FixedFanCurve(target));
                var request = new byte[transport.OutputLength]; request[0] = 0x99; request[1] = 0xDA;
                transport.Write(request);
                var reply = new byte[transport.InputLength]; var length = transport.Read(reply);
                Volatile.Write(ref status, Parse(reply.AsSpan(0, length)));
                return true;
            }
            catch { Volatile.Write(ref status, Missing("Status read failed; no stale values exposed")); return false; }
        }
    }
    public static WaterforceStatus Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 15 || packet[0] != 0x99 || packet[1] != 0xDA) throw new InvalidDataException("Unexpected status response");
        var fan = packet[2] | packet[3] << 8; var pump = packet[5] | packet[6] << 8;
        var candidate = packet[13] + packet[14] / 10f;
        if (fan > 6000 || pump > 6000 || packet[14] > 9 || candidate > 100) throw new InvalidDataException("Implausible status");
        return new(fan, pump, packet[8] <= 100 ? packet[8] : null, candidate, DateTimeOffset.UtcNow, "Measured 99 DA status");
    }
    // Call only after explicit owner approval of the bounded live trial or validated handover.
    // No mode/pump command is used. Existing customized mode must already be active.
    public byte[] ArmFanControl(bool allowModeChange = false)
    {
        lock (gate)
        {
            if (originalFanCurve is not null) throw new InvalidOperationException("Already armed");
            if (QueryLocked(0xDE)[2] != 2) throw new InvalidDataException("Expected X360 model");
            var modes = QueryLocked(0xDD);
            if (modes[2] != 1 && (!allowModeChange || modes[2] != 0)) throw new InvalidDataException("Customized fan mode required; mode changes are excluded");
            var curve = QueryLocked(0xD9, 1);
            if (curve[2] != 1 || curve[3] != 1) throw new InvalidDataException("Unexpected radiator curve channel");
            var restore = curve.AsSpan(0, 16).ToArray(); restore[1] = 0xE6;
            ValidateFanCurve(restore);
            originalFanCurve = restore; ControlError = null;
            OriginalFanMode = modes[2]; originalPumpMode = modes[3]; fanModeChanged = false;
            return curve.AsSpan(0, 16).ToArray();
        }
    }
    public void SwitchFanToCustomForApprovedTrial()
    {
        lock (gate)
        {
            if (originalFanCurve is null) throw new InvalidOperationException("Original curve not captured");
            if (OriginalFanMode == 1) return;
            // Mark intent before the first write, so failure still restores the original mode.
            fanModeChanged = true;
            WriteFanModeLocked(1);
            var observed = QueryLocked(0xDD);
            if (observed[2] != 1 || observed[3] != originalPumpMode) throw new InvalidDataException("Radiator-only mode switch not confirmed");
        }
    }
    private void WriteFanModeLocked(byte mode)
    {
        if (transport is null || originalFanCurve is null || mode > 1) throw new InvalidOperationException("Unsupported fan mode write");
        var request = new byte[transport.OutputLength]; request[0] = 0x99; request[1] = 0xE5; request[2] = 1; request[3] = mode;
        transport.Write(request); CoolingCommandsSent++;
    }
    public static int DutyToRpm(float duty)
    {
        if (!float.IsFinite(duty) || duty is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(duty));
        // The owner-selected lower bound is 400 RPM, verified by a bounded device trial. Zero maps to that lower bound.
        return Math.Clamp((int)Math.Round(duty * 25), 400, 2500);
    }
    public static byte[] FixedFanCurve(int rpm)
    {
        if (rpm is < 400 or > 2500) throw new ArgumentOutOfRangeException(nameof(rpm));
        var result = new byte[16]; result[0] = 0x99; result[1] = 0xE6; result[2] = 1; result[3] = 1;
        for (var i = 0; i < 4; i++) { result[4 + i * 3] = new byte[] { 0, 30, 50, 65 }[i]; result[5 + i * 3] = (byte)(rpm >> 8); result[6 + i * 3] = (byte)rpm; }
        return result;
    }
    private static void ValidateFanCurve(byte[] curve)
    {
        if (curve.Length != 16 || curve[0] != 0x99 || curve[1] != 0xE6 || curve[2] != 1 || curve[3] != 1) throw new InvalidDataException("Invalid fan curve packet");
        for (var i = 0; i < 4; i++)
        {
            var temperature = curve[4 + i * 3]; var rpm = curve[5 + i * 3] << 8 | curve[6 + i * 3];
            if (temperature > 110 || rpm is < 400 or > 2800 || (i > 0 && temperature <= curve[4 + (i - 1) * 3])) throw new InvalidDataException("Unsafe or malformed stored curve");
        }
    }
    private byte[] QueryLocked(byte command, byte channel = 0)
    {
        if (transport is null || transport.OutputLength < 16) throw new InvalidOperationException("Device unavailable");
        // Each HID handle can have old responses queued, including the same opcode.
        // A matching echo alone is insufficient evidence of the newly submitted state.
        transport.DrainPending();
        var request = new byte[transport.OutputLength]; request[0] = 0x99; request[1] = command; request[2] = channel;
        transport.Write(request);
        for (var i = 0; i < 4; i++)
        {
            var response = new byte[transport.InputLength]; var count = transport.Read(response);
            if (count >= 16 && response[0] == 0x99 && response[1] == command && (command != 0xD9 || response[2] == channel)) return response;
        }
        throw new InvalidDataException("Readback response not matched");
    }
    private void WriteFanCurveLocked(byte[] curve)
    {
        if (originalFanCurve is null || transport is null) throw new InvalidOperationException("Fan control not armed");
        ValidateFanCurve(curve);
        var request = new byte[transport.OutputLength]; curve.CopyTo(request, 0);
        transport.Write(request); CoolingCommandsSent++;
    }
    public void SetFanDuty(float value)
    {
        var rpm = DutyToRpm(value);
        lock (gate) { if (originalFanCurve is null) throw new InvalidOperationException("Fan control not armed"); targetFanRpm = rpm; }
    }
    public bool VerifyRequestedFanCurve()
    {
        lock (gate)
        {
            if (targetFanRpm is not int target || originalFanCurve is null) return false;
            var observed = QueryLocked(0xD9, 1); observed[1] = 0xE6;
            var modes = QueryLocked(0xDD);
            return observed.AsSpan(0, 16).SequenceEqual(FixedFanCurve(target)) && modes[2] == 1 && modes[3] == originalPumpMode;
        }
    }
    public void PauseFanWritesForFallbackTest() { lock (gate) targetFanRpm = null; }
    public void ResetFan()
    {
        lock (gate)
        {
            targetFanRpm = null;
            if (originalFanCurve is null) return;
            try
            {
                WriteFanCurveLocked(originalFanCurve);
                // Restore mode even if a following curve query fails; neither rollback depends on the other query.
                try { if (fanModeChanged) WriteFanModeLocked(OriginalFanMode); }
                finally { targetFanRpm = null; }
                var observed = QueryLocked(0xD9, 1); observed[1] = 0xE6;
                if (!observed.AsSpan(0, 16).SequenceEqual(originalFanCurve)) throw new InvalidDataException("Original curve restoration not confirmed");
                var modes = QueryLocked(0xDD);
                if (modes[2] != OriginalFanMode || modes[3] != originalPumpMode) throw new InvalidDataException("Original fan/pump modes not confirmed");
                originalFanCurve = null; ControlError = null;
                fanModeChanged = false;
            }
            catch (Exception ex) { ControlError = ex.Message; throw; }
        }
    }
    public void SetPumpDuty(float value) => throw new NotSupportedException("Pump control excluded");
    public void ResetPump() { } // No pump write is ever emitted.
    public void Dispose()
    {
        lock (gate)
        {
            try { ResetFan(); }
            finally { transport?.Dispose(); transport = null; Volatile.Write(ref status, Missing(ControlError ?? "Closed")); }
        }
    }
    private static IWaterforceStatusTransport OpenHardware()
    {
        var matches = DeviceList.Local.GetHidDevices(0x1044, 0x7A4D).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Expected exactly one Waterforce HID");
        return new HidStatusTransport(matches[0]);
    }
    private sealed class HidStatusTransport : IWaterforceStatusTransport
    {
        private readonly HidStream stream;
        public int InputLength { get; }
        public int OutputLength { get; }
        public HidStatusTransport(HidDevice device)
        {
            InputLength = device.GetMaxInputReportLength(); OutputLength = device.GetMaxOutputReportLength();
            stream = device.Open(); stream.ReadTimeout = 1500; stream.WriteTimeout = 500;
        }
        public void Write(byte[] report) => stream.Write(report);
        public int Read(byte[] report) => stream.Read(report, 0, report.Length);
        public void DrainPending()
        {
            var previous = stream.ReadTimeout;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                stream.ReadTimeout = 10;
                for (var i = 0; i < 32 && timer.ElapsedMilliseconds < 100; i++)
                {
                    try { var pending = new byte[InputLength]; var count = stream.Read(pending, 0, pending.Length); if (count == 0) break; }
                    catch (TimeoutException) { break; }
                }
            }
            finally { stream.ReadTimeout = previous; }
        }
        public void Dispose() => stream.Dispose();
    }
}
