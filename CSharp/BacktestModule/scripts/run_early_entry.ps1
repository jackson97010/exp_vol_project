# run_early_entry.ps1
# Early entry backtest grid:
#   2 entry times (0905, 0903) x 2 surge thresholds (2.5%, 2.0%) x 3 exit modes = 12 combos
#
# 0903 version: 09:03~09:05 only massive matching (skip ratio), 09:05+ normal
# 0905 version: 09:05~09:30 normal (ratio required from start)
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts\run_early_entry.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\run_early_entry.ps1 -CsvFile "my_stocks.csv"

param(
    [string]$CsvFile = "bo_list.csv"
)

$ErrorActionPreference = "Continue"
Set-Location "$PSScriptRoot/.."

$baseOutput = "D:\C#_backtest\early_entry"

# Base config files (one per exit mode)
$configs = @(
    @{ Name = "modeC";    File = "configs/Bo_v2_early_modeC.yaml" },
    @{ Name = "modeD_135"; File = "configs/Bo_v2_early_modeD_135.yaml" },
    @{ Name = "modeD_357"; File = "configs/Bo_v2_early_modeD_357.yaml" }
)

# Variant grid: entry start time x surge threshold
$variants = @(
    @{ Label = "0905_surge2.5"; StartTime = "09:05:00"; SurgeThreshold = 2.5; RatioSkip = "" },
    @{ Label = "0905_surge2.0"; StartTime = "09:05:00"; SurgeThreshold = 2.0; RatioSkip = "" },
    @{ Label = "0903_surge2.5"; StartTime = "09:03:00"; SurgeThreshold = 2.5; RatioSkip = "09:05:00" },
    @{ Label = "0903_surge2.0"; StartTime = "09:03:00"; SurgeThreshold = 2.0; RatioSkip = "09:05:00" }
)

# Resolve CSV path
if (-not (Test-Path $CsvFile)) {
    Write-Host "[ERROR] CSV file not found: $CsvFile" -ForegroundColor Red
    exit 1
}

# Get unique dates from CSV
$dates = @()
$lines = Get-Content $CsvFile
for ($i = 1; $i -lt $lines.Count; $i++) {
    $fields = $lines[$i] -split ","
    if ($fields.Count -ge 2 -and $fields[0].Trim() -match '^\d{4}-\d{2}-\d{2}$') {
        $dates += $fields[0].Trim()
    }
}
$uniqueDates = $dates | Sort-Object -Unique

if ($uniqueDates.Count -eq 0) {
    Write-Host "[ERROR] No valid dates found in $CsvFile" -ForegroundColor Red
    exit 1
}

$totalCombos = $variants.Count * $configs.Count
$globalStart = Get-Date

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  Early Entry Backtest Grid" -ForegroundColor Cyan
Write-Host "  CSV: $CsvFile ($($uniqueDates.Count) dates)" -ForegroundColor Cyan
Write-Host "  Range: $($uniqueDates[0]) ~ $($uniqueDates[-1])" -ForegroundColor Cyan
Write-Host "  Variants: $($variants.Count) (0905/0903 x surge2.5/2.0)" -ForegroundColor Cyan
Write-Host "  Exit modes: $($configs.Count) (modeC, modeD_135, modeD_357)" -ForegroundColor Cyan
Write-Host "  Total combos: $totalCombos" -ForegroundColor Cyan
Write-Host "  Output: $baseOutput" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan

# Save original YAML contents for restore
$originals = @{}
foreach ($cfg in $configs) {
    $originals[$cfg.File] = Get-Content $cfg.File -Raw
}

$comboIndex = 0

