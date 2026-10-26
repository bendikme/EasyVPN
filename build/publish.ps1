<#
    Builds EasyVPN into artifacts\, ready to copy to the machines:

      artifacts\Gateway   for the gateway: EasyVPN.Gateway.exe (the service), EasyVPN.GatewayApp.exe
                          (its status, in the notification area), wintun.dll, connectors\, sharing.ps1,
                          install-gateway.ps1
      artifacts\Client    for each workstation: EasyVPN.exe, EasyVPN.Agent.exe, wintun.dll,
                          install-client.ps1

    Everything is self-contained: the machines need no .NET installed. Wintun (WireGuard's, signed
    by them), the one thing not built here, is downloaded from its release and checked against the
    hash pinned below. The tunnel itself is EasyVPN's own (Wintun.cs, PacketLink.cs) since 0.5.

        .\build\publish.ps1
        .\build\publish.ps1 -Version 1.2.3    stamped with that version (release.ps1 does this)
#>
param(
    [string]$Configuration = 'Release',
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$downloads = Join-Path $artifacts 'downloads'

# To update one: change the URL, run this, check the new release, and pin the hash it reports.
$binaries = @{
    wintun = @{
        Url     = 'https://www.wintun.net/builds/wintun-0.14.1.zip'
        Sha256  = '07C256185D6EE3652E09FA55C0B673E2624B565E02C4B9091C79CA7D2F24EF51'
        File    = 'wintun\bin\amd64\wintun.dll'
        License = 'wintun\LICENSE.txt'
    }
}

function Get-Binary([string]$name) {
    $binary = $binaries[$name]
    $zip = Join-Path $downloads "$name.zip"
    $folder = Join-Path $downloads $name
    if (-not (Test-Path $zip)) {
        New-Item -ItemType Directory -Force $downloads | Out-Null
        Invoke-WebRequest $binary.Url -OutFile $zip -UseBasicParsing
    }
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash
    if ($hash -ne $binary.Sha256) {
        Remove-Item $zip
        throw "The $name download has the hash $hash, not the pinned $($binary.Sha256)."
    }
    if (-not (Test-Path $folder)) { Expand-Archive $zip $folder }
    [pscustomobject]@{
        File    = Join-Path $folder $binary.File
        License = Join-Path $folder $binary.License
    }
}

function Publish-Project([string]$project, [string]$output) {
    # Without -Version, the one in Directory.Build.props.
    $stamp = if ($Version) { "-p:Version=$Version" }
    dotnet publish (Join-Path $root "src\$project\$project.csproj") -c $Configuration -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None $stamp -o $output
    if ($LASTEXITCODE) { throw "Publishing $project failed." }
}

$gateway = Join-Path $artifacts 'Gateway'
$client = Join-Path $artifacts 'Client'
Remove-Item $gateway, $client -Recurse -Force -ErrorAction SilentlyContinue

$wintun = Get-Binary wintun

Publish-Project EasyVPN.Gateway $gateway
Publish-Project EasyVPN.GatewayApp $gateway
Copy-Item $wintun.File, "$PSScriptRoot\install-gateway.ps1" $gateway
Copy-Item $wintun.License (Join-Path $gateway 'wintun-LICENSE.txt')

Publish-Project EasyVPN.Agent $client
Publish-Project EasyVPN $client
Copy-Item $wintun.File, "$PSScriptRoot\install-client.ps1" $client
Copy-Item $wintun.License (Join-Path $client 'wintun-LICENSE.txt')

Write-Output "Built $gateway and $client"
