$dates = @(
    "2025-10-01","2025-10-02","2025-10-03","2025-10-07","2025-10-08",
    "2025-10-09","2025-10-13","2025-10-14","2025-10-15","2025-10-16",
    "2025-10-17","2025-10-20","2025-10-21","2025-10-22","2025-10-23",
    "2025-10-27","2025-10-28","2025-10-29","2025-10-30","2025-10-31",
    "2025-11-03","2025-11-04","2025-11-05","2025-11-06","2025-11-07",
    "2025-11-10","2025-11-11","2025-11-12","2025-11-13","2025-11-14",
    "2025-11-17","2025-11-18","2025-11-19","2025-11-20","2025-11-21",
    "2025-11-24","2025-11-25","2025-11-26","2025-11-27","2025-11-28",
    "2025-12-01","2025-12-02","2025-12-03","2025-12-04","2025-12-05",
    "2025-12-08","2025-12-09","2025-12-10","2025-12-11","2025-12-12",
    "2025-12-15","2025-12-16","2025-12-17","2025-12-18","2025-12-19",
    "2025-12-22","2025-12-23","2025-12-24","2025-12-26",
    "2025-12-29","2025-12-30","2025-12-31",
    "2026-01-02","2026-01-05","2026-01-06","2026-01-07","2026-01-08",
    "2026-01-09","2026-01-12","2026-01-13","2026-01-14","2026-01-15",
    "2026-01-16","2026-01-19","2026-01-20","2026-01-21","2026-01-22",
    "2026-01-23","2026-01-26","2026-01-27","2026-01-28","2026-01-29",
    "2026-01-30"
)
$dateArgs = $dates -join " "

$configs = @(
    @{ Name="A_tp120_top5";        Config="configs/mode_e_group_screening.yaml" },
    @{ Name="B_tp080_top5";        Config="configs/mode_e_B.yaml" },
    @{ Name="C_tp120_top3";        Config="configs/mode_e_C.yaml" },
    @{ Name="D_tp080_top3";        Config="configs/mode_e_D.yaml" },
    @{ Name="E_tp120_top3_rankExit"; Config="configs/mode_e_E.yaml" },
    @{ Name="F_tp080_top3_rankExit"; Config="configs/mode_e_F.yaml" }
)

$screeningCsv = "C:\Users\User\Documents\_02_bt\Backtest_tick_module\screening_results.csv"
$tickData = "D:\feature_data\feature"
$baseOutput = "D:\C#_backtest\modeE_grid_full"

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
Write-Host "All 6 combinations completed! ($($dates.Count) dates each)"
Write-Host "Results in: $baseOutput"
Write-Host "============================================================"
