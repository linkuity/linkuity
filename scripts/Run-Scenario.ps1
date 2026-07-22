<#
.SYNOPSIS
    Run a Linkuity sample scenario end-to-end through the local CLI.

.DESCRIPTION
    Runs sample.csv + *.profile.json (+ optional *.merge.json) through the local
    Linkuity CLI, writes golden records CSV and (optionally) the Neo4j export,
    then verifies the result against expectations.json if present.

    Designed to work with any scenario folder that contains:
      - sample.csv          (required)  the input data
      - *.profile.json       (required)  matching profile, resolved by convention
      - *.merge.json          (optional)  merge policy, resolved by convention
      - expectations.json    (optional)  assertions to verify the output

    The HTTP API's `/run` contract (multipart `profile` + `merge-policy` + `file`) is
    covered by RunEndpointParityTests, so this harness only drives the CLI.

.PARAMETER ScenarioPath
    Path to the scenario folder. Required.

.PARAMETER Mode
    Runtime mode. "Cli" is the only supported value (kept for call-site
    compatibility; the retired API-driven mode was removed since RunEndpointParityTests
    already covers the `/run` contract).

.PARAMETER OutputPath
    Directory where output files are written. Defaults to <ScenarioPath>\output.

.PARAMETER SkipNeo4jExport
    If set, skips writing and extracting the Neo4j export ZIP.

.EXAMPLE
    .\scripts\Run-Scenario.ps1 -ScenarioPath samples\people-multi-source
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioPath,

    [ValidateSet("Cli")]
    [string]$Mode = "Cli",

    [string]$OutputPath,

    [switch]$SkipNeo4jExport
)

$ErrorActionPreference = "Stop"

function Write-Step {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Yellow
}

function Write-Info {
    param([string]$Message)
    Write-Host "      $Message" -ForegroundColor Gray
}

function Write-Pass {
    param([string]$Message)
    Write-Host "  PASS  $Message" -ForegroundColor Green
}

function Write-Fail {
    param([string]$Message)
    Write-Host "  FAIL  $Message" -ForegroundColor Red
}

# --- Resolve and validate paths ---

$ScenarioPath = (Resolve-Path -LiteralPath $ScenarioPath).Path
$csvPath = Join-Path $ScenarioPath "sample.csv"
$expectationsPath = Join-Path $ScenarioPath "expectations.json"

if (-not (Test-Path -LiteralPath $csvPath)) {
    throw "sample.csv not found in $ScenarioPath"
}

