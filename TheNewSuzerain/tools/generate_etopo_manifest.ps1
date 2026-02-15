param(
    [string]$TileDir = "Assets/Map/Textures/etopo15normcomp",
    [string]$OutputDir = "",
    [string]$Pattern = "ETOPO_2022_v1_15s_*_surface.tif"
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = (Resolve-Path (Join-Path $scriptDir "..")).Path

$tileRoot = Resolve-Path (Join-Path $repoRoot $TileDir)
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = (Join-Path $TileDir "manifest") -replace "\\", "/"
}
$outputRoot = Join-Path $repoRoot $OutputDir
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$regex = '^ETOPO_2022_v1_15s_([NS])(\d{2})([EW])(\d{3})_surface\.tif$'

$tiles = Get-ChildItem -Path $tileRoot -File -Filter $Pattern | ForEach-Object {
    $name = $_.Name
    if ($name -notmatch $regex) {
        Write-Warning "Skipping unexpected filename: $name"
        return
    }

    $latTop = [int]$matches[2]
    if ($matches[1] -eq "S") {
        $latTop = -$latTop
    }

    $lonMin = [int]$matches[4]
    if ($matches[3] -eq "W") {
        $lonMin = -$lonMin
    }

    $fullPath = $_.FullName
    $relativePath = $fullPath.Substring($repoRoot.Length).TrimStart('\', '/')
    $relativePath = $relativePath -replace '\\', '/'

    [PSCustomObject]@{
        key       = [IO.Path]::GetFileNameWithoutExtension($name)
        file      = $relativePath
        lonMin    = [double]$lonMin
        lonMax    = [double]($lonMin + 15.0)
        latMin    = [double]($latTop - 15.0)
        latMax    = [double]$latTop
        centerLon = [double]($lonMin + 7.5)
        centerLat = [double]($latTop - 7.5)
    }
} | Where-Object { $_ -ne $null } | Sort-Object key

$jsonPath = Join-Path $outputRoot "etopo15_tiles.json"
$csvPath = Join-Path $outputRoot "etopo15_tiles.csv"

$tiles | ConvertTo-Json -Depth 4 | Set-Content -Path $jsonPath -Encoding UTF8
$tiles | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8

Write-Host "Manifest JSON: $jsonPath"
Write-Host "Manifest CSV : $csvPath"
Write-Host "Tile count   : $($tiles.Count)"
