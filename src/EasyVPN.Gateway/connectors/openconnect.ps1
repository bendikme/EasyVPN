<#
    openconnect - OpenConnect, the open-source client, for servers whose own client has no command
    line: GlobalProtect above all, and also Ivanti/Pulse, Fortinet, F5, Array and AnyConnect. It
    signs in with a user name and password; a sign-in in a browser (SAML) or with a token is for
    the manual connector.

    Settings:  protocol    gp (GlobalProtect, the default), pulse, nc (older Juniper and Pulse),
                           fortinet, f5, array or anyconnect
               host        the server or portal (required)
               user        the user name; the password is the facility's "password"
               group       the gateway or group to choose, when the server offers several
               servercert  the server certificate's pin as OpenConnect prints it (pin-sha256:...),
                           for a certificate Windows does not trust

    OpenConnect for Windows, with its vpnc-script, is looked for in %ProgramFiles%\OpenConnect-GUI
    and %ProgramFiles%\OpenConnect. Some servers check the client (GlobalProtect's HIP check): ask
    the customer before relying on this one.
    Check: OpenConnect installed, and a server set.
#>
param([ValidateSet('Connect', 'Disconnect', 'Check')][string]$Action)
$ErrorActionPreference = 'Stop'
trap { Write-Output $_.Exception.Message; exit 1 }

$openconnect = @(
    "$env:ProgramFiles\OpenConnect-GUI\openconnect.exe",
    "$env:ProgramFiles\OpenConnect\openconnect.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $openconnect) { throw 'OpenConnect is not installed.' }
$protocol = if ($env:EASYVPN_PROTOCOL) { $env:EASYVPN_PROTOCOL } else { 'gp' }
$protocols = 'gp', 'pulse', 'nc', 'fortinet', 'f5', 'array', 'anyconnect'
$passwordFile = Join-Path $env:EASYVPN_WORK 'password.txt'
$logFile = Join-Path $env:EASYVPN_WORK 'openconnect.log'

if ($Action -eq 'Check') {
    if (-not $env:EASYVPN_HOST) { throw 'Set "host" to the server.' }
    if ($protocol -notin $protocols) { throw "Set ""protocol"" to one of $($protocols -join ', ')." }
    Write-Output "OpenConnect, $protocol to $env:EASYVPN_HOST"
    return
}

function Stop-OpenConnect {
    # One facility at a time: whatever OpenConnect runs from here is this connector's.
    $running = Get-Process openconnect -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $openconnect }
    if ($running) {
        $running | Stop-Process -Force
        Start-Sleep -Seconds 2
    }
    Remove-Item $passwordFile -ErrorAction SilentlyContinue
}

Stop-OpenConnect
if ($Action -eq 'Disconnect') { return }

if (-not $env:EASYVPN_HOST) { throw 'Set "host" to the server.' }
if ($protocol -notin $protocols) { throw "Set ""protocol"" to one of $($protocols -join ', ')." }

$arguments = @("--protocol=$protocol", '--non-inter')
if ($env:EASYVPN_USER) { $arguments += "--user=$env:EASYVPN_USER", '--passwd-on-stdin' }
if ($env:EASYVPN_GROUP) { $arguments += "--authgroup=$env:EASYVPN_GROUP" }
if ($env:EASYVPN_SERVERCERT) { $arguments += "--servercert=$env:EASYVPN_SERVERCERT" }
$script = @('vpnc-script-win.js', 'vpnc-script.js') | ForEach-Object { Join-Path (Split-Path $openconnect) $_ } |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($script) { $arguments += "--script=$script" }
$arguments += $env:EASYVPN_HOST
if ($arguments | Where-Object { $_ -match '"' }) { throw 'The settings cannot contain a double quote.' }

# OpenConnect reads the password from standard input, so cmd hands it the file: started this way,
# it holds none of this script's handles, and the gateway does not wait for it. The work folder is
# readable by SYSTEM and administrators only; the file goes on disconnect.
[System.IO.File]::WriteAllText($passwordFile, "$env:EASYVPN_PASSWORD`n")
$quoted = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
# cmd /c drops the outer pair of quotes and runs the rest.
$line = '""' + $openconnect + '" ' + $quoted + ' < "' + $passwordFile + '" > "' + $logFile + '" 2>&1"'
$process = Start-Process cmd.exe -ArgumentList '/d', '/c', $line -WindowStyle Hidden -PassThru

# A refused password or an unknown server ends it within seconds; otherwise the check decides.
Start-Sleep -Seconds 8
if ($process.HasExited) {
    Remove-Item $passwordFile -ErrorAction SilentlyContinue
    Get-Content $logFile -Tail 15 -ErrorAction SilentlyContinue | Write-Output
    throw 'OpenConnect stopped.'
}
Write-Output "OpenConnect started, logging to $logFile"
