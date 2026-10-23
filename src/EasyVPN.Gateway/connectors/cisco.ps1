<#
    cisco - Cisco Secure Client (or AnyConnect), driven through its command-line client, vpncli.

    Settings:  host    the VPN server, as it is typed into the Cisco client (required)
               user    the user name; the password is the facility's "password"
               group   the group to choose, when the server offers several

    The Cisco window is closed first, because vpncli cannot connect while it is open.
    Check: the Cisco client installed, and a server set.
#>
param([ValidateSet('Connect', 'Disconnect', 'Check')][string]$Action)
$ErrorActionPreference = 'Stop'
trap { Write-Output $_.Exception.Message; exit 1 }

$vpncli = @(
    "${env:ProgramFiles(x86)}\Cisco\Cisco Secure Client\vpncli.exe",
    "${env:ProgramFiles(x86)}\Cisco\Cisco AnyConnect Secure Mobility Client\vpncli.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $vpncli) { throw 'Cisco Secure Client is not installed.' }

if ($Action -eq 'Check') {
    if (-not $env:EASYVPN_HOST) { throw 'Set "host" to the VPN server.' }
    Write-Output "Cisco Secure Client, server $env:EASYVPN_HOST"
    return
}

if ($Action -eq 'Disconnect') {
    & $vpncli disconnect
    return
}

if (-not $env:EASYVPN_HOST) { throw 'Set "host" to the VPN server.' }
Get-Process csc_ui, vpnui -ErrorAction SilentlyContinue | Stop-Process -Force

# With -s, vpncli reads its answers from standard input in the order it asks for them: the group
# (only asked when there is a choice), the user name, the password, and "y" to accept a banner.
$answers = @("connect $env:EASYVPN_HOST")
if ($env:EASYVPN_GROUP) { $answers += $env:EASYVPN_GROUP }
if ($env:EASYVPN_USER) { $answers += $env:EASYVPN_USER, $env:EASYVPN_PASSWORD }
$answers += 'y'

$output = $answers | & $vpncli -s
if ($env:EASYVPN_PASSWORD) { $output = $output -replace [regex]::Escape($env:EASYVPN_PASSWORD), '********' }
$output | Where-Object { $_ -match '\S' } | Write-Output
if (-not ($output -match 'state: Connected')) { exit 1 }
