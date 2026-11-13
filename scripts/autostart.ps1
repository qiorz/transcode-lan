param([switch]$Remove)

$ErrorActionPreference = 'Continue'
$taskName = 'Transcode LAN Server'
$vbs      = Join-Path $PSScriptRoot 'launch-hidden.vbs'
$root     = Split-Path $PSScriptRoot -Parent
$startup  = [Environment]::GetFolderPath('Startup')
$lnk      = Join-Path $startup 'Transcode LAN Server.lnk'

if (-not (Test-Path $vbs)) { Write-Host "[FAIL] launch-hidden.vbs not found"; exit 1 }

function Remove-Autostart {
    $removed = $false
    & schtasks.exe /delete /tn $taskName /f 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { $removed = $true }
    if (Test-Path $lnk) { Remove-Item $lnk -Force; $removed = $true }
    return $removed
}

if ($Remove) {
    if (Remove-Autostart) { Write-Host "[OK] Autostart removed." }
    else { Write-Host "[--] Autostart was not installed." }
    exit 0
}

# ---- preferred: scheduled task (needs admin) ----
& schtasks.exe /create /tn $taskName /sc onlogon /tr ('wscript.exe "' + $vbs + '"') /f 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "[OK] Autostart installed as a logon scheduled task."
    exit 0
}

# ---- fallback: Startup folder shortcut (no admin needed) ----
try {
    $ws = New-Object -ComObject WScript.Shell
    $sc = $ws.CreateShortcut($lnk)
    $sc.TargetPath       = 'wscript.exe'
    $sc.Arguments        = '"' + $vbs + '"'
    $sc.WorkingDirectory = $root
    $sc.WindowStyle      = 7
    $sc.Description      = 'Transcode LAN server (http://localhost:8099)'
    $sc.Save()
    if (Test-Path $lnk) {
        Write-Host "[OK] Autostart installed in the Startup folder:"
        Write-Host ("     " + $lnk)
        Write-Host "     (no administrator rights required)"
        exit 0
    }
} catch {
    Write-Host ("[FAIL] " + $_.Exception.Message)
}
Write-Host "[FAIL] Could not install autostart."
exit 1
