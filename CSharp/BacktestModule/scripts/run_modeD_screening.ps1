# Mode D screening backtest: 2 groups (version A: 1-3-5, version B: 3-5-7)
# Uses screening_results.csv

$ErrorActionPreference = "Stop"
Set-Location "$PSScriptRoot/.."

$baseOutputPath = "D:\C#_backtest\grid_results\1.2% stopLoss"

# Ensure output directories exist
$dirs = @(
    "$baseOutputPath\modeD_135",
    "$baseOutputPath\modeD_357"
)
foreach ($dir in $dirs) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

# Read dates from screening_results.csv
$csvPath = "screening_results.csv"
if (-not (Test-Path $csvPath)) {
    Write-Host "[ERROR] screening_results.csv not found at $csvPath"
    exit 1
}

$csv = Import-Csv $csvPath
$dates = $csv | ForEach-Object { $_.date } | Sort-Object -Unique
Write-Host "Found $($dates.Count) unique dates in screening_results.csv"

# ── Version A: 1-3-5 ──
Write-Host ""
Write-Host "=========================================="
Write-Host " Mode D Version A (1m-3m-5m) - $($dates.Count) dates"
Write-Host "=========================================="

foreach ($date in $dates) {
    Write-Host "[A] Running date: $date"
    dotnet run --configuration Release -- `
        --mode batch `
        --date $date `
        --use_screening `
        --use_dynamic_liquidity `
        --config configs/Bo_v2_modeD_135.yaml `
        --output_path "$baseOutputPath\modeD_135" `
        --no_chart
}

# ── Version B: 3-5-7 ──
Write-Host ""
Write-Host "=========================================="
Write-Host " Mode D Version B (3m-5m-7m) - $($dates.Count) dates"
Write-Host "=========================================="

foreach ($date in $dates) {
    Write-Host "[B] Running date: $date"
    dotnet run --configuration Release -- `
        --mode batch `
        --date $date `
        --use_screening `
        --use_dynamic_liquidity `
        --config configs/Bo_v2_modeD_357.yaml `
        --output_path "$baseOutputPath\modeD_357" `
        --no_chart
}

Write-Host ""
Write-Host "=========================================="
Write-Host " All Mode D backtests complete!"
Write-Host "=========================================="
Write-Host " Output A (1-3-5): $baseOutputPath\modeD_135"
Write-Host " Output B (3-5-7): $baseOutputPath\modeD_357"
