<#
    openvpn - an OpenVPN profile, run by OpenVPN itself (the community client, 2.6 or later).

    Settings:  config   full path to the .ovpn file (required)
               user     the user name, when the server asks for one; the password is the
                        facility's "password"
               legacy   "true" allows old certificates, signed with SHA1 or with RSA keys under
                        2048 bits, which OpenVPN 2.6 refuses otherwise (--tls-cert-profile insecure)

    Connect returns once OpenVPN logs "Initialization Sequence Completed".
    Check: OpenVPN installed, and the profile there.
#>
param([ValidateSet('Connect', 'Disconnect', 'Check')][string]$Action)
$ErrorActionPreference = 'Stop'
trap { Write-Output $_.Exception.Message; exit 1 }

$openvpn = "$env:ProgramFiles\OpenVPN\bin\openvpn.exe"
$authFile = Join-Path $env:EASYVPN_WORK 'auth.txt'
$logFile = Join-Path $env:EASYVPN_WORK 'openvpn.log'
# OpenVPN exits cleanly, taking its routes with it, when this event is set (its --service option).
$exitEvent = "EasyVPN_openvpn_$env:EASYVPN_FACILITY"

if ($Action -eq 'Check') {
    if (-not (Test-Path $openvpn)) { throw "OpenVPN 2.6 or later is not installed ($openvpn)." }
    if (-not $env:EASYVPN_CONFIG) { throw 'Set "config" to the .ovpn file.' }
    if (-not (Test-Path $env:EASYVPN_CONFIG)) { throw "The profile $env:EASYVPN_CONFIG is not there." }
    Write-Output "OpenVPN $((Get-Item $openvpn).VersionInfo.ProductVersion), profile $env:EASYVPN_CONFIG"
    return
}

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
if ($env:EASYVPN_LEGACY -eq 'true') { $arguments += '--tls-cert-profile', 'insecure' }
if ($env:EASYVPN_USER) {
    # The work folder is readable by SYSTEM and administrators only; the file goes on disconnect.
    [System.IO.File]::WriteAllLines($authFile, [string[]]@($env:EASYVPN_USER, $env:EASYVPN_PASSWORD))
    $arguments += '--auth-user-pass', "`"$authFile`"", '--auth-retry', 'nointeract'
}
$process = Start-Process $openvpn -ArgumentList $arguments -WindowStyle Hidden -PassThru
Write-Output "OpenVPN started (process $($process.Id)), logging to $logFile"

# Connected is when OpenVPN says so; a refused sign-in, or OpenVPN ending, is a failure now.
$deadline = (Get-Date).AddSeconds(100)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    $log = Get-Content $logFile -ErrorAction SilentlyContinue
    if ($log -match 'Initialization Sequence Completed') {
        Write-Output 'OpenVPN is connected.'
        exit 0
    }
    $refused = $log -match 'AUTH_FAILED'
    if ($refused -or $process.HasExited) {
        $log | Select-Object -Last 12 | Write-Output
        Stop-OpenVpn
        if ($refused) { throw 'The server refused the user name or password.' }
        throw 'OpenVPN stopped.'
    }
}
$log | Select-Object -Last 12 | Write-Output
Stop-OpenVpn
throw 'OpenVPN did not connect within 100 s.'
