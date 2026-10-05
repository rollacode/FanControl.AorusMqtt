# Terminal maintenance

Run commands from the repository root. GNU Make is optional; the PowerShell commands work on their own with Windows PowerShell 5.1 or PowerShell 7. Mutating actions request normal Windows UAC for the same interactive account. Status and backup do not need elevation.

| Task | PowerShell | Optional Make shortcut |
| --- | --- | --- |
| Fresh mode, MQTT readiness and CPU policy | `./scripts/maintenance.ps1 -Action Status` | `make status` |
| Save a local backup | `./scripts/maintenance.ps1 -Action Backup` | `make backup` |
| Build plugins, helper and run tests | `./scripts/build.ps1` | `make build` |
| Restart through native IPC | `./scripts/maintenance.ps1 -Action Restart` | `make restart` |
| Finish restarting a hung host | `./scripts/maintenance.ps1 -Action Restart -Force` | `make restart-force` |
| Install the built MQTT plugin and restart | `./scripts/maintenance.ps1 -Action Install` | `make install` |
| Repair the existing Night CPU card | `./scripts/maintenance.ps1 -Action RepairNightCpu` | `make repair-night-cpu` |

Add `-Force` to Install, Restore or RepairNightCpu when the host cannot finish its normal exit. Installation requires the build output under `.artifacts/mqtt-plugin`. If the separate AORUS plugin is already appropriate for the verified device, use `-Action Install -WithWaterforce` to also copy its build output. It is excluded from ordinary installation.

Restart uses the helper's bounded native `Exit` RPC. It never uses mouse movement, tray menus or window automation and never launches `FanControl.exe -e` against a broken IPC endpoint. After 20 seconds, an unfinished exit is reported as an error unless `-Force` was explicitly supplied. Force terminates only processes whose executable path matches the configured installed Fan Control runtime. Other applications, workloads and vendor services are not stopped by this script.

The existing verified startup task is used when present, otherwise the elevated interactive runtime is started directly. No startup task is recreated or changed. After starting, maintenance waits up to 30 seconds for a fresh observed native mode and control-ready receipt. Read the `verification` object: process startup alone is not proof of ready cooling/MQTT controls.

## Restore a backup

Backups and action receipts stay in the ignored `.local/maintenance` folder. A pre-exit backup is made before restart/install/restore/repair. An additional backup captures normal-exit autosave before files are replaced. Configurations, CACHE, installed plugin DLLs and selected local settings are saved. Broker credentials remain DPAPI-encrypted; passwords are never printed or decrypted by maintenance. Keep backups private and on the same Windows account.

```powershell
./scripts/maintenance.ps1 -Action Restore -BackupPath .local/maintenance/backups/<backup-directory>
```

Or `make restore BACKUP=.local/maintenance/backups/<backup-directory>`.

Restore checks the backup manifest against the current Windows user, runtime path and configuration folder before stopping the host. It creates a new backup, stops Fan Control, restores the selected files and restarts. It does not delete additional files. Backups from another user/runtime or outside the maintenance backup directory are refused. Restore is an explicit full-settings operation; do not use it when you intend only a restart.

## Night CPU card

RepairNightCpu requires the optional CPU feature to already be enabled in local settings. It restores the existing Night card's enabled/manual-100 settings from an intact local card template and keeps the fan graphs, tray icons and other controls intact. It does not enable the feature on a different machine or change CPU voltage/BIOS settings. Current plugin behavior disables Turbo whenever an observed Night mode is active, independently of the optional slider; see [CPU performance](CPU-PERFORMANCE.md).

Status excludes stale telemetry from its reported current mode/CPU values. It displays only allowlisted diagnostic fields, never the broker settings or credentials. Helper `--native-ipc <runtime-folder>` is read-only; helper `--native-exit <runtime-folder>` exits the native host and is used by maintenance. Ordinary helper invocation remains diagnostic status. Build publishes the helper to `.artifacts/probe`.

Validated locally: status in both PowerShell versions, actual native IPC read, backup creation, and terminal restart with fresh Night/boost-disabled/connected-MQTT readback. Restore, DLL installation and Night-card repair have not been exercised through this generic script against live hardware; the deployed update was installed using the earlier bounded installer. Abrupt-loss and reboot cooling fallback remain unverified.
