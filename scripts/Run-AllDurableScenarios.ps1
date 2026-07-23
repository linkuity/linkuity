<#
.SYNOPSIS
    Run every durable Linkuity MDM scenario under samples/durable and fail if any fails.

.DESCRIPTION
    Discovers each samples/durable/<name>/scenario.json and runs it through
    Run-DurableScenario.ps1. Exits non-zero if any scenario reports a failed check.

    This is the CI regression guard for the durable sample suite: these scenarios
    are configuration + data only (no unit-test coverage), so a matching-engine or
    profile change can silently break them. Running them in CI catches that.

.PARAMETER Backend
    Storage backend passed through to each scenario: 'File' (default) or 'Postgres'.
#>
[CmdletBinding()]
param(
    [ValidateSet('File','Postgres')]
    [string]$Backend = 'File'
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$runner = Join-Path $PSScriptRoot "Run-DurableScenario.ps1"
$durableRoot = Join-Path $repoRoot "samples/durable"

$scenarios = Get-ChildItem -LiteralPath $durableRoot -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "scenario.json") } |
    Sort-Object Name

if ($scenarios.Count -eq 0) {
    Write-Host "No durable scenarios found under $durableRoot." -ForegroundColor Red
    exit 1
}

$failed = @()
foreach ($s in $scenarios) {
    Write-Host ""
    Write-Host "================ $($s.Name) ================" -ForegroundColor Cyan
    & pwsh -NoProfile -File $runner -ScenarioPath $s.FullName -Backend $Backend
    if ($LASTEXITCODE -ne 0) { $failed += $s.Name }
}

Write-Host ""
if ($failed.Count -gt 0) {
    Write-Host "FAILED durable scenarios ($($failed.Count)/$($scenarios.Count)): $($failed -join ', ')" -ForegroundColor Red
    exit 1
}

Write-Host "All $($scenarios.Count) durable scenarios passed." -ForegroundColor Green
exit 0
