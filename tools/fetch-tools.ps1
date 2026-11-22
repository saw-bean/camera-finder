# Downloads the helper programs the installer ships in the "tools" folder and checks each download against a
# pinned SHA-256, so a changed or tampered file fails the build instead of being shipped.
#   ffprobe.exe  (FFmpeg, GPL v3, static build from gyan.dev) - video stream checks in the health check
#   iperf3.exe + cygwin1.dll (iperf3 BSD-3, Cygwin LGPL v3, build by ar51an) - the speed test server
# Usage: pwsh tools/fetch-tools.ps1 -Destination publish/tools
# To update: change the URL and put the new file's SHA-256 (GitHub shows it on the release page) below.
param([string]$Destination = "publish/tools")

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$downloads = @(
    @{
        Name   = 'ffmpeg'
        Url    = 'https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip'
        Sha256 = '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba'
        Files  = @{ 'ffmpeg-9.0.2-essentials_build/bin/ffprobe.exe' = 'ffprobe.exe'; 'ffmpeg-9.0.2-essentials_build/LICENSE' = 'licenses/FFmpeg-LICENSE.txt' }
        Source = 'FFmpeg 9.0.2 source: https://github.com/FFmpeg/FFmpeg/commit/946fcce07b (build: https://www.gyan.dev/ffmpeg/builds/)'
    },
    @{
        Name   = 'iperf3'
        Url    = 'https://github.com/ar51an/iperf3-win-builds/releases/download/3.22/iperf-3.22-win64.zip'
        Sha256 = 'c9feabb3d721039508ccb81f74c2ada3ae11b9e75d33d24510573c2aa21da420'
        Files  = @{ 'iperf3.exe' = 'iperf3.exe'; 'cygwin1.dll' = 'cygwin1.dll' }
        Source = 'iperf3 3.22 source: https://github.com/esnet/iperf/releases/tag/3.22 ; Cygwin source: https://cygwin.com/ (build: https://github.com/ar51an/iperf3-win-builds)'
    }
)

New-Item -ItemType Directory -Force -Path (Join-Path $Destination 'licenses') | Out-Null
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("cf-tools-" + [guid]::NewGuid())
New-Item -ItemType Directory -Force -Path $work | Out-Null
try {
    $sources = @()
    foreach ($d in $downloads) {
        $zip = Join-Path $work "$($d.Name).zip"
        Write-Host "Downloading $($d.Url)"
        Invoke-WebRequest -Uri $d.Url -OutFile $zip -UseBasicParsing
        $hash = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
        if ($hash -ne $d.Sha256) { throw "$($d.Name): SHA-256 mismatch. Expected $($d.Sha256), got $hash" }

        $out = Join-Path $work $d.Name
        Expand-Archive -Path $zip -DestinationPath $out
        foreach ($entry in $d.Files.GetEnumerator()) {
            $from = Join-Path $out $entry.Key
            $to = Join-Path $Destination $entry.Value
            if (-not (Test-Path $from)) { throw "$($d.Name): $($entry.Key) not found in the archive" }
            Copy-Item $from $to -Force
            Write-Host "  -> $to"
        }
        $sources += $d.Source
    }
    $sources | Set-Content -Encoding UTF8 (Join-Path $Destination 'licenses/SOURCES.txt')
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
