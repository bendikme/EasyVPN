<#
    Sends one facility's traffic into the EasyVPN adapter, or stops doing so. The agent runs it as
    SYSTEM:  network.ps1 Apply | Clear

    EASYVPN_OLD_ROUTES    routes to remove first, comma separated (what was routed before)
    EASYVPN_ROUTES        the facility's networks, routed to the adapter              (Apply)
    EASYVPN_DNS_DOMAINS   names that resolve through the facility's DNS servers       (Apply)
    EASYVPN_DNS_SERVERS   the facility's DNS servers                                  (Apply)

    The DNS rules are Name Resolution Policy Table rules: only names under the facility's domains
    go to its servers, and the workstation's own DNS settings are left alone.
#>
param([ValidateSet('Apply', 'Clear')][string]$Action)
$ErrorActionPreference = 'Stop'
trap { Write-Output $_.Exception.Message; exit 1 }
$ProgressPreference = 'SilentlyContinue'

$adapter = 'EasyVPN'
$tag = 'EasyVPN'
function Split-List($value) { @("$value" -split ',' | Where-Object { $_ }) }

foreach ($prefix in Split-List $env:EASYVPN_OLD_ROUTES) {
    Remove-NetRoute -InterfaceAlias $adapter -DestinationPrefix $prefix -PolicyStore ActiveStore -Confirm:$false -ErrorAction SilentlyContinue
}
Get-DnsClientNrptRule | Where-Object Comment -eq $tag | ForEach-Object { Remove-DnsClientNrptRule -Name $_.Name -Force }

if ($Action -eq 'Apply') {
    # gost creates the adapter as it starts; it may only just have started.
    for ($i = 0; -not (Get-NetAdapter -Name $adapter -ErrorAction SilentlyContinue); $i++) {
        if ($i -ge 40) { throw "There is no $adapter adapter. Is the tunnel running?" }
        Start-Sleep -Milliseconds 250
    }

    foreach ($prefix in Split-List $env:EASYVPN_ROUTES) {
        New-NetRoute -InterfaceAlias $adapter -DestinationPrefix $prefix -RouteMetric 1 -PolicyStore ActiveStore | Out-Null
        Write-Output "Routed $prefix to $adapter"
    }

    $servers = Split-List $env:EASYVPN_DNS_SERVERS
    if ($servers) {
        foreach ($domain in Split-List $env:EASYVPN_DNS_DOMAINS) {
            $namespace = if ($domain.StartsWith('.')) { $domain } else { ".$domain" }
            Add-DnsClientNrptRule -Namespace $namespace -NameServers $servers -Comment $tag
            Write-Output "Names under $namespace resolve through $($servers -join ', ')"
        }
    }
}

Clear-DnsClientCache
