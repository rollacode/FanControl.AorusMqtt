# Operation and rollback

Fan Control is the cooling host. Install the two locally built plugin DLLs into its Plugins directory, keep the native configurations on that machine, and provide local MQTT settings and protected credentials under the current user's `FanControlMqtt` application-data directory. The default MQTT prefix is neutral; an existing prefix remains an installation setting.

The host must remain running for software curve control. The MQTT bridge switches only the configured native files with the official CLI. A loaded filename is independently observed through the host window. It does not invent an observed mode from the requested value. Saved native files must be readable, valid and have unique basenames; curve edits do not require a validation hash.

Radiator control is opt-in. The local `waterforce-control.json` and radiator-trial receipt attest to the approved bounded radiator handover. The MQTT live gate additionally requires local handover evidence and one matching native runtime. GCC, ASUS fan-control and AORUS service process checks prevent competing writers during the current handover. A fresh native observation and the three available modes determine readiness independently of transport connectivity.

Normal Waterforce Reset captures/restores the exact original curve and fan mode and checks device readback. It never changes the pump mode. Normal exit was tested separately from abrupt host loss.

Crash, USB-loss and reboot fallback remain unverified. The plugin forwards native curve requests; the removed separate CPU Package threshold/maximum-speed override and extra watchdog are not part of operation. Firmware behavior after an abrupt host loss is not inferred from ordinary Reset.

Do not run automatic Waterforce calibration expecting the radiator to stop: 0–16 percent is clamped to 400 RPM. Pump control is excluded. Motherboard/native GPU calibration is owned by Fan Control and must not be automatically launched by these plugins.

Migration, startup, handover and plugin-update scripts are explicit operator tools, not background services. They may require administrator rights, write private rollback receipts, stop a specific host or vendor service, or disable startup entries. Building and testing never invokes them. Obsolete standalone-app startup, migration and one-off configuration repair scripts have been removed. Use INSTALL.md for the current plugin installation procedure.

Keep rollback material local. To return a device to vendor control, first release its native control and confirm restoration, then start only the deliberately stopped writer. Do not leave two writers active on the same device. Stop uncertain mode requests until the actual native configuration is reconciled.

The optional FanControl logon task uses an absolute executable path, Highest rights and an interactive user session; the native cache preserves the last loaded configuration. The installer sets the separate native service and released competing vendor services to Manual. The scheduled-task launch path was exercised; no full PC reboot validation is claimed. See INSTALL.md for setup, migration and persistence. Home Assistant automation is configured separately by the operator.
