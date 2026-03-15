# Run Mode E backtest on screen_limit_up_open_entry.csv
# Group 1: default entry time (0909~1300)
# Group 2: entry time 0905~0930

$ErrorActionPreference = "Continue"
Set-Location "$PSScriptRoot/.."
$csvPath = "screen_limit_up_open_entry.csv"

$dates = Get-Content $csvPath | Select-Object -Skip 1 | ForEach-Object {
    $_.Split(',')[0].Trim()
} | Where-Object { $_ -match '^\d{4}-\d{2}-\d{2}$' } | Sort-Object -Unique

# ── Group 1: Mode E default entry time ──
$config1 = "configs/Bo_v2_modeE_limit_up_open_entry.yaml"
Write-Host "========================================"
Write-Host "Group 1: Mode E (entry 0909~1300)"
Write-Host "Total dates: $($dates.Count)"
Write-Host "Output: D:\C#_backtest\modeE_limit_up_open_entry"
Write-Host "Config: $config1"
Write-Host "========================================"

$completed = 0
foreach ($date in $dates) {
    $completed++
    Write-Host "[$completed/$($dates.Count)] Date: $date" -ForegroundColor Cyan
    dotnet run -- --mode batch --date $date --use_screening --screening_file $csvPath --use_dynamic_liquidity --no_chart --config $config1
    Write-Host ""
}

Write-Host "Group 1 completed ($($dates.Count) dates)." -ForegroundColor Green
Write-Host ""

# ── Group 2: Mode E entry 0905~0930 ──
$config2 = "configs/Bo_v2_modeE_limit_up_open_entry_0905_0930.yaml"
Write-Host "========================================"
Write-Host "Group 2: Mode E (entry 0905~0930)"
Write-Host "Total dates: $($dates.Count)"
Write-Host "Output: D:\C#_backtest\modeE_limit_up_open_entry_0905_0930"
Write-Host "Config: $config2"
Write-Host "========================================"

$completed = 0
foreach ($date in $dates) {
    $completed++
    Write-Host "[$completed/$($dates.Count)] Date: $date" -ForegroundColor Cyan
    dotnet run -- --mode batch --date $date --use_screening --screening_file $csvPath --use_dynamic_liquidity --no_chart --config $config2
    Write-Host ""
}

Write-Host "========================================"
Write-Host "All done. Group 1 + Group 2 ($($dates.Count) x 2 dates)." -ForegroundColor Green
