# EasyVPN

Quick connections to remote facilities, whatever VPN client each customer requires.

One Windows **gateway** has every customer's VPN client installed and connects whichever facility is
asked for. Each engineering **workstation** has one permanent network adapter, `EasyVPN`, and an app
in the style of DynatecRDM: pick a facility, press Connect, and TIA Portal, a browser, Remote Desktop
or anything else reaches the facility's machines through that adapter. The workstation never needs
to know whether the facility uses FortiClient, Cisco, OpenVPN or anything else.

```text
WORKSTATION                                   GATEWAY (a VM or a small PC)
                                                FortiClient · Cisco · OpenVPN · WireGuard · Windows VPN …
 EasyVPN app ──── local API ──── EasyVPN Agent ═══ HTTPS :8420 ═══▶ EasyVPN Gateway
 (pick a facility)                (service)                          (service: runs the facility's
                                     │                                connector, checks the facility)
 TIA Portal, browser, RDP …          │                                       │
        │ routes for the facility    │                                       │
        ▼                            ▼                                       ▼
 "EasyVPN" adapter ── gost (tungo) ═══ TLS :8421 ═══▶ gost relay ──▶ the active VPN ──▶ PLCs, HMIs
   (Wintun)            TCP and UDP     pinned cert,     only to the facility's
                                       access key       networks; closed otherwise
```

## How it works

- **The gateway connects one facility at a time**, shared by every workstation. Connecting takes
  down whatever was up, runs the facility's *connector* (a small PowerShell script that drives one
  kind of VPN client), and waits until the facility's *check* address (a PLC's port 102, an HMI's
  443) answers. Only then does it open its relay, and only to that facility's networks.
- **The workstation's adapter stays put.** The agent keeps gost running in tungo mode: a Wintun
  adapter whose TCP and UDP traffic goes over TLS to the gateway's relay, which makes the
  connections itself, so through whichever VPN is up. Switching facilities changes only which
  networks are routed to the adapter, and which DNS names go to the facility's DNS servers (via the
  Name Resolution Policy Table, so the PC's own DNS is untouched).
- **Connected means verified.** The gateway calls a facility connected when its check answers from
  the gateway; the agent when a connection from the workstation reaches it through the tunnel.
- **It fails closed.** If the facility stops answering, the gateway closes the relay until it
  answers again, so traffic stops rather than going some other way. The workstation keeps its
  routes until you disconnect. Connecting another facility is refused while the previous one still
  answers, because two customers often use the same addresses (`192.168.0.10` at both) and a VPN
  left up underneath would quietly take the other one's traffic.
