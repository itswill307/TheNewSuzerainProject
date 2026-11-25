# Downloads ESA WorldCover 2021 land cover for the whole world, at 160 m instead of the original
# 10 m. The terrain tool turns it into the map's ground: what kind of land each place is, and
# its coastlines, lakes and rivers.
#
# WorldCover is stored as 2,651 GeoTIFFs of 3 x 3 degrees, 115 GB at 10 m. Each file also holds
# smaller copies of itself, and this script fetches only one of those: at 160 m, about 1 GB in
# all and 10 to 20 minutes. 160 m matches the finest terrain tiles (153 m), so nothing finer
# would show. Safe to stop and run again: finished files are skipped. The files are only needed
# for baking; delete them afterwards if you like.
#
# Needs GDAL (gdal_translate). Looks in C:\Program Files\GDAL and on the PATH.
#
#   powershell -ExecutionPolicy Bypass -File download_worldcover.ps1
#   powershell -ExecutionPolicy Bypass -File download_worldcover.ps1 -Parallel 16
#
# Data: ESA WorldCover project 2021 / contains modified Copernicus Sentinel data (2021)
# processed by the ESA WorldCover consortium. Licence CC BY 4.0: the game's credits must say so.
param(
    # Metres per pixel: 160 (about 1 GB), 80 (3.5 GB), 40 (11 GB) or 20. Finer than 160 only
    # helps if the land cover tiles are baked deeper than level 9.
    [ValidateSet(20, 40, 80, 160)][int]$Resolution = 160,
    # Where the files go.
    [string]$Destination = "",
    # How many files to fetch at once.
    [int]$Parallel = 12,
    # Only fetch this many files, for a quick test. 0 fetches everything.
    [int]$Limit = 0,
    # Only fetch files whose name matches this regular expression, such as "N4[25]E0(06|09)".
    [string]$Only = ""
)

$ErrorActionPreference = "Stop"
if (-not $Destination) { $Destination = Join-Path $PSScriptRoot "..\..\..\terrain-sources\worldcover_${Resolution}m" }
$bucket = "https://esa-worldcover.s3.eu-central-1.amazonaws.com"
$prefix = "v200/2021/map/"
$overview = @{ 20 = 0; 40 = 1; 80 = 2; 160 = 3 }[$Resolution]

$gdal = Get-Command gdal_translate -ErrorAction SilentlyContinue
if ($gdal) { $gdal = $gdal.Source } else { $gdal = "C:\Program Files\GDAL\gdal_translate.exe" }
if (-not (Test-Path $gdal)) { throw "gdal_translate not found. Install GDAL, or add it to the PATH." }

New-Item -ItemType Directory -Force $Destination | Out-Null
$Destination = (Resolve-Path $Destination).Path

# The list of files, from the bucket itself (1,000 names per page).
$names = New-Object System.Collections.Generic.List[string]
$token = $null
do {
    $url = "$bucket/?list-type=2&prefix=$prefix"
    if ($token) { $url += "&continuation-token=" + [uri]::EscapeDataString($token) }
    [xml]$page = (Invoke-WebRequest -UseBasicParsing -Uri $url).Content
    foreach ($item in $page.ListBucketResult.Contents) {
        if ($item.Key -like "*.tif") { $names.Add($item.Key.Substring($prefix.Length)) }
    }
    $token = if ($page.ListBucketResult.IsTruncated -eq "true") { $page.ListBucketResult.NextContinuationToken } else { $null }
} while ($token)

$todo = @($names | Where-Object { -not (Test-Path (Join-Path $Destination $_)) })
if ($Only) { $todo = @($todo | Where-Object { $_ -match $Only }) }
if ($Limit -gt 0) { $todo = @($todo | Select-Object -First $Limit) }
Write-Host "$($names.Count) files listed, $($todo.Count) still to download at $Resolution m -> $Destination"
if ($todo.Count -eq 0) { Write-Host "Nothing to do."; return }

# GDAL settings that make reading a part of a remote file much faster.
$env:GDAL_DISABLE_READDIR_ON_OPEN = "EMPTY_DIR"
$env:GDAL_NUM_THREADS = "2"
$env:GDAL_HTTP_MULTIPLEX = "YES"
$env:GDAL_HTTP_VERSION = "2"
$env:GDAL_HTTP_MAX_RETRY = "5"
$env:GDAL_HTTP_RETRY_DELAY = "3"
$env:CPL_VSIL_CURL_USE_HEAD = "NO"

$running = @{}
$done = 0
$failed = New-Object System.Collections.Generic.List[string]
$timer = [System.Diagnostics.Stopwatch]::StartNew()
$queue = New-Object System.Collections.Generic.Queue[string]
$todo | ForEach-Object { $queue.Enqueue($_) }

function Finish($name, $process) {
    $part = Join-Path $Destination "$name.part.tif"
    if ($process.ExitCode -eq 0 -and (Test-Path -LiteralPath $part)) {
        Move-Item -LiteralPath $part -Destination (Join-Path $Destination $name) -Force
        return $true
    }
    Remove-Item -LiteralPath $part -ErrorAction SilentlyContinue
    return $false
}

try {
    while ($queue.Count -gt 0 -or $running.Count -gt 0) {
        while ($queue.Count -gt 0 -and $running.Count -lt $Parallel) {
            $name = $queue.Dequeue()
            $part = Join-Path $Destination "$name.part.tif"
            $arguments = @("-q", "-oo", "OVERVIEW_LEVEL=$overview", "-co", "COMPRESS=DEFLATE", "-co", "PREDICTOR=2",
                "-co", "TILED=YES", "/vsicurl/$bucket/$prefix$name", "`"$part`"")
            $process = Start-Process -FilePath $gdal -ArgumentList $arguments -NoNewWindow -PassThru
            # Without holding the handle, PowerShell forgets the exit code once the process ends.
            $null = $process.Handle
            $running[$name] = $process
        }

        Start-Sleep -Milliseconds 250
        foreach ($name in @($running.Keys)) {
            $process = $running[$name]
            if (-not $process.HasExited) { continue }
            $running.Remove($name)
            if (Finish $name $process) { $done++ } else { $failed.Add($name) }
            $left = $todo.Count - $done - $failed.Count
            $eta = if ($done -gt 0) { [timespan]::FromSeconds($timer.Elapsed.TotalSeconds / $done * $left).ToString("hh\:mm") } else { "?" }
            Write-Host -NoNewline "`r  $done done, $($failed.Count) failed, $left left, about $eta to go   "
        }
    }
} finally {
    foreach ($process in $running.Values) { if (-not $process.HasExited) { $process.Kill() } }
    Get-ChildItem $Destination -Filter *.part.tif | Remove-Item -ErrorAction SilentlyContinue
}

Write-Host ""
$files = Get-ChildItem $Destination -Filter *.tif
$sizeGb = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1GB), 2)
Write-Host "$done downloaded, $($failed.Count) failed. $($files.Count) files, $sizeGb GB in $Destination"
if ($failed.Count -gt 0) { Write-Host "Run the script again to retry the failed ones." }
