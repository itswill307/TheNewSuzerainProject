# Makes land cover for the far north, where ESA WorldCover has none.
#
# WorldCover is drawn from Sentinel-2 pictures, and Sentinel-2 does not look further north than
# about 82.8 N. North of that line (the north coast of Greenland and of Ellesmere Island) every
# WorldCover file says "no data". This script draws that ground from the Natural Earth outlines of
# glaciers and ice caps: snow and ice inside them, bare ground outside. The baker decides land and
# sea there from the elevation, and only uses these files where WorldCover has nothing.
#
# The files are 3 x 3 degree squares at the same resolution as the WorldCover download
# (160 m: 2250 pixels square), named like the WorldCover ones, with the same codes
# (70 snow and ice, 60 bare).
#
# Needs GDAL (gdal_rasterize) and the Natural Earth 1:10 million glaciated areas:
#   https://naciscdn.org/naturalearth/10m/physical/ne_10m_glaciated_areas.zip  (1.6 MB, public domain)
# unzipped into terrain-sources/natural_earth.
#
#   powershell -ExecutionPolicy Bypass -File make_arctic_cover.ps1
param(
    [string]$Glaciers = (Join-Path $PSScriptRoot "..\..\..\terrain-sources\natural_earth\ne_10m_glaciated_areas.shp"),
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\terrain-sources\worldcover_arctic"),
    # Pixels along each side of a 3-degree file: 2250 for the 160 m WorldCover download.
    [int]$Size = 2250,
    # Squares to make, by the longitude of their west edge, on the row from 81 N to 84 N.
    [int]$WestFrom = -96,
    [int]$WestTo = -12
)

$ErrorActionPreference = "Stop"
$rasterize = Get-Command gdal_rasterize -ErrorAction SilentlyContinue
if ($rasterize) { $rasterize = $rasterize.Source } else { $rasterize = "C:\Program Files\GDAL\gdal_rasterize.exe" }
if (-not (Test-Path $rasterize)) { throw "gdal_rasterize not found. Install GDAL, or add it to the PATH." }
if (-not (Test-Path $Glaciers)) { throw "Glacier outlines not found: $Glaciers (see the top of this script)." }
New-Item -ItemType Directory -Force $Destination | Out-Null

$made = 0
for ($west = $WestFrom; $west -le $WestTo; $west += 3) {
    $name = "{0}{1:000}" -f ($(if ($west -lt 0) { "W" } else { "E" })), [math]::Abs($west)
    $out = Join-Path $Destination "arctic_cover_N81$($name)_Map.tif"
    if (Test-Path $out) { Remove-Item -LiteralPath $out -Force }
    & $rasterize -q -burn 70 -init 60 -ot Byte -te $west 81 ($west + 3) 84 -ts $Size $Size `
        -co COMPRESS=DEFLATE -co TILED=YES $Glaciers $out
    if ($LASTEXITCODE -ne 0) { throw "gdal_rasterize failed for $name" }
    $made++
}
Write-Host "$made files in $Destination"
