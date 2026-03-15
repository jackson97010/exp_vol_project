# Run ModeC backtest with original conditions (price_change_limit 8.5%, no min threshold)
# Uses screening_results.csv, loops all unique dates
# Output: CSV only (no HTML/PNG)

$ErrorActionPreference = "Continue"
Set-Location "$PSScriptRoot/.."
$configFile = "configs/Bo_v2_modeC_screening.yaml"
$csvPath = "C:\Users\User\Documents\_02_bt\Backtest_tick_module\screening_results.csv"

# Extract unique dates from CSV (skip header)
$dates = Get-Content $csvPath | Select-Object -Skip 1 | ForEach-Object {
    $_.Split(',')[0].Trim()
} | Where-Object { $_ -match '^\d{4}-\d{2}-\d{2}$' } | Sort-Object -Unique

Write-Host "Total dates: $($dates.Count)"
Write-Host "Output: D:\C#_backtest\ModeC_screening"
Write-Host "Config: $configFile (ModeC, limit 8.5%, CSV only)"
Write-Host "========================================"

$completed = 0
foreach ($date in $dates) {
    $completed++
    Write-Host "[$completed/$($dates.Count)] Date: $date" -ForegroundColor Cyan

    dotnet run -- --mode batch --date $date --use_screening --use_dynamic_liquidity --config $configFile --no_chart

    Write-Host ""
}

Write-Host "========================================"
Write-Host "All $($dates.Count) dates completed." -ForegroundColor Green
