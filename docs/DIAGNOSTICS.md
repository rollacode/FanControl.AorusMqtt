# Local diagnostics

The same-user diagnostic pipe accepts exactly `status\n` with a bounded deadline. It reports a snapshot and cannot change modes, expose credentials, pass shell commands or read arbitrary paths. No HTTP/Bearer endpoint or TCP backdoor is included.

```powershell
dotnet run --project tools/FanControlBridge.Probe -c Release
```

Snapshots contain requested/observed mode, pending/outcome, sensor source/freshness, detected competing processes, native runtime presence, available native configurations, live gate, control readiness and MQTT transport state. Configuration availability is not an old hash comparison. No credentials or arbitrary file contents are returned.

CPU Package, motherboard CPU and GPU are separate readings. Sensor IDs and ages come from actual measurements. Missing or stale readings are marked unavailable. The Waterforce candidate liquid reading remains unverified.

The optional fixed NVIDIA query is temperature-only. Separate status-only HID monitoring uses 99 DA; it does not send radiator/pump control commands. Shared HID access can cause response contention, so do not launch duplicate probes while the cooling host is using that device.

Telemetry and MQTT run inside Fan Control. The standalone hardware/tray applications and live control trial tool have been removed; the remaining helper provides credential setup and explicit diagnostics.
