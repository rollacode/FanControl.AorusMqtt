# FanControl.GigabyteWaterforce

A [FanControl](https://github.com/Rem0o/FanControl.Releases) plugin for **GIGABYTE AORUS WATERFORCE X** AIO liquid coolers (X240 / X280 / X360).

Communicates directly with the cooler via USB HID — no GCC, HWInfo, or any third-party software required.

## Supported Devices

| Device | VID | PID | HID Name |
|--------|-----|-----|----------|
| AORUS WATERFORCE X240 | 0x1044 | 0x7A4D | Castor3 |
| AORUS WATERFORCE X280 | 0x1044 | 0x7A4D | Castor3 |
| AORUS WATERFORCE X360 | 0x1044 | 0x7A4D | Castor3 |

## Sensors Provided

| Sensor | Type | Description |
|--------|------|-------------|
| Liquid Temp | Temperature | Coolant temperature (°C) |
| Fan Speed | Fan | Radiator fan RPM |
| Pump Speed | Fan | Pump RPM |
| Fan Control | Control | Fan speed (0–100 % maps to 750–2800 RPM) |
| Pump Control | Control | Pump mode: ≥50 % → Turbo (~2810 RPM), <50 % → Balanced (~2130–2430 RPM, temp-curve) |

> **Note:** The pump has two hardware modes only — Turbo (forced maximum) and Balanced (temperature-driven curve). The 50 % threshold in FanControl maps these two modes to a single slider.

## Installation

1. Download `FanControl.GigabyteWaterforce.dll` and `HidSharp.dll` from the [latest release](../../releases/latest).
2. Copy both files into your FanControl `Plugins` folder (e.g. `C:\Program Files\FanControl\Plugins\`).
3. Restart FanControl.

> Run FanControl **as Administrator** if the device is not detected.

## Building from Source

Requirements: [.NET 9 SDK](https://dotnet.microsoft.com/download)

```bash
git clone https://github.com/bperucchi/FanControl.GigabyteWaterforce
cd FanControl.GigabyteWaterforce
dotnet build -c Release
```

Output DLL: `bin/Release/net9.0/FanControl.GigabyteWaterforce.dll`

Also copy `HidSharp.dll` from the same output folder into the FanControl Plugins directory.

## Protocol Reference

Protocol reverse-engineered from the Linux kernel driver (`drivers/hwmon/gigabyte_waterforce.c`) and USB captures of GIGABYTE Control Center (GCC).

### Status Request (sub-command 0xDA)

Send `{0x99, 0xDA, 0x00, ...}` via interrupt OUT. The device echoes `{0x99, 0xDA, ...}` on interrupt IN with sensor data at these offsets:

| Offset | Type | Description |
|--------|------|-------------|
| 0x02 | u16 LE | Fan RPM |
| 0x05 | u16 LE | Pump RPM |
| 0x08 | u8 | Fan duty (0–100 %) |
| 0x09 | u8 | Pump duty (0–100 %) |
| 0x0D | u8 | Liquid temperature — integer part (°C) |
| 0x0E | u8 | Liquid temperature — decimal part (÷10) |

### Fan Speed Control (sub-command 0xE6)

Send a flat RPM curve across 4 temperature points (channel `0x01, 0x01` for fan):

```
{0x99, 0xE6, 0x01, 0x01, 0x00, RPM_hi, RPM_lo, 0x1E,
 RPM_hi, RPM_lo, 0x32, RPM_hi, RPM_lo, 0x41, RPM_hi, RPM_lo, ...}
```

RPM values are big-endian uint16. Valid range: 750–2799 RPM (sending ≥2800 triggers auto mode).

### Pump Mode Control (sub-command 0xE5)

Three-packet sequence, re-sent every update cycle to hold the mode:

```
{0x99, 0xE5, 0x01, [mode], 0x00, ...}   // mode select
{0x99, 0xE5, 0x02, [param], 0x00, ...}  // mode parameter
{0x99, 0xB6, 0x00, ...}                 // commit
```

| Mode | Bytes | Result |
|------|-------|--------|
| Turbo | `E5:01:06` + `E5:02:04` + `B6` | ~2810 RPM (forced maximum) |
| Balanced | `E5:01:05` + `E5:02:00` + `B6` | ~2130–2430 RPM (temperature curve) |

## License

MIT

Local integration note: the protocol ranges above describe the upstream implementation. This integration now maps native duty to duty × 25 RPM, clamped to 400–2500 RPM. Bounded X360 trials confirmed 400, 600 and 800 RPM targets through device curve readback and measured RPM; see ../../docs/HARDWARE.md. Pump writes remain excluded.
