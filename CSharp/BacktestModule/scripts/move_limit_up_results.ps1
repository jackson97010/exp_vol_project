# move_limit_up_results.ps1
# Move limit_up grid results to organized folder structure
Set-Location "$PSScriptRoot/.."

$baseOutput = "D:\C#_backtest\grid_results\limit_up"
$ticksList = @(3, 4, 5, 6)
$windowList = @(1, 2, 3)

foreach ($ticks in $ticksList) {
    foreach ($window in $windowList) {
        $src = "D:\C#_backtest\limit_up_sl${ticks}_mm${window}s"
        $dst = "$baseOutput\sl${ticks}_mm${window}s"

        if (Test-Path $src) {
            if (Test-Path $dst) {
                Write-Host "  [SKIP] $dst already exists" -ForegroundColor Yellow
            } else {
                New-Item -ItemType Directory -Path (Split-Path $dst -Parent) -Force | Out-Null
                Move-Item -Path $src -Destination $dst -Force
                Write-Host "  [OK] $src -> $dst" -ForegroundColor Green
            }
        } else {
            Write-Host "  [MISS] $src not found" -ForegroundColor Red
        }
    }
}

Write-Host ""
Write-Host "Done. Results in: $baseOutput"
