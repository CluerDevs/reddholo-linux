# reddholo Fleet: monitor the whole hardware stack, alert on abnormal, manage centrally (plan / handoff)

**One line:** a central **hub** on pve1 collects readings from every device (agents on computers,
agent-less probes for network/power/storage gear), learns what "normal" looks like per device and metric, and
**sends a message** when something drifts out of normal. **reddholo on a Windows PC** is the console for the whole
fleet and, later, the place to run management actions (uninstall, cleanup, startup) on the PCs.

Status: plan only. Nothing here is built yet. Repo `CluerDevs/reddholo`, branch `reddholo`.

Priority order (changed from the first draft): **1. see everything, 2. get told when something is wrong,
3. fix things remotely.**

---

## 1. Goal and non-goals

**Goal**
- Every piece of hardware in one place: computers, servers, GPUs, disks, network gear, UPS / power, storage.
- A message (Teams / email / phone push, decision 3) when a device is **out of normal**: offline, over a limit,
  or behaving unlike its own history.
- Central management of Windows PCs (uninstall, cleanup, startup entries) once monitoring is solid.

**Not in v1**
- Remote GPU fan/curve control (hardware-damage risk without the local crash guard).
- Internet exposure. LAN, WireGuard / L2TP VPN and SSH only.
- Self-repair. Alerts go to a human; "fix it" stays a manual click (see `ait` below for bounded auto-repair later).

---

## 2. Architecture

```
 Computers (agent)                 Agent-less devices                  Server
 ┌──────────────────────┐          ┌─────────────────────┐   ┌──────────────────────────────────────┐
 │ reddholo-agent       │          │ switches, router,   │   │ reddholo-hub (service)                │
 │  Windows service     │ outbound │ firewall, Wi-Fi APs │   │  - enrollment + mTLS (agents)         │
 │  or Linux daemon     │ gRPC ──► │ UPS, PDU, NAS,      │◄──│  - probers: ping, SNMP, HTTP/JSON,    │
 │  - CPU/RAM/disk/SMART│          │ printers, BMC/IPMI  │   │    Redfish/IPMI, NUT (UPS)            │
 │  - temps, fans, GPU  │          │ (polled by the hub) │   │  - time-series store (SQLite)         │
 │  - inventory         │          └─────────────────────┘   │  - baseline + anomaly engine          │
 │  - management jobs   │                                    │  - alert router → Teams/email/push    │
 └──────────────────────┘                                    │  - jobs + audit                       │
                                                             └──────────────────┬───────────────────┘
                                                                                │ gRPC (LAN/VPN)
                                                             ┌──────────────────▼───────────────────┐
                                                             │ reddholo console (WPF, a Windows PC)  │
                                                             │  Fleet page, alerts, per-device pages │
                                                             └───────────────────────────────────────┘
```

- **Agents dial out** to the hub: no inbound firewall rules on each machine, works across NAT and over the WireGuard / L2TP VPN, and a
  dropped stream means "offline" immediately.
- **Agent-less probes run on the hub** for gear that can't run an agent.
- **Machines that already run Tailscale are just devices.** reddholo sees and monitors them like any other box: their agent dials the hub over the LAN or WireGuard / L2TP, and probes use their LAN or VPN address. The agent may report a `tailscale0` interface in inventory as plain data. Nothing in reddholo uses Tailscale: no client, API, tsnet library, MagicDNS or 100.x addressing.
- The hub runs on pve1 (in an LXC container); pve1 and the container run agents too, so the hub monitors itself.

---

## 3. What gets monitored, and how

