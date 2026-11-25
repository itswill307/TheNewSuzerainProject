# Draws a world map of where the square lists put the sharper detail:
#   lists/detail_fine_squares.csv  (bland survey: smooth up close)
#   lists/open_squares.csv         (open ground that is not mountains: blurred at country zoom)
#   lists/fill_squares.csv         (whole forest regions and gaps)
# Land squares come from the bland survey. Writes docs/bland_survey/square_lists_map.png.
param(
    [string]$Survey = "$PSScriptRoot/../../docs/bland_survey/bland_survey_after.csv",
    [string]$Bland = "$PSScriptRoot/lists/detail_fine_squares.csv",
    [string]$Open = "$PSScriptRoot/lists/open_squares.csv",
    [string]$Out = "$PSScriptRoot/../../docs/bland_survey/square_lists_map.png",
    [string]$Fill = "$PSScriptRoot/lists/fill_squares.csv",
    [int]$Px = 4
)
Add-Type -AssemblyName System.Drawing
function Read-Squares($path) {
    $set = @{}
    foreach ($line in Get-Content $path) { $t = $line.Trim(); if ($t -and -not $t.StartsWith('#')) { $set[$t] = $true } }
    $set
}
$blandSet = Read-Squares $Bland; $openSet = Read-Squares $Open
$filled = Read-Squares $Fill
$legendH = 70
$bmp = New-Object System.Drawing.Bitmap (360 * $Px), (180 * $Px + $legendH)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::FromArgb(24, 34, 48))
$colLand = [System.Drawing.Color]::FromArgb(70, 74, 80)
$colBland = [System.Drawing.Color]::FromArgb(66, 146, 230)
$colOpen = [System.Drawing.Color]::FromArgb(240, 170, 60)
$colBoth = [System.Drawing.Color]::FromArgb(150, 220, 120)
$colFill = [System.Drawing.Color]::FromArgb(205, 120, 220)
$brushes = @{ land = New-Object System.Drawing.SolidBrush $colLand; bland = New-Object System.Drawing.SolidBrush $colBland; open = New-Object System.Drawing.SolidBrush $colOpen; both = New-Object System.Drawing.SolidBrush $colBoth; fill = New-Object System.Drawing.SolidBrush $colFill }
$counts = @{ land = 0; bland = 0; open = 0; both = 0; fill = 0 }
foreach ($r in Import-Csv $Survey) {
    $key = "$($r.lat),$($r.lon)"
    $b = $blandSet.ContainsKey($key); $o = $openSet.ContainsKey($key)
    $kind = if ($b -and $o) { 'both' } elseif ($b) { 'bland' } elseif ($o) { 'open' } elseif ($filled.ContainsKey($key)) { 'fill' } else { 'land' }
    $counts[$kind]++
    $x = ([int]$r.lon + 180) * $Px; $y = (89 - [int]$r.lat) * $Px
    $g.FillRectangle($brushes[$kind], $x, $y, $Px, $Px)
}
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$font = New-Object System.Drawing.Font "Segoe UI", 13
$ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(226, 228, 235))
$y0 = 180 * $Px + 22; $x = 20
foreach ($item in @(@('bland', "Bland survey only ($($counts.bland))"), @('both', "Both ($($counts.both))"), @('open', "Open flat ground only ($($counts.open))"), @('fill', "Forest regions and gaps ($($counts.fill))"), @('land', "Neither ($($counts.land))"))) {
    $g.FillRectangle($brushes[$item[0]], $x, $y0, 26, 26)
    $g.DrawString($item[1], $font, $ink, $x + 34, $y0)
    $x += 34 + [int]$g.MeasureString($item[1], $font).Width + 30
}
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"wrote $Out"; $counts
