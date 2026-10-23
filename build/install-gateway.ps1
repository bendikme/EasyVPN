<#
    Installs or updates the EasyVPN gateway on this machine, from the folder this script is in.
    Run it as administrator on the machine (or VM) that has the customers' VPN clients installed.

        .\install-gateway.ps1              install or update, then print what clients need
        .\install-gateway.ps1 -Uninstall   remove the service and the program; the data stays

    The settings, access key, certificate and facilities live in %ProgramData%\EasyVPN\Gateway,
    which an update leaves alone.
#>
#Requires -RunAsAdministrator
param(
    [switch]$Uninstall,
    [string]$Destination = "$env:ProgramFiles\EasyVPN Gateway"
)
$ErrorActionPreference = 'Stop'
$service = 'EasyVPN.Gateway'
$rule = 'EasyVPN Gateway'

if (Get-Service $service -ErrorAction SilentlyContinue) {
    Stop-Service $service
    if ($Uninstall) { sc.exe delete $service | Out-Null }
}
if ($Uninstall) {
    Remove-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue
    Remove-Item $Destination -Recurse -Force -ErrorAction SilentlyContinue
    Write-Output "Removed. The data is still in $env:ProgramData\EasyVPN\Gateway."
    return
}

New-Item -ItemType Directory -Force $Destination | Out-Null
Copy-Item "$PSScriptRoot\*" $Destination -Recurse -Force -Exclude 'install-gateway.ps1'
$exe = Join-Path $Destination 'EasyVPN.Gateway.exe'

# Makes the settings, access key and certificate before the service starts for the first time.
$info = & $exe info

if (-not (Get-Service $service -ErrorAction SilentlyContinue)) {
    New-Service -Name $service -BinaryPathName "`"$exe`"" -DisplayName 'EasyVPN Gateway' -StartupType Automatic `
        -Description 'Connects the facility VPNs and relays EasyVPN clients to the one connected.' | Out-Null
    sc.exe failure $service reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null
}
# The API and the relay, on their default ports (gateway.json can move them).
if (-not (Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $rule -Direction Inbound -Protocol TCP -LocalPort 8420, 8421 -Action Allow | Out-Null
}
Start-Service $service

$info
