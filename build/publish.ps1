<#
    Builds EasyVPN into artifacts\, ready to copy to the machines:

      artifacts\Gateway   for the gateway: EasyVPN.Gateway.exe, gost.exe, connectors\, install-gateway.ps1
      artifacts\Client    for each workstation: EasyVPN.exe, EasyVPN.Agent.exe, gost.exe, wintun.dll,
                          install-client.ps1

    Everything is self-contained: the machines need no .NET installed. gost and Wintun are
    downloaded from their releases and checked against the hashes pinned below.

        .\build\publish.ps1
#>
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$downloads = Join-Path $artifacts 'downloads'

# To update one: change the URL, run this, check the new release, and pin the hash it reports.
$binaries = @{
    gost   = @{
        Url     = 'https://github.com/go-gost/gost/releases/download/v3.3.1/gost_3.3.1_windows_amd64.zip'
        Sha256  = '5FDD15C9BEEB74C69FF36272FA8DD3C6A1C04A980DE6E4267F8F886E56A136FD'
        File    = 'gost.exe'
        License = 'LICENSE'
    }
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
    dotnet publish (Join-Path $root "src\$project\$project.csproj") -c $Configuration -r win-x64 --self-contained `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None -o $output
    if ($LASTEXITCODE) { throw "Publishing $project failed." }
}

$gateway = Join-Path $artifacts 'Gateway'
$client = Join-Path $artifacts 'Client'
Remove-Item $gateway, $client -Recurse -Force -ErrorAction SilentlyContinue

$gost = Get-Binary gost
$wintun = Get-Binary wintun

Publish-Project EasyVPN.Gateway $gateway
Copy-Item $gost.File, "$PSScriptRoot\install-gateway.ps1" $gateway
Copy-Item $gost.License (Join-Path $gateway 'gost-LICENSE.txt')

Publish-Project EasyVPN.Agent $client
Publish-Project EasyVPN $client
Copy-Item $gost.File, $wintun.File, "$PSScriptRoot\install-client.ps1" $client
Copy-Item $gost.License (Join-Path $client 'gost-LICENSE.txt')
Copy-Item $wintun.License (Join-Path $client 'wintun-LICENSE.txt')

Write-Output "Built $gateway and $client"