- **Layer 2 when the VPN has it.** A facility whose VPN gives the gateway its Ethernet (a Tosibox
  Lock in Layer 2 mode) reaches the workstation as Ethernet too, on a second adapter: see
  [Layer 2 facilities](#layer-2-facilities).

This is the design worked out in the "VPN client router" discussion: one shared Windows gateway VM,
one permanent adapter, one active customer, and a controller. Its recommended building blocks are
used: GOST v3 (TUNGO + relay, MIT) and Wintun. Neither Internet Connection Sharing nor a driver of
our own is involved, and the vendor clients run untouched, as they were designed to.

## Projects

| Folder | What it is |
| --- | --- |
| `src\EasyVPN.Gateway` | The gateway service: API, facility catalog, connectors, relay, connection controller |
| `src\EasyVPN.Gateway\connectors` | One script per kind of VPN client |
| `src\EasyVPN.Agent` | The workstation service: tunnel, routes and DNS (`network.ps1`), session |
| `src\EasyVPN` | The app: WPF, using DynatecRDM's design system (`Themes\`), light and dark |
| `src\EasyVPN.GatewayApp` | The EasyVPN Gateway app, on the gateway's desktop: its status, what workstations need, and what it is missing |
| `src\EasyVPN.Shared` | API contracts; the gost, PowerShell and Layer 2 (`layer2.ps1`) helpers the services share |
| `build` | `publish.ps1`, `release.ps1` and the two install scripts |
| `installer` | The two MSIs, gateway and workstation (WiX 5) |

## Building

```powershell
dotnet build EasyVPN.slnx              # development
.\build\publish.ps1                    # artifacts\Gateway and artifacts\Client, with the install scripts
.\build\release.ps1 -Version 1.2.3     # the two installers, as a GitHub release has them
```

Everything is self-contained (no .NET needed on the machines). `publish.ps1` downloads gost 3.3.1
and Wintun 0.14.1 and checks them against pinned SHA-256 hashes. `release.ps1` packages its two
folders as MSIs (`installer\`) with WiX 5, which needs, once:

```powershell
dotnet tool install --global wix --version 5.0.2
wix extension add -g WixToolset.Util.wixext/5.0.2 WixToolset.Firewall.wixext/5.0.2
```

**Releases.** `release.ps1` only builds; GitHub publishes. Commit and push, then:

```powershell
.\build\github-release.ps1 -Version 1.2.3
```

It checks that the commit is pushed and the version is new, then tags it `v1.2.3` and pushes the
tag. That makes `.github\workflows\release.yml` build a GitHub release with
`EasyVPN-Gateway-1.2.3-x64.msi`, `EasyVPN-Client-1.2.3-x64.msi` and `SHA256SUMS.txt`, both
installers from that commit. The script follows the build and prints the release's address.
Pushing the tag by hand does the same (or Actions > Release > Run workflow, with a version).

Versions are three or four numbers: MSI compares numbers only, so `1.2.3-beta` could not upgrade
`1.2.3`. The installers are not code-signed, so SmartScreen asks before running a downloaded one.

## Setting up

Each installer installs, or updates whatever version is there, and asks for administrator rights.
`msiexec /i <installer> /qn` installs silently. Uninstalling (Apps & Features) leaves the settings,
keys and facilities in `%ProgramData%\EasyVPN`.

**The gateway.** A Windows 10/11 or Server machine, often a VM, with the customers' VPN clients
installed. Run `EasyVPN-Gateway-<version>-x64.msi` on it. It installs the `EasyVPN Gateway` service
in `%ProgramFiles%\EasyVPN Gateway`, opens TCP 8420 (API) and 8421 (relay) in the firewall, and
installs the **EasyVPN Gateway app**, which opens when the installer finishes and then lives in the
notification area of everyone who signs in to the gateway (and in the Start menu). It shows:

- the connection the gateway carries, who asked for it, and its activity; a facility can be
  connected from there too, to try it without a workstation;
- what each workstation needs: the gateway's address, its access key (*Show the access key* asks
  for administrator rights) and its certificate fingerprint, each a click from the clipboard;
- every facility, and whether it is ready: its connector checks, without connecting anything, that
  its VPN client is installed and its settings are in place (the profile file is there, the
  Windows VPN connection exists, …);
- what the gateway itself lacks: the firewall rule, and for Layer 2 OpenVPN, the *EasyVPN L2*
  adapter and its bridge, each with a button that fixes it or shows where to;
- mistakes in `facilities.json`, which *Edit facilities* opens in Notepad as administrator.
  *Encrypt a password* makes a facility's `password`.

Facilities go in `%ProgramData%\EasyVPN\Gateway\facilities.json`, which is read again whenever it
changes. For VPN clients that need someone to sign in (MFA, SAML), set the gateway to log on to a
desktop that can be reached with Remote Desktop: the app's *Open gateway desktop* button goes there.
`EasyVPN.Gateway.exe info`, in an administrator console, prints the address, key and fingerprint too.

The customers' VPN clients are not part of EasyVPN: install each one the way its vendor says. The
gateway app says which facility is still missing its client.

**Each workstation.** Run `EasyVPN-Client-<version>-x64.msi`. It installs the `EasyVPN Agent`
service and the app in `%ProgramFiles%\EasyVPN`, with a Start menu shortcut, and starts the app:
choose the gateway in its Settings. To deploy it with the gateway already chosen:

```powershell
msiexec /i EasyVPN-Client-1.2.3-x64.msi /qn GATEWAY=gw01 ACCESSKEY=<key from the gateway>
```

The first time the agent reaches the gateway it pins the gateway's certificate; Settings shows the
fingerprint to compare with the gateway's. An update closes the app on every account signed in; run
by double-clicking, it starts the new one again afterwards.

**A development build.** `publish.ps1`'s folders carry install scripts instead. Copy
`artifacts\Gateway` or `artifacts\Client` to the machine and, as administrator, run
`.\install-gateway.ps1`, or `.\install-client.ps1 -Gateway gw01 -AccessKey <key>`; `-Uninstall`
removes it again. They use the same folders and services as the installers, so remove one kind
before installing the other.

## Facilities

`facilities.json` on the gateway; `src\EasyVPN.Gateway\facilities.sample.json` has one of each kind.

```json
{
  "facilities": [
    {
      "id": "acme-oslo",
      "name": "Oslo plant",
      "customer": "ACME",
      "connector": "cisco",
      "settings": { "host": "vpn.acme.example", "user": "dynatec-service" },
      "password": "dpapi:AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA…",
      "routes": [ "10.60.0.0/24" ],
      "dns": { "domains": [ "plant.acme.local" ], "servers": [ "10.60.0.53" ] },
      "check": "10.60.0.10:102",
      "timeout": 120,
      "notes": "Line 1 S7-1500 at 10.60.0.10.",
      "links": [ { "name": "HMI", "url": "rdp://10.60.0.20" } ]
    }
  ]
}
```

| Field | |
| --- | --- |
| `id` | Letters, digits, `.`, `-`, `_`. |
| `customer` | Groups the list in the app. |
| `connector` | Which script in `connectors\` connects it. |
| `settings` | Handed to the connector as `EASYVPN_<KEY>` environment variables. |
| `password` | Handed to the connector as `EASYVPN_PASSWORD`. Make the encrypted form with *Encrypt a password* in the gateway app (or `EasyVPN.Gateway.exe protect`); only this gateway can decrypt it. |
| `routes` | The facility's networks, in CIDR. Workstations route these, and only these, to the gateway; the relay reaches nothing else. |
| `layer2` | `true` for a facility whose VPN gives the gateway Layer 2: see [Layer 2 facilities](#layer-2-facilities). It needs no `routes`. |
| `dns` | Names under `domains` resolve through `servers` while connected. Optional. |
| `check` | `host:port` inside the facility that answers when it is reachable. Strongly recommended: without it a connector that runs without error is taken at its word, and switching cannot confirm the previous facility is really down. |
| `timeout` | Seconds to wait for the check after the connector has run (default 120); allow time to sign in for `manual`. |
| `links` | Buttons in the app: `https://…` opens the browser, `rdp://host` opens Remote Desktop. |

## Connectors

| Connector | VPN client | Settings |
| --- | --- | --- |
| `manual` | Any client: someone connects it on the gateway desktop (MFA, SAML, FortiClient, GlobalProtect, Ivanti, eWON, SINEMA RC …). EasyVPN waits for the check. | none |
| `windows` | The VPN built into Windows (IKEv2, SSTP, L2TP), dialled with `rasdial`. Create the connection with `Add-VpnConnection -AllUserConnection`. | `name`, `user` |
| `openvpn` | OpenVPN 2.6+ community client | `config`, `user` |
| `wireguard` | WireGuard for Windows | `config` |
| `cisco` | Cisco Secure Client / AnyConnect, through `vpncli` | `host`, `user`, `group` |

A connector is `connectors\<name>.ps1`, run as `<name>.ps1 Connect` or `<name>.ps1 Disconnect`,
with the facility's settings in its environment plus `EASYVPN_FACILITY` and `EASYVPN_WORK` (a
private folder for it). Exit code 0 is success; whatever it prints shows in the app's Activity.
`<name>.ps1 Check` (without the password) must not connect anything: it says whether the facility
could be connected from here, exit code 0 for ready, and what it prints shows in the gateway app.
To add or adapt one, put it in `%ProgramData%\EasyVPN\Gateway\connectors\`, which wins over the
shipped folder and survives updates.

## Layer 2 facilities

Some VPNs give the gateway the facility's Ethernet itself, not just its networks: a Tosibox Lock in
Layer 2 mode, for one. Mark such a facility `"layer2": true` and the workstation gets the same. Its
**EasyVPN L2** adapter sits on the facility's LAN, with an address from the facility's DHCP (or one
set by hand), and broadcasts, ARP and PROFINET DCP discovery work: in TIA Portal, choose
*EasyVPN L2* as the PG/PC interface to find accessible devices.

```text
WORKSTATION                                                   GATEWAY
 TIA Portal ─▶ "EasyVPN L2" ══ Ethernet frames, as WebSocket ══▶ "EasyVPN L2"
               (TAP adapter)   messages on HTTPS :8420           (TAP adapter)
                                                                       ║  Windows Network Bridge
                                                                 Tosibox's Layer 2 adapter ─▶ Lock ─▶ plant LAN
```

- The frames go over the gateway's API port, with the same access key and pinned certificate: no
  other port, no other firewall rule. On the gateway, its own *EasyVPN L2* adapter is bridged, once,
  with the VPN client's Layer 2 adapter.
- The link opens only while the facility is connected. When the facility stops answering or is
  disconnected, the link ends and both adapters' cables are pulled out.
- One workstation at a time: a newer one takes the link over, and the other one's app says who did.
  Routed facilities carry on as before.
- `routes` and `dns` are not used: the facility's LAN is simply the adapter's network. Its `check`
  is reached from the gateway through the bridge, so the bridge needs an address on that LAN (DHCP
  from the facility will do).

```json
{ "id": "aqua-hitra", "name": "Hitra hatchery", "customer": "Aqua Nord", "connector": "manual",
  "layer2": true, "check": "192.168.20.10:102", "timeout": 180 }
```

**Setting it up.** Both ends need OpenVPN's TAP-Windows6 driver, which EasyVPN does not ship:
install OpenVPN 2.6 or later, with its TAP driver, on the gateway and on each workstation that uses
Layer 2 facilities. Its `tapctl` then makes the *EasyVPN L2* adapter, as SYSTEM:

- on a **workstation** the agent does it by itself, the first time a Layer 2 facility is connected
  (and gives it a high metric, so a default gateway from a facility's DHCP never wins over the PC's
  own). Without OpenVPN, connecting says so and where to get it;
- on the **gateway**, the gateway app's *Layer 2* check has an *Add the adapter* button. Then bridge
  the two adapters, once, by hand, since Windows has no supported command for it: in Network
  Connections (the check's *Open Network Connections* button) select *EasyVPN L2* and the VPN
  client's Layer 2 adapter, right-click, and choose *Bridge Connections*. Bridge VPN adapters only,
  never the gateway's own network. The check turns green once it is bridged.

The install scripts' `-Layer2` switch adds the adapter at install time instead. A Layer 2 facility on
a gateway without its *EasyVPN L2* adapter fails straight away, saying so.

**Try it on the real gateway before relying on it.** The pieces this could not be tested with:

- Tosibox does not document its Windows Layer 2 adapter, or putting it in a bridge. Check that the
  TOSIBOX Key keeps working with its adapter bridged. Other OpenVPN-based Layer 2 clients, such as
  eWON eCatcher, work the same way.
- Frames travel over TCP (TLS), which is fine for engineering traffic; heavy packet loss on the path
  makes it sluggish, as with any VPN over TCP.
- Bridge one VPN's Layer 2 adapter per gateway if you can. With more, only the connected facility's
  adapter carries anything, since EasyVPN connects one facility at a time.
- A program that grabs any free TAP adapter (an OpenVPN TAP profile without `dev-node`, eCatcher)
  could take *EasyVPN L2* while it is not in use; point those at an adapter of their own.

## Security

- The gateway API needs the access key and is served over TLS with the gateway's own certificate,
  which workstations pin. The relay uses the same certificate (pinned by gost) and the same key.
- The relay opens only while a facility is connected, and then only to its `routes` and DNS servers.
  The Layer 2 port opens only while a Layer 2 facility is connected, behind the same key and certificate.
- Keys, the private key and VPN passwords live in `%ProgramData%\EasyVPN`, readable by SYSTEM and
  administrators only; passwords are also DPAPI-encrypted to the gateway machine.
- The agent's API listens on 127.0.0.1 only, refuses other host names (DNS rebinding) and requires a
  custom header for changes (so a web page cannot drive it). Any user signed in to the workstation
  can use it, including choosing the gateway: fine for an engineering PC, worth knowing on a shared one.
- The gateway app talks to the gateway on 127.0.0.1:8428, guarded the same way, which answers
  nothing else. Anyone signed in to the gateway can see its status and connect or disconnect a
  facility, as a workstation can, but not the access key: that takes an administrator.

## Limits

- **TCP and UDP only.** The tunnel carries connections, not packets: `ping` through it does not
  reach the facility (use the check, or a TCP port). Connections the facility opens towards the
  workstation are not carried.
- **Layer 2 only where the VPN has it.** PROFINET DCP discovery and other Ethernet-level protocols
  reach a workstation only from a [Layer 2 facility](#layer-2-facilities), one workstation at a
  time. Through a routed facility, address devices by IP.
- **One facility at a time** for all workstations sharing a gateway. The app shows who switched it.
- **The VPN client must let the gateway's own network in.** Some clients block local network access
  while connected (Cisco's "Allow local LAN access", GlobalProtect's "No direct access to local
  network"). The agent says so when the relay stops answering after a connect.
- Overlapping networks: if a facility uses the same subnet as the workstation's own LAN, the facility
  wins while connected; the Activity log warns about it.

## What has been tested

On a development PC (not elevated): the gateway end to end with a test connector, covering
connecting, the relay with the generated certificate pinned by gost, the access key, the whitelist,
UDP, a facility that stops answering and comes back, refusing a switch while the previous facility
still answers, and disconnecting. Also the agent's pinning, API guards and connect flow up to routing,
and the app against both. Creating the Wintun adapter and the routes needs the agent to run as a
service (SYSTEM), as installed, and the vendor connectors need their VPN clients: try those on the
real gateway, with `manual` first.

For Layer 2: the gateway's Layer 2 port (the key, refusing links while no Layer 2 facility is
connected), a Layer 2 facility failing cleanly on a gateway without the adapter, and the app showing
Layer 2 facilities. Frames through real TAP adapters, the bridge and a TOSIBOX Key were not: they
need administrator rights, the adapters and a Tosibox Lock.
