<#
    Installs or updates EasyVPN on this workstation, from the folder this script is in: the
    EasyVPN Agent service, and the EasyVPN app with a Start menu shortcut. Run it as administrator.

        .\install-client.ps1                                   install or update
        .\install-client.ps1 -Gateway gw01 -AccessKey <key>    and choose the gateway too
        .\install-client.ps1 -Layer2                           and add the "EasyVPN L2" adapter
        .\install-client.ps1 -Uninstall                        remove it again

    The gateway can also be chosen later, in the app's Settings. -Layer2 is for Layer 2 facilities
    (a Tosibox Lock in Layer 2 mode, say); it needs OpenVPN 2.6 or later installed first, for its
    TAP-Windows6 driver.
#>
#Requires -RunAsAdministrator
param(
    [switch]$Uninstall,
    [string]$Gateway,
    [string]$AccessKey,
    [switch]$Layer2,
    [string]$Destination = "$env:ProgramFiles\EasyVPN"
)
$ErrorActionPreference = 'Stop'
$service = 'EasyVPN.Agent'
$shortcut = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\EasyVPN.lnk"

# The files are in use while the app or the agent runs. Stopping the agent also takes the
# EasyVPN adapter and its routes down.
Get-Process EasyVPN -ErrorAction SilentlyContinue | Stop-Process -Force
if (Get-Service $service -ErrorAction SilentlyContinue) {
    Stop-Service $service
    if ($Uninstall) { sc.exe delete $service | Out-Null }
}
if ($Uninstall) {
    Remove-Item $shortcut -ErrorAction SilentlyContinue
    Remove-Item $Destination -Recurse -Force -ErrorAction SilentlyContinue
    Write-Output "Removed. The agent's settings are still in $env:ProgramData\EasyVPN\Client."
    return
}

New-Item -ItemType Directory -Force $Destination | Out-Null
Copy-Item "$PSScriptRoot\*" $Destination -Recurse -Force -Exclude 'install-client.ps1'
$agent = Join-Path $Destination 'EasyVPN.Agent.exe'

if (-not (Get-Service $service -ErrorAction SilentlyContinue)) {
    New-Service -Name $service -BinaryPathName "`"$agent`"" -DisplayName 'EasyVPN Agent' -StartupType Automatic `
        -Description 'Keeps the EasyVPN adapter, and routes the connected facility through it.' | Out-Null
    sc.exe failure $service reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null
}
Start-Service $service

if ($Layer2 -and -not (Get-NetAdapter -Name 'EasyVPN L2' -ErrorAction SilentlyContinue)) {
    $tapctl = "$env:ProgramFiles\OpenVPN\bin\tapctl.exe"
    if (-not (Test-Path $tapctl)) { throw 'Layer 2 needs the TAP-Windows6 driver: install OpenVPN 2.6 or later with its TAP driver, then run this again.' }
    & $tapctl create --name 'EasyVPN L2' --hwid 'root\tap0901' | Out-Null
    if ($LASTEXITCODE) { throw 'tapctl could not create the "EasyVPN L2" adapter.' }
    # A default gateway from a facility's DHCP must never win over this PC's own.
    Set-NetIPInterface -InterfaceAlias 'EasyVPN L2' -AddressFamily IPv4 -InterfaceMetric 500
    Write-Output 'Added the "EasyVPN L2" adapter for Layer 2 facilities.'
}

$link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
$link.TargetPath = Join-Path $Destination 'EasyVPN.exe'
$link.Description = 'Quick connections to remote facilities'
$link.Save()

if ($Gateway) {
    $body = @{ gateway = $Gateway; accessKey = $AccessKey } | ConvertTo-Json
    for ($i = 0; ; $i++) {
        try {
            Invoke-RestMethod -Method Put -Uri 'http://127.0.0.1:8429/api/settings' -Headers @{ 'X-EasyVPN' = '1' } `
                -ContentType 'application/json' -Body $body | Out-Null
            break
        }
        catch {
            if ($i -ge 20) { throw "The agent did not answer, so the gateway was not set: choose it in the app's Settings." }
            Start-Sleep -Milliseconds 500
        }
    }
    Write-Output "Using the gateway at $Gateway."
}
Write-Output 'Installed. Start EasyVPN from the Start menu.'
