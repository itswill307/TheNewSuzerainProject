# Makes the close-up ground textures in data/base/ground from the Sentinel-2 samples:
#   <kind>.png                     the standard texture for each kind, used everywhere
#   regional/<kind>_<name>.png     regional versions, used where regions.png names that region or
#                                  zone (make_ground_regions.ps1); elsewhere the standard one
#   sizes.txt                      how much ground each kind's texture covers, km
#
# Two sets of samples: the large ones (88 km, download_s2_windows.ps1, windows chosen by
# terrain samplepick as the plainest ground of their kind) for every kind that has one, and the
# small ones (44 km, download_s2_samples.ps1) for the rest: wetland, which no 88 km window
# anywhere is mostly made of. Deserts get no texture (bare=none in -Pick); their 44 km samples are
# only used if the desert textures are switched back on.
#
# 1. Each sample (reflectance x 10000, 16 bit) becomes an 8-bit PNG holding the square root of
#    reflectance, which keeps the dark ground from losing its steps.
# 2. terrain groundtex keeps only the fine light and dark (finer than about 500 m) as a
#    multiplier around 1, with standout spots eased back (GroundTextures.cs).
#
# -Pick chooses the standard sample for a kind (by default each kind takes its first sample);
# -Regional says which sample each regional version is made from.
#
# Needs GDAL (C:\Program Files\GDAL) and a built terrain tool.
param(
    [string]$Samples = "$PSScriptRoot/../../../terrain-sources/sentinel2_samples",
    [string]$LargeSamples = "$PSScriptRoot/../../../terrain-sources/sentinel2_samples_88km",
    [double]$SmallKm = 44,
    [double]$LargeKm = 88,
    [string]$Out = "$PSScriptRoot/../../data/base/ground",
    # Bare ground (deserts, rock) has no texture: its slope shading from the heights is enough
    # (decision 16). For the desert textures, set bare=bare_rock: each kind of desert then gets its
    # own (make_ground_regions.ps1).
    [string]$Pick = "tree=tree_congo,crop=crop_iowa,grass=grass_kazakh,shrub=shrub_australia,wetland=wetland_pantanal,bare=none,moss=none",
    [string]$Regional = ("crop_europe=crop_beauce,crop_north_eurasia=crop_russia,crop_south_asia=crop_haryana," +
        "crop_east_asia=crop_china,crop_southeast_asia=crop_thailand,crop_latin_america=crop_parana," +
        "crop_africa=crop_nigeria,crop_middle_east=crop_nineveh,crop_australia=crop_australia," +
        "tree_boreal=tree_boreal,tree_north_america=tree_pennsylvania,grass_africa=grass_savanna," +
        "grass_latin_america=grass_savanna,shrub_north_america=shrub_oregon"),
    [string]$Tool = "$PSScriptRoot/TerrainTool/bin/Release/net8.0/terrain.exe"
)

$env:PATH = "C:\Program Files\GDAL;$env:PATH"
$kinds = 'tree', 'shrub', 'grass', 'crop', 'built', 'bare', 'snow', 'water', 'wetland', 'mangrove', 'moss'
$picked = @{}
foreach ($pair in $Pick.Split(',', [StringSplitOptions]::RemoveEmptyEntries)) { $k, $v = $pair.Split('='); $picked[$k] = $v }

# The samples of a set as PNGs, and as textures (every sample, named after it) beside them.
function Convert-Set([string]$folder) {
    $png = Join-Path $folder "png"
    New-Item -ItemType Directory -Force $png | Out-Null
    foreach ($tif in Get-ChildItem $folder -Filter *.tif) {
        $target = Join-Path $png ($tif.BaseName + ".png")
        if (Test-Path $target) { continue }
        & gdal_translate -q -of PNG -ot Byte -scale 0 10000 0 255 -exponent 0.5 $tif.FullName $target
        Write-Host "  $($tif.BaseName).png"
    }
    $png
}

$sets = @()
if (Test-Path $LargeSamples) { $sets += [pscustomobject]@{ Folder = $LargeSamples; Km = $LargeKm } }
$sets += [pscustomobject]@{ Folder = $Samples; Km = $SmallKm }

# Each kind's standard texture from the first set (large first) that has its sample.
$sizes = @()
$textureOf = @{}
foreach ($set in $sets) {
    $png = Convert-Set $set.Folder
    $all = Join-Path $set.Folder "textures"
    $have = Get-ChildItem $png -Filter *.png | ForEach-Object { $_.BaseName }
    # This set makes the kinds whose chosen sample it has and that no earlier set made.
    $pickHere = @()
    foreach ($k in $kinds) {
        $sample = if ($picked.ContainsKey($k)) { $picked[$k] } else { $have | Where-Object { $_ -like "${k}_*" } | Select-Object -First 1 }
        if ($sample -and $sample -ne 'none' -and $have -contains $sample -and -not ($sizes | Where-Object { $_ -like "$k *" })) {
            $pickHere += "$k=$sample"; $sizes += "$k $($set.Km)"
        } else { $pickHere += "$k=none" }
    }
    $pixelM = $set.Km * 1000 / 1024
    & $Tool groundtex $png $Out --pixel-m $pixelM --pick ($pickHere -join ',') --all $all --blur-for 'bare_sand_sea=2000'
    foreach ($t in Get-ChildItem $all -Filter *.png) { if (-not $textureOf.ContainsKey($t.BaseName)) { $textureOf[$t.BaseName] = [pscustomobject]@{ Path = $t.FullName; Km = $set.Km } } }
}
Set-Content -Path (Join-Path $Out "sizes.txt") -Value (@('# Ground covered by each kind''s texture and its regional versions, km (make_ground_textures.ps1).') + $sizes) -Encoding ascii
"  sizes: $($sizes -join ', ')"

# Regional versions: copies of the chosen sample textures under their region's name. A regional
# version must cover as much ground as its kind's standard texture.
$regionalOut = Join-Path $Out "regional"
New-Item -ItemType Directory -Force $regionalOut | Out-Null
Get-ChildItem $regionalOut -Filter *.png | Remove-Item
foreach ($pair in $Regional.Split(',', [StringSplitOptions]::RemoveEmptyEntries)) {
    $name, $sample = $pair.Split('=')
    $kind = $name.Split('_')[0]
    $km = ($sizes | Where-Object { $_ -like "$kind *" }) -replace '^\w+ ', ''
    $source = $textureOf[$sample]
    if (-not $source) { "  ${name}: no sample $sample, left out"; continue }
    if ($km -and [double]$km -ne $source.Km) { "  ${name}: $sample covers $($source.Km) km, not $km like the standard $kind texture; left out"; continue }
    Copy-Item $source.Path (Join-Path $regionalOut "$name.png")
    "  regional/$name.png from $sample ($($source.Km) km)"
}

# Kinds of desert: each desert_<x> zone in regions.txt (make_ground_regions.ps1, run it first)
# gets regional/bare_desert_<x>.png from the bare_<x> sample.
if ($picked['bare'] -ne 'none') {
    $desertZones = Get-Content (Join-Path $Out "regions.txt") | Where-Object { $_ -match '^zone \d+ desert_(\w+)$' } | ForEach-Object { $Matches[1] }
    foreach ($z in $desertZones) {
        $source = $textureOf["bare_$z"]
        if (-not $source) { continue }
        Copy-Item $source.Path (Join-Path $regionalOut "bare_desert_$z.png")
        "  regional/bare_desert_$z.png from bare_$z"
    }
}
