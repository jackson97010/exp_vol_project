# run_limit_up_grid.ps1
# Grid backtest: strategy_b_stop_loss_ticks_small (3/4/5/6) x massive_matching_window (1/2/3) = 12 combos
# Config: Bo_v2_modeC_limit_up_entry.yaml
# Data: limit_up_history.csv

$ErrorActionPreference = "Continue"
Set-Location "$PSScriptRoot/.."
$csvPath = ".\limit_up_history.csv"
$configPath = "configs/Bo_v2_modeC_limit_up_entry.yaml"
$baseOutput = "D:\C#_backtest"

# Grid parameters
$ticksList = @(3, 4, 5, 6)
$windowList = @(1, 2, 3)

# Read original YAML content
$originalYaml = Get-Content $configPath -Raw

# Get unique dates from CSV
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
Write-Host "Grid: stop_loss_ticks_small=$($ticksList -join ',') x massive_matching_window=$($windowList -join ',')"
Write-Host "Total combos: $($ticksList.Count * $windowList.Count)"
Write-Host "=========================================="

$gridStart = Get-Date
$comboIndex = 0
$totalCombos = $ticksList.Count * $windowList.Count

foreach ($ticks in $ticksList) {
    foreach ($window in $windowList) {
        $comboIndex++
        $outputPath = "$baseOutput\limit_up_sl${ticks}_mm${window}s"
        Write-Host ""
        Write-Host "=========================================="
        Write-Host "[$comboIndex/$totalCombos] stop_loss=$ticks, mm_window=${window}s"
        Write-Host "Output: $outputPath"
        Write-Host "=========================================="

        # Modify YAML: update stop_loss_ticks_small and add/update massive_matching_window
        $yaml = $originalYaml

        # Replace strategy_b_stop_loss_ticks_small
        $yaml = $yaml -replace '(strategy_b_stop_loss_ticks_small:\s*)\d+', "`${1}$ticks"

        # Replace output_path
        $escapedOutput = $outputPath -replace '\\', '/'
        $yaml = $yaml -replace '(output_path:\s*).*', "`${1}$escapedOutput"

        # Add or replace massive_matching_window (after massive_matching_amount line)
        if ($yaml -match 'massive_matching_window:') {
            $yaml = $yaml -replace '(massive_matching_window:\s*)\d+', "`${1}$window"
        } else {
            $yaml = $yaml -replace '(massive_matching_amount:\s*\d+)', "`$1`n  massive_matching_window: $window"
        }

        # Write modified YAML
        $yaml | Set-Content $configPath -NoNewline

        $comboStart = Get-Date
        $completed = 0
        $failed = 0

        foreach ($date in $uniqueDates) {
            $completed++
            if ($completed % 50 -eq 1 -or $completed -eq $uniqueDates.Count) {
                Write-Host "  [$completed/$($uniqueDates.Count)] $date" -ForegroundColor Cyan
            }

            dotnet run --project . -- --mode batch --date $date --use_screening --screening_file $csvPath --use_dynamic_liquidity --config $configPath --output_path $outputPath --no_chart 2>$null

            if ($LASTEXITCODE -ne 0) {
                $failed++
            }
        }

        $comboElapsed = (Get-Date) - $comboStart
        Write-Host "  Combo done: $completed dates ($failed failed) in $($comboElapsed.ToString('hh\:mm\:ss'))" -ForegroundColor Green
    }
}

# Restore original YAML
$originalYaml | Set-Content $configPath -NoNewline

$gridElapsed = (Get-Date) - $gridStart
Write-Host ""
Write-Host "=========================================="
Write-Host "All $totalCombos combos completed!"
Write-Host "Total elapsed: $($gridElapsed.ToString('hh\:mm\:ss'))"
Write-Host "=========================================="
Write-Host "Results:"
foreach ($ticks in $ticksList) {
    foreach ($window in $windowList) {
        $dir = "$baseOutput\limit_up_sl${ticks}_mm${window}s"
        if (Test-Path $dir) {
            $csvCount = (Get-ChildItem "$dir\*\*trade_details*" -Recurse -ErrorAction SilentlyContinue).Count
            Write-Host "  sl${ticks}_mm${window}s: $csvCount trade files"
        }
    }
}