| Device type | How | Key readings |
|---|---|---|
| Windows PCs / servers | `reddholo-agent` (Windows service, LocalSystem) reusing the app's code: LibreHardwareMonitor + PawnIO, NVML, WMI, perf counters, SMART | CPU/GPU load + temp, fans, RAM, disk free, SMART health, uptime, WHEA hardware errors, crashes/unexpected reboots (System event log) |
| Linux hosts (GPU workers, WSL hosts' Linux side, NAS that run Linux) | `reddholo-agent` Linux build (.NET 8): `/sys/class/hwmon`, `/proc`, `smartctl`, NVML, `journalctl` | same as above + GPU Xid/ECC errors, systemd failed units |
| NVIDIA GPUs (any host) | NVML via the agent | temp, hot-spot, power vs limit, fan, VRAM, clocks, throttle reasons, Xid/ECC errors |
| Network gear (switches, router, firewall, APs) | SNMP v2c/v3 polling from the hub, ping | up/down, interface errors/discards, link speed changes, port utilisation, device temp, CPU |
| UPS / PDU | NUT (Network UPS Tools) or SNMP | on-battery, battery %, runtime, load %, input voltage |
| Storage (NAS / disks) | SNMP or vendor HTTP API; SMART via agent where possible | volume free, RAID/pool state, disk SMART, temps |
| Server management controllers | Redfish (preferred) or IPMI | PSU state, chassis temps, fans, hardware event log |
| Anything with an HTTP status page | HTTP/JSON probe | status code, latency, a JSON field |

The device list lives in the hub (`devices` table), edited from the console. Agent machines register themselves
by enrolling; agent-less devices are added by hand (address + type + credentials).

---

## 4. "Out of normal": how alerts decide

Three layers, all per device and metric:

1. **Hard limits** (always on, shipped defaults, editable): offline > 2 min, disk free < 10 %, SMART failing,
   UPS on battery, GPU Xid/ECC error, CPU/GPU temp over a vendor-sane limit, PSU/fan failed.
2. **Learned baseline** (after 7 days of data): for each metric keep a rolling **median and MAD per
   hour-of-week** (e.g. "Tuesday 14:00 on this PC"). Alert when a reading stays outside
   `median ± k·MAD` (k≈4) for N consecutive samples (N≈5 min). Catches things like "this GPU idles 20 °C hotter
   than it used to" or "this switch port suddenly has errors" that no fixed limit would.
3. **Stale / missing data**: a metric that stops arriving is its own alert ("could not read" is never shown green;
   borrowed from `ait`'s rule).

Noise control (the part that makes people keep alerts on):
- **Hysteresis**: raise at the threshold, clear only when well back inside it.
- **Dedupe + grouping**: one message per incident, not per sample; a switch going down suppresses the
  "unreachable" alerts of everything behind it (simple parent/child links in the device list).
- **Severity**: `info` (console only), `warning` (message in working hours), `critical` (message any time).
- **Maintenance windows / snooze** per device.
- **Resolve messages**: "back to normal after 14 min".

---

## 5. Messages

- Router with pluggable channels. Candidates (decision 3): **Microsoft Teams** (incoming webhook / workflow),
  **email** (SMTP or Microsoft 365), **phone push** (ntfy / Pushover), Slack.
- Each message: device, what is abnormal, current vs normal value, since when, a link/ID to open it in the console.
- Daily digest (optional): warnings that didn't page, devices with growing trends (disk filling, rising temps).

---

## 6. Agent and management (Windows PCs)

- Agent sends a summary every 10 s, live 1 Hz only while a console is viewing that machine (same idea as the app's
  "poll only while shown"), inventory on change.
- Management commands are **typed and allowlisted** (no shell passthrough), checked against a **local policy file**
  (`%ProgramData%\reddholo\policy.json`) and audited on both ends.
- Long operations (uninstall, cleanup) are **hub-side jobs** with streamed progress.

### Windows gotchas found while reading the code

| Problem | Why | Plan |
|---|---|---|
| Per-user apps vanish | The engine reads `HKEY_CURRENT_USER`. As LocalSystem that is SYSTEM's hive. | Walk `HKEY_USERS\<SID>` per profile; tag apps with their user. |
| Store apps are per user | Appx packages are per user. | Query and uninstall with all-users scope. |
| GUI uninstallers hang | A service runs in session 0; GUI windows there are invisible. | Remote uninstall uses quiet uninstallers only (`BCU-console /Q` shows the engine supports it); others are flagged "do at the PC". |
| Startup entries and cleanup are per user | Run keys, Startup folders, temp folders are per profile. | Iterate profiles; backups to `%ProgramData%\reddholo\StartupBackups\<user>\`. |
| CPU sensors need PawnIO | LibreHardwareMonitor's driver. | Agent installer offers the bundled, hash-checked PawnIO. |

---

## 7. Security (remote admin with SYSTEM rights plus device credentials)

1. Hub listens on LAN, WireGuard / L2TP VPN and SSH only.
2. Agent **enrollment** with a one-time code → client certificate → **mutual TLS**, hub certificate pinned.
3. Commands typed + allowlisted; agent-side policy can refuse; append-only audit on hub and agent.
4. Device credentials (SNMPv3, Redfish, NAS) stored encrypted on the hub (DPAPI on Windows / a key file with
   root-only permissions on Linux). Prefer read-only SNMP/Redfish accounts.
5. Revoking a device in the console revokes its certificate.

---

## 8. Changes inside this repo

- New projects: `source/Reddholo.Protocol` (`.proto`), `source/Reddholo.Agent`, `source/Reddholo.Hub`.
- WPF app seam refactor (no behaviour change): per-feature service interfaces (`IAppsService`, `ICleanupService`,
  `IStartupService`, `IDriversService`, `ISensorFeed`, `IGpuFeed`) with local implementations moved out of the
  views; `IMachine CurrentMachine` on `IHoloHost`. Sensor-reading code moves into a library both the app and the
  agent use.
- `BcuHolo.Plugins` only gains members, so existing plugins keep loading.

---

## 9. Phases and "done means"

| Phase | Scope | Done means |
|---|---|---|
| 0. Spike | Windows agent → hub summary every 10 s; hub pings 2 network devices; console Fleet page. | Server + two PCs + switch visible on one screen; unplugging a PC shows offline within 30 s. |
| 1. Monitoring | mTLS enrollment, Windows + Linux agents (hardware, SMART, GPU, event logs), SNMP / NUT / Redfish probers, SQLite history, per-device pages. | Every device in the inventory reports; 30-day charts work. |
| 2. Alerting | Hard limits, stale-data alerts, message channels, dedupe/hysteresis/maintenance, resolve messages. | Pulling a PC's network, unplugging the UPS, filling a disk each produce exactly one message and one "resolved". |
| 3. Baselines | Per hour-of-week median/MAD, anomaly alerts, daily digest. | After 7 days, an induced abnormal (e.g. a stress test at an unusual hour) alerts; normal daily load does not. |
| 4. Management | Seam refactor, quiet uninstall / cleanup / startup jobs, local policy, two-sided audit. | An app uninstalled from the server is gone on the PC with audit entries on both sides; policy can block it. |
| 5. Packaging | Agent MSI + Linux package with enrollment code, hub installer, signed agent self-update, DB backup. | A new device is enrolled with one command. |

---

## 10. Relationship to `CluerDevs/ait`

`ait` ("watches an estate, pages a human when something genuinely matters, repairs a bounded set of faults") is the
same purpose as phases 2–3. By its own README it has a safety layer and result model but **no scheduler, no probes
and no remote reach**, written in TypeScript/Node.

Options (decision 5):
- **A. reddholo hub does collection + alerting** (recommended for v1): one .NET codebase, reuses the app's sensor
  code, fastest path to working alerts. Port `ait`'s rules: dry-run by default, explicit allowlist,
  "could not run" is never "pass".
- **B. reddholo collects, `ait` decides**: the hub exposes readings (e.g. a Prometheus-style endpoint `ait`'s
  `promql` probe already understands); `ait` owns alert logic and later auto-repair. Two systems to run.
- **C. Merge** into one product later, after both have real users.

---

## 11. Decisions needed before Phase 0

1. **Hub server**: **pve1** (Proxmox, Linux), decided 2026-11-24, to take the processing off the desktops. Run the hub in its own LXC container on pve1, not on the Proxmox host itself, so a hub update can't affect the hypervisor. The WPF console runs on Windows PCs and connects to the hub over the LAN / VPN.
2. **Hardware inventory**: list of device types and rough counts (PCs, Linux GPU hosts, switches/router make,
   UPS make, NAS make, servers with BMC). This picks the probers for Phase 1.
3. **Where messages go**: Teams, email, phone push, Slack? Who receives critical at night?
4. **Network**: LAN plus WireGuard / L2TP VPN, SSH for admin. Settled, local-first.
5. **`ait`**: option A, B or C above.

## 12. Risks

- **Blast radius** of the management side: a compromised hub could uninstall fleet-wide. Typed allowlist, local
  policy, mTLS, audit; extra confirmation for multi-device actions.
- **Alert fatigue** kills monitoring. Phase 2's "exactly one message per incident" test is the bar.
- **Engine assumptions**: `UninstallTools` was built for an interactive admin, not LocalSystem; Phase 1 should diff
  agent inventory against a local reddholo on each pilot PC.
- **Credentials** for network/storage gear live on the hub; use read-only accounts.
- **Single hub** is a single point of failure for alerts: the hub should alert on its own probers stalling, and a
  tiny external heartbeat (e.g. a phone-push "dead man's switch") should page if the hub itself goes silent.
