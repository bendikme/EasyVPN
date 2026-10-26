<#
    forticlient - FortiClient (Fortinet), SSL VPN or IPsec, through its command line: FortiVPN.exe
    --cli, which FortiClient has from 7.4.6.

    Settings:  tunnel       the VPN connection's name, as FortiClient lists it (required)
               user         the user name; the password is the facility's "password". Leave both out
                            for a tunnel that keeps its own
               certificate  the thumbprint of a client certificate in the gateway's computer
                            certificate store, for a tunnel that signs in with one

    The command line connects only tunnels already made in FortiClient (or by its EMS): make the
    tunnel in FortiClient on the gateway first, under the name set here. It cannot answer a sign-in
    in a browser (SAML) or a token's code: such a tunnel is for the manual connector, or the
    openconnect one (protocol fortinet). Nor can an older FortiClient be driven so; and FortiClient
    7.4.4 and later have no IPsec with IKEv1, which leaves such a tunnel to the manual connector with
    an older FortiClient.
    Check: FortiClient 7.4.6 or later installed, and the tunnel among those it lists.
#>
param([ValidateSet('Connect', 'Disconnect', 'Check')][string]$Action)
$ErrorActionPreference = 'Stop'
trap { Write-Output $_.Exception.Message; exit 1 }

$fortivpn = @(
    "$env:ProgramFiles\Fortinet\FortiClient\FortiVPN.exe",
    "${env:ProgramFiles(x86)}\Fortinet\FortiClient\FortiVPN.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $fortivpn) { throw 'FortiClient 7.4.6 or later is not installed: older ones have no command line.' }
# An older FortiVPN.exe takes no --cli, and might open its window instead, where nobody would see it.
$info = (Get-Item $fortivpn).VersionInfo
$version = if ("$($info.ProductVersion) $($info.FileVersion)" -match '\d+\.\d+\.\d+(\.\d+)?') { [version]$Matches[0] }
if ($version -and $version -lt [version]'7.4.6') { throw "FortiClient $version has no command line: install FortiClient 7.4.6 or later." }
if (-not $env:EASYVPN_TUNNEL) { throw 'Set "tunnel" to the VPN connection''s name, as FortiClient lists it.' }
$tunnel = $env:EASYVPN_TUNNEL

function Invoke-FortiVpn([string[]]$arguments) {
    $output = & $fortivpn --cli @arguments | ForEach-Object { "$_" }
    if ($env:EASYVPN_PASSWORD) { $output = $output -replace [regex]::Escape($env:EASYVPN_PASSWORD), '********' }
    # Not its banner, "FortiClient VPN 7.4.6.…", printed each time.
    $output | Where-Object { $_ -match '\S' -and $_ -notmatch '^\s*FortiClient\b.*\d+\.\d+\.\d+' }
}

# Each tunnel's state, by name: it prints "<name> :: Connected" (Connecting, Disconnected).
function Get-Tunnels {
    $states = @{}
    foreach ($line in Invoke-FortiVpn '--status') {
        if ($line -match '^\s*(.+?)\s*::\s*(\S.*?)\s*$') { $states[$Matches[1]] = $Matches[2] }
    }
    $states
}

# Says each state the tunnel passes through, until it is in this one or the time is up.
function Wait-Tunnel([string]$state, [int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    $last = $null
    while ((Get-Date) -lt $deadline) {
        $now = (Get-Tunnels)[$tunnel]
        if ($now -eq $state) { return }
        if ($now -ne $last) {
            Write-Output "FortiClient shows $tunnel`: $(if ($now) { $now } else { 'not listed' })"
            $last = $now
        }
        Start-Sleep -Seconds 2
    }
}

if ($Action -eq 'Check') {
    # Its list, under SSL: and IPSEC:, a tunnel a line.
    $listed = Invoke-FortiVpn '--list' | Where-Object { $_ -match '^\s+\S' } | ForEach-Object { $_.Trim() }
    if ($listed -notcontains $tunnel) {
        throw "FortiClient lists no tunnel called '$tunnel'$(if ($listed) { " (it lists $($listed -join ', '))" }): make it in FortiClient first."
    }
    Write-Output "FortiClient$(if ($version) { " $version" }), tunnel '$tunnel'"
    return
}

if ($Action -eq 'Disconnect') {
    if ((Get-Tunnels)[$tunnel] -in $null, 'Disconnected') {
        Write-Output "FortiClient has $tunnel disconnected."
        return
    }
    Invoke-FortiVpn '--disconnect', '--tunnel', $tunnel
    Wait-Tunnel 'Disconnected' 30
    if ((Get-Tunnels)[$tunnel] -ne 'Disconnected') { throw "FortiClient did not show $tunnel disconnected within 30 s." }
    Write-Output "FortiClient has disconnected $tunnel."
    return
}

# The gateway is FortiClient's only user: another tunnel it has up goes first.
$states = Get-Tunnels
foreach ($other in @($states.Keys | Where-Object { $_ -ne $tunnel -and $states[$_] -ne 'Disconnected' })) {
    Write-Output "FortiClient has $other $($states[$other].ToLower()): disconnecting it first."
    Invoke-FortiVpn '--disconnect', '--tunnel', $other
}
if ($states[$tunnel] -eq 'Connected') {
    Write-Output "FortiClient has $tunnel connected."
    return
}
if (-not $states.ContainsKey($tunnel)) { throw "FortiClient lists no tunnel called '$tunnel': make it in FortiClient first." }

# FortiVPN.exe takes the password only on its command line. On the gateway, only administrators
# can see other processes' command lines. Without a user name, the tunnel's saved sign-in is used.
$arguments = @('--connect', '--tunnel', $tunnel)
if ($env:EASYVPN_USER) { $arguments += '--username', $env:EASYVPN_USER, '--password', $env:EASYVPN_PASSWORD }
if ($env:EASYVPN_CERTIFICATE) { $arguments += '--computerstore-thumbprint', ($env:EASYVPN_CERTIFICATE -replace '\s', '') }
Invoke-FortiVpn $arguments
$code = $LASTEXITCODE

# Connected when FortiClient says so: the command may return while it is still connecting.
Wait-Tunnel 'Connected' 90
if ((Get-Tunnels)[$tunnel] -ne 'Connected') {
    throw "FortiClient did not show $tunnel connected within 90 s$(if ($code) { " (FortiVPN.exe exit code $code)" })."
}
Write-Output "FortiClient has connected $tunnel."
