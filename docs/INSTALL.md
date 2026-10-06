# Installation and persistent startup

## Downloaded plugin bundle

Download the ZIP from [GitHub Releases](https://github.com/rollacode/FanControl.AorusMqtt/releases). It contains `Plugins/FanControl.Mqtt.dll` and `Plugins/FanControl.GigabyteWaterforce.dll`. Exit Fan Control normally, back up your existing plugins and configurations, and copy those DLLs into the existing host's `Plugins` directory. Use the .NET 10 edition of Fan Control V282; its SDK, HidSharp and hardware-monitor assemblies are supplied by the host rather than this archive.

The bundle preserves your existing curves and settings; it contains no user configurations, credentials, native runtime or debug symbols. MQTT still needs the local settings and credential provisioning below. The provisioning helper and optional maintenance scripts are available as repository source rather than packaged executables. Waterforce control remains opt-in after validation, and optional Night CPU policy is disabled by default.

To build the same allowlisted archive from source, run `./scripts/package-release.ps1 -Version <version>`. This rebuilds without debug symbols or local source paths, runs mock tests, and writes a ZIP plus SHA256 checksums under `.artifacts/releases`. It does not install or start anything.

## Runtime and build

1. On Windows, install the required .NET SDK/runtime versions and obtain Fan Control from its official distribution.
2. Put the separately licensed runtime under `.artifacts/runtime`. Copy its `FanControl.Plugins.dll` into `third_party/Waterforce/lib`.
3. Run `./scripts/build.ps1`. It builds both plugins and runs mock/fake-device tests. It does not install or start them.

The working runtime directory is persistent storage, not Windows Temp. It is ignored by Git; do not delete the workspace or run a cleanup that removes ignored runtime files while this installation uses it.

## Native modes and local settings

Keep three native Fan Control files under its Configurations directory: Performance.json, Balanced.json and Night.json. Clone the user's own native configuration so calibration, pairing, temperature sources and tray icons are preserved. Use native graphs called CPU and GPU and supply actual control mappings locally.

Local plugin data lives under:

```text
%LOCALAPPDATA%\FanControlMqtt
```

Create settings.json there with installation-specific values. This example contains placeholders and starts with hardware control locked:

```json
{
  "runtimePath": "<absolute-path-to-FanControl.exe>",
  "liveHandoverValidated": false,
  "handoverEvidencePath": "",
  "profiles": [
    {"name": "Performance", "configPath": "<absolute-path-to-Performance.json>"},
    {"name": "Balanced", "configPath": "<absolute-path-to-Balanced.json>"},
    {"name": "Night", "configPath": "<absolute-path-to-Night.json>"}
  ],
  "mqtt": {
    "enabled": true,
    "host": "<broker-host>",
    "port": 8883,
    "useTls": true,
    "tlsServerName": "<certificate-server-name>",
    "clientId": "fancontrol-mqtt",
    "topicRoot": "fancontrol/aorus"
  }
}
```

Do not invent handover or radiator-test receipts. Radiator control requires the approved local opt-in and verified radiator-trial evidence described in OPERATIONS.md; otherwise the plugin remains read-only. Readiness reports whether mode selection is available.

Provision the actual broker username and password with the local helper. The password is read from stdin and stored with CurrentUser DPAPI:

```text
<authorized-secret-provider> | dotnet run --project tools/FanControlBridge.Probe -c Release -- --provision-mqtt-stdin <broker-username>
```

The provider notation is a placeholder, not a command. Never paste a password into source, a command argument or a log.

## Elevated interactive autostart

For an already approved installation with local settings and protected credentials:

```powershell
./scripts/install-native-startup.ps1
```

Run as administrator for the same Windows user. The script:

- Saves the previous task, service startup settings and replaced plugins to ignored local rollback storage.
- Normally exits the known Fan Control process before replacing plugins; it does not forcibly terminate an armed host.
- Installs FanControl.Mqtt.dll and FanControl.GigabyteWaterforce.dll.
- Registers the FanControl logon task with an absolute executable path, Highest rights, Interactive logon and a 15-second logon delay.
- Starts the native window using -w for local setup. It may then be closed to the tray; native IPC continues to report the loaded configuration while the host stays running.
- Sets the separate native service and previously released competing vendor services to Manual, preventing automatic competing startup. Running competing services are rejected rather than silently stopped.
- Runs the scheduled task to check the same startup path immediately.

Autostart occurs **after this user signs in**, not in a SYSTEM session before login. CurrentUser credentials are not provisioned to SYSTEM. Native cache preserves the last configuration filename; the startup task does not force Performance or a guessed restore mode.

For migrating a renamed plugin/data directory, supply `-MigrationPlanPath <local-plan.json>`. The local plan contains `sourceDataDirectory` and `previousMqttPluginFileName`. It must stay private. The installer copies protected credentials without decryption, retains the original data directory, moves the replaced plugin to rollback and preserves the locally configured MQTT topics and broker account.

## What persists

Ordinary Windows restart does not remove the native files, local settings, protected credentials, opt-in evidence or task. Profile changes are saved by Fan Control; MQTT reports the actual loaded mode after startup. Driver/runtime availability still matters.

The elevated task and manual service startup types were read back after installation, and the task was used to launch the real host. A full PC reboot has not been performed as part of validation.

For rollback, use the private backups and the device-release procedure in OPERATIONS.md. Keep firmware safety and software crash fallback distinct from persistence of settings.
