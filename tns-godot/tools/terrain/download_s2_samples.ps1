# Downloads small samples of the ESA WorldCover Sentinel-2 yearly composite (2021, 10 m, CC BY 4.0;
# public bucket s3://esa-worldcover-s2, no account needed), one or two typical places per kind of
# ground, for the close-up ground textures (make_ground_textures.ps1). Most kinds now use the
# bigger 88 km samples of download_s2_windows.ps1 instead; of these 44 km ones only wetland is
# still used. The desert ones are kept for the desert textures, which are switched off.
#
# Each sample is a 0.4 x 0.4 degree window (about 44 km) read at 1024 x 1024 pixels, about 40 m a
# pixel, from the file's built-in smaller copies, so only a few MB are read per place, not the
# 640 MB the full 1 degree file holds. Needs GDAL (C:\Program Files\GDAL).
#
# Credit: "Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover consortium".
param(
    [string]$Out = "$PSScriptRoot/../../../terrain-sources/sentinel2_samples",
    [int]$Pixels = 1024,
    [double]$SizeDeg = 0.4
)

# name, centre latitude, centre longitude. The window (0.4 degrees north to south) must stay inside
# one 1 degree file; centres at .5 always do.
$sites = @(
    @('crop_kansas', 38.5, -98.5), @('crop_ukraine', 49.5, 32.5), @('crop_iowa', 42.5, -93.5),
    @('grass_kazakh', 48.5, 69.5), @('grass_mongolia', 46.5, 104.5),
    @('shrub_sahel', 15.5, 9.5), @('shrub_australia', -25.5, 133.5),
    @('bare_dunes', 24.5, -2.5), @('bare_rock', 25.5, 9.5), @('bare_sand_sea', 22.5, 54.5),
    @('tree_amazon', -3.5, -61.5), @('tree_congo', 0.5, 22.5), @('tree_taiga', 61.5, 95.5), @('tree_temperate', 37.5, -82.5),
    @('moss_tundra', 69.5, -153.5), @('wetland_pantanal', -18.5, -56.5),
    # Regional versions (make_ground_textures.ps1 says which region each one is for).
    @('crop_france', 48.5, 1.5), @('crop_russia', 51.5, 40.5), @('crop_punjab', 30.5, 75.5),
    @('crop_china', 33.5, 115.5), @('crop_thailand', 15.5, 103.5), @('crop_brazil', -12.5, -55.5),
    @('crop_nigeria', 12.5, 8.5), @('crop_saudi', 28.5, 36.5), @('crop_nile', 30.75, 31.25), @('crop_harran', 36.75, 39.3), @('crop_australia', -32.5, 148.5),
    @('tree_boreal', 58.5, 50.5), @('grass_savanna', -2.5, 34.5), @('shrub_sagebrush', 42.5, -117.5),
    # Kinds of desert, which WorldCover all calls bare ground (make_ground_regions.ps1 matches each
    # bare square to the one it looks most like).
    @('bare_gravel_tanezrouft', 23.5, 0.5), @('bare_erg_oriental', 30.5, 7.5), @('bare_taklamakan', 38.5, 83.5),
    @('bare_great_sand_sea', 26.5, 25.5), @('bare_sand_sheet_selima', 21.5, 29.5), @('bare_namib', -25.25, 15.25), @('bare_salt_kavir', 34.75, 54.5),
    @('bare_gravel_gobi', 42.5, 104.5), @('bare_hamada', 29.5, -1.5)
)

$gdal = 'C:\Program Files\GDAL'
$env:PATH = "$gdal;$env:PATH"
$env:GDAL_DISABLE_READDIR_ON_OPEN = 'EMPTY_DIR'
$env:CPL_VSIL_CURL_ALLOWED_EXTENSIONS = '.tif'
$env:GDAL_HTTP_MULTIRANGE = 'YES'
New-Item -ItemType Directory -Force $Out | Out-Null

foreach ($site in $sites) {
    $name, $lat, $lon = $site
    $file = Join-Path $Out "$name.tif"
    if (Test-Path $file) { "${name}: already there"; continue }
    $la = [Math]::Floor($lat); $lo = [Math]::Floor($lon)
    $code = '{0}{1:00}{2}{3:000}' -f ($(if ($la -lt 0) { 'S' } else { 'N' })), [Math]::Abs($la), ($(if ($lo -lt 0) { 'W' } else { 'E' })), [Math]::Abs($lo)
    $folder = '{0}{1:00}' -f ($(if ($la -lt 0) { 'S' } else { 'N' })), [Math]::Abs($la)
    $url = "/vsicurl/https://esa-worldcover-s2.s3.eu-central-1.amazonaws.com/rgbnir/2021/$folder/ESA_WorldCover_10m_2021_v200_${code}_S2RGBNIR.tif"
    # The same ground distance both ways: a degree of longitude is shorter away from the equator.
    # The window stays inside its 1 degree file (centres are at .5), so far north it shrinks.
    $hLat = $SizeDeg / 2
    $hLon = $hLat / [Math]::Cos($lat * [Math]::PI / 180)
    if ($hLon -gt 0.45) { $hLat *= 0.45 / $hLon; $hLon = 0.45 }
    # Bands 1-3 are red, green, blue (4 is near infrared, not needed).
    & gdal_translate -q -projwin ($lon - $hLon) ($lat + $hLat) ($lon + $hLon) ($lat - $hLat) -outsize $Pixels $Pixels -r average -b 1 -b 2 -b 3 $url $file
    if (Test-Path $file) { "{0}: {1:0.0} MB" -f $name, ((Get-Item $file).Length / 1MB) } else { "${name}: FAILED" }
}