if (-not $OutputPath) {
    $OutputPath = Join-Path $ScenarioPath "output"
}
if (-not (Test-Path -LiteralPath $OutputPath)) {
    New-Item -ItemType Directory -Path $OutputPath | Out-Null
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$cliProjectPath = Join-Path $repoRoot "src\Linkuity.Cli"

Write-Host "Scenario:  $ScenarioPath" -ForegroundColor Cyan
Write-Host "Mode:      $Mode" -ForegroundColor Cyan
Write-Host "Output:    $OutputPath" -ForegroundColor Cyan
Write-Host ""

Write-Step "[1/2] Running local CLI..."
$profilePath = (Get-ChildItem -Path $ScenarioPath -Filter *.profile.json | Select-Object -First 1).FullName
if (-not $profilePath) {
    throw "No *.profile.json found in $ScenarioPath"
}
$mergeFile = Get-ChildItem -Path $ScenarioPath -Filter *.merge.json | Select-Object -First 1

$cliArgs = @(
    "run",
    "--project", $cliProjectPath,
    "--",
    "run",
    "--input", $csvPath,
    "--profile", $profilePath,
    "--output", $OutputPath
)
if ($mergeFile) { $cliArgs += @("--merge-policy", $mergeFile.FullName) }
if (-not $SkipNeo4jExport) {
    $cliArgs += "--neo4j-export"
}

& dotnet @cliArgs
if ($LASTEXITCODE -ne 0) {
    throw "CLI scenario run failed with exit code $LASTEXITCODE"
}

$goldenRecordsPath = Join-Path $OutputPath "golden-records.csv"
if (-not (Test-Path -LiteralPath $goldenRecordsPath)) {
    throw "CLI completed but did not write $goldenRecordsPath"
}

if (-not $SkipNeo4jExport) {
    $neo4jZipPath = Join-Path $OutputPath "neo4j-export.zip"
    if (-not (Test-Path -LiteralPath $neo4jZipPath)) {
        throw "CLI completed but did not write $neo4jZipPath"
    }

    $neo4jExtractPath = Join-Path $OutputPath "neo4j-export"
    if (Test-Path -LiteralPath $neo4jExtractPath) {
        Remove-Item -Recurse -Force -LiteralPath $neo4jExtractPath
    }
    Expand-Archive -LiteralPath $neo4jZipPath -DestinationPath $neo4jExtractPath
    Write-Info "neo4j export  -> $neo4jExtractPath"
}

Write-Step "[2/2] Loading outputs..."
$goldenRecords = @(Import-Csv -LiteralPath $goldenRecordsPath)
$inputRecords = @(Import-Csv -LiteralPath $csvPath)
Write-Host ""
Write-Host "CLI scenario completed." -ForegroundColor Green
Write-Host "Input records:   $($inputRecords.Count)" -ForegroundColor Cyan
Write-Host "Golden records:  $($goldenRecords.Count)" -ForegroundColor Cyan
Write-Host ""

# --- Expectations check (optional) ---

if (-not (Test-Path -LiteralPath $expectationsPath)) {
    Write-Host "(no expectations.json -- skipping verification)" -ForegroundColor DarkGray
    exit 0
}

Write-Host "Verifying against expectations.json:" -ForegroundColor Yellow
$expectations = Get-Content -Raw -LiteralPath $expectationsPath | ConvertFrom-Json

$passed = 0
$failed = 0

# Golden record count
if ($null -ne $expectations.goldenRecordCount) {
    if ($goldenRecords.Count -eq $expectations.goldenRecordCount) {
        Write-Pass "golden record count = $($goldenRecords.Count)"
        $passed++
    } else {
        Write-Fail "golden record count: expected $($expectations.goldenRecordCount), got $($goldenRecords.Count)"
        $failed++
    }
}

# Neo4j export non-empty checks
if ($null -ne $expectations.neo4jExports -and $null -ne $expectations.neo4jExports.nonEmpty) {
    if ($SkipNeo4jExport) {
        Write-Fail "neo4jExports check requested but -SkipNeo4jExport was passed"
        $failed++
    } else {
        $neo4jExtractPath = Join-Path $OutputPath "neo4j-export"
        foreach ($fileName in @($expectations.neo4jExports.nonEmpty)) {
            $filePath = Join-Path $neo4jExtractPath $fileName
            if (-not (Test-Path -LiteralPath $filePath)) {
                Write-Fail "neo4j export file '$fileName' not found in $neo4jExtractPath"
                $failed++
                continue
            }
            $rows = @(Import-Csv -LiteralPath $filePath)
            if ($rows.Count -gt 0) {
                Write-Pass "neo4j export '$fileName' has $($rows.Count) data row(s)"
                $passed++
            } else {
                Write-Fail "neo4j export '$fileName' is header-only (expected at least 1 data row)"
                $failed++
            }
        }
    }
}

# Per-cluster checks
foreach ($cluster in @($expectations.clusters)) {
    $expectedSorted = (@($cluster.members) | Sort-Object) -join "|"

    $matches = @($goldenRecords | Where-Object {
        $actualSorted = (($_.member_ids -split '\|') | Sort-Object) -join "|"
        $actualSorted -eq $expectedSorted
    })

    if ($matches.Count -eq 0) {
        Write-Fail "cluster '$($cluster.name)': no golden record with members [$($cluster.members -join ', ')]"
        $failed++
        continue
    }
    if ($matches.Count -gt 1) {
        Write-Fail "cluster '$($cluster.name)': $($matches.Count) golden records have members [$($cluster.members -join ', ')]"
        $failed++
        continue
    }

    Write-Pass "cluster '$($cluster.name)' = [$($cluster.members -join ', ')]"
    $passed++

    # Expected field values
    if ($null -ne $cluster.expectedFields) {
        $matched = $matches[0]
        foreach ($prop in $cluster.expectedFields.PSObject.Properties) {
            $field = $prop.Name
            $expected = $prop.Value
            $actual = $matched.$field
            if ($actual -eq $expected) {
                Write-Pass "  $($cluster.name).$field = '$actual'"
                $passed++
            } else {
                Write-Fail "  $($cluster.name).${field}: expected '$expected', got '$actual'"
                $failed++
            }
        }
    }
}

Write-Host ""
if ($failed -eq 0) {
    Write-Host "All $passed checks passed." -ForegroundColor Green
    exit 0
} else {
    Write-Host "$failed of $($passed + $failed) checks failed." -ForegroundColor Red
    exit 1
}
