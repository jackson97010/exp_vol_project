# run_limit_up_ratio0.ps1
# ratio_entry_threshold=0, entry 09:09

$ErrorActionPreference = "Continue"
Set-Location "$PSScriptRoot/.."
$csvPath = ".\limit_up_history.csv"
$configPath = "configs/Bo_v2_modeC_limit_up_entry.yaml"
$outputPath = "D:\C#_backtest\limit_up_ratio0"

$dates = @()
$lines = Get-Content $csvPath
for ($i = 1; $i -lt $lines.Count; $i++) {
    $fields = $lines[$i] -split ","
    if ($fields.Count -ge 2 -and $fields[0] -match '^\d{4}-\d{2}-\d{2}$') {
        $dates += $fields[0]
    }
}
$uniqueDates = $dates | Sort-Object -Unique
Write-Host "=== ratio=0, entry=0909 ==="
Write-Host "Total dates: $($uniqueDates.Count), Output: $outputPath"

$startTime = Get-Date
$completed = 0

foreach ($date in $uniqueDates) {
    $completed++
    Write-Host "[$completed/$($uniqueDates.Count)] $date"
    dotnet run --project . -- --mode batch --date $date --use_screening --screening_file $csvPath --use_dynamic_liquidity --config $configPath --output_path $outputPath --no_chart
}

$elapsed = (Get-Date) - $startTime
Write-Host "=== Done: $completed dates, Elapsed: $($elapsed.ToString('hh\:mm\:ss')) ==="
