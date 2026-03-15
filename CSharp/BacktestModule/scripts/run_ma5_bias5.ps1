# run_ma5_bias5.ps1
# MA5 bias threshold backtest: screening_results.csv x 263 dates
# Config: Bo_v2_modeC_screening.yaml + --ma5_bias5 flag
# Output: D:\C#_backtest\MA5_bias5

$ErrorActionPreference = "Continue"
$projectDir = "D:\03_預估量相關資量\CSharp\BacktestModule"
$outputPath = "D:\C#_backtest\MA5_bias5"
$configFile = "configs/Bo_v2_modeC_screening.yaml"
$screeningFile = "screening_results.csv"

# Ensure output directory exists
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

# Get unique dates from screening CSV
$dates = Get-Content "$projectDir\$screeningFile" |
    Select-Object -Skip 1 |
    ForEach-Object { ($_ -split ',')[0].Trim() } |
    Sort-Object -Unique

$totalDates = $dates.Count
$startTime = Get-Date

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "MA5 Bias5 Threshold Backtest" -ForegroundColor Cyan
Write-Host "Config: $configFile + --ma5_bias5" -ForegroundColor Cyan
Write-Host "Screening: $screeningFile" -ForegroundColor Cyan
Write-Host "Output: $outputPath" -ForegroundColor Cyan
Write-Host "Total dates: $totalDates" -ForegroundColor Cyan
Write-Host "Start time: $startTime" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan

$dateIdx = 0
foreach ($date in $dates) {
    $dateIdx++
    $elapsed = (Get-Date) - $startTime
    $pct = [math]::Round($dateIdx / $totalDates * 100, 1)

    if ($dateIdx -gt 1) {
        $avgPerDate = $elapsed.TotalSeconds / ($dateIdx - 1)
        $remaining = [math]::Round($avgPerDate * ($totalDates - $dateIdx + 1) / 60, 1)
        Write-Host "[$dateIdx/$totalDates] ($pct%) Date: $date | ETA: ${remaining}min" -ForegroundColor Yellow
    } else {
        Write-Host "[$dateIdx/$totalDates] ($pct%) Date: $date" -ForegroundColor Yellow
    }

    dotnet run --project $projectDir -- `
        --mode batch `
        --date $date `
        --use_screening `
        --screening_file "$projectDir\$screeningFile" `
        --use_dynamic_liquidity `
        --ma5_bias5 `
        --no_chart `
        --config "$projectDir\$configFile" `
        --output_path $outputPath
}

$endTime = Get-Date
$totalElapsed = $endTime - $startTime

Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host "Backtest Complete!" -ForegroundColor Green
Write-Host "Total time: $([math]::Round($totalElapsed.TotalMinutes, 1)) minutes" -ForegroundColor Green
Write-Host "Output: $outputPath" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
