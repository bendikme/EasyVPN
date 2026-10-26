# EasyVPN

Quick connections to remote facilities, whatever VPN client each customer requires.

One Windows **gateway** has every customer's VPN client installed and connects whichever facility is
asked for. Each engineering **workstation** has one permanent network adapter, `EasyVPN`, and an app
in the style of DynatecRDM: pick a facility, press Connect, and the adapter works as the facility's
VPN client's own adapter would if it ran on the workstation - ping, TIA Portal, a browser, Remote
Desktop. The workstation never needs to know whether the facility uses Tosibox, Sophos, NetExtender,
Pulse, GlobalProtect, OpenVPN or anything else.

```text
WORKSTATION                                   GATEWAY (a VM or a small PC)
                                                FortiClient · Cisco · OpenVPN · WireGuard · Windows VPN …
 EasyVPN app ──── local API ──── EasyVPN Agent ═══ HTTPS :8420 ═══▶ EasyVPN Gateway
 (pick a facility)                (service)                          (service: runs the facility's
                                     │                                connector, checks the facility)
 TIA Portal, browser, RDP …          │                                       │
        │ routes for the facility    │                                       │
        ▼                            ▼                                       ▼
 "EasyVPN" adapter ── gost (tun) ═══ TLS :8421 ═══▶ gost relay ─▶ hub ─▶ "EasyVPN-Gateway"
   (Wintun)            IP packets      pinned cert,                           adapter (Wintun)
                                       access key                                  │ Internet Connection
                                                                                   ▼ Sharing
                                                              the active VPN's adapter ──▶ PLCs, HMIs
```

## How it works

- **The gateway connects one facility at a time**, shared by every workstation. Connecting takes
  down whatever was up, runs the facility's *connector* (a small PowerShell script that drives one
  kind of VPN client) until the client says it is connected, and, for a facility with a *check*
  address (a PLC's port 102, an HMI's 443), until that answers too. Only then does it open the way
  in: it shares the adapter the facility's networks lead to - its VPN's - with the workstations,
  through Windows' Internet Connection Sharing. The networks are the facility's `routes`, or for a
  facility without any, those its VPN set up on the gateway while connecting (most VPN servers push
  them then, rather than writing them into the profile).
- **Packets, not connections.** The agent keeps gost running a Wintun adapter, `EasyVPN`, whose
  packets - ping as well as TCP and UDP - go over TLS to the gateway's relay, and from there to the
  gateway's hub, which puts them out on its own adapter, `EasyVPN-Gateway`, each workstation at an
  address of its own (198.18.0.x, kept for it). Sharing takes them on into the VPN as if the
  gateway had sent them, and brings the answers back. It is the same for every kind of VPN client,
  since Windows shares an adapter whatever made it.
- **The workstation's adapter stays put.** Switching facilities changes only which networks are
  routed to it, and which DNS names go to the facility's DNS servers (via the Name Resolution
  Policy Table, so the PC's own DNS is untouched). Both adapters are made with a fixed identity, so
  Windows keeps seeing the same network on them, and the workstation's stays named `EasyVPN`
  rather than `EasyVPN 2`, `EasyVPN 3` … after each update. TIA Portal and Device Manager list it
  as *Dynatec EasyVPN*.
- **Connected means verified.** The gateway calls a facility connected when its VPN client says so
  and its check, if it has one, answers from the gateway; the agent when the workstation reaches the
  gateway's relay, and the check through the tunnel. Meanwhile the app shows each step as it
  happens - what the connector does, and every 15 s that it is still waiting for a VPN client.
