<#
    globalprotect - Palo Alto Networks GlobalProtect, its own app, on this gateway's desktop. It has
    no command line on Windows, so the EasyVPN Gateway app, started in a desktop session on the
    gateway, does what a person would in its status panel: brings it up, chooses the facility's
    portal, presses Connect, signs in when it asks, and waits until it shows Connected - or presses
    Disconnect, and waits until it shows it is not.

    Settings:  portal   the portal's address, as GlobalProtect lists it (required)
               user     the user name, for when GlobalProtect asks to sign in; the password is the
                        facility's "password". Leave both out for a portal that signs in by itself
                        (a saved password, a certificate, single sign-on)

    The portal must let users disconnect (its connect method On-demand), or the gateway could not
    leave it for another facility; and sign in with a user name and password, or by itself: one
    that opens a browser (SAML) or asks for a code is for the manual connector, or the openconnect
    one. Connect to each portal by hand once on the gateway's desktop, so GlobalProtect has it,
    and accept what it asks the first time (its certificate, its terms). Someone must be signed in
    to the gateway's desktop: set the gateway to sign in by itself. Should it not connect, run this
    on the gateway's desktop and look at what the app sees:
        "C:\Program Files\EasyVPN Gateway\EasyVPN.GatewayApp.exe" --globalprotect dump > globalprotect.txt
    Check: GlobalProtect installed and its service running, and a portal set. It does not touch it.
#>
param([ValidateSet('Connect', 'Disconnect', 'Check')][string]$Action)
$ErrorActionPreference = 'Stop'
trap { Write-Output $_.Exception.Message; exit 1 }

if (-not $env:EASYVPN_PORTAL) { throw 'Set "portal" to the portal''s address, as GlobalProtect lists it.' }
$gateway = $env:EASYVPN_GATEWAY
$app = Join-Path (Split-Path $gateway) 'EasyVPN.GatewayApp.exe'
if (-not (Test-Path $app)) { throw "The EasyVPN Gateway app is not beside the gateway ($app)." }
$notHere = 3

if ($Action -eq 'Check') {
    $installed = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like 'GlobalProtect*' } | Select-Object -First 1
    if (-not $installed -and -not (Test-Path "$env:ProgramFiles\Palo Alto Networks\GlobalProtect\PanGPA.exe")) {
        throw 'GlobalProtect is not installed on this gateway.'
    }
    $service = Get-Service PanGPS -ErrorAction SilentlyContinue
    if (-not $service) { throw 'GlobalProtect''s service (PanGPS) is not installed: install GlobalProtect again.' }
    if ($service.Status -ne 'Running') { throw "GlobalProtect's service (PanGPS) is $($service.Status.ToString().ToLower()): start it." }
    Write-Output "GlobalProtect$(if ($installed.DisplayVersion) { " $($installed.DisplayVersion)" }) on the gateway's desktop, portal $env:EASYVPN_PORTAL"
    return
}

# GlobalProtect's agent on any desktop; the connection itself is its service's, on its own adapter.
$running = Get-Process PanGPA -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -ne 0 }
function Test-Tunnel {
    [bool](Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.InterfaceDescription -like 'PANGP*' -and $_.Status -eq 'Up' })
}

# The gateway starts the app in each signed-in desktop session in turn, and passes on what it
# prints and its exit code: 3 when the agent was in none of them.
if ($Action -eq 'Disconnect') {
    $LASTEXITCODE = $notHere
    if ($running) { & $gateway desktop --globalprotect disconnect }
    if ($LASTEXITCODE -eq $notHere) {
        if (-not (Test-Tunnel)) {
            Write-Output 'GlobalProtect is not connected.'
            exit 0
        }
        # Up with no agent on any desktop to see it: the agent is started to disconnect it.
        & $gateway desktop --globalprotect disconnect --start
    }
    exit $LASTEXITCODE
}

# Only where someone is at the desktop, unlocked first: it may be typed into. When the agent runs
# on no desktop at all, it is started straight away, rather than looked for there first.
$LASTEXITCODE = $notHere
if ($running) { & $gateway desktop --usable-only --globalprotect connect $env:EASYVPN_PORTAL }
if ($LASTEXITCODE -eq $notHere) {
    & $gateway desktop --usable-only --globalprotect connect $env:EASYVPN_PORTAL --start
}
exit $LASTEXITCODE
