# Draws two overview pictures of the close-up ground textures, into docs/ground_textures:
#   textures_and_sources.png: each standard and regional texture beside the Sentinel-2 sample it
#                             was made from (its name, place and the version it is)
#   regions_map.png:          regions.png in colour, with the sand-sea and boreal zones
# Reads the same -Pick and -Regional lists as make_ground_textures.ps1.
param(
    [string]$Ground = "$PSScriptRoot/../../data/base/ground",
    [string]$Samples = "$PSScriptRoot/../../../terrain-sources/sentinel2_samples",
    [string]$LargeSamples = "$PSScriptRoot/../../../terrain-sources/sentinel2_samples_88km",
    [string]$Out = "$PSScriptRoot/../../docs/ground_textures"
)
Add-Type -AssemblyName System.Drawing

# The lists from make_ground_textures.ps1, so both stay in step.
$script = Get-Content "$PSScriptRoot/make_ground_textures.ps1" -Raw
$pick = [regex]::Match($script, '\[string\]\$Pick = "([^"]*)"').Groups[1].Value
$regional = ([regex]::Matches(([regex]::Match($script, '\[string\]\$Regional = \(([^)]*)\)').Groups[1].Value), '"([^"]*)"') |
    ForEach-Object { $_.Groups[1].Value }) -join ''
$sites = @{}
foreach ($m in [regex]::Matches((Get-Content "$PSScriptRoot/download_s2_samples.ps1" -Raw), "@\('(\w+)', ([-\d.]+), ([-\d.]+)\)")) {
    $sites[$m.Groups[1].Value] = '{0}{1} {2}{3}' -f [Math]::Abs([double]$m.Groups[2].Value), $(if ([double]$m.Groups[2].Value -lt 0) { 'S' } else { 'N' }),
        [Math]::Abs([double]$m.Groups[3].Value), $(if ([double]$m.Groups[3].Value -lt 0) { 'W' } else { 'E' })
}
# The large samples' places (sample_windows.csv) and sizes; each sample is looked for there first.
$size = @{}
foreach ($w in Import-Csv "$PSScriptRoot/sample_windows.csv") {
    $sites[$w.name] = '{0}{1} {2}{3}' -f [Math]::Abs([double]$w.lat), $(if ([double]$w.lat -lt 0) { 'S' } else { 'N' }), [Math]::Abs([double]$w.lon), $(if ([double]$w.lon -lt 0) { 'W' } else { 'E' })
}
function Get-SamplePng([string]$name) {
    foreach ($set in @(@($LargeSamples, 88), @($Samples, 44))) { if (Test-Path "$($set[0])/png/$name.png") { $script:size[$name] = $set[1]; return "$($set[0])/png/$name.png" } }
}

$entries = @()
$kinds = 'tree', 'shrub', 'grass', 'crop', 'bare', 'wetland', 'moss', 'snow', 'built', 'mangrove'
$picks = @{}; foreach ($p in $pick.Split(',', [StringSplitOptions]::RemoveEmptyEntries)) { $k, $v = $p.Split('='); $picks[$k] = $v }
foreach ($kind in $kinds) {
    if (-not (Test-Path "$Ground/$kind.png")) { continue }
    $sample = if ($picks.ContainsKey($kind)) { $picks[$kind] } else { (Get-ChildItem "$LargeSamples/png", "$Samples/png" -Filter "${kind}_*.png" | Sort-Object Name | Select-Object -First 1).BaseName }
    $entries += [pscustomobject]@{ Title = "$kind (standard)"; Texture = "$Ground/$kind.png"; Sample = $sample }
}
foreach ($p in $regional.Split(',', [StringSplitOptions]::RemoveEmptyEntries)) {
    $name, $sample = $p.Split('=')
    if (Test-Path "$Ground/regional/$name.png") { $entries += [pscustomobject]@{ Title = $name; Texture = "$Ground/regional/$name.png"; Sample = $sample } }
}

