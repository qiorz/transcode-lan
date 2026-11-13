$ErrorActionPreference = 'Continue'
$taskName = 'Transcode LAN Server'
$startup  = [Environment]::GetFolderPath('Startup')
$lnk      = Join-Path $startup 'Transcode LAN Server.lnk'
$removed  = $false

& schtasks.exe /delete /tn $taskName /f 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { $removed = $true }
if (Test-Path $lnk) { Remove-Item $lnk -Force; $removed = $true }

if ($removed) { Write-Host "[OK] Autostart removed." } else { Write-Host "[--] Autostart was not installed." }
