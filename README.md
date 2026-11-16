# Usage Meter

<a href="LICENSE"><img alt="MIT License" src="https://img.shields.io/badge/license-MIT-C51B3A?style=flat-square"></a>
<img alt="Version 1.0.1" src="https://img.shields.io/badge/version-1.0.1-C51B3A?style=flat-square">
<img alt="C#" src="https://img.shields.io/badge/language-C%23-239120?style=flat-square&amp;logo=csharp">
<img alt=".NET 8" src="https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&amp;logo=dotnet">
<img alt="Windows 10 and 11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?style=flat-square&amp;logo=windows11">

Usage Meter records **observed IPv4 payload usage** by MAC address and keeps
those measured totals and device names between restarts. It is not a complete
router/WAN meter: traffic that does not pass through a supported capture path
is not counted, and **zero observed does not mean zero used**.

> [!NOTE]
> Usage Meter is based on
> [LANtern Control](https://github.com/humane125/LANtern-Control) by humane125,
> released under the MIT License. It is a modified version of LANtern Control,
> not a new project written from scratch. Device discovery, the ARP engine,
> packet capture, speed limits, domain features and the user interface all come
> from LANtern Control. See [NOTICE](NOTICE).

## What Usage Meter adds

- **Persistent observed usage.** Download, upload and combined counters keep
  their existing meaning: observed IPv4 transport-payload bytes, not full IP
  packets, Ethernet bytes, ISP billing, or confirmed delivery. Existing totals
  are preserved without conversion, estimated backfill, or invented traffic.
- **MAC-keyed inventory, not one device per IP.** A changed IP under the same
  MAC keeps its saved profile and totals. A different/private MAC remains a
  separate record even if its advertised name matches another device. The app
  does not automatically transfer names, rules or consumption across MACs;
  MAC records are not proof of distinct physical devices.
- **Durable device names and inventory.** Saved preferences and usage records
  are shown before rediscovery and are not removed after a short silence.
  Manual names take precedence over automatic names; IP addresses are displayed
  separately. Matching hostnames are allowed and are not evidence for merging.
- **A protected row for this computer.** Conservatively identified original
  local IPv4 internet payload is measured separately from forwarded packets.
  The row can be renamed but cannot be paused or limited. IPv6 usage remains
  outside the cumulative counters.
- **Background persistence.** Usage is saved every 30 seconds independently of
  the UI, then flushed when control stops, on normal close, and during Windows
  session ending. Saving pending usage does not consume the live-speed window.
  A crash or power loss can still lose traffic since the last successful save;
  traffic used while the app is closed cannot be recovered by this capture mode.
- **A simpler Windows overview.** Six columns show identity, separate IP and
  observation state, download/upload rates, measured usage, and configured IPv4
  policy. Select a row for MAC, exact byte totals, name and policy editors.
  Sizes use decimal units (1 KB = 1000 bytes). Stopped rates are `—`, not a
  claim that a device is idle or offline. Page scroll positions are retained.
- **Truthful diagnostics.** Persistent status reports capture visibility,
  observed directions, IPv6 sightings, capture/injection problems and save
  failures. None of these is a completeness certificate.
- **No inherited update prompt.** The Windows app does not check the LANtern
  Control releases for updates and never offers to replace itself with a
  LANtern release.

## Features inherited from LANtern Control

- Discover devices connected to the same Ethernet or Wi-Fi network.
- Resolve device names using DHCP, reverse DNS, NetBIOS, and mDNS information.
- Remember device names, aliases, and rules between application launches.
- Display live per-device download and upload speeds.
- Keep a ten-minute network activity chart with 2.5-second samples.
- Apply independent download and upload limits in `KB/s`.
- Pause and restore a device's IPv4 internet connection.
- Observe domains through DNS queries, TLS server names, and plain HTTP host
  headers without decrypting private HTTPS content.
- Block domains for individual devices, including presets for common services.
- Attempt corrective client/router IPv4 ARP restoration when control stops;
  sending repair frames does not verify that peers accepted them.

## Where data is stored

Usage Meter keeps its data in its own folder and never writes to the
`LANternControl` folder, so it can be installed next to LANtern Control.

```text
Settings:     %LOCALAPPDATA%\UsageMeter\settings.json
Usage totals: %LOCALAPPDATA%\UsageMeter\usage.json
```

Writes use temporary files followed by replacement, with a backup beside each
file (`settings.backup.json`, `usage.backup.json`). A valid backup can recover a
missing or unreadable primary. If neither saved copy is readable, monitoring
stays disabled rather than accepting an empty history and replacing the files.
Usage saves validate schema and nonnegative counters and cannot lower or omit
existing totals. Conflicting usage copies or a newer schema require manual
recovery, not automatic summing or guessing. Save failures remain visible and
are retried; a normal close waits for the final save.

Only one updated app instance may run for a Windows user, including across
Windows sessions. Do not run older binaries alongside it: they do not share
all of the new persistence protections.

The usage JSON remains schema version 1; saved counters are not reinterpreted
as IPv6, full-packet or WAN totals. Before manual recovery, close every copy of
the app and make a separate copy of the entire data folder. To intentionally
reset all usage, remove both usage files only after backing them up; deleting
one copy alone allows the other to restore it. Device aliases/settings are
stored separately.

## Screenshots

The screenshots below come from the original LANtern Control project and use
synthetic demo data. They are historical references, not screenshots of the
current simplified overview, diagnostics or observed-domain labels.

### Network overview (LANtern Control)

![LANtern Control network overview](assets/screenshots/overview.png)

### Historical domain screen (LANtern Control)

![LANtern Control visited domains](assets/screenshots/visited-domains.png)

## Requirements

- Windows 10 or Windows 11, x64.
- Administrator access.
- [Npcap](https://npcap.com/#download) installed on the controller computer.
  WinPcap API-compatible mode is recommended for compatibility.

Usage Meter does not include or redistribute Npcap. Install Npcap separately
from its official website, then restart Usage Meter.

The controller computer and target devices must be on the same IPv4 broadcast
network. Ethernet is recommended. If Wi-Fi is used, keep the controller near
the router and prefer 5 GHz when possible.

Windows is the target platform for Usage Meter 1.0.1. The Linux project from
LANtern Control still builds, but the usage columns are only in the Windows
app.

## Installation

Build the installer or portable version from source (see below). The build
produces:

- `Usage-Meter-Setup-v1.0.1.msi`: run it, choose the installation folder and
  shortcuts, then launch Usage Meter and accept the Administrator prompt.
- `Usage-Meter-v1.0.1-win-x64.zip`: extract the whole archive to a folder where
  it can stay, then run `Usage Meter.exe` as Administrator.

The Usage Meter installer is separate from the LANtern Control installer and
does not replace an existing LANtern Control installation.

Always click **Stop & restore** before closing Usage Meter, changing networks,
disconnecting the selected adapter, suspending, or rebooting.

## How to use

1. Launch Usage Meter as Administrator.
2. Select the network adapter whose local IP address and gateway match the
   network you want to measure.
3. Review saved devices and the protected **This computer** row. These records
   are remembered metadata, not a claim that every device is currently online.
4. Click **Start control**, then inspect the persistent scope notice and
   diagnostics. A device's **MEASURED** value grows only for observed traffic
   that reaches the accounting paths. Select it for exact down/up/combined bytes.
5. Use **Refresh devices** for a fresh discovery scan without clearing history.
   Name or policy edits are saved separately; blanking a manual name restores
   the actual automatic name, not a copied alias.
6. Click **Stop & restore** before changing adapters, leaving the network, or
   shutting down the controller computer. A restoration warning must not be
   treated as proof that the network was restored successfully.

New usage is only counted while capture is active. Background saving continues
while the app is open even if a different page or dialog is visible. Restored
records have no live speed until monitoring starts and fresh packets are seen.

No router username or password is required, and Usage Meter does not
permanently change the router configuration.

## Domain visibility and blocking

**Observed domains are network signals, not visits or browsing history.** DNS,
TLS SNI and plain HTTP host observations may be produced by background apps,
caches and shared services. The app does not decrypt HTTPS, read searches,
inspect messages, or view private page contents.

Observations are session-only and are not written to disk. The view retains at
most **250 device/domain pairs globally**, evicting the oldest last-observed
pairs. Counts, sources and original first/last times are aggregated in 200 ms UI
batches without repeatedly moving rows or dropping offline groups that still
have retained observations. A bounded 2,048-key pending buffer explicitly
reports any omitted observations if full; these display omissions do not reset
or modify the separate usage counters. Clearing the view or starting a new
capture session clears observations, not usage totals or saved block rules.

A configured rule and the last observed signal's drop outcome are shown
separately: adding or removing a rule does not rewrite past outcomes. Domain
blocking is IPv4-only and best effort because applications may use cached
connections, several CDN domains, or encrypted protocols.

The following can hide domains or bypass domain-based rules:

- VPNs, Tor, and proxy applications.
- Encrypted DNS such as DoH or DoT.
- TLS Encrypted Client Hello (ECH).
- QUIC/HTTP3 and cached or already-open connections.
- Applications that use direct IP addresses or frequently changing domains.

## Network compatibility

Usage Meter works on the local IPv4 network rather than through a
router-specific API, so it can work with many ordinary home routers. It cannot
be guaranteed on every network or adapter.

- Captured IPv6 frames are diagnostic only. IPv6 internet usage is not added
  to totals or intercepted/forwarded by this engine. The app does not disable
  IPv6 or send NDP interception packets; IPv4 pause/domain rules do not cover
  IPv6 bypass paths.
- Guest Wi-Fi or client isolation can prevent discovery and measurement.
- Dynamic ARP inspection or other ARP protection can block interception.
- Devices behind another router or NAT are not on the same broadcast LAN.
- Some Wi-Fi drivers do not reliably support packet capture or injection.
- While a device is redirected, the controller computer becomes part of its
  traffic path. A weak controller connection can reduce speed or increase
  latency for that device.
- The controller must remain awake and connected while measuring.
- MAC/address observations are not proof of a complete physical-device
  inventory. Gateway/proxy, protected and out-of-subnet mappings are rejected;
  conflicting or changing bindings produce diagnostics. Keeping a latest
  binding does not solve every multi-address or private-MAC topology.

**Complete per-device IPv4/IPv6 accounting is not established by these fixes.**
It needs a suitable router accounting/export source or a genuine gateway/bridge
capture point, plus controlled upload/download comparisons from the devices
being measured. Validate both directions, IPv4/IPv6 paths, capture drops and the
router's counters before relying on totals for quotas or billing. Do not disable
IPv6 merely to make partial counters look complete.

## Troubleshooting

### Npcap is not detected

Install the latest version from the [official Npcap download page](https://npcap.com/#download),
enable WinPcap API-compatible mode if offered, and restart Usage Meter.

### Devices do not appear

- Confirm that the correct adapter is selected.
- Make sure the controller and devices are on the same normal LAN, not an
  isolated guest network.
- Run Usage Meter as Administrator.
- Click **Refresh devices** and allow the discovery scan to finish.

### A device loses connectivity

Click **Stop & restore**, wait for the restoration attempt to finish, and read
its final status. Peer acceptance cannot be verified by sending frames alone.
If connectivity does not return, reconnect the affected device to Wi-Fi or
disable and re-enable its network adapter.

## Building from source

Requires the .NET 8 SDK. The WiX 5 SDK used to produce the Windows MSI is
restored automatically by `dotnet`.

```powershell
dotnet test .\tests\Lantern.Core.Tests\Lantern.Core.Tests.csproj
dotnet test .\tests\Lantern.App.Tests\Lantern.App.Tests.csproj
.\scripts\publish.ps1
```

The publishing script runs those tests, then creates the portable ZIP and the
MSI installer in the `outputs` folder.

The project, namespace and solution names (`Lantern.*`, `LanternControl.slnx`)
were kept from LANtern Control to keep the changes small.

## License

Usage Meter is released under the [MIT License](LICENSE).

- Copyright (c) 2026 humane125 (LANtern Control)
- Copyright (c) 2026 mahmoud-pixel7 (Usage Meter modifications)

See [NOTICE](NOTICE) and [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

## Responsible use

Use Usage Meter only on networks and devices that you own or are authorized to
administer. Monitoring, limiting, pausing, or blocking another person's traffic
without permission may violate policies or local laws.

## Feedback and bug reports

If you find a problem, [open a GitHub issue](https://github.com/mahmoud-pixel7/usage-meter/issues)
and include the Usage Meter version, Windows version, adapter type, Npcap
version, router model, and steps needed to reproduce it.

Please report problems in Usage Meter here rather than to the LANtern Control
project.
