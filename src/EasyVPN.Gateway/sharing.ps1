<#
    Shares the connected facility's VPN with the workstations, through Windows' Internet Connection
    Sharing: what they send into the tunnel comes out of the EasyVPN-Gateway adapter and goes on
    into the VPN's adapter as if this machine had sent it, and the answers come back to them. It
    works the same whichever VPN client made the adapter. The gateway runs it as SYSTEM:

        sharing.ps1 Share | Unshare | Status

    EASYVPN_HUB           the EasyVPN-Gateway adapter: its GUID
    EASYVPN_HUB_NAME      and its name
    EASYVPN_HUB_ADDRESS   its address, which sharing gives it (ScopeAddress): the tunnel's .1
    EASYVPN_VPN_ID        the VPN's adapter: its GUID                                    (Share)
    EASYVPN_VPN_NAME      its name                                                       (Share)
    EASYVPN_VPN_DEVICE    its device, as Network Connections shows it                   (Share)

    Windows shares one adapter at a time, so sharing it ends any other sharing on this machine.
    Unshare ends only the gateway's own: the EasyVPN-Gateway adapter's, and what it was shared with.
#>
param([ValidateSet('Share', 'Unshare', 'Status')][string]$Action)
$ErrorActionPreference = 'Stop'
trap { Write-Output $_.Exception.Message; exit 1 }

$public, $private = 0, 1 # ICSSHARINGTYPE

$manager = New-Object -ComObject HNetCfg.HNetShare

function Get-Connections {
    foreach ($connection in $manager.EnumEveryConnection) {
        $props = $manager.NetConnectionProps.Invoke($connection)
        [pscustomobject]@{
            Name    = $props.Name
            Guid    = $props.Guid
            Device  = $props.DeviceName
            Sharing = $manager.INetSharingConfigurationForINetConnection.Invoke($connection)
        }
    }
}

function Find-Hub($connections) {
    $connections | Where-Object { $_.Guid -eq $env:EASYVPN_HUB -or $_.Name -eq $env:EASYVPN_HUB_NAME } | Select-Object -First 1
}

function Shared($connections, $type) {
    $connections | Where-Object { $_.Sharing.SharingEnabled -and $_.Sharing.SharingConnectionType -eq $type }
}

$connections = @(Get-Connections)
$hub = Find-Hub $connections

if ($Action -eq 'Status') {
    $service = Get-Service SharedAccess -ErrorAction SilentlyContinue
    if (-not $service) { throw 'This Windows has no Internet Connection Sharing.' }
    if ($service.StartType -eq 'Disabled') { throw 'The "Internet Connection Sharing (ICS)" service is disabled. Set it to Manual in Services.' }
    $policy = Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Network Connections' -ErrorAction SilentlyContinue
    if ($policy -and $policy.NC_ShowSharedAccessUI -eq 0) {
        throw 'Group policy forbids it here: "Prohibit use of Internet Connection Sharing on your DNS domain network".'
    }
    if (-not $hub) { throw "The $env:EASYVPN_HUB_NAME adapter is not there. Is the EasyVPN Gateway service running?" }
    $shared = Shared $connections $public
    if ($hub.Sharing.SharingEnabled -and $shared) { Write-Output "shared=$($shared[0].Name)" } else { Write-Output 'shared=' }
    return
}

if ($Action -eq 'Unshare') {
    if ($hub -and $hub.Sharing.SharingEnabled) {
        foreach ($connection in @(Shared $connections $public)) {
            $connection.Sharing.DisableSharing()
            Write-Output "Stopped sharing $($connection.Name) with the workstations"
        }
        $hub.Sharing.DisableSharing()
    }
    return
}

# The hub's adapter is made as the gateway starts; it may only just have started.
for ($i = 0; -not $hub; $i++) {
    if ($i -ge 20) { throw "The $env:EASYVPN_HUB_NAME adapter is not there. Is the gateway's tunnel running? Its activity log says." }
    Start-Sleep -Milliseconds 500
    $connections = @(Get-Connections)
    $hub = Find-Hub $connections
}

$vpn = $connections | Where-Object { $_.Guid -eq $env:EASYVPN_VPN_ID } | Select-Object -First 1
if (-not $vpn) { $vpn = $connections | Where-Object { $_.Name -eq $env:EASYVPN_VPN_NAME } | Select-Object -First 1 }
if (-not $vpn) { $vpn = $connections | Where-Object { $_.Device -eq $env:EASYVPN_VPN_DEVICE } | Select-Object -First 1 }
if (-not $vpn) { throw "Windows does not offer $env:EASYVPN_VPN_NAME ($env:EASYVPN_VPN_DEVICE) for sharing." }
if ($vpn.Guid -eq $hub.Guid) { throw "The facility's networks lead back into the tunnel, not into its VPN." }

# Sharing gives the adapter it shares with this address, read when sharing starts.
$parameters = 'HKLM:\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters'
if (-not (Test-Path $parameters)) { New-Item $parameters -Force | Out-Null }
foreach ($name in 'ScopeAddress', 'ScopeAddressBackup') {
    Set-ItemProperty $parameters -Name $name -Value $env:EASYVPN_HUB_ADDRESS -Type String
}

$already = $vpn.Sharing.SharingEnabled -and $vpn.Sharing.SharingConnectionType -eq $public `
    -and $hub.Sharing.SharingEnabled -and $hub.Sharing.SharingConnectionType -eq $private
if (-not $already) {
    foreach ($connection in @($connections | Where-Object { $_.Sharing.SharingEnabled })) {
        $connection.Sharing.DisableSharing()
        if ($connection.Guid -ne $hub.Guid -and $connection.Guid -ne $vpn.Guid) {
            Write-Output "Stopped Windows sharing $($connection.Name), as it shares one adapter at a time"
        }
    }
    $vpn.Sharing.EnableSharing($public)
    $hub.Sharing.EnableSharing($private)
}

# Should sharing have given the adapter an address of its own, it gets the hub's back: the
# workstations are on the tunnel's network.
$address = Get-NetIPAddress -InterfaceAlias $env:EASYVPN_HUB_NAME -AddressFamily IPv4 -ErrorAction SilentlyContinue
if (-not ($address | Where-Object IPAddress -eq $env:EASYVPN_HUB_ADDRESS)) {
    & netsh interface ip set address name="$env:EASYVPN_HUB_NAME" source=static addr=$env:EASYVPN_HUB_ADDRESS mask=255.255.255.0 gateway=none | Out-Null
    Write-Output "Gave $env:EASYVPN_HUB_NAME its address $env:EASYVPN_HUB_ADDRESS back after sharing changed it to $($address.IPAddress -join ', ')"
}
Write-Output "Sharing $($vpn.Name) ($($vpn.Device)) with the workstations"
