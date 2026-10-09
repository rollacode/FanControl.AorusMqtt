# Local diagnostics

The same-user diagnostic pipe accepts exactly `status\n` with a bounded deadline. It reports a snapshot and cannot change modes, expose credentials, pass shell commands or read arbitrary paths. No HTTP/Bearer endpoint or TCP backdoor is included.

```powershell
dotnet run --project tools/FanControlBridge.Probe -c Release
```

Snapshots contain requested/observed mode, pending/outcome, sensor source/freshness, detected competing processes, native runtime presence, available native configurations, live gate, control readiness and MQTT transport state. Configuration availability is not an old hash comparison. No credentials or arbitrary file contents are returned.

CPU Package, motherboard CPU and GPU are separate readings. Sensor IDs and ages come from actual measurements. Missing or stale readings are marked unavailable. The Waterforce candidate liquid reading remains unverified.

The optional fixed NVIDIA query is temperature-only. Separate status-only HID monitoring uses 99 DA; it does not send radiator/pump control commands. Shared HID access can cause response contention, so do not launch duplicate probes while the cooling host is using that device.

Telemetry and MQTT run inside Fan Control. The standalone hardware/tray applications and live control trial tool have been removed; the remaining helper provides credential setup and explicit diagnostics.

## Native IPC stalls

A live broker connection with fresh sensors but no observed profile can be a native IPC failure rather than an MQTT outage. Compare `make status` and the CPU receipt timestamps. An actual stalled host stack showed the CPU worker and diagnostic poller blocked in `GrpcDotNetNamedPipes.NamedPipeChannel.BlockingUnaryCall` / `ListAvailableConfigs`, even though the caller supplied a deadline and cancellation.

The observer and maintenance helper now use `ListAvailableConfigsAsync` / `ExitAsync`. They independently bound response waiting and dispose every async call on success, failure and timeout. Disposal requests transport cancellation; its completion is owned by the host library. Response and header faults are observed even when they arrive after our wait times out, preventing abandoned RPC exceptions from reaching the host's global `UnobservedTaskException` error dialog. Regression tests simulate a response that never completes or honors cancellation, and collect a response faulting after timeout to verify it cannot escape into that handler. A timeout still means unknown observed mode; no mode is guessed from the requested profile or CACHE. Night CPU policy continues to hold its current values while observation is unknown.

Install a rebuilt plugin using `./scripts/maintenance.ps1 -Action Install -Force` if an old host is already wedged. This makes a backup and restarts only the configured native runtime; it does not use mouse or window automation. Verify fresh observed mode, CPU receipt and broker state after restart. A short successful check does not establish overnight stability.

A later live failure exposed a cancellation race in the host's transport: `PayloadQueue.SetCanceled` waited for a monitor while `MessageReader.MoveNext` and `ClientConnectionContext.HandleTrailers` waited for cancellation callbacks. The UI and MQTT remained alive, but an independent elevated native IPC reader also failed. The bridge now supplies no transport deadline or cancellable token for its fixed read/exit RPCs. Its own bounded `WaitAsync` deadline remains in effect, and call disposal still requests ordinary protocol cancellation after timeout. This avoids arming the transport callback race without removing the caller's timeout or launching duplicate hosts. Unit tests cover both the noncancellable invocation and independently bounded stalled response. A host already affected by the race must be restarted after installing the fix.
