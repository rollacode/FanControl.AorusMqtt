# Installation and persistent startup

## Runtime and build

1. On Windows, install the .NET 9 and .NET 10 SDKs and obtain the .NET 10 edition of Fan Control V282 from its official distribution. The .NET Framework edition is not supported by this MQTT plugin.
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

Radiator control starts read-only. Complete the explicit validation step below to obtain real local evidence and enable it; do not invent handover or radiator-test receipts. Readiness reports whether mode selection is available.

Provision the actual broker username and password with the local helper. The password is read from stdin and stored with CurrentUser DPAPI:

```text
<authorized-secret-provider> | dotnet run --project tools/FanControlBridge.Probe -c Release -- --provision-mqtt-stdin <broker-username>
```

The provider notation is a placeholder, not a command. Never paste a password into source, a command argument or a log.

## Validate and enable the radiator

Capture your existing vendor settings, release competing writers, and stop Fan Control normally. Check that you have identified the CPU radiator and that the workload permits a brief 800 RPM test. Configure the three native files and local settings first.

From an elevated PowerShell for the same user, run this explicit hardware operation:

```powershell
dotnet run --project tools/Waterforce.Validation -c Release -- --validate-and-enable
```

This command captures the original curve/mode, requests 800 RPM for eight seconds, checks measured response and device readback, then restores the original state with readback. It changes no pump setting. It does not stop programs automatically and refuses to test while Fan Control or a detected competing writer is running.

Only after confirmed success does it write waterforce-control.json, the validation receipt and native-mqtt-handover.json, and enable liveHandoverValidated in local settings. The legacy receipt filename includes 2500; its requestedRpm records the actual 800 RPM target. Generated evidence stays local. Failure does not fabricate a successful receipt.

Install both built DLLs into the native Plugins directory while the host is stopped, then restart Fan Control with -w. Pair the radiator control with its radiator RPM sensor and assign your CPU curve. Preserve those assignments in every native configuration. A missing validation receipt leaves the radiator control unregistered. MQTT availability also requires actual native configuration observation; connection alone is insufficient.

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

The task startup path was exercised in the tested installation. A full PC reboot has not been performed as part of validation.

For rollback, use the private backups and the device-release procedure in OPERATIONS.md. Keep firmware safety and software crash fallback distinct from persistence of settings.

## Home Assistant

Merge [examples/home-assistant.yaml](../examples/home-assistant.yaml) under your existing MQTT configuration and adapt the topic prefix and unique ID. It creates a direct three-mode selector using actual observed state and online/readiness availability. No schedule, presence or charging automation is installed. See the [MQTT guide](MQTT.md).
