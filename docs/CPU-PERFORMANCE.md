# CPU performance in Night

The optional CPU control runs inside `FanControl.Mqtt.dll`. No separate service, tray application, HA automation, BIOS setting, voltage change or vendor tuning driver is required.

Set `cpuPerformanceControlEnabled` to `true` in the local plugin settings and restart Fan Control. It defaults to `false` for new installations. Keep the existing Performance, Balanced and Night profile mappings.

Night automatically disables boost when this optional feature is enabled. The `CPU performance limit — Night only` card is an optional additional CPU percentage limit: enable it and set its manual value to 100 for full non-boost performance. No extra temperature curve or RPM pairing is required. Do not run fan calibration on this CPU policy control. In Performance and Balanced, disable and hide this card.

When the active native configuration is Night, the plugin disables Windows processor boost even if the card is disabled or absent. A disabled card releases only its additional percentage cap, defaulting the maximum to 100. A value of 100 means the full non-boost processor performance limit. Lower values also cap Windows maximum processor state, including the second efficiency class when present. Performance and Balanced release the restriction and restore the exact Windows values captured before Night. On a system whose original boost mode was enabled, this re-enables Turbo Boost; a previously disabled owner policy is preserved.

The plugin changes only the active plan's AC processor boost and maximum-performance indexes. It does not select another power plan, alter sleep/display settings, or configure DC/battery behavior. All power-plan identifiers and backups stay in local application data.

If native configuration observation temporarily fails under heavy load, the current CPU policy remains unchanged. An unavailable observation is not interpreted as a daytime mode. The primary native IPC query works while the UI is closed to the tray. Only an observed Performance/Balanced configuration or normal plugin close restores the original policy. To disable the Night CPU feature entirely, set `cpuPerformanceControlEnabled` to `false` and restart the host.

`cpu-performance-before-night.json` persists the original policy before any write; `cpu-performance-status.json` records actual Windows policy readback. An unchanged request does not repeatedly reapply the plan. Native sensor callbacks only queue intent; Windows writes run on one plugin worker. Normal plugin close restores the original CPU policy. After an abrupt process loss, the Windows restriction can remain until plugin startup or manual restoration; abrupt-loss and reboot recovery have not been live-tested.

This is a CPU power-policy control, not a guaranteed 90°C thermal limit. The owner's load test with boost disabled was successful, but workloads, cooling and ambient temperature affect the result. Fan curves remain independently editable in Fan Control.

Windows API references: [boost mode](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/options-for-perf-state-engine-perfboostmode), [maximum processor performance](https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/options-for-perf-state-engine-maxperformance), [read AC policy](https://learn.microsoft.com/en-us/windows/win32/api/powrprof/nf-powrprof-powerreadacvalueindex).
