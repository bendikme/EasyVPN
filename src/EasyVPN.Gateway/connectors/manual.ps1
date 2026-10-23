<#
    manual - the VPN is connected by hand, with its own client, on the gateway's desktop.

    For any VPN client that cannot be scripted, or that needs a person to sign in (MFA, SAML, a
    token). EasyVPN waits for the facility's "check" to answer, for up to its "timeout" seconds, so
    give a manual facility both - and a timeout long enough to sign in, such as 300.

    Disconnecting is by hand too: EasyVPN will not call the facility disconnected, or connect
    another one, while its check still answers.

    Check has nothing to look at: any VPN client will do.
#>
param([ValidateSet('Connect', 'Disconnect', 'Check')][string]$Action)

if ($Action -eq 'Check') {
    Write-Output 'Connected by hand, in its VPN client on this desktop.'
}
elseif ($Action -eq 'Connect') {
    Write-Output "Connect $env:EASYVPN_FACILITY with its VPN client on the gateway desktop."
}
else {
    Write-Output "Disconnect $env:EASYVPN_FACILITY in its VPN client on the gateway desktop."
}
