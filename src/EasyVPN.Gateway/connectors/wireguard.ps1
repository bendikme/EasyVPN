<#
    wireguard - a tunnel run by WireGuard for Windows, as its own service.

    Settings:  config   full path to the tunnel's .conf file (required); the tunnel takes the
                        file's name
#>
param([ValidateSet('Connect', 'Disconnect')][string]$Action)
$ErrorActionPreference = 'Stop'
trap { Write-Output $_.Exception.Message; exit 1 }

$wireguard = "$env:ProgramFiles\WireGuard\wireguard.exe"
if (-not (Test-Path $wireguard)) { throw "WireGuard is not installed ($wireguard)." }
if (-not $env:EASYVPN_CONFIG) { throw 'Set "config" to the tunnel''s .conf file.' }
$tunnel = [System.IO.Path]::GetFileNameWithoutExtension($env:EASYVPN_CONFIG)

if ($Action -eq 'Connect') {
    $arguments = '/installtunnelservice', "`"$env:EASYVPN_CONFIG`""
}
else {
    $arguments = '/uninstalltunnelservice', $tunnel
}
# wireguard.exe is a windowed program, so PowerShell would not wait for it on its own.
$process = Start-Process $wireguard -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
exit $process.ExitCode
