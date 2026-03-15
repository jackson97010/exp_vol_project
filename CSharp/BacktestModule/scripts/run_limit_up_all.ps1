# run_limit_up_all.ps1
# Run all 5 versions: ratio=0 (0909), ratio=1 (0909/0908/0907/0906/0905)
# Uses limit_up_history.csv, cleans output dirs before each run

$ErrorActionPreference = "Continue"
Set-Location "$PSScriptRoot/.."
$csvPath = ".\limit_up_history.csv"
$configPath = "configs/Bo_v2_modeC_limit_up_entry.yaml"

# Parse unique dates from CSV
$dates = @()
$lines = Get-Content $csvPath
for ($i = 1; $i -lt $lines.Count; $i++) {
    $fields = $lines[$i] -split ","
    if ($fields.Count -ge 2 -and $fields[0] -match '^\d{4}-\d{2}-\d{2}$') {
        $dates += $fields[0]
    }
}
$uniqueDates = $dates | Sort-Object -Unique
Write-Host "CSV: $($uniqueDates.Count) unique dates ($($uniqueDates[0]) ~ $($uniqueDates[-1]))"

# Define all runs
$runs = @(
    @{ ratio = 0; entry = "09:09:00"; suffix = "ratio0_0909"; output = "D:\C#_backtest\limit_up_ratio0" },
    @{ ratio = 1; entry = "09:09:00"; suffix = "ratio1_0909"; output = "D:\C#_backtest\limit_up_entry" },
    @{ ratio = 1; entry = "09:08:00"; suffix = "ratio1_0908"; output = "D:\C#_backtest\limit_up_entry_0908" },
    @{ ratio = 1; entry = "09:07:00"; suffix = "ratio1_0907"; output = "D:\C#_backtest\limit_up_entry_0907" },
    @{ ratio = 1; entry = "09:06:00"; suffix = "ratio1_0906"; output = "D:\C#_backtest\limit_up_entry_0906" },
    @{ ratio = 1; entry = "09:05:00"; suffix = "ratio1_0905"; output = "D:\C#_backtest\limit_up_entry_0905" }
)

$globalStart = Get-Date

foreach ($run in $runs) {
    $outputPath = $run.output
    $ratio = $run.ratio
    $entry = $run.entry
    $suffix = $run.suffix

    Write-Host ""
    Write-Host "=========================================="
    Write-Host "=== $suffix : ratio=$ratio, entry=$entry ==="
    Write-Host "=== Output: $outputPath ==="
    Write-Host "=========================================="

    # Clean output directory
    if (Test-Path $outputPath) {
        Write-Host "Cleaning output directory..."
        Remove-Item -Recurse -Force $outputPath
    }
    New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

    # Update YAML ratio_entry_threshold
    $yamlContent = Get-Content $configPath -Raw
    $yamlContent = $yamlContent -replace '(ratio_entry_threshold:\s*)\d+', "`${1}$ratio"
    Set-Content -Path $configPath -Value $yamlContent -NoNewline

    $startTime = Get-Date
    $completed = 0

    foreach ($date in $uniqueDates) {
        $completed++
        Write-Host "[$completed/$($uniqueDates.Count)] $date"

        $entryArg = @()
        if ($entry -ne "09:09:00") {
            $entryArg = @("--entry_start_time", $entry)
        }

        & dotnet run --project . -- --mode batch --date $date --use_screening --screening_file $csvPath --use_dynamic_liquidity --config $configPath --output_path $outputPath @entryArg --no_chart
    }

    $elapsed = (Get-Date) - $startTime
    Write-Host "=== Done: $suffix, $completed dates, Elapsed: $($elapsed.ToString('hh\:mm\:ss')) ==="
}

# Restore YAML to ratio=1
$yamlContent = Get-Content $configPath -Raw
$yamlContent = $yamlContent -replace '(ratio_entry_threshold:\s*)\d+', '${1}1'
Set-Content -Path $configPath -Value $yamlContent -NoNewline

$totalElapsed = (Get-Date) - $globalStart
Write-Host ""
Write-Host "=========================================="
Write-Host "=== ALL DONE. Total: $($totalElapsed.ToString('hh\:mm\:ss')) ==="
Write-Host "=========================================="
