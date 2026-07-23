<#
.SYNOPSIS
  Resolve real companies across SEC EDGAR + GLEIF with Linkuity, then validate.
.DESCRIPTION
  By default runs offline against the committed run/companies.csv. With -Refresh it
  re-acquires live from SEC + GLEIF and rebuilds the input first. Runs `linkuity run`
  and prints an honest scorecard against the held-out CIK/LEI ground truth.
.PARAMETER Refresh
  Re-acquire live data and rebuild run/companies.csv before running.
.PARAMETER Neo4j
  Also emit neo4j-export.zip (graph of golden orgs + source records).
.PARAMETER UserAgent
  SEC User-Agent, required only with -Refresh.
.EXAMPLE
  ./run-demo.ps1
.EXAMPLE
  ./run-demo.ps1 -Refresh -UserAgent "Linkuity-demo you@example.com" -Neo4j
#>
[CmdletBinding()]
param(
    [switch]$Refresh,
    [switch]$Neo4j,
    [string]$UserAgent
)

$ErrorActionPreference = "Stop"
function Write-Step { param($m) Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Fail { param($m) Write-Host "!!! $m" -ForegroundColor Red }

$here = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $here '..\..')).Path
$cli = Join-Path $repoRoot 'src\Linkuity.Cli'
$inputCsv = Join-Path $here 'run\companies.csv'
$profile = Join-Path $here 'run\company.profile.json'
$merge = Join-Path $here 'run\company.merge.json'
$output = Join-Path $here 'output'

if ($Refresh) {
    if (-not $UserAgent) { Write-Fail "-Refresh requires -UserAgent for SEC access."; exit 2 }
    Write-Step "Re-acquiring live SEC + GLEIF data"
    & (Join-Path $here 'acquire\Get-Sources.ps1') -UserAgent $UserAgent
    Write-Step "Rebuilding input from cache"
    & (Join-Path $here 'prepare\Build-Input.ps1')
}

Write-Step "Running Linkuity"
$runArgs = @('run','--input',$inputCsv,'--profile',$profile,'--merge-policy',$merge,'--output',$output)
if ($Neo4j) { $runArgs += '--neo4j-export' }
dotnet run --project $cli -- @runArgs
if ($LASTEXITCODE -ne 0) { Write-Fail "linkuity run failed ($LASTEXITCODE)"; exit $LASTEXITCODE }

Write-Step "Validating against held-out ground truth"
& (Join-Path $here 'validate\Test-Resolution.ps1')
exit $LASTEXITCODE
