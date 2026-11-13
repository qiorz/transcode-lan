param([switch]$FirewallOnly)

$ErrorActionPreference = 'Continue'
$taskName  = 'Transcode LAN Server'
$port      = 8099
$vbs       = Join-Path $PSScriptRoot 'launch-hidden.vbs'
$root      = Split-Path $PSScriptRoot -Parent

# honour a custom port from data\config.json
$cfgPath = Join-Path $root 'data\config.json'
if (Test-Path $cfgPath) {
    try {
        $cfg = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($cfg.port) { $port = [int]$cfg.port }
    }
    catch { }
}
$admin     = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host "--------------------------------------------------"
Write-Host (" Administrator : " + $admin)
Write-Host "--------------------------------------------------"

if ($admin) {
    & netsh.exe advfirewall firewall delete rule name="Transcode LAN $port" 2>$null | Out-Null
    & netsh.exe advfirewall firewall add rule name="Transcode LAN $port" dir=in action=allow protocol=TCP localport=$port profile=private,domain 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { Write-Host "[OK]   Firewall: inbound TCP $port allowed (private/domain)." }
    else { Write-Host "[FAIL] Firewall rule could not be added." }
} else {
    Write-Host "[SKIP] Firewall: needs administrator rights."
}

if (-not $FirewallOnly) {
    if ($admin) {
        & schtasks.exe /delete /tn $taskName /f 2>$null | Out-Null
        $action = 'wscript.exe "' + $vbs + '"'
        & schtasks.exe /create /tn $taskName /sc onlogon /tr $action /f 2>$null | Out-Null
        & schtasks.exe /query /tn $taskName 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) { Write-Host "[OK]   Autostart: server will start when you log in." }
        else { Write-Host "[FAIL] Autostart task could not be created." }
    } else {
        Write-Host "[SKIP] Autostart: needs administrator rights."
    }
}

Write-Host ""
Write-Host " Open these URLs from other devices on the same network:"
$ips = @()
try { $ips = @(Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } | Select-Object -ExpandProperty IPAddress) } catch { }
if ($ips.Count -eq 0) { Write-Host ("   http://<this-pc-ip>:{0}/" -f $port) }
foreach ($ip in $ips) { Write-Host ("   http://{0}:{1}/" -f $ip, $port) }
Write-Host ""
