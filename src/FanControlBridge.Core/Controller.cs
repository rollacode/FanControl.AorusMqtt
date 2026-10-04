namespace FanControlBridge;

public record Submission(bool Accepted, string Message, string? VerifiedProfile = null, bool MayHaveApplied = true, string ObservationCode = "verified");
public interface ICoolingRuntime { Task<Submission> SubmitAsync(Profile profile); }

public sealed class Controller(string statePath, Settings settings, ICoolingRuntime runtime)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private BridgeState state = LoadState(statePath);
    private static BridgeState LoadState(string path)
    {
        var loaded = Storage.Read(path, new BridgeState());
        string? Canonical(string? name) => name switch { "Normal" => "Performance", "Silent" or "Quiet" => "Night", _ => name };
        // Legacy Night overrides are retired. Restart never restores or guesses a mode.
        return loaded with { ObservedProfile = null, ObservedAt = null, NightActive = false, PreviousProfile = null,
            RequestedProfile = Canonical(loaded.RequestedProfile) };
    }
    public async Task<BridgeState> StatusAsync()
    { await gate.WaitAsync(); try { return state; } finally { gate.Release(); } }
    public Profile[] Profiles => settings.Profiles.ToArray();
    public async Task ObserveRuntimeAsync(string? profile)
    {
        await gate.WaitAsync();
        try
        {
            if (profile is null) { state = state with { ObservedProfile = null, ObservedAt = null }; return; }
            Find(profile);
            var changed = state.ObservedProfile != profile || state.Pending;
            var next = state with { ObservedProfile = profile, ObservedAt = DateTimeOffset.UtcNow, Pending = false,
                NightActive = false, PreviousProfile = null };
            if (changed) Save(next with { OutcomeCode = "runtime_config_observed", Outcome = "Loaded native configuration observed; physical outputs monitored separately" });
            else state = next;
        }
        finally { gate.Release(); }
    }
    private void Save(BridgeState next) { Storage.Write(statePath, next); state = next; }
    private Profile Find(string name) => settings.Profiles.SingleOrDefault(p => p.Name == name) ?? throw new InvalidOperationException("Unknown profile");
    public async Task<BridgeState> ConfirmObservedAsync(string profile)
    {
        await gate.WaitAsync(); try
        {
            Find(profile);
            Save(state with { ObservedProfile = profile, ObservedAt = DateTimeOffset.UtcNow, Pending = false,
                NightActive = false, PreviousProfile = null, OutcomeCode = "operator_observed",
                Outcome = "Operator observed in Fan Control; hardware outputs not independently verified" });
            return state;
        } finally { gate.Release(); }
    }
    public async Task<BridgeState> CommandAsync(string command, string? profile = null)
    {
        await gate.WaitAsync(); try
        {
            if (command != "select") throw new InvalidOperationException("Only direct profile selection is supported");
            var selected = Find(profile ?? throw new InvalidOperationException("Profile required"));
            if (state.Pending) throw new InvalidOperationException("Unresolved submission; reconcile active Fan Control configuration first");
            if (state.ObservedProfile == selected.Name && state.ObservedAt is not null && DateTimeOffset.UtcNow - state.ObservedAt < TimeSpan.FromMinutes(2)) return state;
            var before = state;
            Save(state with { LastCommand = command, RequestedProfile = selected.Name, ObservedProfile = null, ObservedAt = null,
                NightActive = false, PreviousProfile = null, Pending = true, OutcomeCode = "pending", Outcome = "Submission pending; applied profile unknown" });
            Submission result;
            try { result = await runtime.SubmitAsync(selected); }
            catch { result = new(false, "Runtime submission failed; applied profile unknown"); }
            if (!result.Accepted && !result.MayHaveApplied)
                Save(before with { RequestedProfile = selected.Name, LastCommand = command,
                    OutcomeCode = result.Message == FanControlRuntime.HardwareLockMessage ? "hardware_locked" : "preflight_rejected", Outcome = result.Message });
            else
                Save(state with { Pending = !result.Accepted || result.VerifiedProfile is null,
                    OutcomeCode = !result.Accepted ? "runtime_failure" : result.VerifiedProfile is null ? "submitted_unverified" : result.ObservationCode,
                    Outcome = result.Message, ObservedProfile = result.VerifiedProfile,
                    ObservedAt = result.VerifiedProfile is null ? null : DateTimeOffset.UtcNow });
            return state;
        } finally { gate.Release(); }
    }
}
