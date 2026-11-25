# Downloads the large ground texture samples: for each window in sample_windows.csv (chosen by
#   terrain samplepick <worldcover_160m> sample_sites.csv sample_windows.csv
# as the plainest ground of its kind near the place in sample_sites.csv), a square of the ESA
# WorldCover Sentinel-2 yearly picture of 2021 (CC BY 4.0, public bucket, no account), SizeKm
# across, read at Pixels x Pixels from the files' built-in smaller copies, so only a few MB are
# read per place. Twice the size of download_s2_samples.ps1's windows: a quarter as many repeats
# at the closest zoom.
#
# A window this size crosses several of the picture's 1 x 1 degree files, so they are joined
# first (gdalbuildvrt). Needs GDAL (C:\Program Files\GDAL).
#
# Credit: "Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover consortium".
param(
    [string]$Windows = "$PSScriptRoot/sample_windows.csv",
    [string]$Out = "$PSScriptRoot/../../../terrain-sources/sentinel2_samples_88km",
    [int]$Pixels = 1024,
    [double]$SizeKm = 88
)

$gdal = 'C:\Program Files\GDAL'
$env:PATH = "$gdal;$env:PATH"
$env:GDAL_DISABLE_READDIR_ON_OPEN = 'EMPTY_DIR'
$env:CPL_VSIL_CURL_ALLOWED_EXTENSIONS = '.tif'
$env:GDAL_HTTP_MULTIRANGE = 'YES'
New-Item -ItemType Directory -Force $Out | Out-Null

function Get-Url([int]$la, [int]$lo) {
    $code = '{0}{1:00}{2}{3:000}' -f ($(if ($la -lt 0) { 'S' } else { 'N' })), [Math]::Abs($la), ($(if ($lo -lt 0) { 'W' } else { 'E' })), [Math]::Abs($lo)
    $folder = '{0}{1:00}' -f ($(if ($la -lt 0) { 'S' } else { 'N' })), [Math]::Abs($la)
    "/vsicurl/https://esa-worldcover-s2.s3.eu-central-1.amazonaws.com/rgbnir/2021/$folder/ESA_WorldCover_10m_2021_v200_${code}_S2RGBNIR.tif"
}

foreach ($w in Import-Csv $Windows) {
    $name = $w.name
    $file = Join-Path $Out "$name.tif"
    if (Test-Path $file) { "${name}: already there"; continue }
    $lat = [double]$w.lat; $lon = [double]$w.lon
    # The same ground distance both ways: a degree of longitude is shorter away from the equator.
    $hLat = $SizeKm / 2 / 111.32
    $hLon = $hLat / [Math]::Cos($lat * [Math]::PI / 180)
    # Each file's part is read on its own at about the final pixel size (a joined virtual file
    # would not use the files' smaller copies and would read them at 10 m), then the parts are
    # joined here. Bands 1-3 are red, green, blue (4 is near infrared, not needed).
    $west = $lon - $hLon; $east = $lon + $hLon; $south = $lat - $hLat; $north = $lat + $hLat
    $resLat = 2 * $hLat / $Pixels; $resLon = 2 * $hLon / $Pixels
    $parts = @()
    for ($la = [Math]::Floor($south); $la -le [Math]::Floor($north); $la++) {
        for ($lo = [Math]::Floor($west); $lo -le [Math]::Floor($east); $lo++) {
            $part = Join-Path $Out "$name.part$($parts.Count).tif"
            $w0 = [Math]::Max($west, $lo); $e0 = [Math]::Min($east, $lo + 1); $s0 = [Math]::Max($south, $la); $n0 = [Math]::Min($north, $la + 1)
            & gdal_translate -q -projwin $w0 $n0 $e0 $s0 -tr $resLon $resLat -r average -b 1 -b 2 -b 3 (Get-Url $la $lo) $part
            if (Test-Path $part) { $parts += $part }
        }
    }
    & gdalwarp -q -te $west $south $east $north -ts $Pixels $Pixels -r bilinear @parts $file
    $parts | Remove-Item -ErrorAction SilentlyContinue
    if (Test-Path $file) { "{0}: {1} parts joined, {2:0.0} MB" -f $name, $parts.Count, ((Get-Item $file).Length / 1MB) } else { "${name}: FAILED" }
}
