<#
.SYNOPSIS
  Acquire raw SEC EDGAR and GLEIF records for the seed company list.
.DESCRIPTION
  For each row in companies.seed.csv, resolves the SEC CIK from its ticker (via
  SEC company_tickers.json) and the GLEIF LEI from its legal name (via the GLEIF
  API), then caches the raw SEC submissions JSON and GLEIF LEI-record JSON. Writes
  a verified resolved-keys.csv crosswalk. Fails loudly on any unresolved or
  ambiguous entity so no wrong company enters the demo.
.PARAMETER UserAgent
  Descriptive User-Agent for SEC requests (required by SEC), e.g. "Linkuity-demo you@example.com".
.PARAMETER DelayMs
  Delay between requests to respect SEC's <10 req/s guidance. Default 200.
.EXAMPLE
  ./Get-Sources.ps1 -UserAgent "Linkuity-demo you@example.com"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$UserAgent,
    [string]$SeedPath  = (Join-Path $PSScriptRoot 'companies.seed.csv'),
    [string]$CachePath = (Join-Path $PSScriptRoot 'cache'),
    [int]$DelayMs = 200
)

$ErrorActionPreference = "Stop"

function Write-Step { param($m) Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Pass { param($m) Write-Host "    $m" -ForegroundColor Green }
function Write-Info { param($m) Write-Host "    $m" -ForegroundColor Gray }
function Write-Fail { param($m) Write-Host "!!! $m" -ForegroundColor Red }

# PowerShell's Invoke-WebRequest only decodes .Content to a [string] for a
# small allowlist of "known text" MIME types. GLEIF responds with
# "application/vnd.api+json", which isn't on that list, so .Content comes
# back as a raw [byte[]] instead of text. Force UTF-8 decoding so cached
# JSON is always written as text, never a byte dump.
function Get-ResponseText {
    param($WebResponse)
    if ($WebResponse.Content -is [byte[]]) {
        return [System.Text.Encoding]::UTF8.GetString($WebResponse.Content)
    }
    return $WebResponse.Content
}

$secDir   = Join-Path $CachePath 'sec'
$gleifDir = Join-Path $CachePath 'gleif'
New-Item -ItemType Directory -Force -Path $secDir, $gleifDir | Out-Null

# NOTE: SEC requires a descriptive User-Agent. PowerShell's Invoke-WebRequest
# rejects a UA containing an email via both -Headers validation AND the -UserAgent
# parameter, so we pass it in -Headers with -SkipHeaderValidation (the only form
# that works). PS auto-decompresses, so no manual Accept-Encoding is needed.
$secHeaders = @{ "User-Agent" = $UserAgent }

Write-Step "Downloading SEC company_tickers.json"
$tickersPath = Join-Path $CachePath 'company_tickers.json'
Invoke-WebRequest -Uri 'https://www.sec.gov/files/company_tickers.json' -Headers $secHeaders -SkipHeaderValidation -OutFile $tickersPath
$tickersRaw = Get-Content $tickersPath -Raw | ConvertFrom-Json
# Build ticker -> {cik10, title}. company_tickers.json is an object keyed by index.
$tickerMap = @{}
foreach ($p in $tickersRaw.PSObject.Properties) {
    $rec = $p.Value
    $cik10 = ([string]$rec.cik_str).PadLeft(10, '0')
    $tickerMap[[string]$rec.ticker] = [pscustomobject]@{ Cik10 = $cik10; Title = $rec.title }
}
Write-Pass "Loaded $($tickerMap.Count) ticker->CIK mappings"

function Resolve-Lei {
    param([string]$LegalName)
    # GLEIF fuzzy-ish name filter; pick the best US legal-address match.
    $enc = [uri]::EscapeDataString($LegalName)
    $url = "https://api.gleif.org/api/v1/lei-records?filter[entity.legalName]=$enc&page[size]=10"
    $resp = Invoke-RestMethod -Uri $url -Headers @{ "Accept" = "application/vnd.api+json" }
    if (-not $resp.data -or $resp.data.Count -eq 0) { return $null }
    $us = @($resp.data | Where-Object { $_.attributes.entity.legalAddress.country -eq 'US' })
    $pool = if ($us.Count -gt 0) { $us } else { @($resp.data) }
    # Prefer an exact (case-insensitive) legal-name match; else the first in pool.
    $exact = @($pool | Where-Object { $_.attributes.entity.legalName.name -ieq $LegalName })
    $chosen = if ($exact.Count -ge 1) { $exact[0] } else { $pool[0] }
    # GLEIF sometimes returns a record whose LEI registration has been marked a
    # DUPLICATE with a live successor LEI (same legal entity, re-registered).
    # Follow the chain to the current record so we cache the active LEI.
    while ($chosen.attributes.registration.status -eq 'DUPLICATE' -and $chosen.attributes.entity.successorEntity.lei) {
        $successorLei = $chosen.attributes.entity.successorEntity.lei
        $successorResp = Invoke-RestMethod -Uri "https://api.gleif.org/api/v1/lei-records/$successorLei" -Headers @{ "Accept" = "application/vnd.api+json" }
        if (-not $successorResp.data) { break }
        $chosen = $successorResp.data
    }
    return $chosen
}

$seed = Import-Csv $SeedPath
$resolved = New-Object System.Collections.Generic.List[object]
$failures = New-Object System.Collections.Generic.List[string]

foreach ($row in $seed) {
    Write-Step "$($row.canonical_name) [$($row.ticker)]"

    # --- SEC ---
    $t = $tickerMap[[string]$row.ticker]
    if (-not $t) { $failures.Add("SEC: ticker '$($row.ticker)' not found for $($row.canonical_key)"); continue }
    $cik10 = $t.Cik10
    $secUrl = "https://data.sec.gov/submissions/CIK$cik10.json"
    Start-Sleep -Milliseconds $DelayMs
    $secJson = Invoke-WebRequest -Uri $secUrl -Headers $secHeaders -SkipHeaderValidation
    Set-Content -Path (Join-Path $secDir "$($row.canonical_key).json") -Value (Get-ResponseText $secJson) -Encoding utf8
    Write-Info "SEC CIK $cik10"

    # --- GLEIF ---
    Start-Sleep -Milliseconds $DelayMs
    $lei = Resolve-Lei -LegalName $row.canonical_name
    if (-not $lei) { $failures.Add("GLEIF: no LEI record for '$($row.canonical_name)' ($($row.canonical_key))"); continue }
    $leiId = $lei.attributes.lei
    $leiUrl = "https://api.gleif.org/api/v1/lei-records/$leiId"
    Start-Sleep -Milliseconds $DelayMs
    $leiJson = Invoke-WebRequest -Uri $leiUrl -Headers @{ "Accept" = "application/vnd.api+json" }
    Set-Content -Path (Join-Path $gleifDir "$($row.canonical_key).json") -Value (Get-ResponseText $leiJson) -Encoding utf8
    Write-Info "GLEIF LEI $leiId ($($lei.attributes.entity.legalName.name))"

    $resolved.Add([pscustomobject]@{
        canonical_key  = $row.canonical_key
        canonical_name = $row.canonical_name
        ticker         = $row.ticker
        cik            = $cik10
        lei            = $leiId
        include_former = $row.include_former
    })
    Write-Pass "resolved"
}

if ($failures.Count -gt 0) {
    Write-Fail "Unresolved entities ($($failures.Count)):"
    $failures | ForEach-Object { Write-Fail "  $_" }
    throw "Acquisition incomplete: $($failures.Count) unresolved. Fix the seed and re-run."
}

$resolvedPath = Join-Path $CachePath 'resolved-keys.csv'
$resolved | Export-Csv -Path $resolvedPath -NoTypeInformation -Encoding utf8
Write-Step "Wrote $($resolved.Count) resolved keys -> $resolvedPath"
