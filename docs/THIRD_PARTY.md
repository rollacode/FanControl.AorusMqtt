# Third-party notices

## Waterforce source

The implementation under third_party/Waterforce is derived from
[brenoperucchi/FanControl.GigabyteWaterforce](https://github.com/brenoperucchi/FanControl.GigabyteWaterforce)
at upstream commit `793477a1db9b0668348e48726044e0b711840369`.
The upstream README declares **MIT**. Its original text and declaration are
retained in that directory. Upstream does not provide a separate full LICENSE
file at that commit; this project does not invent an upstream copyright notice.

Local changes include bounded and validated HID transactions, model checks,
readback and restoration, queued native curve commands, the 400–2500 RPM range,
read-only pump telemetry and removal of pump writes. Product family statements
in the original README should not be read as this integration's validation of
X240/X280 or coolant interpretation.

This repository is a fresh source snapshot with explicit attribution, not a
GitHub network fork. It contains no upstream SDK binary or prior development
history.

## Fan Control and its SDK

Fan Control is proprietary software by Rémi Mercier. Obtain it and its plugin
SDK from the [official release repository](https://github.com/Rem0o/FanControl.Releases)
and follow its [license](https://github.com/Rem0o/FanControl.Releases/blob/master/LICENSE).
The application and SDK are not bundled or relicensed. CI obtains a pinned
release only to compile and test plugins; no SDK/runtime artifact is uploaded.

## Dependencies and research

- MQTTnet: .NET Foundation / project contributors, MIT.
- Microsoft System.Security.Cryptography.ProtectedData: Microsoft/.NET contributors, MIT.
- Fody and Costura: their respective project contributors, MIT.
- HidSharp: James F. Bellinger, its package/vendor license.
- LibreHardwareMonitor: its project contributors, MPL-2.0; supplied by the Fan Control host.

Dependency licenses remain independent of the license for original integration
code. NuGet packages supply their own license metadata. Host SDK,
LibreHardwareMonitor and HidSharp are excluded from MQTT embedding and supplied
by the host environment.

Protocol facts were also compared with [waterc](https://github.com/antoArd/waterc)
without copying its GPL implementation. Upstream credits Linux waterforce-hwmon
and vendor captures as earlier protocol sources. These acknowledgements do not
establish compatibility with untested hardware.
