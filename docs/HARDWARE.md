# Hardware support

| Source/channel | Support |
|---|---|
| AORUS WATERFORCE X360 radiator | Model-checked USB HID control; curve/mode readback and exact restoration tested |
| Radiator and pump RPM | Real 99 DA measurements with range and freshness checks |
| Pump control | Excluded; existing pump mode is preserved |
| Candidate coolant temperature | Unverified diagnostic interpretation |
| CPU Package | LibreHardwareMonitor CPU-family and sensor-name lookup |
| Motherboard CPU/fan sensors | Host-dependent, physical mappings configured locally |
| GPU temperature | Read-only NVIDIA query |
| Motherboard/GPU fan control | Fan Control's existing native controls |

The Waterforce path requires VID 1044, PID 7A4D and X360 model code 2.
Exactly one matching device must be present. These are product identifiers,
not installation-specific hardware IDs.

Native duty maps to `round(duty * 25)`, clamped to **400–2500 RPM**.
400, 600 and 800 RPM targets were confirmed by device curve readback, with
measured samples approximately 479, 624–682 and 878 RPM respectively. A
higher-speed trial and original-state restoration were validated too. Target
RPM is not a promise of exact measured speed or identical behavior on every unit.

The radiator implementation sends channel-one E6 curve commands and channel-one
E5 mode commands when required. It captures the original curve/mode first and
restores them with independent readback. It emits no channel-two pump or B6
command. Stale status is not represented as a fresh sensor measurement.

Crash, USB disconnect and full reboot cooling fallback have not been validated.
Normal Reset does not prove device behavior after abrupt host loss. X240/X280
are not validated by this integration. Native fan-stop calibration is unsuitable
for this clamped radiator control.
