<#
.SYNOPSIS
  Project cached SEC + GLEIF records into Linkuity input + held-out ground truth.
.DESCRIPTION
  Reads acquire/cache and emits run/companies.csv (id,source,organization_name,
  address_line,postal_code) and validate/ground-truth.csv. Field values pass
  through verbatim from the sources; address components are only concatenated with
  fixed separators. No name/address is cleaned or corrected. For companies flagged
  include_former=true, one extra SEC record per former name is emitted (former name
  + the same current business address), representing SEC's own formerNames data.
#>
[CmdletBinding()]
param(
    [string]$CachePath   = (Join-Path $PSScriptRoot '..\acquire\cache'),
    [string]$OutInput    = (Join-Path $PSScriptRoot '..\run\companies.csv'),
    [string]$OutTruth    = (Join-Path $PSScriptRoot '..\validate\ground-truth.csv')
)

$ErrorActionPreference = "Stop"

function Write-Step { param($m) Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Pass { param($m) Write-Host "    $m" -ForegroundColor Green }

function Join-Address {
    param([string[]]$Parts)
    # Trim each part, drop empties, join with ", ". Pure assembly, no value edits.
    ($Parts | ForEach-Object { ($_ ?? '').Trim() } | Where-Object { $_ -ne '' }) -join ', '
}

$resolved = Import-Csv (Join-Path $CachePath 'resolved-keys.csv')
$rows  = New-Object System.Collections.Generic.List[object]
$truth = New-Object System.Collections.Generic.List[object]

foreach ($r in $resolved) {
    $key = $r.canonical_key
    $cik10 = $r.cik
    $lei = $r.lei

    # --- GLEIF record ---
    $gleif = (Get-Content (Join-Path $CachePath "gleif\$key.json") -Raw | ConvertFrom-Json).data.attributes
    $ga = $gleif.entity.legalAddress
    $gName = $gleif.entity.legalName.name
    $gAddr = Join-Address @(@($ga.addressLines) -join ' '), $ga.city, $ga.region
    $gId = "gleif-$lei"
    $rows.Add([pscustomobject]@{ id=$gId; source='GLEIF'; organization_name=$gName; address_line=$gAddr; postal_code=$ga.postalCode })
    $truth.Add([pscustomobject]@{ record_id=$gId; source='GLEIF'; source_key=$lei; canonical_key=$key; canonical_name=$r.canonical_name })

    # --- SEC current record ---
    $sec = Get-Content (Join-Path $CachePath "sec\$key.json") -Raw | ConvertFrom-Json
    $ba = $sec.addresses.business
    $sName = $sec.name
    $sAddr = Join-Address @("$($ba.street1) $($ba.street2)", $ba.city, $ba.stateOrCountry)
    $sId = "sec-$cik10"
    $rows.Add([pscustomobject]@{ id=$sId; source='SEC'; organization_name=$sName; address_line=$sAddr; postal_code=$ba.zipCode })
    $truth.Add([pscustomobject]@{ record_id=$sId; source='SEC'; source_key=$cik10; canonical_key=$key; canonical_name=$r.canonical_name })

    # --- SEC former-name records (verbatim from SEC formerNames) ---
    if ($r.include_former -eq 'true' -and $sec.formerNames) {
        $n = 0
        foreach ($fn in @($sec.formerNames)) {
            $n++
            $fId = "sec-$cik10-former$n"
            $rows.Add([pscustomobject]@{ id=$fId; source='SEC'; organization_name=$fn.name; address_line=$sAddr; postal_code=$ba.zipCode })
            $truth.Add([pscustomobject]@{ record_id=$fId; source='SEC'; source_key=$cik10; canonical_key=$key; canonical_name=$r.canonical_name })
        }
    }
}

New-Item -ItemType Directory -Force -Path (Split-Path $OutInput), (Split-Path $OutTruth) | Out-Null
$rows  | Export-Csv -Path $OutInput -NoTypeInformation -Encoding utf8
$truth | Export-Csv -Path $OutTruth -NoTypeInformation -Encoding utf8
Write-Step "Wrote $($rows.Count) input rows -> $OutInput"
Write-Pass "Wrote $($truth.Count) ground-truth rows -> $OutTruth"
