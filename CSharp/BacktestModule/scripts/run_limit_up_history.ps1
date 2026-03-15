# run_limit_up_history.ps1
# Batch backtest using limit_up_history.csv with ratio_entry_threshold=1
# Config: Bo_v2_modeC_limit_up_entry.yaml
# Output: D:\C#_backtest\limit_up_entry

$ErrorActionPreference = "Continue"
Set-Location "$PSScriptRoot/.."
$csvPath = ".\limit_up_history.csv"
$configPath = "configs/Bo_v2_modeC_limit_up_entry.yaml"
$outputPath = "D:\C#_backtest\limit_up_entry"

# Get unique dates from CSV (no -Encoding for compatibility)
$dates = @()
$lines = Get-Content $csvPath
for ($i = 1; $i -lt $lines.Count; $i++) {
    $fields = $lines[$i] -split ","
    if ($fields.Count -ge 2 -and $fields[0] -match '^\d{4}-\d{2}-\d{2}$') {
        $dates += $fields[0]
    }
}
$uniqueDates = $dates | Sort-Object -Unique
Write-Host "Total unique dates: $($uniqueDates.Count)"
if ($uniqueDates.Count -gt 0) {
    Write-Host "Date range: $($uniqueDates[0]) ~ $($uniqueDates[-1])"
}
Write-Host "Output path: $outputPath"
Write-Host "Config: $configPath"
Write-Host "ratio_entry_threshold: 1"
Write-Host "=========================================="

$startTime = Get-Date
$completed = 0
$failed = 0

foreach ($date in $uniqueDates) {
    $completed++
    Write-Host "[$completed/$($uniqueDates.Count)] Processing date: $date" -ForegroundColor Cyan

    dotnet run --project . -- --mode batch --date $date --use_screening --screening_file $csvPath --use_dynamic_liquidity --config $configPath --output_path $outputPath --no_chart

    if ($LASTEXITCODE -ne 0) {
        Write-Host "  [WARN] Date $date exited with code $LASTEXITCODE" -ForegroundColor Yellow
        $failed++
    }
}

$elapsed = (Get-Date) - $startTime
Write-Host "=========================================="
Write-Host "Completed: $completed dates ($failed failed)"
Write-Host "Elapsed: $($elapsed.ToString('hh\:mm\:ss'))"
Write-Host "Output: $outputPath"
