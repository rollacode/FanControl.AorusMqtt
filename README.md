![Fan Control AORUS and MQTT](docs/assets/banner.svg)

# Fan Control · AORUS + MQTT

[![Windows](https://img.shields.io/badge/platform-Windows-0078D4)](docs/INSTALL.md)
[![Fan Control](https://img.shields.io/badge/tested_with-Fan_Control_V282-444)](https://github.com/Rem0o/FanControl.Releases)
[![Build](https://github.com/rollacode/FanControl.AorusMqtt/actions/workflows/build.yml/badge.svg)](https://github.com/rollacode/FanControl.AorusMqtt/actions/workflows/build.yml)

**Use your own Fan Control curves. Switch complete configurations over MQTT.**

Two plugins extend the existing [Fan Control](https://github.com/Rem0o/FanControl.Releases) application: USB HID radiator control for **AORUS WATERFORCE X360**, and MQTT configuration switching with live telemetry. Both run inside Fan Control; no separate tray application or background watcher is needed.

## What you get

- **Waterforce radiator control:** native curves request 400–2500 RPM; radiator and pump RPM are measured separately.
- **Three configurations:** Performance, Balanced and Night. Assign CPU/GPU curves in Fan Control and switch locally or through MQTT.
- **Observed state:** the reported mode follows the actual loaded native configuration, including manual changes.
- **Telemetry:** CPU Package, available motherboard sensors, NVIDIA GPU temperature and Waterforce RPM.
- **Protected MQTT credentials:** CurrentUser DPAPI storage, validated TLS, and a configurable topic prefix.
- **Persistent startup:** an elevated interactive Windows logon task, with the last native configuration preserved.

## Start here

1. Obtain the **.NET 10 build of Fan Control V282** and the .NET 9/10 SDKs.
2. Follow [installation](docs/INSTALL.md) to build and install the two DLLs. Fan Control and its SDK are obtained separately.
3. Clone your own native configuration into Performance.json, Balanced.json and Night.json. Keep calibration, sensor pairing and tray icons in every copy.
4. Configure your local broker and provision credentials. Validate the Waterforce device before enabling radiator control.
5. Use the [Home Assistant example](examples/home-assistant.yaml) or another MQTT client to select a mode.

```text
Fan Control + CPU/GPU curves
       │
       ├─ Waterforce plugin → CPU radiator / RPM telemetry
       │
       └─ MQTT plugin ↔ broker ↔ Home Assistant or your own client
```

## MQTT in one table

Default prefix: `fancontrol/aorus`. Change it locally when running multiple computers.

| Topic | Payload | Retained |
|---|---|---|
| `fancontrol/aorus/profile/set` | `Performance`, `Balanced`, `Night` | No |
| `fancontrol/aorus/state` | Observed configuration and telemetry JSON | Yes |
| `fancontrol/aorus/availability` | `online` / `offline` | Yes, including Last Will |

Home Assistant should display `bridge.observedProfile` and make mode selection available only when transport is online **and** `controlReady` is true. A requested mode alone is not proof that the native configuration loaded. [MQTT details →](docs/MQTT.md)

## Compatibility

| Feature | Status |
|---|---|
| AORUS WATERFORCE **X360** radiator | Tested, model code 2; targets 400/600/800 and higher RPM validated |
| Waterforce radiator / pump RPM | Measured through USB HID |
| Waterforce pump control | Not provided; existing pump setting is preserved |
| X240 / X280 | Not validated by this integration |
| Motherboard and GPU fan control | Provided by Fan Control, subject to its hardware support |
| Candidate coolant temperature | Unverified diagnostic reading |

The RPM target and measured speed can differ. Below 16%, radiator requests map to 400 RPM; 24% maps to 600 RPM and 32% to 800 RPM. A separate CPU-temperature override does not alter your curves. Fan-stop calibration is not supported by this radiator path.

Fan Control must stay running to apply software curves. Normal Reset restores the captured device curve/mode with readback; crash, USB-loss and reboot fallback have not been validated. Only one cooling writer should own a device. [Hardware details →](docs/HARDWARE.md)

## Build and test

```powershell
./scripts/build.ps1
```

The build runs mock configuration/MQTT tests and fake HID tests; it sends no live hardware commands. The included workflow repeats those checks on Windows. Installation and hardware validation are explicit separate steps.

## Documentation

| Guide | Covers |
|---|---|
| [Installation](docs/INSTALL.md) | SDKs, plugins, broker credentials, control enablement and startup |
| [MQTT](docs/MQTT.md) | Topics, authentication, observed state and Home Assistant |
| [Configurations](docs/MODES.md) | Performance / Balanced / Night and manual selection |
| [Operation and rollback](docs/OPERATIONS.md) | Device ownership, persistence and restoring vendor control |
| [Diagnostics](docs/DIAGNOSTICS.md) | Read-only status, missing sensors and unavailable controls |
| [Hardware](docs/HARDWARE.md) | Verified channels, limits and protocol behavior |
| [Attribution](docs/THIRD_PARTY.md) | Upstream source, dependency licenses and redistribution boundaries |

## License and credits

Original integration code is MIT-licensed; see [LICENSE](LICENSE). The Waterforce implementation derives from [brenoperucchi/FanControl.GigabyteWaterforce](https://github.com/brenoperucchi/FanControl.GigabyteWaterforce), whose upstream README declares MIT. That declaration and the original README are retained. Fan Control itself is separate proprietary software and is not distributed here.

No local configuration, broker credential, device snapshot, runtime binary or installation history is included in this repository.
