<#
.SYNOPSIS
    Run a durable Linkuity MDM scenario described by a scenario.json manifest.

.DESCRIPTION
    Executes an ordered list of Linkuity CLI commands against a fresh temporary
    durable metadata database. Threads captured IDs between steps via {var}
    placeholders and verifies stdout lines and read-back CSV rows.

.PARAMETER ScenarioPath
    Path to the scenario folder containing scenario.json and a data/ directory.

.PARAMETER Backend
    Storage backend to use: 'File' (default) or 'Postgres'. When 'Postgres', a
    throwaway Docker container is started and torn down automatically.

.PARAMETER KeepArtifacts
    If set, the temp working directory and metadata DB are left on disk.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ScenarioPath,

    [ValidateSet('File','Postgres')]
    [string]$Backend = 'File',

    [switch]$KeepArtifacts
)

$ErrorActionPreference = "Stop"

function Write-Step { param([string]$m) Write-Host $m -ForegroundColor Yellow }
function Write-Pass { param([string]$m) Write-Host "  PASS  $m" -ForegroundColor Green }
function Write-Fail { param([string]$m) Write-Host "  FAIL  $m" -ForegroundColor Red }

$guidRegex = '[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}'

$ScenarioPath = (Resolve-Path -LiteralPath $ScenarioPath).Path
$manifestPath = Join-Path $ScenarioPath "scenario.json"
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "scenario.json not found in $ScenarioPath"
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$cliProject = Join-Path $repoRoot "src\Linkuity.Cli"

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json

# Fresh working directory + temp metadata DB.
$workDir = Join-Path ([System.IO.Path]::GetTempPath()) ("linkuity-durable-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $workDir | Out-Null
$metadataPath = Join-Path $workDir "metadata.json"

# Built-in variables available to {placeholders}.
$vars = @{ workdir = $workDir }

Write-Host "Scenario:  $($manifest.name)" -ForegroundColor Cyan
Write-Host "Backend:   $Backend" -ForegroundColor Cyan
Write-Host "WorkDir:   $workDir" -ForegroundColor Cyan
Write-Host ""

# Build the CLI once, then invoke the produced DLL directly so step stdout is
# free of build noise (clean CSV for row assertions).
Write-Step "Building Linkuity.Cli..."
& dotnet build $cliProject -c Debug --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
$dll = Get-ChildItem -Recurse -Path (Join-Path $cliProject "bin\Debug") -Filter "Linkuity.Cli.dll" |
    Select-Object -First 1
if (-not $dll) { throw "Could not locate Linkuity.Cli.dll after build." }

function Expand-Placeholders {
    param([string]$Value)
    if ($null -eq $Value) { return $Value }
    return [regex]::Replace($Value, '\{(\w+)\}', {
        param($match)
        $key = $match.Groups[1].Value
        if (-not $vars.ContainsKey($key)) { throw "Unknown placeholder {$key}." }
        return [string]$vars[$key]
    })
}

function Resolve-ArgValue {
    param([string]$Value)
    $expanded = Expand-Placeholders $Value
    # Resolve data-relative paths against the scenario folder.
    if ($expanded -match '^data[\\/]') {
        return (Join-Path $ScenarioPath $expanded)
    }
    # Also resolve any other relative path that exists at the scenario root (e.g. *.profile.json).
    if (-not [System.IO.Path]::IsPathRooted($expanded)) {
        $candidate = Join-Path $ScenarioPath $expanded
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }
    return $expanded
}

$passed = 0
$failed = 0

# Postgres backend: start a throwaway container and tear it down in finally.
$containerName = $null
$connectionString = $null
$pgIndexDir = $null

if ($Backend -eq 'Postgres') {
    $port = Get-Random -Minimum 15000 -Maximum 25000
    $containerName = "linkuity-pg-$([Guid]::NewGuid().ToString('N'))"
    $pgIndexDir = Join-Path $workDir "lucene-index"
    New-Item -ItemType Directory -Force $pgIndexDir | Out-Null

    Write-Step "Starting Postgres container '$containerName' on port $port..."
    $dockerOut = & docker run -d --name $containerName `
        -e POSTGRES_PASSWORD=postgres `
        -e POSTGRES_DB=linkuity `
        -p "${port}:5432" `
        postgres:16-alpine 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "docker run failed: $dockerOut"
    }

    $connectionString = "Host=localhost;Port=$port;Database=linkuity;Username=postgres;Password=postgres"

    # Wait for Postgres to be ready.
    Write-Step "Waiting for Postgres to be ready..."
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        $pgReady = & docker exec $containerName pg_isready -U postgres -d linkuity 2>&1
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) {
        & docker rm -f $containerName 2>$null
        throw "Postgres container '$containerName' did not become ready within 60 seconds."
    }
    Write-Host "  Postgres ready." -ForegroundColor Green
    Write-Host ""
}

