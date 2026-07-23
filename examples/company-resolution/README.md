# Cross-Source Company Resolution

Resolve real companies across two independent public systems — using nothing but
their names and addresses.

Recording: generate a terminal recording with `vhs assets/demo.tape` (produces
`assets/demo.gif`).

## What this proves

[SEC EDGAR](https://www.sec.gov/) and [GLEIF](https://www.gleif.org/) describe the
same companies independently, and **share no common identifier** — SEC keys on CIK,
GLEIF on LEI. Given 107 source records that only agree on a fuzzy company name and a
postal address, Linkuity reconciles them into 60 golden organizations, then we prove
it got them right against a CIK↔LEI crosswalk the matcher never saw.

- Correctly unified: **38** companies
- Left separate (honest hard cases): **11**
- Incorrectly merged: **0**
- Pairwise precision / recall: **100.0% / 80.6%** (F1 89.2%)

## The datasets

| Source | Provides | Identifier | License |
|--------|----------|-----------|---------|
| SEC EDGAR | EDGAR-conformed name, former names, business address | CIK | US-gov public domain |
| GLEIF | legal name, legal/HQ address | LEI | CC0 |

Both are current, free, and redistributable. See [acquire/Get-Sources.ps1](acquire/Get-Sources.ps1).

The dataset covers 49 well-known public companies. For each, `prepare/Build-Input.ps1`
projects one GLEIF record and one current SEC record into `run/companies.csv`, plus
one extra SEC record per retired filer name for the companies that have them (e.g.
Apple's former names as "APPLE COMPUTER INC"). That yields 107 rows resolving to 49
true companies — a gap the matcher has to close with fuzzy name and address matching
alone.

**A methodological honesty note on the GLEIF address:** GLEIF exposes both a
`legalAddress` and a `headquartersAddress` per entity. The legal address is frequently
a shared registered-agent address (e.g. a Delaware CSC/CT "C/O ..." address used by
hundreds of unrelated companies) — using it would be noise, not signal, for matching.
This demo deliberately uses GLEIF's **headquarters address** (the real operating
address, comparable to SEC's business address), falling back to the legal address only
when no headquarters address is present. That choice is visible in
[prepare/Build-Input.ps1](prepare/Build-Input.ps1) and is disclosed here rather than
buried, because it materially affects which pairs can match on address at all.

## Why this is a real entity-resolution problem

No join key exists between SEC and GLEIF, so resolution must work from noisy names
(`Inc` vs `Incorporated`, `&` vs `and`, ALL-CAPS vs title case, former names) and
differently formatted addresses (business vs legal/HQ). Examples from this dataset:

- **Microsoft** — SEC `MICROSOFT CORP, ONE MICROSOFT WAY, REDMOND, WA 98052-6399` vs
  GLEIF `MICROSOFT CORPORATION, ONE MICROSOFT WAY, REDMOND, US-WA 98052-8300` →
  unified. Same street address, but the name abbreviation (`CORP` vs `CORPORATION`)
  and even the ZIP+4 suffix differ (`-6399` vs `-8300`).
- **IBM** — SEC `INTERNATIONAL BUSINESS MACHINES CORP, 1 NEW ORCHARD ROAD, ARMONK, NY
  10504` vs GLEIF `INTERNATIONAL BUSINESS MACHINES CORPORATION, ONE NORTH CASTLE DRIVE
  ARMONK, NORTH CASTLE, US-NY 10504` → unified. The street address lines are
  completely different strings for the same corporate campus; matching name similarity
  plus the shared exact postal code (`10504`) is what bridges it.
- **Apple** — one GLEIF record (`Apple Inc.`) plus four SEC records at the identical
  address (`ONE APPLE PARK WAY, CUPERTINO, CA 95014`): the current name `Apple Inc.`
  and three retired SEC filer names, `APPLE INC`, `APPLE COMPUTER INC`, and
  `APPLE COMPUTER INC/ FA` → all five unify into one golden organization. See the
  worked graph in [assets/golden-graph.md](assets/golden-graph.md).
- **Boeing** (left separate) — GLEIF `THE BOEING COMPANY, 2711 Centerville Road Suite
  400, Wilmington, US-DE 19808` (a Delaware registered-agent address) vs SEC
  `BOEING CO, 929 LONG BRIDGE DRIVE, ARLINGTON, VA 22202` (the real Arlington HQ).
  Both the name pattern (`THE X COMPANY` vs `X CO`) and the address diverge — the same
  "THE X COMPANY" (GLEIF) vs "X CO" (SEC) split also splits Coca-Cola, Procter &
  Gamble, and Walt Disney in this dataset. These pairs never even become match
  candidates.
- **Verizon** (left separate) — SEC carries a retired filer name, `BELL ATLANTIC CORP`,
  at the same address as `VERIZON COMMUNICATIONS INC.` (`1095 AVENUE OF THE AMERICAS,
  NEW YORK, NY 10036`). Name and address alone can't bridge a genuine corporate
  rebrand: `BELL ATLANTIC` and `VERIZON COMMUNICATIONS` share no name tokens, so the
  matcher correctly leaves that historical record as its own golden organization
  rather than guessing.

## How Linkuity processes it

The end-to-end flow — acquire → prepare → run → validate — is sketched in the
[pipeline diagram](assets/pipeline.md).

`run/company.profile.json` uses `organization_name` as a fuzzy primary signal
(token-name blocking, jaccard similarity, weight 4.0) plus `address_line` (jaccard,
weight 2.5) and `postal_code` (exact, weight 0.5). **No identifier is fed to the
matcher** — CIK/LEI live only in the ground truth. The auto-match threshold (0.41) is
tuned so incorrect merges are zero across this dataset. See
[docs/configuration.md](../../docs/configuration.md).

## Run it

Prerequisite: the .NET 10 SDK and PowerShell 7.

```powershell
./run-demo.ps1                 # offline, uses committed input
./run-demo.ps1 -Refresh -UserAgent "Linkuity-demo you@example.com"   # re-pull live
./run-demo.ps1 -Neo4j          # also emit a Neo4j graph export
```

## Expected output

`60` golden records in `output/golden-records.csv`, e.g.:

```
cluster_id,record_count,member_ids,address_line,organization_name,postal_code
d5e31479-ba6d-4da0-9cd7-6d0d42a657f5,5,gleif-HWUPKR0MPOU8FGXBT394|sec-0000320193|sec-0000320193-former1|sec-0000320193-former2|sec-0000320193-former3,"ONE APPLE PARK WAY, CUPERTINO, CA",Apple Inc.,95014
4a7c31b4-ef71-4568-9123-a080e1726bec,2,gleif-INR2EJN1ERAN0W5ZP974|sec-0000789019,"ONE MICROSOFT WAY, REDMOND, WA",MICROSOFT CORPORATION,98052-6399
744a5fe1-f0f0-4321-9683-7f7e65bc7af2,2,gleif-VGRQXHF3J8VDLUA7XE92|sec-0000051143,"1 NEW ORCHARD ROAD, ARMONK, NY",INTERNATIONAL BUSINESS MACHINES CORPORATION,10504
```

The validator (`validate/Test-Resolution.ps1`) prints the scorecard above and exits
non-zero on any incorrect merge or count drift.

## Validation

`validate/ground-truth.csv` maps each record to its true company via CIK/LEI. It is
**never** referenced by the profile, so a passing scorecard proves genuine name+address
resolution, not an ID lookup. Left-separate companies are reported honestly; bridging
them is the advanced move — add the known-ID crosswalk as an `Identifier` field (the
strong-key mechanism this demo deliberately withholds).

## Repository structure

```
examples/company-resolution/
├── README.md                    # this file
├── run-demo.ps1                 # end-to-end entry point
├── acquire/
│   ├── Get-Sources.ps1          # pulls live SEC + GLEIF data
│   ├── companies.seed.csv       # the 49 companies to acquire
│   └── cache/                   # committed raw JSON (SEC + GLEIF), offline-safe
├── prepare/
│   └── Build-Input.ps1          # projects cache -> run/companies.csv + ground truth
├── run/
│   ├── companies.csv            # 107 input records (name + address only)
│   ├── company.profile.json     # matching profile (no identifiers)
│   ├── company.merge.json       # golden-record field merge policy
│   └── expectations.json        # pinned scorecard for the validator
├── validate/
│   ├── ground-truth.csv         # CIK/LEI crosswalk — held out from matching
│   └── Test-Resolution.ps1      # scores golden records against ground truth
├── output/
│   └── golden-records.csv       # linkuity run output (regenerated by run-demo.ps1)
└── assets/
    ├── demo.tape                # VHS script to render assets/demo.gif
    ├── pipeline.md               # mermaid: acquire -> prepare -> run -> validate
    └── golden-graph.md          # mermaid: Apple's 5 source records -> 1 golden org
```

## Learn more

- [Matching configuration reference](../../docs/configuration.md)
- [Architecture overview](../../docs/architecture.md)
- [How matching works](../../docs/how-matching-works.md)
