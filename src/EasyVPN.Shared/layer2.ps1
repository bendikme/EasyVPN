<#
    The "EasyVPN L2" TAP adapter, which carries Layer 2 facilities, on a workstation and on the
    gateway. OpenVPN 2.6 or later, installed with its TAP-Windows6 driver, brings what makes it:
    the driver, and tapctl.

        layer2.ps1 Status                  prints adapter=, tapctl= and bridged=, each True or False
        layer2.ps1 Create [-Workstation]   adds the adapter, unless it is there already

    The services run it as SYSTEM; the install scripts as administrator.
#>
param(
    [ValidateSet('Status', 'Create')][string]$Action,
    [switch]$Workstation
)
$ErrorActionPreference = 'Stop'
trap { Write-Output $_.Exception.Message; exit 1 }

$name = 'EasyVPN L2'
$tapctl = "$env:ProgramFiles\OpenVPN\bin\tapctl.exe"
$adapter = Get-NetAdapter -Name $name -ErrorAction SilentlyContinue

if ($Action -eq 'Status') {
    # A member of a Network Bridge has the MAC Bridge bound; Windows lists that binding only once a bridge exists.
    $bridged = $adapter -and (Get-NetAdapterBinding -Name $name -ComponentID ms_bridge -ErrorAction SilentlyContinue).Enabled -eq $true
    "adapter=$([bool]$adapter)"
    "tapctl=$(Test-Path $tapctl)"
    "bridged=$([bool]$bridged)"
    return
}

if (-not $adapter) {
    if (-not (Test-Path $tapctl)) {
        throw "Layer 2 needs OpenVPN 2.6 or later on this PC, installed with its TAP-Windows6 driver: https://openvpn.net/community-downloads/"
    }
    & $tapctl create --name $name --hwid 'root\tap0901' | Out-Null
    if ($LASTEXITCODE) { throw "tapctl could not create the `"$name`" adapter (exit code $LASTEXITCODE)." }
    Write-Output "Added the `"$name`" adapter"
}
if ($Workstation) {
    # A default gateway from a facility's DHCP must never win over this PC's own.
    Set-NetIPInterface -InterfaceAlias $name -AddressFamily IPv4 -InterfaceMetric 500
}
