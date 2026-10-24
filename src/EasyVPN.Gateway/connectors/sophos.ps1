<#
    sophos - Sophos Connect, driven through its command-line client, sccli.

    Settings:  config      the file the customer sent: .scx (IPsec), .ovpn (SSL VPN) or .pro
                           (provisioning). It is imported again each time, so a new file takes
                           effect; leave it out for a connection already in Sophos Connect
               connection  the connection's name in Sophos Connect (default: the facility's id)
               user        the user name; the password is the facility's "password"

    A Sophos SSL VPN .ovpn profile runs in OpenVPN itself too, with no Sophos software: the openvpn
    connector. This one is for what only Sophos Connect takes, such as IPsec.
    Check: Sophos Connect installed, and the file there.
#>
param([ValidateSet('Connect', 'Disconnect', 'Check')][string]$Action)
$ErrorActionPreference = 'Stop'
trap { Write-Output $_.Exception.Message; exit 1 }

$sccli = @(
    "${env:ProgramFiles(x86)}\Sophos\Connect\sccli.exe",
    "$env:ProgramFiles\Sophos\Connect\sccli.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $sccli) { throw 'Sophos Connect is not installed.' }
$name = if ($env:EASYVPN_CONNECTION) { $env:EASYVPN_CONNECTION } else { $env:EASYVPN_FACILITY }

if ($Action -eq 'Check') {
    if ($env:EASYVPN_CONFIG -and -not (Test-Path $env:EASYVPN_CONFIG)) { throw "The file $env:EASYVPN_CONFIG is not there." }
    Write-Output "Sophos Connect, connection '$name'"
    return
}

if ($Action -eq 'Disconnect') {
    & $sccli disable -n $name
    exit $LASTEXITCODE
}

if ($env:EASYVPN_CONFIG) {
    & $sccli add -f $env:EASYVPN_CONFIG -n $name -o
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# sccli takes the password only on its command line. On the gateway, only administrators can see
# other processes' command lines. Without a user name, the connection's saved sign-in is used.
$arguments = @('enable', '-n', $name)
if ($env:EASYVPN_USER) { $arguments += '-u', $env:EASYVPN_USER, '-p', $env:EASYVPN_PASSWORD }
$output = & $sccli @arguments
$code = $LASTEXITCODE
if ($env:EASYVPN_PASSWORD) { $output = $output -replace [regex]::Escape($env:EASYVPN_PASSWORD), '********' }
$output | Where-Object { $_ -match '\S' } | Write-Output
exit $code
