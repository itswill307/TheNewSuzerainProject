# Writes the map that chooses regional ground textures: data/base/ground/regions.png, 360 x 180
# pixels, one per 1 x 1 degree square (north at the top, 180 W on the left), and regions.txt
# naming the numbers in it.
#   red:   the region (a continent, roughly), 0 for none
#   green: a zone that cuts across regions, 0 for none:
#          boreal:      forest north of 52 N in North America, Europe and northern Eurasia
#          desert_<x>:  the kind of desert a square of bare ground looks most like (below)
# A kind of ground with a texture regional/<kind>_<zone>.png uses it in that zone; otherwise
# regional/<kind>_<region>.png in that region; otherwise the standard <kind>.png.
#
# Kinds of desert. WorldCover has one class, bare ground, for sand seas, rock plateaus, gravel
# plains and salt flats, so one texture for all of them puts rock blotches on sand and one dune
# pattern everywhere. Instead each bare square (between 60 S and 60 N) takes the desert sample
# (download_s2_samples.ps1, the bare_* places) it looks most like in the bland survey: how much
# fine texture the 500 m picture has there (sharp_contrast, on a log scale), its brightness, its
# redness, and how much its slope shading varies (shade, log scale). Each measure is divided by its
# spread over all bare squares, and the nearest sample, measured in the survey square it was
# taken from, wins. ETOPO's fine relief was tried first to find sand seas and could not tell them
# from rock (the Tassili rock plateau scores 9.5 m, Erg Chech's dunes 3.8 m).
param(
    [string]$Out = "$PSScriptRoot/../../data/base/ground",
    [string]$Survey = "$PSScriptRoot/../../docs/bland_survey/bland_survey_squares.csv",
    [string]$Sites = "$PSScriptRoot/download_s2_samples.ps1",
    [double]$MaxDesertLat = 60,
    # Samples left out: the hamada window is half dune sea, half rock ridge, an edge that would show
    # wherever it was laid. (The first Namib window, at 24.5 S, had a dry riverbed and the coast in
    # it; the one now in download_s2_samples.ps1 is from the middle of the sand sea.)
    # The Dasht-e Kavir salt-flat sample is left out too: in the survey's numbers salt flats look like
    # bright smooth gravel, so it was matched to 352 squares across Arabia, the Sahara and Central
    # Asia. Finding salt flats would need a map of them.
    [string[]]$Exclude = @('hamada', 'salt_kavir')
)
Add-Type -AssemblyName System.Drawing

$regions = [ordered]@{ north_america = 1; latin_america = 2; europe = 3; north_eurasia = 4; middle_east = 5
    africa = 6; south_asia = 7; east_asia = 8; southeast_asia = 9; australia = 10 }
$zones = [ordered]@{ boreal = 1 }

# What a square looks like, as numbers.
function Get-Look($r) {
    $sum = [double]$r.r + [double]$r.g + [double]$r.b
    # (Each in brackets: in a PowerShell list the comma binds tighter than division.)
    @(([Math]::Log([Math]::Max([double]$r.sharp_contrast, 0.02))), ($sum / 3), ([double]$r.r / [Math]::Max($sum, 1e-6)),
        ([Math]::Log([Math]::Max([double]$r.shade, 0.001))))
}

$bySquare = @{}; $bare = @()
foreach ($r in Import-Csv $Survey) {
    $bySquare["$($r.lat),$($r.lon)"] = $r
    if ($r.kind -eq 'bare' -and $r.sharp_contrast -ne '' -and [Math]::Abs([int]$r.lat) -lt $MaxDesertLat) { $bare += $r }
}

# The desert samples, and their look in the square each was taken from.
$deserts = @()
foreach ($m in [regex]::Matches((Get-Content $Sites -Raw), "@\('bare_(\w+)', ([-\d.]+), ([-\d.]+)\)")) {
    if ($Exclude -contains $m.Groups[1].Value) { continue }
    $key = "$([Math]::Floor([double]$m.Groups[2].Value)),$([Math]::Floor([double]$m.Groups[3].Value))"
    if (-not $bySquare.ContainsKey($key) -or $bySquare[$key].sharp_contrast -eq '') { "  no survey square for bare_$($m.Groups[1].Value), left out"; continue }
    $name = "desert_$($m.Groups[1].Value)"
    $zones[$name] = $zones.Count + 1
    $deserts += [pscustomobject]@{ Name = $name; Look = (Get-Look $bySquare[$key]) }
}

