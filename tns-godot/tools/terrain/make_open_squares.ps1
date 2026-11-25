# Writes lists/open_squares.csv: the 1 x 1 degree squares of open ground that is not mountains,
# where the blended colour and the detail go deeper (openSquares in recipes/base_surface.json).
#
# Open ground is where flat colour shows most when zooming in: deserts, fields, grassland,
# shrub, tundra, wetland and towns. Forest is left out (one dark green, little to gain, and
# a lot of ground), and so are ice and mountains: there the slope shading is most of the
# picture, and detail fades out on steep ground anyway.
#
# The kind of ground and the shading come from the bland surveys (terrain bland): "kind" is the
# most common kind in the measured patches, "shade" how much the slope shading varies (plains
# below 0.02, the Alps about 0.15). The first survey took one 20 km patch at each square's
# centre, the second a 3 x 3 grid of them; a square is listed if either finds it open, so a
# river or a wood at the centre does not leave out open country.
param(
    # Both surveys: a square is open if either finds it so (the centre patch, or the 3 x 3 patches).
    [string[]]$Survey = @("$PSScriptRoot/../../docs/bland_survey/bland_survey_after.csv", "$PSScriptRoot/../../docs/bland_survey/bland_survey_squares.csv"),
    [string]$Out = "$PSScriptRoot/lists/open_squares.csv",
    [double]$MaxShade = 0.06,
    [string[]]$Kinds = @('bare', 'crop', 'grass', 'shrub', 'moss', 'wetland', 'built')
)

$rows = $Survey | ForEach-Object { Import-Csv $_ } | Where-Object { $Kinds -contains $_.kind -and [double]$_.land -gt 0 -and [double]$_.shade -lt $MaxShade }
$lines = @("# Open ground that is not mountains: lat,lon of each 1 x 1 degree square's south-west corner.",
    "# Made by make_open_squares.ps1 (kinds $($Kinds -join ', '); shade below $MaxShade).")
$squares = $rows | ForEach-Object { "$($_.lat),$($_.lon)" } | Sort-Object -Unique { [int]$_.Split(',')[0] }, { [int]$_.Split(',')[1] }
$lines += $squares
Set-Content -Path $Out -Value $lines -Encoding ascii
"$($squares.Count) squares written to $Out"
