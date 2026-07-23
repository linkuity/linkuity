<#
.SYNOPSIS
  Score the company-resolution golden records against the held-out ground truth.
.DESCRIPTION
  Reports correctly-unified, left-separate, and incorrectly-merged companies plus
  pairwise precision/recall/F1 over the "same company" relation. The ground truth
  (CIK/LEI crosswalk) is never seen by the matcher, so a pass proves real
  name+address resolution. Exits non-zero on any incorrect merge or count drift.
#>
[CmdletBinding()]
param(
    [string]$GoldenPath  = (Join-Path $PSScriptRoot '..\output\golden-records.csv'),
    [string]$TruthPath   = (Join-Path $PSScriptRoot 'ground-truth.csv'),
    [string]$ExpectPath  = (Join-Path $PSScriptRoot '..\run\expectations.json')
)

$ErrorActionPreference = "Stop"
function Write-Step { param($m) Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Pass { param($m) Write-Host "    PASS $m" -ForegroundColor Green }
function Write-Fail { param($m) Write-Host "!!!  FAIL $m" -ForegroundColor Red }
function Write-Info { param($m) Write-Host "    $m" -ForegroundColor Gray }

$truth = @{}; $names = @{}
Import-Csv $TruthPath | ForEach-Object { $truth[$_.record_id] = $_.canonical_key; $names[$_.canonical_key] = $_.canonical_name }
$expect = Get-Content $ExpectPath -Raw | ConvertFrom-Json

$clusters = @()
Import-Csv $GoldenPath | ForEach-Object {
    $ids = $_.member_ids -split '\|' | Where-Object { $_ -ne '' }
    $clusters += ,@($ids)
}

# Per-company cluster spread
$keyClusters = @{}    # canonical_key -> list of cluster indices it appears in
$incorrect = @()
for ($i = 0; $i -lt $clusters.Count; $i++) {
    $keys = @($clusters[$i] | ForEach-Object { $truth[$_] } | Where-Object { $_ } | Sort-Object -Unique)
    if ($keys.Count -gt 1) { $incorrect += [pscustomobject]@{ Cluster = $i; Keys = ($keys -join '+'); Members = ($clusters[$i] -join '|') } }
    foreach ($k in $keys) {
        if (-not $keyClusters.ContainsKey($k)) { $keyClusters[$k] = @() }
        $keyClusters[$k] += $i
    }
}

$unified = @(); $split = @()
foreach ($k in $keyClusters.Keys) {
    if (@($keyClusters[$k] | Sort-Object -Unique).Count -eq 1) { $unified += $k } else { $split += $k }
}

# Pairwise precision/recall over "same company"
function Pairs { param($members, $truth)
    $tp = 0; $fp = 0
    foreach ($c in $members) {
        $arr = @($c)
        for ($x = 0; $x -lt $arr.Count; $x++) { for ($y = $x + 1; $y -lt $arr.Count; $y++) {
            if ($truth[$arr[$x]] -eq $truth[$arr[$y]]) { $tp++ } else { $fp++ }
        } }
    }
    return @{ TP = $tp; FP = $fp }
}
$p = Pairs $clusters $truth
# Total same-company pairs = sum over keys of C(n,2)
$byKey = @{}; $truth.Values | ForEach-Object { if ($byKey.ContainsKey($_)) { $byKey[$_]++ } else { $byKey[$_] = 1 } }
$totalSame = 0; $byKey.Values | ForEach-Object { $totalSame += ($_ * ($_ - 1) / 2) }
$precision = if (($p.TP + $p.FP) -gt 0) { $p.TP / ($p.TP + $p.FP) } else { 1 }
$recall    = if ($totalSame -gt 0) { $p.TP / $totalSame } else { 1 }
$f1        = if (($precision + $recall) -gt 0) { 2 * $precision * $recall / ($precision + $recall) } else { 0 }

Write-Step "Company-resolution scorecard"
Write-Info ("golden records : {0}  (expected {1})" -f $clusters.Count, $expect.goldenRecordCount)
Write-Info ("companies      : {0}" -f $byKey.Count)
Write-Info ("correctly unified : {0}" -f $unified.Count)
Write-Info ("left separate     : {0}" -f $split.Count)
Write-Info ("incorrect merges  : {0}" -f $incorrect.Count)
Write-Info ("precision {0:P1}  recall {1:P1}  F1 {2:P1}" -f $precision, $recall, $f1)

if ($split.Count -gt 0) {
    Write-Step "Left-separate companies (honest hard cases)"
    $split | ForEach-Object { Write-Info ("  {0} ({1})" -f $_, $names[$_]) }
}
if ($incorrect.Count -gt 0) {
    Write-Step "Incorrect merges"
    $incorrect | ForEach-Object { Write-Fail ("  {0}  <-  {1}" -f $_.Keys, $_.Members) }
}

$ok = $true
if ($incorrect.Count -gt $expect.maxIncorrectMerges) { Write-Fail "incorrect merges exceed allowed $($expect.maxIncorrectMerges)"; $ok = $false }
if ($clusters.Count -ne $expect.goldenRecordCount) { Write-Fail "golden count drifted from pinned $($expect.goldenRecordCount)"; $ok = $false }
if ($ok) { Write-Pass "resolution matches pinned expectations"; exit 0 } else { exit 1 }