# The spread of each measure over bare ground, so each counts the same.
$looks = $bare | ForEach-Object { , (Get-Look $_) }
$spread = 0..3 | ForEach-Object {
    $i = $_; $values = $looks | ForEach-Object { $_[$i] }
    $mean = ($values | Measure-Object -Average).Average
    [Math]::Max([Math]::Sqrt(($values | ForEach-Object { ($_ - $mean) * ($_ - $mean) } | Measure-Object -Average).Average), 1e-6)
}
$desertOf = @{}; $tally = @{}
foreach ($r in $bare) {
    $look = Get-Look $r; $best = $null; $bestD = [double]::MaxValue
    foreach ($d in $deserts) {
        $dist = 0.0; for ($i = 0; $i -lt 4; $i++) { $dist += [Math]::Pow(($look[$i] - $d.Look[$i]) / $spread[$i], 2) }
        if ($dist -lt $bestD) { $bestD = $dist; $best = $d.Name }
    }
    $desertOf["$($r.lat),$($r.lon)"] = $best; $tally[$best] = 1 + [int]$tally[$best]
}

function Get-Region([double]$lat, [double]$lon) {
    if ($lat -ge 12 -and $lat -lt 37 -and $lon -ge -25 -and $lon -lt 63) { return 'middle_east' }
    if ($lat -ge 36 -and $lon -ge -25 -and $lon -lt 30) { return 'europe' }
    if ($lat -ge 15 -and $lon -ge -170 -and $lon -lt -10) { return 'north_america' }
    if ($lat -lt 15 -and $lon -ge -120 -and $lon -lt -30) { return 'latin_america' }
    if ($lon -ge 118 -and $lon -lt 150 -and $lat -ge 20 -and $lat -lt 50) { return 'east_asia' }
    if ($lat -ge 37 -and $lon -ge 30) { return 'north_eurasia' }
    if ($lat -ge 5 -and $lat -lt 37 -and $lon -ge 60 -and $lon -lt 92) { return 'south_asia' }
    if ($lat -ge 20 -and $lat -lt 37 -and $lon -ge 92 -and $lon -lt 150) { return 'east_asia' }
    if ($lat -ge -12 -and $lat -lt 20 -and $lon -ge 92 -and $lon -lt 160) { return 'southeast_asia' }
    if ($lat -lt 12 -and $lon -ge -25 -and $lon -lt 60) { return 'africa' }
    if ($lat -lt -10 -and $lon -ge 110) { return 'australia' }
    return $null
}

New-Item -ItemType Directory -Force $Out | Out-Null
$bmp = New-Object System.Drawing.Bitmap 360, 180
$count = @{}
for ($y = 0; $y -lt 180; $y++) {
    $lat = 89 - $y
    for ($x = 0; $x -lt 360; $x++) {
        $lon = $x - 180
        $name = Get-Region ($lat + 0.5) ($lon + 0.5)
        $region = if ($name) { $regions[$name] } else { 0 }
        $key = "$lat,$lon"
        $zone = if ($desertOf.ContainsKey($key)) { $zones[$desertOf[$key]] }
            elseif ($lat -ge 52 -and $name -in 'north_america', 'europe', 'north_eurasia') { $zones.boreal } else { 0 }
        $bmp.SetPixel($x, $y, [System.Drawing.Color]::FromArgb($region, $zone, 0))
        if ($name) { $count[$name] = 1 + [int]$count[$name] }
    }
}
$bmp.Save((Join-Path $Out 'regions.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

$lines = @('# Names of the numbers in regions.png (make_ground_regions.ps1): "region <red value> <name>" and',
    '# "zone <green value> <name>". Textures: regional/<kind>_<name>.png.')
$lines += $regions.GetEnumerator() | ForEach-Object { "region $($_.Value) $($_.Key)" }
$lines += $zones.GetEnumerator() | ForEach-Object { "zone $($_.Value) $($_.Key)" }
Set-Content -Path (Join-Path $Out 'regions.txt') -Value $lines -Encoding ascii
"wrote regions.png and regions.txt to $Out; $($bare.Count) bare squares matched to $($deserts.Count) kinds of desert:"
$tally.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "  {0,-28} {1,5} squares" -f $_.Name, $_.Value }
