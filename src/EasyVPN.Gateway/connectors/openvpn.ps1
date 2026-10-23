<#
    openvpn - an OpenVPN profile, run by OpenVPN itself (the community client, 2.6 or later).

    Settings:  config   full path to the .ovpn file (required)
               user     the user name, when the server asks for one; the password is the
                        facility's "password"
#>
param([ValidateSet('Connect', 'Disconnect')][string]$Action)
$ErrorActionPreference = 'Stop'
trap { Write-Output $_.Exception.Message; exit 1 }

$openvpn = "$env:ProgramFiles\OpenVPN\bin\openvpn.exe"
$authFile = Join-Path $env:EASYVPN_WORK 'auth.txt'
$logFile = Join-Path $env:EASYVPN_WORK 'openvpn.log'
# OpenVPN exits cleanly, taking its routes with it, when this event is set (its --service option).
$exitEvent = "EasyVPN_openvpn_$env:EASYVPN_FACILITY"

function Stop-OpenVpn {
    try {
        $handle = [System.Threading.EventWaitHandle]::OpenExisting($exitEvent)
        $handle.Set() | Out-Null
        $handle.Dispose()
        Start-Sleep -Seconds 3
    }
    catch {
        # No such event: OpenVPN is not running.
    }
    Remove-Item $authFile -ErrorAction SilentlyContinue
}

Stop-OpenVpn
if ($Action -eq 'Disconnect') { return }

if (-not (Test-Path $openvpn)) { throw "OpenVPN is not installed ($openvpn)." }
if (-not $env:EASYVPN_CONFIG) { throw 'Set "config" to the .ovpn file.' }

$arguments = @('--config', "`"$env:EASYVPN_CONFIG`"", '--log', "`"$logFile`"", '--service', $exitEvent, '0')
if ($env:EASYVPN_USER) {
    # The work folder is readable by SYSTEM and administrators only; the file goes on disconnect.
    [System.IO.File]::WriteAllLines($authFile, [string[]]@($env:EASYVPN_USER, $env:EASYVPN_PASSWORD))
    $arguments += '--auth-user-pass', "`"$authFile`"", '--auth-retry', 'nointeract'
}
$process = Start-Process $openvpn -ArgumentList $arguments -WindowStyle Hidden -PassThru
Write-Output "OpenVPN started (process $($process.Id)), logging to $logFile"
