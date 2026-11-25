# Puts the baked terrain packs on GitHub as a release, so a fresh clone of the repository can
# download them (download_packs.ps1) instead of downloading 18 GB of raw data and baking for an
# hour.
#
# The packs are too big for git itself: over 250,000 tile files and 2.7 GB, and GitHub refuses any
# file over 100 MB. A release holds files outside the git history, up to 2 GB each, free. So each
# pack is zipped on its own (all are under 2 GB) and attached to a release tagged
# terrain-<date>, and that tag is written to pack_release.txt next to this script, which
# download_packs.ps1 reads. Commit pack_release.txt afterwards so the code and its data stay
# matched.
#
# The tiles are already compressed, so the zips are stored without compressing them again: much
# faster, and only about 1% bigger.
#
# Needs the GitHub command-line tool, signed in once: gh auth login.
#
#   powershell -ExecutionPolicy Bypass -File make_pack_release.ps1
#   powershell -ExecutionPolicy Bypass -File make_pack_release.ps1 -ZipOnly      (zip, don't upload)
#   powershell -ExecutionPolicy Bypass -File make_pack_release.ps1 -ReuseZips   (upload zips made by -ZipOnly)
#   powershell -ExecutionPolicy Bypass -File make_pack_release.ps1 -Tag terrain-2026-11-24b
param(
    [string]$Data = "$PSScriptRoot/../../data/base",
    [string]$Work = "$PSScriptRoot/../../../terrain-sources/pack_release",
    [string]$Tag = ("terrain-" + (Get-Date -Format 'yyyy-MM-dd')),
    [string[]]$Packs = @('terrain', 'cover', 'water', 'surface', 'detail'),
    [switch]$ZipOnly,
    # Use zips already in $Work instead of making them again. Only for zips made from the packs
    # as they are now (the file counts and sizes are still measured from the packs).
    [switch]$ReuseZips
)
$ErrorActionPreference = 'Stop'
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh -and (Test-Path "$env:ProgramFiles\GitHub CLI\gh.exe")) { $gh = "$env:ProgramFiles\GitHub CLI\gh.exe" }
if (-not $ZipOnly) {
    if (-not $gh) { throw "The GitHub command-line tool isn't installed: winget install GitHub.cli" }
    & $gh auth status *> $null
    if ($LASTEXITCODE -ne 0) { throw "Sign in to GitHub first: gh auth login" }
}

$Data = (Resolve-Path $Data).Path
New-Item -ItemType Directory -Force $Work | Out-Null
$Work = (Resolve-Path $Work).Path

# What is in each pack, so download_packs.ps1 can check nothing went missing on the way.
$manifest = @("# Baked terrain packs in release $Tag (make_pack_release.ps1): pack, files, bytes")
$zips = @()
foreach ($pack in $Packs) {
    $folder = Join-Path $Data $pack
    if (-not (Test-Path $folder)) { throw "No pack at ${folder}: bake it first." }
    $files = Get-ChildItem $folder -Recurse -File
    $bytes = ($files | Measure-Object Length -Sum).Sum
    $manifest += "$pack,$($files.Count),$bytes"

    $zip = Join-Path $Work "$pack.zip"
    if ($ReuseZips -and (Test-Path $zip)) {
        "using the existing $pack.zip"
    } else {
        Remove-Item $zip -ErrorAction SilentlyContinue
        "zipping $pack ({0:N0} files, {1:0} MB)..." -f $files.Count, ($bytes / 1MB)
        # Windows' own tar (bsdtar) writes zip files; "store" keeps the already compressed tiles as they are.
        & "$env:SystemRoot\System32\tar.exe" -a -c --options zip:compression=store -f $zip -C $Data $pack
        if ($LASTEXITCODE -ne 0) { throw "zipping $pack failed" }
    }
    $size = (Get-Item $zip).Length
    if ($size -ge 2GB) { throw "$pack.zip is $([math]::Round($size / 1GB, 2)) GB; a release file must be under 2 GB." }
    "  $pack.zip: {0:0} MB" -f ($size / 1MB)
    $zips += $zip
}

$manifestFile = Join-Path $Work "packs.txt"
Set-Content -Path $manifestFile -Value $manifest -Encoding ascii
$zips += $manifestFile
if ($ZipOnly) { "zipped into $Work; not uploaded (-ZipOnly)"; return }

$notes = @"
Baked terrain packs for tns-godot, made on $(Get-Date -Format 'yyyy-MM-dd') from tools/terrain/recipes.
After cloning, run tools/terrain/download_packs.ps1 to download and unpack them into data/base.
$(($manifest | Select-Object -Skip 1 | ForEach-Object { $p = $_.Split(','); "- {0}: {1:N0} files, {2:0} MB" -f $p[0], [int]$p[1], ([double]$p[2] / 1MB) }) -join "`n")

Made from these sources, whose licences ask for this credit (more in CREDITS.md):
- ESA WorldCover 2021 v200: (c) ESA WorldCover project 2021 / Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover consortium. CC BY 4.0.
- Copernicus DEM GLO-90: (c) DLR e.V. 2010-2014 and (c) Airbus Defence and Space GmbH 2014-2018 provided under COPERNICUS by the European Union and ESA; all rights reserved.
- NOAA ETOPO 2022 15 Arc-Second Global Relief Model, NOAA National Centers for Environmental Information. Public domain.
- NASA Blue Marble Next Generation, NASA Earth Observatory (Reto Stockli). Public domain.
- Natural Earth glaciated areas. Made with Natural Earth. Public domain.
"@
"creating release $Tag and uploading (this takes a while)..."
& $gh release create $Tag @zips --title "Terrain data $Tag" --notes $notes
if ($LASTEXITCODE -ne 0) { throw "creating the release failed" }

Set-Content -Path (Join-Path $PSScriptRoot "pack_release.txt") -Value $Tag -Encoding ascii
"done: release $Tag. pack_release.txt now names it; commit that file."
