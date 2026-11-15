# Vihanga — Product-grade Cloud GCS Delivery Contract

**Date:** 15 November 2026  
**Status:** Development requirements recorded; NOT deployed or flight-authorized  
**Baseline:** release/vihanga-stable-hud-20261109 at 52221bd0ed60b90fb5bbf929c4d516154d66096a  
**Target:** Full cloud-assisted GCS for one real aircraft, architected for 12 independently controlled aircraft and a future interchangeable LTE/5G edge module.

## Executive requirement

Vihanga is not a proof-of-concept dashboard or a semester demonstrator. The delivered product must support real telemetry, authorized and acknowledged flight commands, mission lifecycle management, video, safety supervision, independent pilot fallback, fleet isolation, maintainable deployment, and a clean, coherent Mission Planner-based operator UX. No cosmetic mockups, simulated status, placeholder features, or unverifiable success claims may be shipped as production functionality.

The work is to be integrated as one coherent product program rather than an endless sequence of unrelated experimental branches. Engineering implementation may be grouped into one reviewed integration branch, but *physical activation remains gated by sequential evidence* (SITL -> HIL -> propeller-free bench -> authorized field test). A green build cannot waive a hardware or flight-safety acceptance gate.

## Confirmed starting point and limitations

- Confirmed in operator-reported November 8-9 bench sessions: real Pixhawk MAVLink serial data reached Pi 5, a private authenticated Oracle relay, and a native Windows Vihanga/Mission Planner HUD. Camera feed was visible in a separate pipeline. One observed Oracle-to-Pi diagnostic RTT was 286 ms; this is NOT a control-loop SLA.
- Source includes authenticated operator viewing, token-pinned Pi origin/instance identity, receive-only native bridge, bounded disarmed-only telemetry-stream requests, a simulated control-plane authority model, offline mission preview, and research fleet/API modules.
- **Not yet confirmed:** long-run telemetry reliability, physical roll/pitch response acceptance, independent radio failover, parameter parity, command ACK/vehicle state confirmation, or safe powered flight via cloud.
- **Current physical boundary:** services/vihanga_link/pi_agent.py and relay.py deliberately refuse arbitrary control bytes and do not implement a physical command-dispatch path. services/vihanga_link/supervised_control.py is simulator-only. Cloud flight control is not enabled.
- Stable source is pinned on GitHub. Installed Windows binaries must be separately backed up and hash-verified on the operator's machine; do not assume local backup completion.

## Required system architecture

1. **Aircraft autopilot:** ArduPilot/Pixhawk retains stabilization, onboard mission execution, geofencing, return/landing failsafes and independent RC/pilot priority. Neither Oracle nor Windows is the inner flight-control loop.
2. **Aircraft edge agent:** single owner of the Pixhawk MAVLink UART; full duplex only after wiring/protocol verification; bounded frame queue; message integrity; source-system identity; command policy arbiter; modem/network-health metrics; securely provisioned per-vehicle identity. No second serial reader.
3. **Cloud broker:** private encrypted authenticated transport, independent operator/device identities, short-lived role-scoped authorizations, per-vehicle exclusive write authority, ordered/replay-resistant command envelopes, audit events, command result correlation and time-bounded fanout. Multiple observational GCS clients may connect without write authority.
4. **Native GCS:** retain genuine Mission Planner flight-data, calibration, parameter and mission engines. Implement a command-capable link adapter integrated with the native MAVLink interface. A receive-only HUD feed is not equivalent to full connection parity. Provide clear cloud/direct connection selection and status.
5. **Video:** independent low-latency camera path with explicit loss/freshness indicators. Never multiplex full video payload through the flight-command queue or assume a successful video feed proves MAVLink works.
6. **Independent backup:** operator-controlled direct USB/radio path, independent of cloud, explicitly selected by a qualified pilot. Never infer pilot-link availability from a cloud service; never automatically claim control ownership on reconnection.
7. **Future interchangeable edge hardware:** documented Pixhawk connector/electrical interfaces, isolated regulated power, GNSS/modem antenna considerations, Pi/compute provisioning, dual-carrier LTE/5G optionality, watchdog, thermal budget and physical serviceability. Hardware module is a product track after software command/parity acceptance, not a reason to couple software to one modem model.

## Definition of done — integrated capability matrix

