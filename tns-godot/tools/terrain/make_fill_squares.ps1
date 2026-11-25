# Writes lists/fill_squares.csv: more squares that get the sharper detail, so a uniform region
# never changes detail along a square's edge (detailFillSquares in recipes/base_surface.json).
#
# 1. Whole regions. A region is a patch of touching 1 x 1 degree squares (8 neighbours) whose most
#    common kind of ground is the same. A forest region that the bland and open lists already
#    reach into for at least MinShare of its squares gets all of its squares: the rainforests
#    (Amazon, Congo, Borneo, New Guinea, West Africa). Squares miss the bland survey because of
#    what sits at the centre of their 20 km patch, such as a river or a clearing, not because the
#    forest is different there.
# 2. Gaps. Then, FillPasses times, every square with at least 5 of its 8 neighbours in a list
#    joins it.
#
# The kind of ground comes from the bland survey (terrain bland).
param(
    [string]$Survey = "$PSScriptRoot/../../docs/bland_survey/bland_survey_squares.csv",
    [string]$Bland = "$PSScriptRoot/lists/detail_fine_squares.csv",
    [string]$Open = "$PSScriptRoot/lists/open_squares.csv",
    [string]$Out = "$PSScriptRoot/lists/fill_squares.csv",
    [string[]]$RegionKinds = @('tree'),
    [double]$MinShare = 0.15,
    [int]$MinRegion = 8,
    [int]$FillPasses = 3
)

function Read-Squares($path) {
    $set = @{}
    foreach ($line in Get-Content $path) { $t = $line.Trim(); if ($t -and -not $t.StartsWith('#')) { $set[$t] = $true } }
    $set
}
function Get-Neighbours([string]$square) {
    $p = $square.Split(','); $a = [int]$p[0]; $b = [int]$p[1]
    foreach ($da in -1, 0, 1) {
        foreach ($db in -1, 0, 1) {
            if ($da -eq 0 -and $db -eq 0) { continue }
            $lon = $b + $db; if ($lon -ge 180) { $lon -= 360 } elseif ($lon -lt -180) { $lon += 360 }
            "$($a + $da),$lon"
        }
    }
}

$listed = Read-Squares $Bland; foreach ($k in (Read-Squares $Open).Keys) { $listed[$k] = $true }
$kind = @{}; foreach ($r in Import-Csv $Survey) { $kind["$($r.lat),$($r.lon)"] = $r.kind }
$fill = @{}; $why = @()

# 1. Whole regions.
$seen = @{}
foreach ($start in @($kind.Keys)) {
    if ($seen.ContainsKey($start) -or $RegionKinds -notcontains $kind[$start]) { continue }
    $k = $kind[$start]; $queue = New-Object System.Collections.Generic.Queue[string]
    $queue.Enqueue($start); $seen[$start] = $true; $members = New-Object System.Collections.Generic.List[string]
    while ($queue.Count -gt 0) {
        $s = $queue.Dequeue(); $members.Add($s)
        foreach ($n in Get-Neighbours $s) { if (-not $seen.ContainsKey($n) -and $kind[$n] -eq $k) { $seen[$n] = $true; $queue.Enqueue($n) } }
    }
    if ($members.Count -lt $MinRegion) { continue }
    $in = @($members | Where-Object { $listed.ContainsKey($_) }).Count
    if ($in / $members.Count -lt $MinShare) { continue }
    $added = 0
    foreach ($m in $members) { if (-not $listed.ContainsKey($m) -and -not $fill.ContainsKey($m)) { $fill[$m] = $true; $added++ } }
    $lats = $members | ForEach-Object { [int]$_.Split(',')[0] }; $lons = $members | ForEach-Object { [int]$_.Split(',')[1] }
    $why += "# region: $k, $($members.Count) squares around $([Math]::Round(($lats | Measure-Object -Average).Average)),$([Math]::Round(($lons | Measure-Object -Average).Average)), $([Math]::Round(100 * $in / $members.Count))% listed: $added added"
}

# 2. Gaps.
$all = @{}; foreach ($k in $listed.Keys + $fill.Keys) { $all[$k] = $true }
$gaps = 0
for ($pass = 0; $pass -lt $FillPasses; $pass++) {
    $candidates = @{}
    foreach ($s in @($all.Keys)) { foreach ($n in Get-Neighbours $s) { if (-not $all.ContainsKey($n) -and $kind.ContainsKey($n)) { $candidates[$n] = $true } } }
    $joining = foreach ($c in $candidates.Keys) { if (@(Get-Neighbours $c | Where-Object { $all.ContainsKey($_) }).Count -ge 5) { $c } }
    if (-not $joining) { break }
    foreach ($c in $joining) { $all[$c] = $true; $fill[$c] = $true; $gaps++ }
}
$why += "# gaps: $gaps squares with at least 5 of 8 neighbours listed ($FillPasses passes)"

$lines = @("# More squares for the sharper detail: whole forest regions and gaps. lat,lon of each 1 x 1",
    "# degree square's south-west corner. Made by make_fill_squares.ps1.") + $why
$lines += $fill.Keys | Sort-Object { [int]$_.Split(',')[0] }, { [int]$_.Split(',')[1] }
Set-Content -Path $Out -Value $lines -Encoding ascii
$why
"$($fill.Count) squares written to $Out"
