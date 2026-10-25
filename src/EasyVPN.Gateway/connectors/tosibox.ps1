<#
    tosibox - Tosi Client (once TOSIBOX Key) on this gateway's desktop. It has no command line, so
    the EasyVPN Gateway app, started in a desktop session on the gateway, does what a person would
    there: starts the client if it is not running (or shows it from the notification area), enters
    its password if it asks, searches for the Lock, presses its Connect or Disconnect, and waits
    until the client shows it connected or disconnected.

    Settings:  lock      the Lock's name exactly as Tosi Client lists it, such as Tosibox 15 [Plant]
                         (required)
               program   Tosi Client's .exe, when it is not where Windows' list of installed
                         programs says
               The facility's "password" is Tosi Client's own, entered when the client asks for it.

    Someone must be signed in to the gateway's desktop: set the gateway to sign in by itself. The
    client is looked for, and started, only where someone is at the desktop (unlocked first); a
    copy left on another desktop is closed first. Should a Lock not connect, run this on the
    gateway's desktop and look at what the app sees:
        "C:\Program Files\EasyVPN Gateway\EasyVPN.GatewayApp.exe" --tosibox dump > tosi.txt
    Check: the gateway app there, and a Lock set. It does not touch the client.
#>
param([ValidateSet('Connect', 'Disconnect', 'Check')][string]$Action)
$ErrorActionPreference = 'Stop'
trap { Write-Output $_.Exception.Message; exit 1 }

if (-not $env:EASYVPN_LOCK) { throw 'Set "lock" to the Lock''s name as Tosi Client lists it.' }
$gateway = $env:EASYVPN_GATEWAY
$app = Join-Path (Split-Path $gateway) 'EasyVPN.GatewayApp.exe'
if (-not (Test-Path $app)) { throw "The EasyVPN Gateway app is not beside the gateway ($app)." }
$notHere = 3

if ($Action -eq 'Check') {
    if ($env:EASYVPN_PROGRAM -and -not (Test-Path $env:EASYVPN_PROGRAM)) { throw "Tosi Client is not at $env:EASYVPN_PROGRAM." }
    Write-Output "Tosi Client on the gateway's desktop, Lock '$env:EASYVPN_LOCK'"
    return
}

# The gateway starts the app in each signed-in desktop session in turn, and passes on what it
# prints and its exit code: 3 when the client was in none of them.
if ($Action -eq 'Disconnect') {
    & $gateway desktop --tosibox disconnect $env:EASYVPN_LOCK
    if ($LASTEXITCODE -eq $notHere) {
        Write-Output 'Tosi Client is not running, so nothing is connected.'
        exit 0
    }
    exit $LASTEXITCODE
}

# Only where someone is at the desktop, unlocked first: the client is typed into.
& $gateway desktop --usable-only --tosibox connect $env:EASYVPN_LOCK
if ($LASTEXITCODE -eq $notHere) {
    # Not running there, or hidden away: start it there - closing a copy left on a desktop nobody
    # is at, which would keep it from starting.
    & $gateway desktop --usable-only --close-elsewhere tosi --tosibox connect $env:EASYVPN_LOCK --start
}
exit $LASTEXITCODE
