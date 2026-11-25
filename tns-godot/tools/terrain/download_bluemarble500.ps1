# Downloads NASA Blue Marble Next Generation at 500 m (July 2004, eight JPEG tiles of 21600 x 21600,
# 351 MB) and splits each into three positioned single-colour GeoTIFFs the terrain tool reads
# (NAME_r.tif, NAME_g.tif, NAME_b.tif). The surface recipe's "detailImages" points at the folder;
# it sharpens the detail tiles where the land would otherwise look smooth up close.
#
# Needs GDAL (gdal_translate). Safe to stop and run again: finished files are skipped. Only needed
# for baking; delete the folder afterwards if you like.
#
#   powershell -ExecutionPolicy Bypass -File download_bluemarble500.ps1
#
# Credit: NASA Earth Observatory, Blue Marble Next Generation (Reto Stockli). Public domain.
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\terrain-sources\bluemarble_500m"),
    # Month of 2004: january ... december.
    [string]$Month = "july"
)

$ErrorActionPreference = "Stop"
$number = @{ january = "01"; february = "02"; march = "03"; april = "04"; may = "05"; june = "06"; july = "07";
             august = "08"; september = "09"; october = "10"; november = "11"; december = "12" }[$Month]
if (-not $number) { throw "Unknown month: $Month" }
$base = "https://assets.science.nasa.gov/content/dam/science/esd/eo/images/bmng/bmng-base/$Month"

$gdal = Get-Command gdal_translate -ErrorAction SilentlyContinue
if ($gdal) { $gdal = $gdal.Source } else { $gdal = "C:\Program Files\GDAL\gdal_translate.exe" }
if (-not (Test-Path $gdal)) { throw "gdal_translate not found. Install GDAL, or add it to the PATH." }

New-Item -ItemType Directory -Force $Destination | Out-Null
$Destination = (Resolve-Path $Destination).Path

# Tile name and its [west, north, east, south] in degrees.
$tiles = [ordered]@{
    A1 = @(-180, 90, -90, 0); B1 = @(-90, 90, 0, 0); C1 = @(0, 90, 90, 0); D1 = @(90, 90, 180, 0);
    A2 = @(-180, 0, -90, -90); B2 = @(-90, 0, 0, -90); C2 = @(0, 0, 90, -90); D2 = @(90, 0, 180, -90)
}

foreach ($name in $tiles.Keys) {
    $file = "world.2004$number.3x21600x21600.$name.jpg"
    $jpeg = Join-Path $Destination $file
    if (-not (Test-Path (Join-Path $Destination "$name`_b.tif"))) {
        if (-not (Test-Path $jpeg)) {
            Write-Host "Downloading $file"
            & curl.exe --fail --location --silent --show-error --retry 4 -o "$jpeg.part" "$base/$file"
            if ($LASTEXITCODE -ne 0) { throw "Download of $file failed." }
            Move-Item -Force "$jpeg.part" $jpeg
        }

        $box = $tiles[$name]
        $band = 1
        foreach ($colour in "r", "g", "b") {
            Write-Host "  $name, colour $colour"
            & $gdal -q -b $band -a_srs EPSG:4326 -a_ullr $box[0] $box[1] $box[2] $box[3] -co TILED=YES -co COMPRESS=DEFLATE `
                $jpeg (Join-Path $Destination "$name`_$colour.part.tif")
            if ($LASTEXITCODE -ne 0) { throw "Converting $file failed." }
            Move-Item -Force (Join-Path $Destination "$name`_$colour.part.tif") (Join-Path $Destination "$name`_$colour.tif")
            $band++
        }
    }
}

$size = [math]::Round(((Get-ChildItem $Destination -Filter *.tif | Measure-Object Length -Sum).Sum / 1GB), 2)
Write-Host "Done: $($tiles.Count) tiles in $Destination ($size GB of GeoTIFFs; the .jpg files can be deleted)."
