# Run ModeC limit-up backtest with different entry start times: 0905~0908
# Single YAML config + --entry_start_time CLI override

$ErrorActionPreference = "Continue"
Set-Location "$PSScriptRoot/.."
$configFile = "configs/Bo_v2_modeC_limitup.yaml"
$csvPath = "screen_limit_up.csv"

$dates = Get-Content $csvPath | Select-Object -Skip 1 | ForEach-Object {
    $_.Split(',')[0].Trim()
} | Where-Object { $_ -match '^\d{4}-\d{2}-\d{2}$' } | Sort-Object -Unique

$entryTimes = @(
    @{ Name = "0905"; Time = "09:05:00"; Output = "D:\C#_backtest\t-1_limit_up_0905" },
    @{ Name = "0906"; Time = "09:06:00"; Output = "D:\C#_backtest\t-1_limit_up_0906" },
    @{ Name = "0907"; Time = "09:07:00"; Output = "D:\C#_backtest\t-1_limit_up_0907" },
    @{ Name = "0908"; Time = "09:08:00"; Output = "D:\C#_backtest\t-1_limit_up_0908" }
)

foreach ($et in $entryTimes) {
    Write-Host "========================================" -ForegroundColor Yellow
    Write-Host "Starting: entry_start_time = $($et.Time)" -ForegroundColor Yellow
    Write-Host "Output: $($et.Output)" -ForegroundColor Yellow
    Write-Host "Total dates: $($dates.Count)"
    Write-Host "========================================" -ForegroundColor Yellow

    $completed = 0
    foreach ($date in $dates) {
        $completed++
        Write-Host "[$($et.Name)] [$completed/$($dates.Count)] Date: $date" -ForegroundColor Cyan
        dotnet run -- --mode batch --date $date --use_screening --screening_file $csvPath --use_dynamic_liquidity --config $configFile --entry_start_time $($et.Time) --output_path $($et.Output)
        Write-Host ""
    }

    Write-Host "[$($et.Name)] Completed all $($dates.Count) dates." -ForegroundColor Green
    Write-Host ""
}

Write-Host "========================================" -ForegroundColor Green
Write-Host "All 4 entry time variants completed." -ForegroundColor Green
