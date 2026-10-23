<#
    windows - a VPN connection built into Windows (IKEv2, SSTP, L2TP/IPsec), dialled with rasdial.

    Create the connection once, for all users so the gateway service can dial it, for example:
        Add-VpnConnection -AllUserConnection -Name "ACME" -ServerAddress vpn.acme.com `
            -TunnelType Ikev2 -SplitTunneling
        Add-VpnConnectionRoute -AllUserConnection -ConnectionName "ACME" -DestinationPrefix 10.60.0.0/24

    Settings:  name   the connection's name (required)
               user   the user name; the password is the facility's "password"

    Check: the connection exists, for all users.
#>
param([ValidateSet('Connect', 'Disconnect', 'Check')][string]$Action)
$ErrorActionPreference = 'Stop'
trap { Write-Output $_.Exception.Message; exit 1 }

$name = $env:EASYVPN_NAME
if (-not $name) { throw 'Set "name" to the Windows VPN connection to dial.' }
$phonebook = "$env:ProgramData\Microsoft\Network\Connections\Pbk\rasphone.pbk"

if ($Action -eq 'Check') {
    $connection = Get-VpnConnection -AllUserConnection -Name $name -ErrorAction SilentlyContinue
    if (-not $connection) { throw "There is no Windows VPN connection called '$name' for all users: make it with Add-VpnConnection -AllUserConnection." }
    Write-Output "Windows VPN connection '$name', $($connection.TunnelType) to $($connection.ServerAddress)"
    return
}

if ($Action -eq 'Connect') {
    # rasdial only takes the password on its command line. On the gateway, only administrators
    # can see other processes' command lines.
    if ($env:EASYVPN_USER) {
        rasdial $name $env:EASYVPN_USER $env:EASYVPN_PASSWORD "/phonebook:$phonebook"
    }
    else {
        rasdial $name "/phonebook:$phonebook"
    }
}
else {
    rasdial $name /disconnect
}
exit $LASTEXITCODE
