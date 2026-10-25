<#
    Sends one facility's traffic into the EasyVPN adapter, or stops doing so. The agent runs it as
    SYSTEM:  network.ps1 Apply | Clear | Prepare

    Prepare is for a new adapter, each time gost makes one: NetBIOS off, so Windows' name
    broadcasts do not try to go through the tunnel; and the network Windows sees on it named
    EasyVPN, the ones it remembered for earlier adapters gone.

    EASYVPN_OLD_ROUTES    routes to remove first, comma separated (what was routed before)
    EASYVPN_ROUTES        the facility's networks, routed to the adapter              (Apply)
    EASYVPN_DNS_DOMAINS   names that resolve through the facility's DNS servers       (Apply)
    EASYVPN_DNS_SERVERS   the facility's DNS servers                                  (Apply)

    The DNS rules are Name Resolution Policy Table rules: only names under the facility's domains
    go to its servers, and the workstation's own DNS settings are left alone.
#>
param([ValidateSet('Apply', 'Clear', 'Prepare')][string]$Action)
$ErrorActionPreference = 'Stop'
trap { Write-Output $_.Exception.Message; exit 1 }
$ProgressPreference = 'SilentlyContinue'

$adapter = 'EasyVPN'
$tag = 'EasyVPN'
function Split-List($value) { @("$value" -split ',' | Where-Object { $_ }) }

if ($Action -eq 'Prepare') {
    # gost has only just started; the adapter, and its IP settings, follow in a moment.
    for ($i = 0; ; $i++) {
        $nic = Get-NetAdapter -Name $adapter -ErrorAction SilentlyContinue
        $config = if ($nic) { Get-CimInstance Win32_NetworkAdapterConfiguration -Filter "InterfaceIndex=$($nic.ifIndex)" }
        if ($config.IPEnabled) { break }
        if ($i -ge 80) { throw "The $adapter adapter did not appear." }
        Start-Sleep -Milliseconds 250
    }
    # 2: NetBIOS over TCP/IP off. 0 is done, 1 done after a restart.
    $result = Invoke-CimMethod -InputObject $config -MethodName SetTcpipNetbios -Arguments @{ TcpipNetbiosOptions = [uint32]2 }
    if ($result.ReturnValue -gt 1) { throw "Windows answered $($result.ReturnValue)." }

    # Windows names a network it has not seen after its adapter, numbered when the name is taken:
    # "EasyVPN 12" after eleven adapters made with new identities. The adapter keeps one identity
    # now, so the networks remembered for the others go, and its own is called EasyVPN again.
    for ($i = 0; -not ($network = Get-NetConnectionProfile -InterfaceAlias $adapter -ErrorAction SilentlyContinue); $i++) {
        if ($i -ge 40) { return }
        Start-Sleep -Milliseconds 250
    }
    $list = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList'
    foreach ($key in Get-ChildItem "$list\Profiles") {
        $name = (Get-ItemProperty $key.PSPath).ProfileName
        if ($name -notmatch "^$adapter( \d+)?$") { continue }
        if ($name -eq $network.Name) {
            if ($name -ne $adapter) {
                Set-ItemProperty $key.PSPath -Name ProfileName -Value $adapter
                Set-ItemProperty $key.PSPath -Name Description -Value $adapter
            }
            continue
        }
        Get-ChildItem "$list\Signatures\Unmanaged" -ErrorAction SilentlyContinue |
            Where-Object { (Get-ItemProperty $_.PSPath).ProfileGuid -eq $key.PSChildName } |
            Remove-Item -Recurse
        Remove-Item $key.PSPath -Recurse
    }
    return
}

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
