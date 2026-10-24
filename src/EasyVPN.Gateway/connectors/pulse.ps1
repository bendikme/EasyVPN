<#
    pulse - Ivanti Secure Access, once Pulse Secure, through its command-line launcher,
    pulselauncher.

    Settings:  url    the server's sign-in address, such as https://vpn.example.com (required)
               realm  the realm to sign in to (required)
               user   the user name (required); the password is the facility's "password"

    The launcher cannot answer a second sign-in (a token after the password) or choose between
    roles: a facility that needs either is one for the manual connector.
    Check: the client installed, and the address and realm set.
#>
param([ValidateSet('Connect', 'Disconnect', 'Check')][string]$Action)
$ErrorActionPreference = 'Stop'
trap { Write-Output $_.Exception.Message; exit 1 }

$launcher = @(
    "$env:ProgramFiles\Common Files\Ivanti\Integration\pulselauncher.exe",
    "${env:ProgramFiles(x86)}\Common Files\Ivanti\Integration\pulselauncher.exe",
    "$env:ProgramFiles\Common Files\Pulse Secure\Integration\pulselauncher.exe",
    "${env:ProgramFiles(x86)}\Common Files\Pulse Secure\Integration\pulselauncher.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $launcher) { throw 'Ivanti Secure Access (Pulse Secure) is not installed.' }
if (-not $env:EASYVPN_URL) { throw 'Set "url" to the server''s sign-in address.' }

if ($Action -eq 'Check') {
    if (-not $env:EASYVPN_REALM) { throw 'Set "realm" to the realm to sign in to.' }
    Write-Output "Ivanti Secure Access, $env:EASYVPN_URL, realm $env:EASYVPN_REALM"
    return
}

if ($Action -eq 'Disconnect') {
    & $launcher -signout -url $env:EASYVPN_URL
    exit $LASTEXITCODE
}

if (-not $env:EASYVPN_REALM) { throw 'Set "realm" to the realm to sign in to.' }
if (-not $env:EASYVPN_USER) { throw 'Set "user" to the user name.' }

# The launcher takes the password only on its command line. On the gateway, only administrators can
# see other processes' command lines.
$output = & $launcher -url $env:EASYVPN_URL -u $env:EASYVPN_USER -p $env:EASYVPN_PASSWORD -r $env:EASYVPN_REALM
$code = $LASTEXITCODE
if ($env:EASYVPN_PASSWORD) { $output = $output -replace [regex]::Escape($env:EASYVPN_PASSWORD), '********' }
$output | Where-Object { $_ -match '\S' } | Write-Output

# Its exit codes, as Ivanti documents them.
$meaning = @{
    -1 = 'The Ivanti Secure Access client is not running.'
    1  = 'The launcher was given something it does not take.'
    2  = 'The connection failed, or the server could not be reached. A realm that has a role to choose fails this way too.'
    3  = 'Connected, but with an error.'
    5  = 'The connection was cancelled.'
    6  = 'The client certificate was refused.'
    7  = 'The connection timed out.'
    8  = 'The server does not allow connecting this way.'
    100 = 'The client failed.'
}
if ($code -ne 0 -and $meaning.ContainsKey($code)) { Write-Output $meaning[$code] }
exit $code
