# Downloads the ETOPO 2022 15 arc-second global elevation tiles from NOAA
# (288 GeoTIFF files, about 7 GB). Safe to stop and run again: finished files
# are skipped and a partly downloaded file is resumed.
#
#   powershell -ExecutionPolicy Bypass -File download_etopo2022.ps1
#   powershell -ExecutionPolicy Bypass -File download_etopo2022.ps1 -ListOnly
param(
    # Where the tiles go. The default is terrain-sources/etopo2022_15s beside the game projects.
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\terrain-sources\etopo2022_15s"),
    # Print what would be downloaded without downloading anything.
    [switch]$ListOnly
)

$ErrorActionPreference = "Stop"
$base = "https://www.ngdc.noaa.gov/mgg/global/relief/ETOPO2022/data/15s/15s_surface_elev_gtif/"

Write-Host "Reading the tile list from NOAA..."
$listing = (Invoke-WebRequest -Uri $base -UseBasicParsing).Content
$names = [regex]::Matches($listing, 'href="(ETOPO_2022_v1_15s_[NS]\d+[EW]\d+_surface\.tif)"') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
if ($names.Count -ne 288) {
    Write-Warning "Expected 288 tiles but NOAA lists $($names.Count)."
}

New-Item -ItemType Directory -Force $Destination | Out-Null
$Destination = (Resolve-Path $Destination).Path
Write-Host "$($names.Count) tiles -> $Destination"
if ($ListOnly) { $names; return }

$done = 0
foreach ($name in $names) {
    $done++
    $target = Join-Path $Destination $name
    $marker = "$target.done"
    if (Test-Path $marker) { continue }

    Write-Host "[$done/$($names.Count)] $name"
    # -C - resumes a partial file; --fail makes HTTP errors count as failures.
    & curl.exe --fail --location --silent --show-error --retry 5 --retry-delay 3 -C - -o $target ($base + $name)
    if ($LASTEXITCODE -eq 0) {
        New-Item -ItemType File -Force $marker | Out-Null
    } else {
        Write-Warning "Failed: $name (curl exit code $LASTEXITCODE). Run the script again to retry."
    }
}

$finished = (Get-ChildItem $Destination -Filter *.done).Count
$sizeGb = [math]::Round(((Get-ChildItem $Destination -Filter *.tif | Measure-Object Length -Sum).Sum / 1GB), 2)
Write-Host ""
Write-Host "$finished of $($names.Count) tiles downloaded, $sizeGb GB in $Destination"
if ($finished -lt $names.Count) { Write-Host "Some tiles are missing. Run the script again." }
