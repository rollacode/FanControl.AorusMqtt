# MQTT profile selection

The default prefix is `fancontrol/aorus`. Set `mqtt.topicRoot` locally to retain an existing installation's prefix.

| Topic suffix | Direction | Payload |
|---|---|---|
| `profile/set` | command, nonretained | `Performance`, `Balanced`, `Night` |
| `state` | retained state/telemetry | JSON |
| `availability` | retained / Last Will | `online` / `offline` |

State contains `bridge`, `sensors`, `liveControlEnabled`, `controlReady`, `commandResult` and `sampledAt`. Legacy `nightActive` and `previousProfile` fields stay false/null for schema compatibility. No `night/set` topic or toggle is accepted.

Commands are allowlisted, at most 64 bytes, queued with a capacity of eight and executed serially. Retained commands are ignored and MQTT sessions are clean. Configuration paths come only from local settings; MQTT carries a mode name, never a path or shell command. Repeated recently observed selections are idempotent. Uncertain submissions remain pending until native configuration observation resolves them.

The actual loaded native filename supplies `observedProfile`; `requestedProfile` remains intent. Observation acknowledges configuration loading, not every physical fan output. Native manual changes refresh observations. Restart initially invalidates observations until a real configuration is seen.

Configure broker host, port, client ID and TLS server name locally. TLS uses OS certificate-chain and target-name validation, including when a LAN connection address differs from the certificate name. There is no insecure certificate mode. Plain MQTT requires explicit private-network approval.

The local credential helper accepts the broker username as its argument and password through stdin:

```text
FanControlBridge.Probe --provision-mqtt-stdin <broker-username>
```

Use an authorized local secret-provider pipeline. Never put the password in an argument or source file. The helper stores it with CurrentUser DPAPI and does not display it.

A Home Assistant select can expose the three modes, use nonretained QoS1 commands and report observed state nonoptimistically. Its availability combines `online` with top-level `controlReady=true`. Sensors have independent freshness rules. Existing native files must be valid, readable and have unique basenames; saving the user's curves does not trigger a hash lock. Broker authentication does not imply scoped topic ACLs; configure broker permissions separately.

Automation is external to these plugins. No charging, presence, schedule, previous-profile restore or owner identity is hardcoded into the code.
