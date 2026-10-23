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
| `src\EasyVPN.Shared` | API contracts, and the gost and PowerShell helpers both services use |
| `build` | `publish.ps1` and the two install scripts |

## Building

```powershell
dotnet build EasyVPN.slnx        # development
.\build\publish.ps1              # release: artifacts\Gateway and artifacts\Client
```

Releases are self-contained (no .NET needed on the machines). `publish.ps1` downloads gost 3.3.1
and Wintun 0.14.1 and checks them against pinned SHA-256 hashes.

## Setting up

**The gateway.** A Windows 10/11 or Server machine, often a VM, with the customers' VPN clients
installed. Copy `artifacts\Gateway` to it and, as administrator:

```powershell
.\install-gateway.ps1
```

It installs the `EasyVPN Gateway` service, opens TCP 8420 (API) and 8421 (relay) in the firewall, and
prints the address, access key and certificate fingerprint for the workstations. Facilities go in
`%ProgramData%\EasyVPN\Gateway\facilities.json`, which is read again whenever it changes. For VPN
clients that need someone to sign in (MFA, SAML), set the gateway to log on to a desktop that can be
reached with Remote Desktop: the app's *Open gateway desktop* button goes there.

**Each workstation.** Copy `artifacts\Client` to it and, as administrator:

```powershell
.\install-client.ps1 -Gateway gw01 -AccessKey <key from the gateway>
```

It installs the `EasyVPN Agent` service and the app, with a Start menu shortcut. The gateway can also
be set later, in the app's Settings. The first time the agent reaches the gateway it pins the
gateway's certificate; Settings shows the fingerprint to compare with the gateway's.

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
| `password` | Handed to the connector as `EASYVPN_PASSWORD`. Make the encrypted form with `EasyVPN.Gateway.exe protect`; only this gateway can decrypt it. |
| `routes` | The facility's networks, in CIDR. Workstations route these, and only these, to the gateway; the relay reaches nothing else. |
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
To add or adapt one, put it in `%ProgramData%\EasyVPN\Gateway\connectors\`, which wins over the
shipped folder and survives updates.

## Security

- The gateway API needs the access key and is served over TLS with the gateway's own certificate,
  which workstations pin. The relay uses the same certificate (pinned by gost) and the same key.
- The relay opens only while a facility is connected, and then only to its `routes` and DNS servers.
- Keys, the private key and VPN passwords live in `%ProgramData%\EasyVPN`, readable by SYSTEM and
  administrators only; passwords are also DPAPI-encrypted to the gateway machine.
- The agent's API listens on 127.0.0.1 only, refuses other host names (DNS rebinding) and requires a
  custom header for changes (so a web page cannot drive it). Any user signed in to the workstation
  can use it, including choosing the gateway: fine for an engineering PC, worth knowing on a shared one.

## Limits

- **TCP and UDP only.** The tunnel carries connections, not packets: `ping` through it does not
  reach the facility (use the check, or a TCP port). Connections the facility opens towards the
  workstation are not carried.
- **No Layer 2.** PROFINET DCP discovery and other Ethernet-level protocols do not cross any routed
  VPN; address devices by IP.
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