# One cell: the sample (true colour) and the texture made from it, side by side.
$cell = 220; $pad = 10; $textH = 40; $perRow = 3
$cellW = 2 * $cell + $pad
$rows = [Math]::Ceiling($entries.Count / $perRow)
$bmp = New-Object System.Drawing.Bitmap ([int]($pad + $perRow * ($cellW + 2 * $pad))), ([int]($pad + $rows * ($cell + $textH + 2 * $pad)))
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::FromArgb(20, 22, 28))
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$bold = New-Object System.Drawing.Font "Segoe UI", 10, ([System.Drawing.FontStyle]::Bold)
$font = New-Object System.Drawing.Font "Segoe UI", 9
$ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(226, 228, 235))
$dim = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(160, 165, 175))
for ($i = 0; $i -lt $entries.Count; $i++) {
    $e = $entries[$i]
    $x = $pad + ($i % $perRow) * ($cellW + 2 * $pad); $y = $pad + [Math]::Floor($i / $perRow) * ($cell + $textH + 2 * $pad)
    $g.DrawString($e.Title, $bold, $ink, $x, $y)
    $png = Get-SamplePng $e.Sample
    $g.DrawString("from $($e.Sample), $($sites[$e.Sample]), $($size[$e.Sample]) km   |   sample, texture", $font, $dim, $x, $y + 18)
    foreach ($pair in @(@($png, 0), @($e.Texture, $cell + $pad))) {
        if (-not $pair[0] -or -not (Test-Path $pair[0])) { continue }
        $img = [System.Drawing.Image]::FromFile((Resolve-Path $pair[0]))
        $g.DrawImage($img, $x + $pair[1], $y + $textH, $cell, $cell)
        $img.Dispose()
    }
}
New-Item -ItemType Directory -Force $Out | Out-Null
$bmp.Save("$Out/textures_and_sources.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()

# The region map in colour.
$colours = @{ 1 = (70, 130, 200); 2 = (60, 170, 110); 3 = (150, 110, 210); 4 = (200, 90, 90); 5 = (220, 170, 70)
    6 = (230, 120, 50); 7 = (200, 80, 160); 8 = (80, 190, 200); 9 = (140, 200, 80); 10 = (200, 160, 120) }
$names = @{}; foreach ($line in Get-Content "$Ground/regions.txt") { if ($line -match '^(region|zone) (\d+) (\w+)$') { $names["$($Matches[1])$($Matches[2])"] = $Matches[3] } }
$map = New-Object System.Drawing.Bitmap "$Ground/regions.png"
$scale = 4; $legendH = 120
$view = New-Object System.Drawing.Bitmap (360 * $scale), (180 * $scale + $legendH)
$g = [System.Drawing.Graphics]::FromImage($view)
$g.Clear([System.Drawing.Color]::FromArgb(20, 22, 28))
for ($y = 0; $y -lt 180; $y++) {
    for ($x = 0; $x -lt 360; $x++) {
        $p = $map.GetPixel($x, $y)
        $c = if ($colours.ContainsKey([int]$p.R)) { $colours[[int]$p.R] } else { (45, 48, 55) }
        $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($c[0], $c[1], $c[2]))
        $g.FillRectangle($brush, $x * $scale, $y * $scale, $scale, $scale); $brush.Dispose()
        if ($p.G -eq 1) { $g.FillRectangle([System.Drawing.Brushes]::White, $x * $scale + 1, $y * $scale + 1, 2, 2) }
        if ($p.G -eq 2) { $g.FillRectangle([System.Drawing.Brushes]::Black, $x * $scale + 1, $y * $scale + 1, 2, 2) }
    }
}
$map.Dispose()
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$lx = 20; $ly = 180 * $scale + 16
foreach ($id in 1..10) {
    $c = $colours[$id]
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb($c[0], $c[1], $c[2]))
    $g.FillRectangle($brush, $lx, $ly, 18, 18); $brush.Dispose()
    $g.DrawString($names["region$id"], $font, $ink, $lx + 24, $ly)
    $lx += 140; if ($id -eq 5) { $lx = 20; $ly += 30 }
}
$ly += 30; $lx = 20
$g.FillRectangle([System.Drawing.Brushes]::White, $lx + 7, $ly + 7, 4, 4); $g.DrawString("white dots: boreal zone", $font, $ink, $lx + 24, $ly)
$g.FillRectangle([System.Drawing.Brushes]::Black, $lx + 287, $ly + 7, 4, 4); $g.DrawString("black dots: sand-sea zone", $font, $ink, $lx + 304, $ly)
$view.Save("$Out/regions_map.png", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $view.Dispose()
"wrote $Out/textures_and_sources.png ($($entries.Count) textures) and $Out/regions_map.png"
