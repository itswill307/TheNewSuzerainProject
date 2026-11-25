# Downloads the baked terrain packs from the repository's GitHub release and unpacks them into
# data/base, so a fresh clone can run the game without baking (make_pack_release.ps1 made the
# release). The release is the one named in pack_release.txt next to this script, which matches
# the code you have checked out.
#
# It uses the GitHub command-line tool, which you sign in to once (gh auth login); that also
# works if the repository is ever made private. About 2.7 GB to download and 2.7 GB once
# unpacked. Windows only; on a Mac, download the zips from the release page and unzip them into
# data/base by hand.
#
# A pack that is already there is left alone unless you add -Replace; then the old one is moved
# aside to <pack>-old (delete it yourself once the new one works).
#
#   powershell -ExecutionPolicy Bypass -File download_packs.ps1
#   powershell -ExecutionPolicy Bypass -File download_packs.ps1 -Replace
#   powershell -ExecutionPolicy Bypass -File download_packs.ps1 -Tag terrain-2026-11-24
param(
    [string]$Tag = "",
    [string]$Data = "$PSScriptRoot/../../data/base",
    [string]$Work = "$PSScriptRoot/../../../terrain-sources/pack_download",
    [switch]$Replace,
    [switch]$KeepZips
)
$ErrorActionPreference = 'Stop'
if (-not $Tag) {
    $file = Join-Path $PSScriptRoot "pack_release.txt"
    if (-not (Test-Path $file)) { throw "No pack_release.txt: say which release with -Tag." }
    $Tag = (Get-Content $file -TotalCount 1).Trim()
}

$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh -and (Test-Path "$env:ProgramFiles\GitHub CLI\gh.exe")) { $gh = "$env:ProgramFiles\GitHub CLI\gh.exe" }
if (-not $gh) { throw "The GitHub command-line tool isn't installed: winget install GitHub.cli, then gh auth login" }
& $gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw "Sign in to GitHub first: gh auth login" }

# Windows' own tar.exe unpacks zip files.
$tar = "$env:SystemRoot\System32\tar.exe"

New-Item -ItemType Directory -Force $Data, $Work | Out-Null
$Data = (Resolve-Path $Data).Path
$Work = (Resolve-Path $Work).Path

# The list of packs and what each should hold.
Remove-Item (Join-Path $Work "packs.txt") -ErrorAction SilentlyContinue
& $gh release download $Tag --pattern "packs.txt" --dir $Work --clobber
if ($LASTEXITCODE -ne 0) { throw "Couldn't find release $Tag on GitHub (or you don't have access to the repository)." }
$packs = Get-Content (Join-Path $Work "packs.txt") | Where-Object { $_ -and -not $_.StartsWith('#') } | ForEach-Object {
    $p = $_.Split(','); [pscustomobject]@{ Name = $p[0]; Files = [int]$p[1]; Bytes = [long]$p[2] }
}

foreach ($pack in $packs) {
    $target = Join-Path $Data $pack.Name
    if (Test-Path $target) {
        if (-not $Replace) { "  $($pack.Name): already there, left alone (add -Replace to swap in the release's)"; continue }
        $old = "$target-old"
        if (Test-Path $old) { throw "$old is in the way; delete or rename it first." }
        Move-Item $target $old
        "  $($pack.Name): moved the existing pack to $($pack.Name)-old"
    }

    $zip = Join-Path $Work "$($pack.Name).zip"
    "  $($pack.Name): downloading ({0:0} MB)..." -f ($pack.Bytes / 1MB)
    & $gh release download $Tag --pattern "$($pack.Name).zip" --dir $Work --clobber
    if ($LASTEXITCODE -ne 0) { throw "downloading $($pack.Name).zip failed" }

    "  $($pack.Name): unpacking..."
    & $tar -x -f $zip -C $Data
    if ($LASTEXITCODE -ne 0) { throw "unpacking $($pack.Name).zip failed" }

    # Check it all arrived.
    $files = Get-ChildItem $target -Recurse -File
    $bytes = ($files | Measure-Object Length -Sum).Sum
    if ($files.Count -ne $pack.Files -or $bytes -ne $pack.Bytes) {
        throw "$($pack.Name) unpacked to $($files.Count) files and $bytes bytes, but the release says $($pack.Files) and $($pack.Bytes)."
    }
    "  $($pack.Name): {0:N0} files, all there" -f $files.Count
    if (-not $KeepZips) { Remove-Item $zip }
}

"done: the terrain packs from $Tag are in $Data"
