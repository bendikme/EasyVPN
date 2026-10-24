<#
    tosibox - Tosi Client (once TOSIBOX Key) on this gateway's desktop. It has no command line, so
    the EasyVPN Gateway app, started in the desktop session where the client runs, does what a
    person would there: enters the client's password if it asks, searches for the Lock, presses its
    Connect or Disconnect, and waits until the client shows it connected or disconnected.

    Settings:  lock   the Lock's name exactly as Tosi Client lists it, such as Tosibox 15 [Plant]
                      (required)
               The facility's "password" is Tosi Client's own, entered when the client asks for it.

    Tosi Client must be running in a signed-in desktop session on the gateway: set the gateway to
    sign in by itself and the client to start with Windows. Should a Lock not connect, run this on
    the gateway's desktop and look at what the app sees of the client:
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

if ($Action -eq 'Check') {
    Write-Output "Tosi Client on the gateway's desktop, Lock '$env:EASYVPN_LOCK'"
    return
}

# The gateway starts the app in the desktop session, and passes on what it prints and its exit code.
& $gateway desktop --tosibox $Action.ToLowerInvariant() $env:EASYVPN_LOCK
exit $LASTEXITCODE