| Domain | Required working capability | Evidence that is mandatory |
| --- | --- | --- |
| Telemetry | HEARTBEAT, attitude, GPS when available, home/position, mode, speed, battery, EKF, status text, fence and link freshness | FC/Pi/Oracle/GCS per-message counters, data ages, hardware tilt, reconnect/reboot soak; no fabricated GPS |
| Secure command link | Typed command request with vehicle ID, actor, command ID, monotonic sequence, epoch, expiry, immutable mission revision, signature/authorization and explicit action policy | Auth positive/negative tests, duplicate/reorder/expiry/foreign-aircraft injection, command audit trace |
| Native command ACK | Distinguish request accepted by GCS, broker accepted, Pi accepted, real FC COMMAND_ACK/mission ACK, and final observed state transition | Timestamped correlated IDs at every boundary; timeout/reject/partial success handled distinctly |
| Remote supervised controls | Mode change, disarm/arm, takeoff, land and RTL for supported airframes/firmware and approved conditions | SITL/HIL then controlled propeller-free bench and separately approved field test; no unsupervised flight |
| Mission operations | Route creation, revision review, geofence/altitude/home checks, upload/readback, start/pause/resume/abort and progress | Byte-accurate mission protocol, readback/hash or item comparison, negative test of stale/modified plan |
| Configuration | Full parameter snapshot and missing-index retry, typed edits with range/readback, curated safe calibration flows | Direct USB versus cloud comparison; prohibit physically dependent calibrations without on-site supervision |
| Reliability | No stale frame replay, bounded queues, reconnect, reboot recovery, session/lease revocation, cloud loss procedures | Long-run measured p50/p95/p99 latency, loss, throughput, FC message rates, fault-injection results |
| Security | Aircraft-bound auth, operator MFA/role policy, one active pilot writer per aircraft, credential rotation, audit provenance, least privilege | Independent security assessment, wrong-tenant/role/device denial, replay/spoof tests, log retention policy |
| Fleet | 12 independently identified vehicles, command authorization per vehicle, concurrent observation, multi-vehicle operational dashboard | 12 SITL instances stress test first; later multiple real airframes; no shared SYSID or lease collision |
| Video | Reliable camera feed with bounded resource use and telemetry-independent diagnostics | Disconnect and bitrate/load tests with simultaneous MAVLink commands |
| Ground-station UX | Professional clear flight deck, planning, vision, fleet, operations, link and settings; no ornamental AI terminology | Windows UI smoke at 100/150/200% DPI, mouse/keyboard/touch, workflow acceptance, no fake enabled controls |
| Packaging | Reproducible Windows EXE/DLL, Pi and Oracle deploy artifacts, exact manifest, rollback, recovery and runbook | Clean install test, CI green for exact commit, hash match, migration/rollback rehearsal |

## Command execution contract — non-negotiable

**Lifecycle:** CREATE -> VALIDATE -> APPROVE -> ISSUE LEASE -> DISPATCH -> PI RECEIVED -> FC ACKNOWLEDGED -> STATE OBSERVED -> COMPLETE. Each transition is separately timestamped/audited; ERROR, REFUSED, EXPIRED and UNKNOWN are honest terminal/non-success states.

- Commands must be typed and allowlisted; no public raw MAVLink forwarding endpoint, wildcard aircraft target or unrestricted command console.
- One operator-controlled aircraft lease per vehicle, mutually exclusive across sessions; separate observation sessions; explicit takeover/revocation policy; clear pilot precedence.
- Reject if authenticated identity, authorization role, aircraft-system identity, lease epoch, command sequence, immutable plan revision or TTL fails. Never auto-replay an unacknowledged takeoff/arm/mission-start after a reconnect.
- Require fresh verified FC heartbeat, health state and compatible mode. Navigation-dependent commands require real verified position, home, GPS/EKF, fence, battery and mission evidence. Missing data means **refuse**, not assume normal.
- Forbid writes on stale link, ambiguous aircraft, pending failsafe, loss of pilot override or outside authorized operational envelope. Platform shall not silently override onboard arming/failsafe checks.
- Remote ARM/TAKEOFF, LAND and RTL have individually reviewed guard conditions and operator confirmations; local independent pilot can override. Don't mistake a cloud API 200 or Pi packet receipt for successful aircraft actuation.
- Parameter writes and calibrations are separately privileged; safety-critical changes require on-site, propeller-free maintenance and verified readback. ESC/motor tests remain locally supervised only.
- Log reason codes, source identities, effective command policy and observed FC responses; redact tokens, passwords and precise private identity secrets.

## Engineering implementation units in one integration release

| Workstream | Code boundaries | Exit condition |
| --- | --- | --- |
| W1: preserve baseline and CI | Git branches, Windows build scripts, Pi/Oracle source, releases | Existing November 9 baseline untouched; automatic checks for exact reviewed commit |
| W2: full-duplex transport | Pi single UART owner, relay command broker, GCS link adapter | SITL binary MAVLink bidirectional exchange and frame/ACK correlation under fault injection |
| W3: command authority | Operator identity, relay leases, Pi arbiter, audit event store | Denied and accepted typed-command simulation/HIL test suite; no bypass via raw MAVLink |
| W4: mission and configuration parity | Native Mission Planner MAVLink interfaces, plan upload/download, parameter transactions | Direct-vs-cloud protocol behavior equivalence with fault/timeout recovery |
| W5: operations UX | FLIGHT, PLAN, VISION, FLEET, OPS, LINK, SECURITY, SETTINGS | Remove AI labels, decorative/duplicate toolbar controls, dead buttons and placeholder screens; operator test evidence |
| W6: resilience/security | Backup radio interface, stale state, security tests, recovery and observability | Lost/recovered cloud exercises; independent manual pilot authority; measured latency/loss |
| W7: fleet + packaging | Per-aircraft tenant/lease isolation, edge package interfaces, deployment automation | 12 SITL instances + controlled concurrency tests; rollback rehearsal and signed release evidence |
| W8: hardware adapter preparation | Open electrical/network interface spec, modem profiles, enclosure and watchdog | Component design review only after single-aircraft cloud command path is physically accepted |

