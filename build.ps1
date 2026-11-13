param([switch]$SkipHandBrake)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc   = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fw    = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$asm   = Join-Path $fw 'System.Web.Extensions.dll'
$mgmt  = Join-Path $fw 'System.Management.dll'

if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

New-Item -ItemType Directory -Force (Join-Path $root 'bin') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $root 'web') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $root 'data') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $root 'work') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $root 'output') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $root 'inbox') | Out-Null

& $csc /nologo /target:exe /platform:anycpu /optimize+ /codepage:65001 `
    /out:"$root\TranscodeServer.exe" `
    /reference:"$asm" `
    /reference:"$mgmt" `
    "$root\src\server.cs"

if ($LASTEXITCODE -ne 0) { throw "compile failed ($LASTEXITCODE)" }

if (-not $SkipHandBrake) {
    $dst = Join-Path $root 'bin\HandBrakeCLI.exe'
    if (-not (Test-Path $dst)) {
        $alt = Join-Path $root 'HandBrakeCLI.exe'
        if (Test-Path $alt) {
            Copy-Item $alt $dst
            Write-Host "copied HandBrakeCLI.exe from the project root"
        } else {
            Write-Warning "HandBrakeCLI.exe not found in bin\"
            Write-Host "         run:  powershell -ExecutionPolicy Bypass -File scripts\get-binaries.ps1"
            Write-Host "         (or copy the official HandBrakeCLI.exe there manually)"
        }
    }
}

$exe = Join-Path $root 'TranscodeServer.exe'
Write-Host ("build ok -> {0}  ({1:N0} KB)" -f $exe, ((Get-Item $exe).Length / 1KB))
