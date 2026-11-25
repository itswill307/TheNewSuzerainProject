# Downloads Copernicus GLO-90 elevation (one sample every 90 m) for a list of 1 x 1 degree squares.
# Each square is one GeoTIFF of roughly 1 to 6 MB. Safe to stop and run again: squares already
# downloaded are skipped.
#
# Make the list of squares first, with the terrain tool:
#   terrain rugged <pack folder> rugged_squares.txt --squares 2000
#
#   powershell -ExecutionPolicy Bypass -File download_copernicus90.ps1
#   powershell -ExecutionPolicy Bypass -File download_copernicus90.ps1 -List my_squares.txt -Parallel 16
param(
    # Text file with one square per line, such as N46_00_E010_00 (its south-west corner).
    [string]$List = (Join-Path $PSScriptRoot "lists\rugged_squares.txt"),
    # Where the files go.
    [string]$Destination = (Join-Path $PSScriptRoot "..\..\..\terrain-sources\copernicus_glo90"),
    # How many files to fetch at once.
    [int]$Parallel = 12
)

$ErrorActionPreference = "Stop"
$base = "https://copernicus-dem-90m.s3.amazonaws.com"

if (-not (Test-Path $List)) { throw "List of squares not found: $List" }
$squares = Get-Content $List | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^[NS]\d{2}_00_[EW]\d{3}_00$' }
New-Item -ItemType Directory -Force $Destination | Out-Null
$Destination = (Resolve-Path $Destination).Path

# Squares with no file on the server (open sea) are remembered so they are not asked for again.
$missingFile = Join-Path $Destination "_not_available.txt"
$missing = @{}
if (Test-Path $missingFile) { Get-Content $missingFile | ForEach-Object { $missing[$_] = $true } }

$todo = @($squares | Where-Object {
    -not $missing.ContainsKey($_) -and -not (Test-Path (Join-Path $Destination "Copernicus_DSM_COG_30_$($_)_DEM.tif"))
})
Write-Host "$($squares.Count) squares listed, $($todo.Count) still to download -> $Destination"
if ($todo.Count -eq 0) { Write-Host "Nothing to do."; return }

# curl writes each transfer to a temporary file. Process each result as it finishes.
$config = Join-Path $Destination "_curl_config_$PID.txt"
$lines = foreach ($square in $todo) {
    $name = "Copernicus_DSM_COG_30_$($square)_DEM"
    "url = `"$base/$name/$name.tif`""
    "output = `"$((Join-Path $Destination "$name.tif.part") -replace '\\','/')`""
}
Set-Content -Path $config -Value $lines -Encoding ascii

$ok = 0
$absent = 0
try {
    $curlArgs = @('--parallel', '--parallel-max', $Parallel, '--fail', '--location', '--silent',
        '--show-error', '--retry', '4', '--retry-delay', '2', '--config', $config,
        '--write-out', '%{exitcode} %{http_code} %{filename_effective}\n')
    & curl.exe @curlArgs | ForEach-Object {
        if ($_ -match '^(\d+) (\d{3}) (.+)$') {
            $result = $Matches[1]
            $code = $Matches[2]
            $part = $Matches[3] -replace '/','\'
            if ($result -eq "0" -and $code -eq "200" -and (Test-Path -LiteralPath $part)) {
                Move-Item -LiteralPath $part -Destination ($part -replace '\.part$','') -Force
                $ok++
            } elseif ($code -eq "404" -and $part -match 'COG_30_(.+)_DEM\.tif\.part$') {
                Add-Content -LiteralPath $missingFile -Value $Matches[1]
                $absent++
            }
        }
    }
} finally {
    Remove-Item -LiteralPath $config -Force
}
$failed = $todo.Count - $ok - $absent

$files = Get-ChildItem $Destination -Filter *.tif
$sizeGb = [math]::Round((($files | Measure-Object Length -Sum).Sum / 1GB), 2)
Write-Host "$ok downloaded, $absent not available (sea), $failed failed."
Write-Host "$($files.Count) files, $sizeGb GB in $Destination"
if ($failed -gt 0) { Write-Host "Run the script again to retry the failed ones." }
