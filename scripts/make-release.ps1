<#
    Builds the Transcode LAN release artifacts.

    Usage:
        powershell -ExecutionPolicy Bypass -File scripts\make-release.ps1 -Version 1.0.0

    Output (default: ..\release next to the repo root):
        transcode-lan-<version>-win-x64-full.zip   program + HandBrakeCLI + ffmpeg
        transcode-lan-<version>-win-x64-lite.zip   program only
        SHA256SUMS.txt

    The full bundle needs bin\HandBrakeCLI.exe and bin\ffmpeg.exe.
    Run scripts\get-binaries.ps1 first if they are missing.

    Note: the third-party binaries are NOT part of the source tree (see .gitignore);
    packaging them here is the redistribution step, which is why THIRD-PARTY-NOTICES.md
    and the licenses\ folder are copied into every bundle.
#>
param(
    [string]$Version = '1.0.0',
    [string]$OutDir,
    [string]$BinDir,
    [switch]$KeepStage
)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
if (-not $OutDir) { $OutDir = Join-Path (Split-Path $root -Parent) 'release' }
if (-not $BinDir) { $BinDir = Join-Path $root 'bin' }

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fw  = Split-Path $csc
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$stage = Join-Path ([IO.Path]::GetTempPath()) ('transcode-lan-release-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$fullDir = Join-Path $stage 'full'
$liteDir = Join-Path $stage 'lite'
New-Item -ItemType Directory -Force $fullDir, $liteDir, $OutDir | Out-Null

# ------------------------------------------------------------------ build

Write-Host "[1/5] building TranscodeServer.exe"
$exe = Join-Path $stage 'TranscodeServer.exe'
& $csc /nologo /target:exe /platform:anycpu /optimize+ /codepage:65001 /out:"$exe" `
    /reference:"$fw\System.Web.Extensions.dll" `
    /reference:"$fw\System.Management.dll" `
    "$root\src\server.cs"
if ($LASTEXITCODE -ne 0) { throw "compile failed ($LASTEXITCODE)" }
Write-Host ("      " + (Get-Item $exe).Length.ToString("N0") + " bytes")

# ------------------------------------------------------------------ stage

Write-Host "[2/5] assembling package contents"

function Copy-Common([string]$Dest) {
    Copy-Item $exe $Dest -Force
    foreach ($d in @('web', 'scripts', 'licenses')) {
        $src = Join-Path $root $d
        if (-not (Test-Path $src)) { throw "missing folder: $d" }
        Copy-Item $src (Join-Path $Dest $d) -Recurse -Force
    }
    # only the screenshot referenced by the README; the other docs images are repo-only
    $shot = Join-Path $root 'docs\screenshot.png'
    if (Test-Path $shot) {
        New-Item -ItemType Directory -Force (Join-Path $Dest 'docs') | Out-Null
        Copy-Item $shot (Join-Path $Dest 'docs') -Force
    }
    foreach ($f in @('README.md', 'README.en.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md')) {
        Copy-Item (Join-Path $root $f) $Dest -Force
    }
    Get-ChildItem $root -Filter *.bat -File | Copy-Item -Destination $Dest -Force
    New-Item -ItemType Directory -Force (Join-Path $Dest 'data') | Out-Null
    Copy-Item (Join-Path $root 'data\config.json') (Join-Path $Dest 'data\config.json') -Force
}

Copy-Common $fullDir
Copy-Common $liteDir

# third-party binaries: full bundle only
$hb = Join-Path $BinDir 'HandBrakeCLI.exe'
$ff = Join-Path $BinDir 'ffmpeg.exe'
foreach ($f in @($hb, $ff)) {
    if (-not (Test-Path $f)) {
        throw ("missing " + (Split-Path $f -Leaf) + " in " + $BinDir + " - run scripts\get-binaries.ps1 first")
    }
}
$binOut = Join-Path $fullDir 'bin'
New-Item -ItemType Directory -Force $binOut | Out-Null
Copy-Item $hb $binOut -Force
Copy-Item $ff $binOut -Force
# keep the explanatory readme next to them
Copy-Item (Join-Path $root 'bin\README.md') $binOut -Force

# the release bundles should carry no runtime state
Remove-Item (Join-Path $fullDir 'data\jobs.json') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $liteDir 'data\jobs.json') -Force -ErrorAction SilentlyContinue

# ------------------------------------------------------------------ zip

Write-Host "[3/5] compressing"
$fullZip = Join-Path $OutDir ("transcode-lan-" + $Version + "-win-x64-full.zip")
$liteZip = Join-Path $OutDir ("transcode-lan-" + $Version + "-win-x64-lite.zip")

function New-Zip([string]$Dir, [string]$ZipPath) {
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    $base = (Resolve-Path $Dir).Path.TrimEnd('\')
    $zip = [System.IO.Compression.ZipFile]::Open($ZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem $Dir -Recurse -File | ForEach-Object {
            # zip entries must use forward slashes, even when built on Windows
            $rel = $_.FullName.Substring($base.Length + 1).Replace('\', '/')
            $entry = $zip.CreateEntry($rel, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $_.LastWriteTime
            $out = $entry.Open()
            $in = [System.IO.File]::OpenRead($_.FullName)
            try { $in.CopyTo($out) } finally { $in.Dispose(); $out.Dispose() }
        }
    }
    finally {
        $zip.Dispose()
    }
}

New-Zip $fullDir $fullZip
New-Zip $liteDir $liteZip

# ------------------------------------------------------------------ checksums

Write-Host "[4/5] writing checksums"
$sums = foreach ($z in @($fullZip, $liteZip)) {
    $h = (Get-FileHash $z -Algorithm SHA256).Hash.ToLower()
    $n = Split-Path $z -Leaf
    "$h  $n"
}
$sumsPath = Join-Path $OutDir 'SHA256SUMS.txt'
Set-Content -Path $sumsPath -Value $sums -Encoding ASCII
Get-Content $sumsPath | ForEach-Object { "      $_" }

# ------------------------------------------------------------------ report

Write-Host "[5/5] done"
Write-Host ""
Write-Host "Artifacts in $OutDir :"
Get-ChildItem $OutDir | ForEach-Object {
    Write-Host ("  {0,-45} {1,10:N1} MB" -f $_.Name, ($_.Length / 1MB))
}
Write-Host ""
Write-Host "Next: create a GitHub release for tag v$Version and upload the two zips plus SHA256SUMS.txt."

if (-not $KeepStage) {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
else {
    Write-Host "Staging folder kept at $stage"
}