try {
    foreach ($step in $manifest.steps) {
        $commandTokens = $step.command -split '\s+'
        $cliArgs = @()
        $cliArgs += $commandTokens

        # All commands except `run` take durable-store args; the runner injects them.
        if ($commandTokens[0] -ne "run") {
            if ($Backend -eq 'Postgres') {
                $cliArgs += @("--metadata-store", "postgres", "--connection-string", $connectionString, "--index-dir", $pgIndexDir)
            } else {
                $cliArgs += @("--metadata", $metadataPath)
            }
        }

        if ($step.PSObject.Properties.Name -contains "args" -and $null -ne $step.args) {
            foreach ($prop in $step.args.PSObject.Properties) {
                $cliArgs += "--$($prop.Name)"
                $cliArgs += (Resolve-ArgValue ([string]$prop.Value))
            }
        }

        Write-Step "step: $($step.command)"
        # Keep stderr OUT of $stdout so it never reaches ConvertFrom-Csv in row assertions.
        $errFile = [System.IO.Path]::GetTempFileName()
        $stdout = (& dotnet $dll.FullName @cliArgs 2>$errFile | Out-String)
        $exit = $LASTEXITCODE
        $stderr = (Get-Content -Raw -LiteralPath $errFile -ErrorAction SilentlyContinue)
        Remove-Item -LiteralPath $errFile -ErrorAction SilentlyContinue

        $assert = $null
        if ($step.PSObject.Properties.Name -contains "assert") { $assert = $step.assert }
        $expectsError = ($null -ne $assert -and ($assert.PSObject.Properties.Name -contains "error") -and $assert.error)

        if ($expectsError) {
            if ($exit -ne 0) { Write-Pass "step failed as expected"; $passed++ }
            else { Write-Fail "step expected to fail but exit code was 0"; $failed++ }
            continue
        }

        if ($exit -ne 0) {
            Write-Fail "step '$($step.command)' exited $exit.`nstdout:`n$stdout`nstderr:`n$stderr"
            $failed++
            continue
        }

        # Capture: store the first GUID found in stdout under the named variable.
        if ($step.PSObject.Properties.Name -contains "capture" -and $step.capture) {
            $m = [regex]::Match($stdout, $guidRegex)
            if (-not $m.Success) { Write-Fail "capture '$($step.capture)': no GUID in output"; $failed++; continue }
            $vars[$step.capture] = $m.Value
        }

        if ($null -eq $assert) { $passed++; continue }

        # stdout assertions: each property must appear as "name: value".
        if ($assert.PSObject.Properties.Name -contains "stdout" -and $null -ne $assert.stdout) {
            foreach ($prop in $assert.stdout.PSObject.Properties) {
                $needle = "$($prop.Name): $($prop.Value)"
                if ($stdout -match [regex]::Escape($needle)) {
                    Write-Pass "stdout '$needle'"; $passed++
                } else {
                    Write-Fail "stdout missing '$needle'. Output:`n$stdout"; $failed++
                }
            }
        }

        # rows assertions: parse stdout as CSV, match a row by `where`, check `expect`.
        if ($assert.PSObject.Properties.Name -contains "rows" -and $null -ne $assert.rows) {
            $csvRows = @($stdout | ConvertFrom-Csv)
            foreach ($rowAssert in $assert.rows) {
                $candidates = $csvRows
                foreach ($wprop in $rowAssert.where.PSObject.Properties) {
                    $field = $wprop.Name
                    $want = [string]$wprop.Value
                    if ($field.EndsWith("~")) {
                        $col = $field.TrimEnd("~")
                        $candidates = @($candidates | Where-Object { [string]$_.$col -like "*$want*" })
                    } else {
                        $candidates = @($candidates | Where-Object { [string]$_.$field -eq $want })
                    }
                }
                if ($candidates.Count -ne 1) {
                    Write-Fail "rows where [$($rowAssert.where.PSObject.Properties.Name -join ', ')]: matched $($candidates.Count) rows (expected 1)"
                    $failed++
                    continue
                }
                $row = $candidates[0]
                foreach ($eprop in $rowAssert.expect.PSObject.Properties) {
                    $actual = [string]$row.$($eprop.Name)
                    $want = [string]$eprop.Value
                    if ($actual -eq $want) { Write-Pass "row.$($eprop.Name) = '$actual'"; $passed++ }
                    else { Write-Fail "row.$($eprop.Name): expected '$want', got '$actual'"; $failed++ }
                }
            }
        }
    }
} finally {
    if ($containerName) {
        Write-Host ""
        Write-Step "Tearing down Postgres container '$containerName'..."
        & docker rm -f $containerName 2>$null
    }
}

if (-not $KeepArtifacts) {
    Remove-Item -Recurse -Force -LiteralPath $workDir -ErrorAction SilentlyContinue
}

Write-Host ""
if ($failed -eq 0) {
    Write-Host "All $passed checks passed." -ForegroundColor Green
    exit 0
} else {
    Write-Host "$failed of $($passed + $failed) checks failed." -ForegroundColor Red
    exit 1
}