try {
    foreach ($variant in $variants) {
        foreach ($cfg in $configs) {
            $comboIndex++
            $outputPath = "$baseOutput\$($variant.Label)\$($cfg.Name)"

            Write-Host ""
            Write-Host "==========================================" -ForegroundColor Yellow
            Write-Host "  [$comboIndex/$totalCombos] $($variant.Label) / $($cfg.Name)" -ForegroundColor Yellow
            Write-Host "  Entry: $($variant.StartTime)~09:30" -ForegroundColor Yellow
            Write-Host "  Surge filter: 3min < $($variant.SurgeThreshold)%" -ForegroundColor Yellow
            if ($variant.RatioSkip) {
                Write-Host "  Ratio skip before: $($variant.RatioSkip) (massive matching only)" -ForegroundColor Yellow
            }
            Write-Host "  Output: $outputPath" -ForegroundColor Yellow
            Write-Host "==========================================" -ForegroundColor Yellow

            # Modify YAML in-place
            $yaml = $originals[$cfg.File]

            # Update entry_start_time
            $yaml = $yaml -replace "(entry_start_time:\s*)'[^']*'", "`${1}'$($variant.StartTime)'"

            # Update interval_pct_threshold
            $yaml = $yaml -replace "(interval_pct_threshold:\s*)[\d.]+", "`${1}$($variant.SurgeThreshold)"

            # Add or update ratio_skip_before_time
            if ($variant.RatioSkip) {
                if ($yaml -match 'ratio_skip_before_time:') {
                    $yaml = $yaml -replace "(ratio_skip_before_time:\s*).*", "`${1}'$($variant.RatioSkip)'"
                } else {
                    $yaml = $yaml -replace "(ratio_entry_threshold:\s*\d+)", "`$1`n  ratio_skip_before_time: '$($variant.RatioSkip)'"
                }
            } else {
                # Remove ratio_skip_before_time if present (0905 variant doesn't need it)
                if ($yaml -match 'ratio_skip_before_time:') {
                    $yaml = $yaml -replace "(ratio_skip_before_time:\s*).*", "`${1}''"
                }
            }

            # Write modified YAML
            $yaml | Set-Content $cfg.File -NoNewline

            $comboStart = Get-Date
            $completed = 0
            $failed = 0

            foreach ($date in $uniqueDates) {
                $completed++
                if ($completed % 20 -eq 1 -or $completed -eq $uniqueDates.Count) {
                    $elapsed = (Get-Date) - $comboStart
                    if ($completed -gt 1) {
                        $avgPerDate = $elapsed.TotalSeconds / ($completed - 1)
                        $remaining = [math]::Round($avgPerDate * ($uniqueDates.Count - $completed + 1) / 60, 1)
                        Write-Host "  [$completed/$($uniqueDates.Count)] $date | ETA: ${remaining}min" -ForegroundColor Cyan
                    } else {
                        Write-Host "  [$completed/$($uniqueDates.Count)] $date" -ForegroundColor Cyan
                    }
                }

                dotnet run --configuration Release -- `
                    --mode batch `
                    --date $date `
                    --use_screening `
                    --screening_file $CsvFile `
                    --use_dynamic_liquidity `
                    --config $($cfg.File) `
                    --output_path $outputPath `
                    --no_chart

                if ($LASTEXITCODE -ne 0) {
                    $failed++
                }
            }

            $comboElapsed = (Get-Date) - $comboStart
            Write-Host "  Done: $completed dates ($failed failed) in $($comboElapsed.ToString('hh\:mm\:ss'))" -ForegroundColor Green
        }
    }
} finally {
    # Always restore original YAML files
    foreach ($cfg in $configs) {
        $originals[$cfg.File] | Set-Content $cfg.File -NoNewline
    }
    Write-Host ""
    Write-Host "[INFO] YAML configs restored to original." -ForegroundColor DarkGray
}

$totalElapsed = (Get-Date) - $globalStart
Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host "  All $totalCombos combos complete!" -ForegroundColor Green
Write-Host "  Total elapsed: $($totalElapsed.ToString('hh\:mm\:ss'))" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host "Results:"
foreach ($variant in $variants) {
    foreach ($cfg in $configs) {
        $dir = "$baseOutput\$($variant.Label)\$($cfg.Name)"
        if (Test-Path $dir) {
            $csvCount = (Get-ChildItem "$dir\*\*trade_details*" -Recurse -ErrorAction SilentlyContinue).Count
            Write-Host "  $($variant.Label)/$($cfg.Name): $csvCount trade files"
        } else {
            Write-Host "  $($variant.Label)/$($cfg.Name): (no output)"
        }
    }
}
