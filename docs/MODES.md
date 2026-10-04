# Configurations and curves

Use three native files named Performance.json, Balanced.json and Night.json.
Copy your existing native configuration so sensor pairing, calibration, layout,
tray icons and temperature sources stay identical in each file. Edit CPU and
GPU graphs locally to define the desired behavior of each mode.

The plugin selects complete native configurations. It does not adjust individual
curve points over MQTT. Names are allowlisted; paths come from local settings.
Saving or editing a curve does not require a configuration fingerprint update.

Night is an ordinary mode, with no Windows-side override or remembered return
mode. Schedules and restoration policies belong in your automation system.
Manual selection inside Fan Control is observed and published too.

The optional prepare-native-modes.ps1 script requires a native source and explicit
local control mapping. Review its proposed mapping and keep generated files
local. No predefined motherboard header mapping is distributed.

The radiator forwards the host's requested value to the 400–2500 RPM path. It
has no separate CPU Package threshold override or background watchdog.
