<#
    Builds a release of EasyVPN into artifacts\, as the GitHub release workflow does:

      EasyVPN-Gateway-<version>-x64.msi   for the gateway
      EasyVPN-Client-<version>-x64.msi    for each workstation: the agent and the app
      SHA256SUMS.txt

    It runs publish.ps1 and packages each of its folders with installer\Gateway.wxs and
    installer\Client.wxs. One-time setup, WiX 5 and two of its extensions:

        dotnet tool install --global wix --version 5.0.2
        wix extension add -g WixToolset.Util.wixext/5.0.2 WixToolset.Firewall.wixext/5.0.2

    WiX 6 and later need the Open Source Maintenance Fee licence accepted, which is why 5 is pinned.

        .\build\release.ps1 -Version 1.2.3
#>
param(
    # Three or four numbers: MSI compares numbers only, so 1.2.3-beta could never upgrade an install.
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version
)
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw 'The WiX command line tool was not found: dotnet tool install --global wix --version 5.0.2'
}

& "$PSScriptRoot\publish.ps1" -Version $Version

function Build-Msi([string]$name) {
    $msi = Join-Path $artifacts "EasyVPN-$name-$Version-x64.msi"
    Remove-Item $msi -ErrorAction SilentlyContinue
    # Out-Host, so WiX's output is not taken for this function's result.
    wix build -nologo -arch x64 -pdbtype none -ext WixToolset.Util.wixext -ext WixToolset.Firewall.wixext `
        -d "Version=$Version" -d "PublishDir=$(Join-Path $artifacts $name)" `
        -o $msi (Join-Path $root "installer\$name.wxs") | Out-Host
    if ($LASTEXITCODE -or -not (Test-Path $msi)) {
        # Never leave half an installer behind to be uploaded.
        Remove-Item $msi -ErrorAction SilentlyContinue
        throw "Building the $name installer failed. It needs WiX 5 and its two extensions (see the top of this script)."
    }
    $msi
}

$msis = @(
    Build-Msi Gateway
    Build-Msi Client
)

$sums = foreach ($msi in $msis) {
    "$((Get-FileHash $msi -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $msi -Leaf)"
}
[IO.File]::WriteAllLines((Join-Path $artifacts 'SHA256SUMS.txt'), [string[]]$sums)

Get-Item $msis | Format-Table Name, @{ Name = 'MB'; Expression = { '{0:N1}' -f ($_.Length / 1MB) } } -AutoSize | Out-Host
Write-Output "Built release $Version in $artifacts"
