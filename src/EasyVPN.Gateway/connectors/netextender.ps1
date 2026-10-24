<#
    netextender - SonicWall NetExtender, through its command line: nxcli in the newer NetExtender,
    NECLI in 10.2 and earlier.

    Settings:  host    the server, as server or server:port (required)
               domain  the domain the server asks for, such as LocalDomain
               user    the user name; the password is the facility's "password"
               trust   "true" accepts the server's certificate without checking it: for a server
                       with a self-signed certificate, where NetExtender would otherwise stop and ask

    nxcli keeps a profile, made for all users under the facility's id, and made again on each
    connect so it follows the settings. Sign-ins that ask for more (a one-time code, a browser) are
    for the manual connector.
    Check: NetExtender installed, and a server set.
#>
param([ValidateSet('Connect', 'Disconnect', 'Check')][string]$Action)
$ErrorActionPreference = 'Stop'
trap { Write-Output $_.Exception.Message; exit 1 }

$cli = foreach ($folder in "${env:ProgramFiles(x86)}\SonicWall\SSL-VPN\NetExtender", "$env:ProgramFiles\SonicWall\SSL-VPN\NetExtender") {
    foreach ($exe in 'nxcli.exe', 'NECLI.exe') {
        if (Test-Path "$folder\$exe") { "$folder\$exe" }
    }
}
$cli = $cli | Select-Object -First 1
if (-not $cli) { throw 'SonicWall NetExtender is not installed.' }
$nxcli = (Split-Path $cli -Leaf) -eq 'nxcli.exe'
# $host is PowerShell's own.
$server = $env:EASYVPN_HOST

if ($Action -eq 'Check') {
    if (-not $server) { throw 'Set "host" to the server.' }
    Write-Output "NetExtender ($(Split-Path $cli -Leaf)), server $server"
    return
}

if ($Action -eq 'Disconnect') {
    & $cli disconnect
    exit $LASTEXITCODE
}

if (-not $server) { throw 'Set "host" to the server.' }
$sign = @()
if ($env:EASYVPN_DOMAIN) { $sign += '-d', $env:EASYVPN_DOMAIN }
if ($env:EASYVPN_USER) { $sign += '-u', $env:EASYVPN_USER, '-p', $env:EASYVPN_PASSWORD }
$trust = if ($env:EASYVPN_TRUST -eq 'true') { @('--always-trust') } else { @() }

# Both take the password only on their command line. On the gateway, only administrators can see
# other processes' command lines.
if ($nxcli) {
    & $cli connection delete $env:EASYVPN_FACILITY | Out-Null
    & $cli connection add $env:EASYVPN_FACILITY -s $server @sign -g @trust | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "nxcli could not make the profile (exit code $LASTEXITCODE)." }
    $output = & $cli connect $env:EASYVPN_FACILITY @trust
}
else {
    $output = & $cli connect -s $server @sign
}
$code = $LASTEXITCODE
if ($env:EASYVPN_PASSWORD) { $output = $output -replace [regex]::Escape($env:EASYVPN_PASSWORD), '********' }
$output | Where-Object { $_ -match '\S' } | Write-Output
exit $code