An approved integrated code change is not a deployment. Release is promoted through development, SITL, HIL, propeller-free bench and qualified operational trials with separate approvals.

## Explicit acceptance scenarios

1. **Normal mission:** pilot selects one verified airframe, approves plan, uploads, reads back, authorizes execution; FC acknowledgments and observed state match without stale telemetry.
2. **Cloud outage:** link drops before command dispatch, during receipt, and after FC ACK; system does not replay unsafe operations; onboard failsafe and independent radio operate as designed.
3. **Delayed command:** 2+ second delay, expired TTL, out-of-order sequence, duplicate request ID, lost ACK, stale home/GPS, wrong aircraft SYSID, expired operator token: refusal or safely reconciled UNKNOWN, never a false SUCCESS.
4. **Dual operators:** two GCS clients request control of same aircraft simultaneously; exactly one active writer; all other viewers remain read-only and receive clear denial.
5. **12 aircraft:** every command, sample, mission, camera and lease is bound to the intended airframe; enforce per-aircraft queue/resource budgets; no cross-vehicle leakage or actuator action.
6. **Reboot recovery:** Pi, Oracle and GCS individually reboot; identity and health revalidate, no stale sample revival, old command leases cannot actuate.
7. **UI honesty:** no GPS fix produces NO FIX, not 0,0 on a map; inactive cloud command cannot display enabled button; flight-mode labels reflect FC feedback, not requested target state.
8. **Security attack simulation:** spoofed or replayed MAVLink/source, stolen-but-revoked session token, cross-tenant command, stream flooding, credential expiry, payload tampering; record expected deny behavior.

## UI and product presentation policy

- Keep the operator cockpit first: FLIGHT (live HUD), PLAN (route/mission), VISION (camera), FLEET (aircraft), OPS (execution), LINK (connections/diagnostics), SECURITY (authority/audit), SETTINGS (parameters/system).
- Consolidate overlapping modules. Preserve useful upstream Mission Planner features; do not rename or remove controls blindly because they look old.
- Remove irrelevant AI labels, generic AI-generated placeholder explanations, unnecessary dashboards, redundant toolbar buttons, and technical chatter in flight-critical views.
- Use a restrained theme, typography hierarchy, consistent spacing and operator-friendly data-density. Severity and link freshness are meaningful; decorative badges are not.
- Clearly separate *commanded* versus *acknowledged* versus *observed* vehicle state. Display direct pilot-link readiness separately from cloud-connected status.
- Prefer Windows-native robustness for this release; Ubuntu on edge/cloud is not a reason to rewrite Mission Planner on Ubuntu. Evaluate a cross-platform GCS as a separate business/technical decision.

## Release evidence ledger (required for every workstream)

Each change must record: precise code commit, changed components, tests run and their actual exit codes, environment, physical/simulated source, logs/artifact hashes, open defects, qualified reviewer, deployment target, rollback point and resulting acceptance state: **IMPLEMENTED / VERIFIED IN SITL / VERIFIED IN HIL / VERIFIED ON DISARMED BENCH / FIELD ACCEPTED**. Never collapse these states into one green check.

## Present risks and fixed boundaries

- The proven link is a *single-aircraft receive-only* telemetry deployment, not yet a general purpose cloud flight controller.
- Pi 5 telemetry access and FC wiring must be verified before any TX path is activated; never connect an unverified 5V supply to Pi GPIO and never operate powered propellers while testing command integration.
- The observed 286 ms cloud diagnostic RTT is not an acceptable control-loop timebase. Local FC autonomy and RC/manual takeover must remain effective during cloud variability.
- The small Oracle instance used in the proof of concept has not been capacity-qualified for twelve live airframes or video processing; undertake sizing/load tests and separate compute tiers.
- Demo success must be backed by full audit/acceptance traces rather than screenshots alone.
- No modification to the existing stable executable, master branch, Pi UART service, Oracle production relay, or live command-enable setting is authorized by this requirements document.

## Final product exit criteria

Vihanga graduates from telemetry demonstrator to cloud-controlled system **only when** verified real hardware can complete an approved supervised operation with correlated FC ACK and observed state, retains deterministic onboard failsafes and an independently tested pilot link, and passes security, recovery, performance and UI acceptance. Fleet readiness then requires a separately measured twelve-aircraft validation campaign.