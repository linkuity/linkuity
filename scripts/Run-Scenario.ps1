<#
.SYNOPSIS
    Run a Linkuity sample scenario end-to-end through the local CLI or API.

.DESCRIPTION
    Runs sample.csv + match-config.json through either the local Linkuity CLI or the
    running Linkuity API, writes golden records CSV and (optionally) the Neo4j export,
    then verifies the result against expectations.json if present.

    Designed to work with any scenario folder that contains:
      - sample.csv          (required)  the input data
      - match-config.json   (required)  drop-in body for POST /jobs
      - expectations.json   (optional)  assertions to verify the output

.PARAMETER ScenarioPath
    Path to the scenario folder. Required.

.PARAMETER ApiBaseUrl
    Base URL of the Linkuity API. Defaults to http://localhost:5017.

.PARAMETER Mode
    Runtime mode. Defaults to Cli. Use Api to exercise the hosted API flow.

.PARAMETER OutputPath
    Directory where output files are written. Defaults to <ScenarioPath>\output.

.PARAMETER SkipNeo4jExport
    If set, skips downloading and extracting the Neo4j export ZIP.

.PARAMETER PollIntervalSeconds
    Seconds between status polls while waiting for the job to finish. Default 2.

.PARAMETER TimeoutSeconds
    Maximum time to wait for processing. Default 300.

.EXAMPLE
    .\scripts\Run-Scenario.ps1 -ScenarioPath samples\people-multi-source

.EXAMPLE
    .\scripts\Run-Scenario.ps1 -ScenarioPath samples\people-multi-source -Mode Api -SkipNeo4jExport
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioPath,

    [ValidateSet("Cli", "Api")]
    [string]$Mode = "Cli",

    [string]$ApiBaseUrl = "http://localhost:5017",

    [string]$OutputPath,

    [switch]$SkipNeo4jExport,

    [int]$PollIntervalSeconds = 2,

    [int]$TimeoutSeconds = 300
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
$configPath = Join-Path $ScenarioPath "match-config.json"
$expectationsPath = Join-Path $ScenarioPath "expectations.json"

if (-not (Test-Path -LiteralPath $csvPath)) {
    throw "sample.csv not found in $ScenarioPath"
}
if (-not (Test-Path -LiteralPath $configPath)) {
    throw "match-config.json not found in $ScenarioPath"
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
Write-Host "API:       $ApiBaseUrl" -ForegroundColor Cyan
Write-Host "Output:    $OutputPath" -ForegroundColor Cyan
Write-Host ""

if ($Mode -eq "Api") {
    # --- Health check ---

    try {
        Invoke-RestMethod -Uri "$ApiBaseUrl/health" -Method Get -TimeoutSec 5 | Out-Null
    } catch {
        throw "API not reachable at $ApiBaseUrl/health. Is the AppHost running? Original error: $($_.Exception.Message)"
    }

    # --- Step 1: Create job ---

    Write-Step "[1/5] Creating job..."
    $configBody = Get-Content -Raw -LiteralPath $configPath
    $job = Invoke-RestMethod -Uri "$ApiBaseUrl/jobs" -Method Post -ContentType "application/json" -Body $configBody
    $jobId = $job.id
    Write-Info "job id: $jobId"
    Write-Info "state:  $($job.state)"

    # --- Step 2: Upload CSV ---
    # Windows PowerShell 5.1 lacks native multipart support; use curl.exe (ships with Windows 10+).

    Write-Step "[2/5] Uploading $csvPath..."
    $uploadResult = & curl.exe -s -S -o NUL -w "%{http_code}" -X POST "$ApiBaseUrl/jobs/$jobId/upload" -F "file=@${csvPath};type=text/csv"
    if ($LASTEXITCODE -ne 0 -or $uploadResult -notmatch "^2\d\d$") {
        throw "Upload failed (curl exit $LASTEXITCODE, http $uploadResult)"
    }
    Write-Info "uploaded (http $uploadResult)"

    # --- Step 3: Mark upload complete (autoStart triggers processing) ---

    Write-Step "[3/5] Completing upload..."
    $completeResult = Invoke-RestMethod -Uri "$ApiBaseUrl/jobs/$jobId/upload-complete" -Method Post
    Write-Info "state: $($completeResult.state)"

    # --- Step 4: Poll for completion ---

    Write-Step "[4/5] Polling for completion (interval ${PollIntervalSeconds}s, timeout ${TimeoutSeconds}s)..."
    $startTime = Get-Date
    $lastState = $completeResult.state
    Write-Info "state: $lastState"

    while ($lastState -ne "complete" -and $lastState -ne "failed") {
        Start-Sleep -Seconds $PollIntervalSeconds

        $elapsed = (Get-Date) - $startTime
        if ($elapsed.TotalSeconds -gt $TimeoutSeconds) {
            throw "Timed out after ${TimeoutSeconds}s waiting for completion. Last state: $lastState"
        }

        $status = Invoke-RestMethod -Uri "$ApiBaseUrl/jobs/$jobId" -Method Get
        if ($status.state -ne $lastState) {
            Write-Info "state: $($status.state)"
            $lastState = $status.state
        }
    }

    if ($lastState -eq "failed") {
        throw "Job $jobId reported state 'failed'. Check API logs for the cause."
    }

    # --- Step 5: Download outputs ---

    Write-Step "[5/5] Downloading outputs..."

    $goldenRecordsPath = Join-Path $OutputPath "golden-records.csv"
    Invoke-WebRequest -Uri "$ApiBaseUrl/jobs/$jobId/golden-records" -OutFile $goldenRecordsPath -UseBasicParsing
    Write-Info "golden records -> $goldenRecordsPath"

    if (-not $SkipNeo4jExport) {
        $neo4jZipPath = Join-Path $OutputPath "neo4j-export.zip"
        Invoke-WebRequest -Uri "$ApiBaseUrl/jobs/$jobId/neo4j-export" -OutFile $neo4jZipPath -UseBasicParsing

        $neo4jExtractPath = Join-Path $OutputPath "neo4j-export"
        if (Test-Path -LiteralPath $neo4jExtractPath) {
            Remove-Item -Recurse -Force -LiteralPath $neo4jExtractPath
        }
        Expand-Archive -LiteralPath $neo4jZipPath -DestinationPath $neo4jExtractPath
        Write-Info "neo4j export  -> $neo4jExtractPath"
    }

    # --- Summary ---

    Write-Host ""
    $goldenRecords = @(Import-Csv -LiteralPath $goldenRecordsPath)
    Write-Host "Job $jobId completed." -ForegroundColor Green
    Write-Host "Input records:   $($status.recordCount)" -ForegroundColor Cyan
    Write-Host "Golden records:  $($goldenRecords.Count)" -ForegroundColor Cyan
    Write-Host ""
} else {
    Write-Step "[1/2] Running local CLI..."
    $cliArgs = @(
        "run",
        "--project", $cliProjectPath,
        "--",
        "run",
        "--input", $csvPath,
        "--config", $configPath,
        "--output", $OutputPath
    )
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
}

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
