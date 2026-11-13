param(
    [ValidateSet('start', 'stop', 'status')]
    [string]$Action = 'status',
    [switch]$NoBrowser
)

$ErrorActionPreference = 'SilentlyContinue'
$root = Split-Path $PSScriptRoot -Parent
$exe  = Join-Path $root 'TranscodeServer.exe'
$port = 8099

try {
    $cfgPath = Join-Path $root 'data\config.json'
    if (Test-Path $cfgPath) {
        $cfg = Get-Content $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($cfg.port) { $port = [int]$cfg.port }
    }
} catch { }

function Get-ServerProcess {
    Get-Process TranscodeServer -ErrorAction SilentlyContinue
}

function Test-Api {
    try {
        $r = Invoke-WebRequest -Uri ("http://127.0.0.1:{0}/api/status" -f $port) -UseBasicParsing -TimeoutSec 2
        if ($r.StatusCode -eq 200) { return ($r.Content | ConvertFrom-Json) }
    } catch { }
    return $null
}

function Show-Url($status) {
    $lan = ''
    if ($status -and $status.addresses) {
        $lan = ($status.addresses -split ',')[0].Trim()
    }
    Write-Host ("     local : http://127.0.0.1:{0}/" -f $port)
    if ($lan) { Write-Host ("     LAN   : http://{0}:{1}/" -f $lan, $port) }
}

switch ($Action) {

    'start' {
        $p = Get-ServerProcess
        if ($p) {
            Write-Host ("[i] Server already running (pid {0})." -f $p.Id)
        }
        else {
            if (-not (Test-Path $exe)) {
                Write-Host "[FAIL] TranscodeServer.exe not found next to this folder."
                exit 1
            }
            Write-Host "Starting transcode server ..."
            Start-Process -FilePath $exe -WorkingDirectory $root -WindowStyle Hidden
            $status = $null
            for ($i = 0; $i -lt 40; $i++) {
                Start-Sleep -Milliseconds 250
                $status = Test-Api
                if ($status) { break }
            }
            if (-not $status) {
                Write-Host ("[FAIL] Server did not answer on port {0}. Run 启动服务.bat to see the error." -f $port)
                exit 1
            }
            Write-Host "[OK] Server started."
        }
        $status = Test-Api
        Show-Url $status
        if (-not $NoBrowser) { Start-Process ("http://127.0.0.1:{0}/" -f $port) }
        exit 0
    }

    'stop' {
        $p = Get-ServerProcess
        if (-not $p) {
            Write-Host "[i] Server is not running."
            exit 0
        }

        $running = 0
        try {
            $jobs = Invoke-WebRequest -Uri ("http://127.0.0.1:{0}/api/jobs" -f $port) -UseBasicParsing -TimeoutSec 2
            $list = $jobs.Content | ConvertFrom-Json
            $running = @($list | Where-Object { $_.status -eq 'running' }).Count
        } catch { }

        if ($running -gt 0) {
            Write-Host ("[!] {0} job(s) are still encoding. Stopping will interrupt them." -f $running)
            Write-Host "    Continuing in 3 seconds (Ctrl+C to cancel) ..."
            Start-Sleep -Seconds 3
        }

        # stop encoder children first so they do not linger
        $ids = @($p.Id)
        try {
            Get-CimInstance Win32_Process | Where-Object {
                $ids -contains $_.ParentProcessId -and $_.Name -match '^(HandBrakeCLI|ffmpeg)'
            } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
        } catch { }

        $p | Stop-Process -Force
        Start-Sleep -Milliseconds 800

        if (Get-ServerProcess) {
            Write-Host "[FAIL] Server is still running."
            exit 1
        }
        Write-Host "[OK] Server stopped."
        exit 0
    }

    'status' {
        $status = Test-Api
        if ($status) {
            Write-Host "[OK] Server is running."
            Show-Url $status
            try {
                $list = (Invoke-WebRequest -Uri ("http://127.0.0.1:{0}/api/jobs" -f $port) -UseBasicParsing -TimeoutSec 2).Content | ConvertFrom-Json
                $r = @($list | Where-Object { $_.status -eq 'running' }).Count
                $q = @($list | Where-Object { $_.status -eq 'queued' }).Count
                Write-Host ("     jobs  : {0} running, {1} queued" -f $r, $q)
            } catch { }
            Write-Host ("     load  : CPU {0}%  GPU {1}%" -f $status.cpuLoad, $status.gpuLoad)
        }
        else {
            Write-Host "[i] Server is not running."
            exit 1
        }
        exit 0
    }
}
