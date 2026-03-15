# Run ModeC 7-9% backtest with different entry start times: 0908, 0907, 0906, 0905
# Each config uses a separate output folder

$ErrorActionPreference = "Continue"
Set-Location "$PSScriptRoot/.."
$csvPath = "C:\Users\User\Documents\_02_bt\Backtest_tick_module\screening_results.csv"

# Extract unique dates
$dates = Get-Content $csvPath | Select-Object -Skip 1 | ForEach-Object {
    $_.Split(',')[0].Trim()
} | Where-Object { $_ -match '^\d{4}-\d{2}-\d{2}$' } | Sort-Object -Unique

$configs = @(
    @{ Name = "0908"; File = "configs/Bo_v2_modeC_0908.yaml"; Output = "D:\C#_backtest\ModeC_7to9_0908" },
    @{ Name = "0907"; File = "configs/Bo_v2_modeC_0907.yaml"; Output = "D:\C#_backtest\ModeC_7to9_0907" },
    @{ Name = "0906"; File = "configs/Bo_v2_modeC_0906.yaml"; Output = "D:\C#_backtest\ModeC_7to9_0906" },
    @{ Name = "0905"; File = "configs/Bo_v2_modeC_0905.yaml"; Output = "D:\C#_backtest\ModeC_7to9_0905" }
)

foreach ($cfg in $configs) {
    Write-Host "========================================" -ForegroundColor Yellow
    Write-Host "Starting: entry_start_time = $($cfg.Name)" -ForegroundColor Yellow
    Write-Host "Config: $($cfg.File)" -ForegroundColor Yellow
    Write-Host "Output: $($cfg.Output)" -ForegroundColor Yellow
    Write-Host "Total dates: $($dates.Count)"
    Write-Host "========================================" -ForegroundColor Yellow

    $completed = 0
    foreach ($date in $dates) {
        $completed++
        Write-Host "[$($cfg.Name)] [$completed/$($dates.Count)] Date: $date" -ForegroundColor Cyan
        dotnet run -- --mode batch --date $date --use_screening --use_dynamic_liquidity --config $($cfg.File)
        Write-Host ""
    }

    Write-Host "[$($cfg.Name)] Completed all $($dates.Count) dates." -ForegroundColor Green
    Write-Host ""
}

Write-Host "========================================" -ForegroundColor Green
Write-Host "All 4 entry time variants completed." -ForegroundColor Green
