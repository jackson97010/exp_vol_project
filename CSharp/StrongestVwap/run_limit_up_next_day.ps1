$dates = @(
    "2026-01-02","2026-01-05","2026-01-06","2026-01-07","2026-01-08",
    "2026-01-09","2026-01-12","2026-01-13","2026-01-14","2026-01-15",
    "2026-01-16","2026-01-19","2026-01-20","2026-01-21","2026-01-22",
    "2026-01-23","2026-01-26","2026-01-27","2026-01-28","2026-01-29",
    "2026-01-30"
)
$dateArgs = $dates -join " "

$screeningCsv = "D:\C#_backtest\screen_limit_up_next_day.csv"
$tickData = "D:\feature_data\feature"
$baseOutput = "D:\C#_backtest\LimitUp_NextDay"

# 2 configs: bypass group screening, extended (0905) vs massive (0909)
$configs = @(
    @{ Name="C1_bypass_extended_0905"; Config="configs/C1_bypass_extended_0905.yaml" },
    @{ Name="C2_bypass_massive_0909"; Config="configs/C2_bypass_massive_0909.yaml" }
)

foreach ($c in $configs) {
    $outputPath = "$baseOutput\$($c.Name)"
    Write-Host ""
    Write-Host "============================================================"
    Write-Host "Running: $($c.Name)"
    Write-Host "Config:  $($c.Config)"
    Write-Host "Output:  $outputPath"
    Write-Host "Dates:   $($dates.Count)"
    Write-Host "============================================================"

    $cmd = "dotnet run -- --mode batch --dates $dateArgs --config `"$($c.Config)`" --screening_csv `"$screeningCsv`" --tick_data `"$tickData`" --output_path `"$outputPath`""
    Invoke-Expression $cmd
}

Write-Host ""
Write-Host "============================================================"
Write-Host "All 2 configs completed!"
Write-Host "Results:"
Write-Host "  C1 (extended 0905): $baseOutput\C1_bypass_extended_0905"
Write-Host "  C2 (massive 0909):  $baseOutput\C2_bypass_massive_0909"
Write-Host "============================================================"
