# Vihanga duplex audit

| Operation | Native Windows USB/radio | Private Windows–Oracle–Pi link | Simulator coverage |
| --- | --- | --- | --- |
| Heartbeat and attitude | Native flight-data stream | Pi publishes original frames, relay validates them and the Windows viewer receives them. HUD motion and recovery still need a current bench check. | Evidence is supplied to the model by the test fixture. |
| Parameter read | Raw Parameters calls `getParamList`. | The relay exposes a read-only snapshot. The Pi has a guarded snapshot request. The Windows viewer does not forward native parameter requests over the link. | Parameter lookup by name returns the stored simulated value after two distinct acknowledgements. |
| Parameter write | Raw Parameters calls `setParam`. | No native write path from Windows through the relay to the Pi. | Typed parameter write changes model state after the simulated result. |
| Mission read/upload | Flight Planner uses the native MAVLink mission interface. | No native mission protocol forwarding path. | Reads and uploads operate on the approved simulated mission revision digest. |
| Link diagnostics | Native transport | Relay-to-Pi ping matches a Pi transport reply. The separate guarded telemetry probe observes flight-controller responses to fixed telemetry-interval requests. | No network or serial execution in this unit model. |
| General command and result | Native link carries flight-controller responses. | The Windows bridge is receive-only. The relay and Pi expose specific diagnostics. The diagnostics are not a general command channel. | The model distinguishes Pi acceptance from a simulated flight-controller result. |

## Source locations

- [Native raw parameters](../GCSViews/ConfigurationView/ConfigRawParams.cs) and [Flight Planner](../GCSViews/FlightPlanner.cs).
- [Windows receive-only bridge](../plugins/AeroHelmReceiveOnlyBridge.cs) receives cloud frames into the native GCS stream.
- [Private relay](../services/vihanga_link/relay.py) has separate device and viewer WebSockets and explicit parameter snapshot and diagnostic routes.
- [Pi publisher](../services/vihanga_link/pi_agent.py) exclusively owns the UART. Its private request handler dispatches fixed diagnostic intents.
- [Private-link validator](../services/vihanga_link/deploy/validate_full_duplex.py) exercises relay identity, transport ping and observed frames. Its optional telemetry probe is limited to stream-rate diagnostics.
- [Simulator control model](../services/vihanga_link/supervised_control.py) and [contract tests](../services/vihanga_link/test_supervised_control.py) exercise typed transactions, aircraft isolation and expiry.

## Evidence to collect before marking a link complete

Capture the installed Windows build revision, deployed relay/Pi revisions, selected aircraft identity, and timestamps at each hop. For each simulated transaction record the application request ID, transport receipt, terminal result and subsequent readback. Repeat with disconnect before receipt, disconnect after receipt, expired lease, wrong aircraft, and simultaneous operators. Complete the physical HUD motion and restart checks separately. The in-process simulator model does not exercise the Windows bridge, private relay, Pi process or flight controller.