- **It fails closed.** While a facility is connected the gateway watches it every 10 s: its VPN must
  still carry its networks (they lead into the VPN's adapter, and it is up), and its check, if it has
  one, answer. So a VPN client that loses the connection by itself - a Tosibox Lock gone from Tosi
  Client, an OpenVPN tunnel down - is noticed with or without a check. After two misses the facility
  is *Interrupted*: the gateway stops sharing its VPN, so traffic stops rather than going some other
  way, and the workstations are told. When it is back (on the same adapter or a new one) it is
  shared again and *Connected*. The workstation keeps its routes until you disconnect. Connecting another facility is refused while the previous one still
  answers, because two customers often use the same addresses (`192.168.0.10` at both) and a VPN
  left up underneath would quietly take the other one's traffic.
- **Layer 2 when the VPN has it.** A facility whose VPN gives the gateway its Ethernet (a Tosibox
  Lock in Layer 2 mode) reaches the workstation as Ethernet too, on a second adapter: see
  [Layer 2 facilities](#layer-2-facilities).

This is the design worked out in the "VPN client router" discussion: one shared Windows gateway VM,
one permanent adapter, one active customer, and a controller. Its building blocks are GOST v3 (TUN
and relay, MIT), Wintun and Windows' own Internet Connection Sharing; there is no driver of our own,
and the vendor clients run untouched, as they were designed to.

## Projects

| Folder | What it is |
| --- | --- |
| `src\EasyVPN.Gateway` | The gateway service: API, facility catalog, connectors, relay and hub, sharing (`sharing.ps1`), connection controller |
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
dotnet run --project tests/EasyVPN.Tests  # isolated regression checks (no administrator rights)
.\build\publish.ps1                    # artifacts\Gateway and artifacts\Client, with the install scripts
.\build\release.ps1 -Version 1.2.3     # the two installers, as a GitHub release has them
```

The regression checks use temporary settings and cover profile import, facility validation and
editing, route selection, disconnect cleanup, and the PowerShell helper. Add `-- --services` to
also start a disposable gateway and check its API guards, certificate pinning and connection
lifecycle. That check needs local port 8428 free and replaces sharing and VPN connectors with
stand-ins; it does not change adapters, routes, DNS or Internet Connection Sharing. Real VPN
clients and packet forwarding still need testing on a configured gateway and workstation.

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
- what the gateway itself lacks: the firewall rule, Internet Connection Sharing (the *Internet
  Connection Sharing (ICS)* service must not be disabled, nor sharing forbidden by group policy),
  and for Layer 2 OpenVPN, the *EasyVPN L2* adapter and its bridge, each with a button that fixes
  it or shows where to;
- mistakes in `facilities.json`.

*Edit facilities* (or *Edit* beside one) opens the **facility editor**, as administrator: add,
duplicate and remove facilities, choose each one's VPN client from the connectors and fill in what
that one needs, browse for its profile file (copied into `%ProgramData%\EasyVPN\Gateway\vpn`, where
only administrators can change it) and type its password (encrypted for this machine). An OpenVPN
profile is read as it is copied: its `route` lines fill in the routes, and in the copy, what
OpenVPN 2.6 no longer takes is rewritten (`tls-remote` becomes `verify-x509-name`; `cipher` is
added to `data-ciphers`; `comp-lzo` gets `allow-compression asym`), and old certificates tick
*Allow old certificates*. It marks a
facility with a mistake in red, and the gateway uses what is saved straight away. The facilities
are kept in `%ProgramData%\EasyVPN\Gateway\facilities.json`, which *Open facilities.json* in the
editor opens in Notepad; *Encrypt a password* in the app makes a `password` for writing it by hand.
*Import…* adds the facilities in another file in the same form - another gateway's, or one made from
Tosibox Monitor's list of Locks - leaving out those already there (the same id, or for Tosibox the
same Lock), so importing a newer list adds only what is new; *Save* keeps them.

*Settings…*, at the top of the app, opens (as administrator) what the facilities of a kind share,
set once rather than in each: for Tosibox, Tosi Client's program (`TosiboxKey.exe`) and its
password; for eCatcher, the Talk2M account, user and password. They are kept in `%ProgramData%\EasyVPN\Gateway\settings.json`, which the gateway reads
again whenever it changes; a facility's own setting, or its own password, wins over them.

For VPN clients that need someone to sign in (MFA, SAML), set the gateway to log on to a
desktop that can be reached with Remote Desktop: the app's *Open gateway desktop* button goes there.
`EasyVPN.Gateway.exe info`, in an administrator console, prints the address, key and fingerprint too.

The customers' VPN clients are not part of EasyVPN: install each one the way its vendor says. The
gateway app says which facility is still missing its client. Some customers send an installer with
their settings built in, such as Sophos's *client and configuration* package or a preconfigured
Ivanti, FortiClient or GlobalProtect installer: run it on the gateway once, then point the facility
at what it installed (the profile it left, its connection's name, its server).

**Each workstation.** Run `EasyVPN-Client-<version>-x64.msi`. It installs the `EasyVPN Agent`
service and the app in `%ProgramFiles%\EasyVPN`, with a Start menu shortcut, and starts the app:
choose the gateway in its Settings. To deploy it with the gateway already chosen:

```powershell
msiexec /i EasyVPN-Client-1.2.3-x64.msi /qn GATEWAY=gw01 ACCESSKEY=<key from the gateway>
```

The first time the agent reaches the gateway it pins the gateway's certificate; Settings shows the
fingerprint to compare with the gateway's. An update closes the app on every account signed in; run
by double-clicking, it starts the new one again afterwards.

**The app** lives in the notification area, like DynatecRDM. A click on its icon (or `Ctrl+Alt+F`
anywhere) opens the tray menu: a search box, then the facility connected - with what it is doing,
and *Disconnect* - your favourites, the facilities you used lately, and every facility by group.
Closed groups say how many they hold, so a hundred Tosibox Locks take a line; with more than 15
facilities they start closed, and the app remembers which you open. Typing shows every facility
whose name, customer, group, VPN client, networks or notes match, wherever it is. The keys are
those of DynatecRDM's quick launch: ↑ ↓ (Page Up, Page Down) to choose, → ← to open and close a
group, Enter to connect, Delete to disconnect, Esc to close. Connecting another facility while the
gateway carries one asks first, since it disconnects it for every workstation. The window (*Open
EasyVPN*, or a right-click on the icon) has the same list, the facility's details and its activity.
The icon shows the connection in its colour: amber and breathing while it connects, green with a
tick when connected, red with an exclamation mark when it is interrupted or failed, grey when the
agent or the gateway cannot be reached. A Windows notification says when a facility is connected,
interrupted, back, or could not be connected, and when someone else's switch disconnects yours - not
what you did yourself (Settings turns them off). The gateway app's icon shows its connection the
same way.
The shortcut is set in `%LOCALAPPDATA%\EasyVPN\app.json` (`"hotkey": "Ctrl+Alt+F"`, or `""` for none).

**A development build.** `publish.ps1`'s folders carry install scripts instead. Copy
`artifacts\Gateway` or `artifacts\Client` to the machine and, as administrator, run
`.\install-gateway.ps1`, or `.\install-client.ps1 -Gateway gw01 -AccessKey <key>`; `-Uninstall`
removes it again. They use the same folders and services as the installers, so remove one kind
before installing the other.

## Updates

The gateway and each workstation keep themselves up to date from this repository's GitHub
releases. The service (SYSTEM) looks for a newer release a minute after it starts and then every
six hours. It downloads that release's installer for itself into its data folder, which only SYSTEM
and administrators can write, checks it against the release's `SHA256SUMS.txt`, and installs it
silently. The installer replaces the service and starts the new one; the app closes a few seconds
before and opens again by itself once the service is back, as it was (in the notification area, or
with its window open).

- **Automatically** only while nothing is connected: on a workstation, no facility; on the gateway,
  no facility for anyone. Otherwise it waits, trying again every quarter of an hour.
- **By hand:** the status strip at the foot of the workstation app and of the gateway app shows
  the version and a button, *Check for updates*, which turns into *Install 1.2.3* when there is
  one. Installing by hand while connected asks first, since it disconnects.
- `updates.json` in the service's data folder (`%ProgramData%\EasyVPN\Client` or `\Gateway`) turns
  the automatic part off, or points at another repository:
  `{ "automatic": false, "repository": "owner/name" }`. The button still works.
- Installs from 0.2.1 and earlier do not update themselves: install the first release with updates
  once by hand. A development build (not running as the installed service) never updates.
- The installers are not code-signed, so an update is only as trustworthy as the GitHub account
  that publishes it: anyone who can publish a release here can run code as SYSTEM on every
  machine. Protect that account with two-factor authentication.

## Facilities

`facilities.json` on the gateway, as the facility editor writes it; `src\EasyVPN.Gateway\facilities.sample.json`
has one of each kind.

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
| `customer` | Shown with the facility, and its group in the app when it has no `group`. |
| `group` | Optional. The group the app lists it in, nested with `/`: `Tosibox/ACME`. |
| `connector` | Which script in `connectors\` connects it. |
| `settings` | Handed to the connector as `EASYVPN_<KEY>` environment variables. |
| `password` | Handed to the connector as `EASYVPN_PASSWORD`. The facility editor encrypts what is typed there; by hand, make the encrypted form with *Encrypt a password* in the gateway app (or `EasyVPN.Gateway.exe protect`). Only this gateway can decrypt it. |
| `routes` | The facility's networks, in CIDR. Workstations route these, and only these, to the gateway, which shares the adapter they lead to (its VPN's) with them. Left out, they are the networks the VPN sets up on the gateway when it connects: the IPv4 routes that appear on its own adapter, without host routes for the adapter itself and anything wider than /8. Connecting fails if there are none. |
| `layer2` | `true` for a facility whose VPN gives the gateway Layer 2: see [Layer 2 facilities](#layer-2-facilities). It needs no `routes`. |
| `dns` | Names under `domains` resolve through `servers` while connected. Optional. |
| `check` | Optional. `host:port` inside the facility that answers when it is reachable. With one, connected also means the plant answers, a plant that stops answering is noticed (and its traffic blocked), and switching confirms the previous facility is really down. Without one, connected is when the connector says its VPN client is connected, and only the VPN is watched: a VPN that stays up while the plant behind it is unreachable goes unnoticed. |
| `timeout` | Seconds to wait for the check, or the networks the VPN sets up, after the connector has run (default 120); allow time to sign in for `manual`. |
| `links` | Buttons in the app: `https://…` opens the browser, `rdp://host` opens Remote Desktop. |

## Connectors

| Connector | VPN client | Settings |
| --- | --- | --- |
| `manual` | Any client: someone connects it on the gateway desktop (MFA, SAML, FortiClient before 7.4.6, SINEMA RC …). EasyVPN waits for the check, or for the networks the VPN sets up when `routes` is empty. | none |
| `tosibox` | Tosi Client (once TOSIBOX Key), which has no command line: the EasyVPN Gateway app, started by the gateway in a desktop session, starts the client if need be, types its password if it asks, finds the Lock, presses Connect or Disconnect and waits until the client shows it. Its program and password are usually set once, in Settings. | `lock`, `program` |
| `ecatcher` | Ewon eCatcher (Talk2M), which has no command line: the gateway drives it through its Java's accessibility bridge on a desktop session, signs in to Talk2M, finds the Ewon and connects it. Its account, user and password are usually set once, in Settings. | `ewon`, `account`, `user`, `program` |
| `globalprotect` | Palo Alto Networks GlobalProtect, its own app, which has no command line on Windows: the EasyVPN Gateway app, started by the gateway in a desktop session, brings up its status panel, chooses the portal, presses Connect, signs in if it asks and waits until it shows Connected. The portal must let users disconnect (On-demand). | `portal`, `user` |
| `forticlient` | FortiClient 7.4.6 or later, SSL VPN or IPsec, through `FortiVPN.exe --cli`: a tunnel already made in FortiClient | `tunnel`, `user`, `certificate` |
| `windows` | The VPN built into Windows (IKEv2, SSTP, L2TP), dialled with `rasdial`. Create the connection with `Add-VpnConnection -AllUserConnection`. | `name`, `user` |
| `openvpn` | OpenVPN 2.6+ community client. Sophos SSL VPN `.ovpn` profiles too, with no Sophos software. `legacy`: `true` allows certificates signed with SHA1 or with short RSA keys (`--tls-cert-profile insecure`), which OpenVPN 2.6 refuses otherwise. | `config`, `user`, `legacy` |
| `wireguard` | WireGuard for Windows | `config` |
| `cisco` | Cisco Secure Client / AnyConnect, through `vpncli` | `host`, `user`, `group` |
| `sophos` | Sophos Connect, through `sccli`: IPsec (`.scx`) and provisioning (`.pro`) files | `config`, `connection`, `user` |
| `netextender` | SonicWall NetExtender, through `nxcli` (or `NECLI` in 10.2 and earlier) | `host`, `domain`, `user`, `trust` |
| `pulse` | Ivanti Secure Access / Pulse Secure, through `pulselauncher`. No second sign-in, no choice of role. | `url`, `realm`, `user` |
| `openconnect` | OpenConnect, for GlobalProtect (and Pulse, Fortinet, F5, Array, AnyConnect) servers that sign in with a user name and password, without their own clients | `protocol`, `host`, `user`, `group`, `servercert` |

All but `manual` sign in by themselves, with the facility's `user` and `password`: a sign-in that
asks for a token or opens a browser needs `manual`, or a service account without one from the
customer. Each script's header says what its settings mean. Each returns once its client says it is
connected (OpenVPN's "Initialization Sequence Completed", Cisco's "state: Connected", Tosi Client's
Disconnect button, FortiClient's "Connected"), so a facility needs no `check` to turn green.

**Tosibox.** Someone must be at the gateway's desktop: set the gateway to sign in by itself. The
gateway looks for Tosi Client only in the sessions someone is connected to, unlocked ones first
(at the console, or over Remote Desktop), with the app run there with that user's full rights (so
a client running as administrator can be reached too). When it is in none of them, or only in the
notification area, the app starts it there - with the program set in *Settings*
(`TosiboxKey.exe`), or else one it finds where Windows keeps track of programs - after closing any
copy of it left on another desktop, where nobody could use it and it would keep another from
starting; then it types its password (from *Settings*, or the facility's own) and waits for the
list to fill. It finds the Lock in view, or else with the client's search, which filters the list
to it and so keeps it in view whoever scrolls (or else scrolled to, or typed into the list); the
search is left showing it. It presses Connect or
Disconnect (Cancel, for one still connecting), and waits until the client shows it. The gateway
is the client's only user: before a Lock connects, any other the client has connected is
disconnected, and after it disconnects, none is left connected. For that the app reads the whole
list (scrolled through, when the client shows UI Automation only the rows in view), and counts a
Lock as connected only on plain signs - its devices listed, a Disconnect, Connected or Connecting -
never a blank status alone, so it never presses one that is not into connecting. The app drives
the client through UI Automation where it allows, and otherwise with the mouse and keyboard, as a
person would: typing needs that session's screen on and unlocked. Give each facility the Lock's name exactly
as the client lists it, such as `Tosibox 15 [Matbørsen]`, and leave `routes` empty unless the Lock
is in Layer 2 mode. When it cannot find the Lock or read its state, the app saves what it saw of
the client to `%LOCALAPPDATA%\EasyVPN\tosi-client.txt` and says so; `"C:\Program Files\EasyVPN
Gateway\EasyVPN.GatewayApp.exe" --tosibox dump > tosi.txt` on the gateway's desktop writes the same.

**eCatcher.** eCatcher (Ewon's Talk2M client) has no command line either, and is a Java program
that UI Automation cannot see into, so it is driven through the accessibility bridge of the Java
it ships with. That Java is 32-bit, so the gateway runs `ecatcher-ui.ps1` (beside it) in 32-bit
PowerShell, in the desktop session someone is at, with that user's full rights - eCatcher runs as
administrator, so that user must be one. It switches the bridge on for that user the first time
(`jabswitch -enable`), starting again an eCatcher already running without it; starts eCatcher if
it is not running; signs in to Talk2M with the facility's account, user and password (usually set
once, in *Settings*); searches the list for the Ewon, selects it and presses its Connect (or
Disconnect), and waits until eCatcher shows it done. Give the facility the Ewon's name exactly as
eCatcher lists it, and leave `routes` empty: Talk2M adds the Ewon's LAN as a route on the
*Talk2m-eCatcher* adapter, which the gateway then shares. Give the gateway a Talk2M user of its
own, as Talk2M signs a user in on one computer at a time; a user with two-factor sign-in signs in
by hand once on the gateway, ticking *Remember this PC*. An offline Ewon that can be woken by SMS
is left to a person. When it cannot go on, it saves what eCatcher showed to
`%LOCALAPPDATA%\EasyVPN\ecatcher.txt`; `ecatcher-ui.ps1 Dump`, run as administrator in
`C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe` on the gateway's desktop, writes the same.

**GlobalProtect.** GlobalProtect's app has no command line on Windows (only on Linux), so it is
driven as Tosi Client is: by the EasyVPN Gateway app, in the session someone is at, unlocked first,
through UI Automation and, where that does not reach, the mouse and keyboard. It brings up the
status panel as a person would - starting GlobalProtect again, which shows the one running, or
pressing its icon in the notification area - and starts its agent (`PanGPA.exe`) when it runs on
no desktop. Then it chooses the facility's portal: typed into the portal box GlobalProtect shows
the first time, or picked from its *Change Portal* drop-down. It presses Connect, and when
GlobalProtect asks to sign in it types the facility's user name and password and presses Sign In:
twice at most (the portal, then its gateway), never a third time, so a wrong password cannot lock
the account. It waits until GlobalProtect shows Connected. Disconnecting presses Disconnect (Cancel,
for one still connecting). The gateway is GlobalProtect's only user: connected to another portal,
it is disconnected first. So that it can be, each portal must let users disconnect, which takes the
*On-demand* connect method: ask the customer. Before relying on a facility, connect its portal by
hand once on the gateway's desktop, so GlobalProtect has it in its list, and accept what it asks
the first time (a certificate, terms of use). A portal that signs in in a browser (SAML) or asks
for a code needs a person: `manual`. Leave `routes` empty: the portal sets up the networks on
GlobalProtect's adapter (*PANGP Virtual Ethernet Adapter*). When it cannot read the panel, the app
saves what it saw to `%LOCALAPPDATA%\EasyVPN\globalprotect.txt` and says so; `"C:\Program
Files\EasyVPN Gateway\EasyVPN.GatewayApp.exe" --globalprotect dump > globalprotect.txt` on the
gateway's desktop writes the same.

**FortiClient.** From 7.4.6, FortiClient connects and disconnects tunnels from its command line,
`FortiVPN.exe --cli`, which the connector runs as the gateway does every connector, needing nobody
at the desktop. It connects only tunnels already made in FortiClient (or pushed by the customer's
EMS), so make each in FortiClient on the gateway and give the facility its name exactly as
FortiClient lists it; *Check* in the gateway app says whether FortiClient lists it. The gateway is
FortiClient's only user: another tunnel it has up is disconnected first. An older FortiClient has
no command line, so its facilities are `manual`; and FortiClient 7.4.4 and later have no IPsec with
IKEv1, which leaves such a tunnel to `manual` with an older FortiClient. OpenConnect
(`protocol` `fortinet`) reaches FortiGate SSL VPNs without FortiClient.

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
  which workstations pin. The relay uses the same certificate (pinned by gost) and the same key, and
  takes what it carries to the hub and nowhere else.
- The gateway shares a VPN with the workstations only while its facility is connected, and only that
  VPN: they reach what the gateway reaches through it, as they would running its client themselves,
  and nothing else - with nothing shared, their packets stop at the hub. The Layer 2 port opens only
  while a Layer 2 facility is connected, behind the same key and certificate.
- Sharing a VPN ends any other Internet Connection Sharing on the gateway: Windows shares one
  adapter at a time. Keep the gateway for EasyVPN.
- Keys, the private key and VPN passwords live in `%ProgramData%\EasyVPN`, readable by SYSTEM and
  administrators only; passwords are also DPAPI-encrypted to the gateway machine.
- The agent's API listens on 127.0.0.1 only, refuses other host names (DNS rebinding) and requires a
  custom header for changes (so a web page cannot drive it). Any user signed in to the workstation
  can use it, including choosing the gateway: fine for an engineering PC, worth knowing on a shared one.
- The gateway app talks to the gateway on 127.0.0.1:8428, guarded the same way, which answers
  nothing else. Anyone signed in to the gateway can see its status and connect or disconnect a
  facility, as a workstation can, but not the access key: that takes an administrator.

## Limits

- **Workstation to facility.** The facility sees the gateway, not the workstation, so connections
  have to start at the workstation: a device cannot open one towards it (a TwinCAT ADS route back
  to the PC, an active-mode FTP data connection).
- **Layer 2 only where the VPN has it.** PROFINET DCP discovery and other Ethernet-level protocols
  reach a workstation only from a [Layer 2 facility](#layer-2-facilities), one workstation at a
  time. Through a routed facility, address devices by IP.
- **One facility at a time** for all workstations sharing a gateway. The app shows who switched it.
- **The VPN client must let the gateway's own network in.** Some clients block local network access
  while connected (Cisco's "Allow local LAN access", GlobalProtect's "No direct access to local
  network"). The agent says so when the relay stops answering after a connect.
- **The tunnel's network, 198.18.0.0/24,** is taken from the benchmarking range, which no facility
  should be using; one that does cannot be reached there.
- Overlapping networks: if a facility uses the same subnet as the workstation's own LAN, the facility
  wins while connected; the Activity log warns about it.

## What has been tested

The packet tunnel (0.4): UDP through gost's relay over TLS, the relay taking it to its one target
whatever was asked for. Not yet the adapters themselves, Internet Connection Sharing with a VPN's
adapter, or ping through to a facility: they need administrator rights, a gateway and a VPN, so try
them on the real gateway first. The gateway app's *Sharing* check says whether sharing can be used.

eCatcher (0.4): reading eCatcher 7.1's sign-in window through its Java's accessibility bridge, and
the driver starting in 32-bit PowerShell. Not yet signing in, finding an Ewon or connecting it: try
it on the gateway, and send `ecatcher.txt` when it stops.

On a development PC (not elevated): the gateway end to end with a test connector, covering
connecting, the relay with the generated certificate pinned by gost, the access key, the whitelist,
UDP, a facility that stops answering and comes back, refusing a switch while the previous facility
still answers, and disconnecting. Also the agent's pinning, API guards and connect flow up to routing,
and the app against both. Creating the Wintun adapter and the routes needs the agent to run as a
service (SYSTEM), as installed, and the vendor connectors need their VPN clients: try those on the
real gateway, with `manual` first. The `sophos`, `netextender`, `pulse`, `openconnect` and
`forticlient` connectors follow their vendors' documented command lines but have not met a real
server yet; `forticlient` was run against a stand-in that prints what Fortinet documents.

GlobalProtect: the driver builds, but has not yet met GlobalProtect, which was not to be had here;
it goes by Palo Alto's description of the app (its *Connect*, *Change Portal*, *Username*,
*Password* and *Sign In*). Try it on the gateway, and send `globalprotect.txt` when it stops.

The facility editor: reading the sample and writing it back unchanged, a new facility's id,
validation, switching connectors, passwords, links, a file changed in Notepad meanwhile and a file
that does not parse, all without its window. Its window loads with every binding resolved, but has
not been looked at on a real desktop yet. Reading OpenVPN profiles: `route` lines, and the
rewrites for OpenVPN 2.6 on an old Sophos UTM profile.

Routes from the VPN: reading this machine's routing table, and picking out the VPN's networks from
a made-up one (its own adapter's host routes, broadcasts, multicast, a route for all traffic and
routes on other adapters left out). Not yet with a real VPN adding them.

For Layer 2: the gateway's Layer 2 port (the key, refusing links while no Layer 2 facility is
connected), a Layer 2 facility failing cleanly on a gateway without the adapter, and the app showing
Layer 2 facilities. Frames through real TAP adapters, the bridge and a TOSIBOX Key were not: they
need administrator rights, the adapters and a Tosibox Lock.
