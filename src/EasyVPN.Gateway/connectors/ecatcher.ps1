<#
    ecatcher - an Ewon, through Talk2M, with eCatcher on this gateway's desktop. eCatcher has no
    command line, so ecatcher-ui.ps1, beside the gateway, does what a person would there, through
    the accessibility bridge of eCatcher's own Java: starts eCatcher if it is not running, signs in
    to Talk2M with the facility's account and user, searches for the Ewon, presses Connect or
    Disconnect, and waits until eCatcher shows it.

    Settings:  ewon      the Ewon's name as eCatcher lists it (required)
               account   the Talk2M account          } here, or for every eCatcher facility in the
               user      the Talk2M user             } gateway app's Settings
               program   eCatcher.exe, when it is not where its installer put it
               The facility's "password" is the Talk2M user's (or the one in Settings).

    Someone must be signed in to the gateway's desktop, as an administrator: eCatcher runs as one.
    Give the gateway a Talk2M user of its own: Talk2M lets a user be signed in on one computer at a
    time. A user with two-factor sign-in must sign in by hand once on the gateway, ticking "Remember
    this PC". Should an Ewon not connect, run this as administrator on the gateway's desktop, and
    look at what eCatcher showed:
        C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe -ExecutionPolicy Bypass -File "C:\Program Files\EasyVPN Gateway\ecatcher-ui.ps1" Dump
    Check: eCatcher installed, and an Ewon, account and user set. It does not touch eCatcher.
#>
#check-shared: program, account, user
#check-needs: ewon
param([ValidateSet('Connect', 'Disconnect', 'Check')][string]$Action)
$ErrorActionPreference = 'Stop'
trap { Write-Output $_.Exception.Message; exit 1 }

$gateway = $env:EASYVPN_GATEWAY
$ui = Join-Path (Split-Path $gateway) 'ecatcher-ui.ps1'
if (-not (Test-Path $ui)) { throw "ecatcher-ui.ps1 is not beside the gateway ($ui)." }
# eCatcher's Java is 32-bit, and only a 32-bit program can use its accessibility bridge.
$powershell = "$env:SystemRoot\SysWOW64\WindowsPowerShell\v1.0\powershell.exe"
$notHere = 3

if ($Action -eq 'Check') {
    if ($env:EASYVPN_PROGRAM -and -not (Test-Path $env:EASYVPN_PROGRAM)) { throw "eCatcher is not at $env:EASYVPN_PROGRAM." }
    if (-not $env:EASYVPN_PROGRAM) {
        $installed = Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
            Where-Object { $_.DisplayName -like 'eCatcher*' } | Select-Object -First 1
        $default = "${env:ProgramFiles(x86)}\eCatcher-Talk2M\eCatcher.exe"
        if (-not $installed -and -not (Test-Path $default)) { throw 'eCatcher is not installed on this gateway.' }
    }
    if (-not $env:EASYVPN_ACCOUNT -or -not $env:EASYVPN_USER) {
        throw 'Set the Talk2M account and user: for this facility, or for every eCatcher facility in Settings.'
    }
    Write-Output "eCatcher on the gateway's desktop, account $env:EASYVPN_ACCOUNT as $env:EASYVPN_USER"
    return
}
if (-not $env:EASYVPN_EWON) { throw 'Set "ewon" to the Ewon''s name as eCatcher lists it.' }

$run = @('--program', $powershell, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $ui)

# eCatcher on any desktop: its launcher, or the Java it runs in, from its own folder.
$running = Get-Process eCatcher, javaw, java -ErrorAction SilentlyContinue |
    Where-Object { $_.SessionId -ne 0 -and "$($_.Path)" -like '*eCatcher*' }

# The gateway runs it in each signed-in desktop session in turn, and passes on what it prints and
# its exit code: 3 when eCatcher was in none of them.
if ($Action -eq 'Disconnect') {
    $LASTEXITCODE = $notHere
    if ($running) { & $gateway desktop @run Disconnect }
    if ($LASTEXITCODE -eq $notHere) {
        Write-Output 'eCatcher is not running, so nothing is connected.'
        exit 0
    }
    exit $LASTEXITCODE
}

# Only where someone is at the desktop, unlocked first; started there straight away when it runs
# nowhere, rather than looked for there first.
$LASTEXITCODE = $notHere
if ($running) { & $gateway desktop --usable-only @run Connect }
if ($LASTEXITCODE -eq $notHere) {
    & $gateway desktop --usable-only @run Connect -Start
}
exit $LASTEXITCODE
