# run_limit_up_ratio1_times.ps1
# ratio_entry_threshold=1 x 4 entry times (0908/0907/0906/0905)
# NOTE: YAML must have ratio_entry_threshold: 1 before running this

$ErrorActionPreference = "Continue"
Set-Location "$PSScriptRoot/.."
$csvPath = ".\limit_up_history.csv"
$configPath = "configs/Bo_v2_modeC_limit_up_entry.yaml"

$entryTimes = @(
    @{ time = "09:08:00"; suffix = "0908" },
    @{ time = "09:07:00"; suffix = "0907" },
    @{ time = "09:06:00"; suffix = "0906" },
    @{ time = "09:05:00"; suffix = "0905" }
)

$dates = @()
$lines = Get-Content $csvPath
for ($i = 1; $i -lt $lines.Count; $i++) {
    $fields = $lines[$i] -split ","
    if ($fields.Count -ge 2 -and $fields[0] -match '^\d{4}-\d{2}-\d{2}$') {
        $dates += $fields[0]
    }
}
$uniqueDates = $dates | Sort-Object -Unique

$globalStart = Get-Date

foreach ($et in $entryTimes) {
    $outputPath = "D:\C#_backtest\limit_up_entry_$($et.suffix)"
    Write-Host "=========================================="
    Write-Host "=== ratio=1, entry=$($et.time) ==="
    Write-Host "Total dates: $($uniqueDates.Count), Output: $outputPath"
    Write-Host "=========================================="

    $startTime = Get-Date
    $completed = 0

    foreach ($date in $uniqueDates) {
        $completed++
        Write-Host "[$completed/$($uniqueDates.Count)] $date"
        dotnet run --project . -- --mode batch --date $date --use_screening --screening_file $csvPath --use_dynamic_liquidity --config $configPath --output_path $outputPath --entry_start_time $et.time --no_chart
    }

    $elapsed = (Get-Date) - $startTime
    Write-Host "=== Done: $($et.suffix), $completed dates, Elapsed: $($elapsed.ToString('hh\:mm\:ss')) ==="
}

$totalElapsed = (Get-Date) - $globalStart
Write-Host "=========================================="
Write-Host "=== All 4 entry times complete, Total: $($totalElapsed.ToString('hh\:mm\:ss')) ==="
