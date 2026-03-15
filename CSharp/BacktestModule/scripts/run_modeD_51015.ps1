# Mode D version C (5m-10m-15m) screening backtest
$ErrorActionPreference = "Stop"
Set-Location "$PSScriptRoot/.."

$outputPath = "D:\C#_backtest\grid_results\1.2% stopLoss\modeD_51015"
if (-not (Test-Path $outputPath)) {
    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
}

$csv = Import-Csv "screening_results.csv"
$dates = $csv | ForEach-Object { $_.date } | Sort-Object -Unique
Write-Host "Found $($dates.Count) unique dates"

foreach ($date in $dates) {
    Write-Host "[C] Running date: $date"
    dotnet run --configuration Release -- `
        --mode batch `
        --date $date `
        --use_screening `
        --use_dynamic_liquidity `
        --config configs/Bo_v2_modeD_51015.yaml `
        --output_path $outputPath `
        --no_chart
}

Write-Host ""
Write-Host "Mode D (5m-10m-15m) backtest complete!"
Write-Host "Output: $outputPath"
