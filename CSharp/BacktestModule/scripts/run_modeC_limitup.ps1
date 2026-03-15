# Run ModeC backtest on t-1 limit-up stocks

$ErrorActionPreference = "Continue"
Set-Location "$PSScriptRoot/.."
$configFile = "configs/Bo_v2_modeC_limitup.yaml"
$csvPath = "screen_limit_up.csv"

$dates = Get-Content $csvPath | Select-Object -Skip 1 | ForEach-Object {
    $_.Split(',')[0].Trim()
} | Where-Object { $_ -match '^\d{4}-\d{2}-\d{2}$' } | Sort-Object -Unique

Write-Host "Total dates: $($dates.Count)"
Write-Host "Output: D:\C#_backtest\t-1_limit_up"
Write-Host "Config: $configFile (ModeC, max 8.5%, no min)"
Write-Host "========================================"

$completed = 0
foreach ($date in $dates) {
    $completed++
    Write-Host "[$completed/$($dates.Count)] Date: $date" -ForegroundColor Cyan
    dotnet run -- --mode batch --date $date --use_screening --screening_file $csvPath --use_dynamic_liquidity --config $configFile
    Write-Host ""
}

Write-Host "========================================"
Write-Host "All $($dates.Count) dates completed." -ForegroundColor Green
