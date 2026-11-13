param(
    [string]$HandBrakeVersion = '1.11.2',
    [switch]$SkipFfmpeg,
    [switch]$Force
)

# Downloads the two third-party programs this project drives:
#   bin\HandBrakeCLI.exe  - the transcode engine (HandBrake, GPL-2.0)
#   bin\ffmpeg.exe        - only needed to burn ASS/SSA subtitles with their styles (FFmpeg)
# They are intentionally not shipped in this repository.

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$bin  = Join-Path $root 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('transcode-lan-bin-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $tmp | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-RemoteFile([string]$Url, [string]$OutFile) {
    Write-Host ("[..] downloading " + $Url)
    $old = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing -TimeoutSec 3600
    }
    finally {
        $ProgressPreference = $old
    }
    $mb = (Get-Item $OutFile).Length / 1MB
    Write-Host ("     " + $mb.ToString('N1') + " MB")
}

function Expand-SingleFile([string]$ZipPath, [string]$NamePattern, [string]$Destination) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $entry = $archive.Entries |
            Where-Object { $_.FullName -match $NamePattern } |
            Select-Object -First 1
        if (-not $entry) {
            throw ("no entry matching '" + $NamePattern + "' inside " + (Split-Path $ZipPath -Leaf))
        }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $Destination, $true)
    }
    finally {
        $archive.Dispose()
    }
}

$hbOut = Join-Path $bin 'HandBrakeCLI.exe'
if ((Test-Path $hbOut) -and -not $Force) {
    Write-Host "[--] bin\HandBrakeCLI.exe already exists (use -Force to replace)."
}
else {
    $hbUrl = "https://github.com/HandBrake/HandBrake/releases/download/$HandBrakeVersion/HandBrakeCLI-$HandBrakeVersion-win-x86_64.zip"
    $hbZip = Join-Path $tmp 'handbrake.zip'
    try {
        Get-RemoteFile $hbUrl $hbZip
        Expand-SingleFile $hbZip '(^|/)HandBrakeCLI\.exe$' $hbOut
        Write-Host "[OK] bin\HandBrakeCLI.exe"
    }
    catch {
        Write-Warning ("HandBrakeCLI download failed: " + $_.Exception.Message)
        Write-Warning ("Download HandBrakeCLI-" + $HandBrakeVersion + "-win-x86_64.zip from https://github.com/HandBrake/HandBrake/releases and copy HandBrakeCLI.exe into bin\")
    }
}

$ffOut = Join-Path $bin 'ffmpeg.exe'
if ($SkipFfmpeg) {
    Write-Host "[--] skipping ffmpeg (ASS style-preserving burn-in will fall back to plain SRT)."
}
elseif ((Test-Path $ffOut) -and -not $Force) {
    Write-Host "[--] bin\ffmpeg.exe already exists (use -Force to replace)."
}
else {
    $ffUrl = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'
    $ffZip = Join-Path $tmp 'ffmpeg.zip'
    try {
        Get-RemoteFile $ffUrl $ffZip
        Expand-SingleFile $ffZip '/bin/ffmpeg\.exe$' $ffOut
        Write-Host "[OK] bin\ffmpeg.exe"
    }
    catch {
        Write-Warning ("ffmpeg download failed: " + $_.Exception.Message)
        Write-Warning "Download a Windows build from https://ffmpeg.org/download.html and copy ffmpeg.exe into bin\"
    }
}

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Versions:"
if (Test-Path $hbOut) {
    $v = (& $hbOut --version 2>&1 | Out-String)
    Write-Host ("  HandBrakeCLI : " + (($v -split "`n") | Select-Object -First 1).Trim())
}
else {
    Write-Host "  HandBrakeCLI : MISSING"
}
if (Test-Path $ffOut) {
    $v = (& $ffOut -version 2>&1 | Out-String)
    Write-Host ("  ffmpeg       : " + (($v -split "`n") | Select-Object -First 1).Trim())
}

if (-not (Test-Path $hbOut)) { exit 1 }
