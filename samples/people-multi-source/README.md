# People — Multi-Source Sample

A 28-row people dataset that exercises Linkuity's source-priority merging across a range of cluster shapes. Use it as a teaching example, a smoke test, or a starting point for tuning your own merge rules.

## Files

- `sample.csv` — 28 input rows representing 10 distinct people, ingested from 4 different source systems.
- `match-config.json` — standalone CLI/API job configuration. Declares matching fields and per-field source-priority merge rules.

## Sources and merge priorities

Four sources are present: **CRM**, **Marketing**, **Support**, **Billing**. Each priority-merged field uses a different ranking, so no single source dominates the golden record:

| Field           | Priority order                          | "System of record" story                       |
|-----------------|-----------------------------------------|------------------------------------------------|
| `email`         | CRM → Marketing → Support → Billing     | CRM owns sales/contact email                   |
| `phone`         | Support → CRM → Billing → Marketing     | Support keeps the most current contact number  |
| `date_of_birth` | CRM → Billing → Support → Marketing     | CRM captures DOB at signup; Billing verifies   |
| `address_line`  | Billing → CRM → Support → Marketing     | Billing has the legal address                  |
| `postal_code`   | Billing → CRM → Support → Marketing     | Same as address                                |

Fields **not** listed in `mergeConfiguration` (`first_name`, `last_name`, `company`) fall back to **consensus merge**: most-frequent value wins; ties broken by longest string. The `source` column itself is excluded from the golden output.

## How merging works

For each field with a priority rule, the merger walks the priority list and takes the **first non-empty value from the highest-ranked source** present in the cluster. If no priority-listed source contributes a value, the merger falls back to consensus. When multiple rows in the same priority source have non-empty values, one of them is picked (the order is not contractual — design rows so they agree on the value, or accept either as correct).

## Worked example: cluster 7

Cluster 7 (Joseph Martinez) is the cleanest illustration — all four sources contribute one row each, and each stores the same person a little differently: a nickname (`Joe`), a misspelling (`Joesph`), a one-letter email typo (`joseph.martimez`), the same phone in four formats, and an apartment number that only reached two systems. The four input rows from `sample.csv`:

| id        | source    | first_name | email                          | phone           | address_line               |
|-----------|-----------|------------|--------------------------------|-----------------|----------------------------|
| crm-050   | CRM       | Joseph     | joseph.martinez@example.com    | (312) 555-0147  | 1420 Maple Avenue          |
| mkt-051   | Marketing | Joe        | joe.martinez@example.com       | 312-555-0147    | 1420 Maple Ave             |
| sup-052   | Support   | Joesph     | joseph.martinez@example.com    | 312.555.0147    | 1420 Maple Avenue Apt 3B   |
| bil-053   | Billing   | Joseph     | joseph.martimez@example.com    | 3125550147      | 1420 Maple Ave Apt 3B      |

After the pipeline runs, the merger applies each field's priority list (falling back to consensus for fields without one) to produce the golden record:

| Field          | Priority list                          | Winner      | Golden value                  |
|----------------|----------------------------------------|-------------|-------------------------------|
| `first_name`   | *(none — consensus)*                    | consensus   | `Joseph`                      |
| `email`        | **CRM** → MKT → SUP → BIL              | `crm-050`   | `joseph.martinez@example.com` |
| `phone`        | **SUP** → CRM → BIL → MKT              | `sup-052`   | `+13125550147`                |
| `address_line` | **BIL** → CRM → SUP → MKT              | `bil-053`   | `1420 Maple Ave Apt 3B`       |

Different sources win different fields — the golden record is a *composite*, not a copy of any single input row. `first_name` has no priority list, so the merger falls back to the most common spelling (`Joseph`, 2 of 4), discarding the nickname and the typo. Phone is shown post-normalization (libphonenumber → E.164); see [Notes on normalization](#notes-on-normalization) below.

## Cluster catalog

Member IDs are prefixed by source (`crm-`, `mkt-`, `sup-`, `bil-`) so you can read them straight off the `member_ids` column in the golden output.

Each cluster below includes a Cypher query to inspect it in Neo4j after loading the export (see [Loading into Neo4j](#loading-into-neo4j)). The graph schema is:

- `(:Entity {id, source, first_name, last_name, email, phone, date_of_birth, address_line, postal_code, company})` — one node per input row.
- `(:GoldenRecord {cluster_id, record_count, …merged fields})` — one node per cluster.
- `(:Entity)-[:RESOLVED_TO {cluster_size}]->(:GoldenRecord)` — links a member to its cluster.

Each query anchors on a known member id and pivots through `RESOLVED_TO` to surface every member alongside the merged golden values, so you can eyeball who contributed what.

### How clusters form

Two records end up in the same cluster when the native matcher's output graph connects them:

1. **Blocks** — blocking-gated candidate retrieval partitions records so only plausibly-related rows are compared (name-token and exact-identifier blocking keys), keeping the pairwise comparison space small.
2. **Scores** — identifier-weighted scoring compares each candidate pair: a shared exact strong-identifier field (e.g. email, phone, DOB) floors the pair to a match (~0.98, the identifier floor); otherwise a weighted average of per-field similarity is computed. Pairs whose score reaches the auto-match cut (**0.90** by default) become edges in `matches.csv`.
3. **Unions** — a Union-Find clustering strategy merges connected components over those edges.

Step 3 makes cluster membership **transitive**: two records can land in the same cluster even if their *direct* similarity is below threshold, as long as a chain of above-threshold edges connects them.

**Verifying the sample:**

- `.\scripts\Run-Scenario.ps1 -Scenario samples\people-multi-source -Mode Cli` — runs the full pipeline locally without Azure, AppHost, or a hosted API, then asserts every cluster against `expectations.json`.
- `dotnet test src\Linkuity.Cli.Tests --filter SampleScenarioTests` — exercises the matcher directly (no API needed) via the native CLI runner. Useful when iterating on `sample.csv`.

**Debugging a single pair** — if you change the data and a cluster falls apart, this Cypher (after loading the Neo4j export) tells you whether two specific rows ended up together:

```cypher
MATCH (a:Entity {id: 'crm-010'})-[:RESOLVED_TO]->(g1:GoldenRecord)
MATCH (b:Entity {id: 'mkt-011'})-[:RESOLVED_TO]->(g2:GoldenRecord)
RETURN a.id AS left, b.id AS right,
       g1.cluster_id = g2.cluster_id AS clustered_together,
       g1.cluster_id AS left_cluster, g2.cluster_id AS right_cluster;
```

If `clustered_together` is `false`, re-run the CLI with `--output` pointed at a scratch directory and inspect `artifacts/` for the per-pair score breakdown, then tighten the rows where field similarity is weakest (typically email, phone, or address) or check whether a shared strong identifier is missing.

### Cluster 1 — singleton, CRM only
**Members:** `crm-001`
Singleton clusters return the source row unchanged. Priority logic is moot. **Expected golden:** Alice Anderson with all `crm-001` values.

```cypher
MATCH (e:Entity {id: 'crm-001'})-[:RESOLVED_TO]->(g:GoldenRecord)
RETURN e.id AS member_id, e.source AS source,
       e.email AS email, e.phone AS phone, e.address_line AS address,
       g.email AS golden_email, g.phone AS golden_phone, g.address_line AS golden_address,
       g.record_count AS record_count;
```

### Cluster 2 — singleton, Marketing only
**Members:** `mkt-002`
Same as above but from a low-priority source. Confirms that priority is *not* a filter — Marketing-only data still appears in the golden output. **Expected golden:** Bob Baker with all `mkt-002` values.

```cypher
MATCH (e:Entity {id: 'mkt-002'})-[:RESOLVED_TO]->(g:GoldenRecord)
RETURN e.id AS member_id, e.source AS source,
       e.email AS email, e.phone AS phone, e.address_line AS address,
       g.email AS golden_email, g.phone AS golden_phone, g.address_line AS golden_address,
       g.record_count AS record_count;
```

### Cluster 3 — 2 rows, CRM + Marketing
**Members:** `crm-010`, `mkt-011`
| Field            | Winner    | Why |
|------------------|-----------|-----|
| `email`          | `crm-010` | CRM is #1 for email |
| `phone`          | `crm-010` | Support absent → CRM is next in `[Support, CRM, Billing, Marketing]` |
| `date_of_birth`  | `crm-010` | CRM is #1 |
| `address_line`   | `crm-010` | Billing absent → CRM is next in `[Billing, CRM, Support, Marketing]` |
| `postal_code`    | (same)    | Both rows agree |
| `first_name`/`last_name`/`company` | consensus | Both agree |

```cypher
MATCH (:Entity {id: 'crm-010'})-[:RESOLVED_TO]->(g:GoldenRecord)
MATCH (m:Entity)-[:RESOLVED_TO]->(g)
RETURN m.id AS member_id, m.source AS source,
       m.email AS email, m.phone AS phone, m.address_line AS address,
       g.email AS golden_email, g.phone AS golden_phone, g.address_line AS golden_address
ORDER BY m.id;
```

### Cluster 4 — 2 rows, Support + Billing
**Members:** `sup-020`, `bil-021`
| Field            | Winner    | Why |
|------------------|-----------|-----|
| `email`          | `sup-020` | CRM/Marketing absent → Support is next in `[CRM, Marketing, Support, Billing]` |
| `phone`          | `sup-020` | Support is #1 |
| `date_of_birth`  | `bil-021` | CRM absent → Billing is #2; both rows agree anyway |
| `address_line`   | `bil-021` | Billing is #1 |
| `postal_code`    | `bil-021` | Billing is #1 (both agree) |

```cypher
MATCH (:Entity {id: 'sup-020'})-[:RESOLVED_TO]->(g:GoldenRecord)
MATCH (m:Entity)-[:RESOLVED_TO]->(g)
RETURN m.id AS member_id, m.source AS source,
       m.email AS email, m.phone AS phone, m.address_line AS address,
       g.email AS golden_email, g.phone AS golden_phone, g.address_line AS golden_address
ORDER BY m.id;
```

### Cluster 5 — 3 rows, CRM + Support + Billing (each owns one field)
**Members:** `crm-030`, `sup-031`, `bil-032`
This is the showcase cluster — three priority sources are present and each is the top-ranked source for a different field.
| Field            | Winner    | Why |
|------------------|-----------|-----|
| `email`          | `crm-030` | CRM is #1 |
| `phone`          | `sup-031` | Support is #1 |
| `address_line`   | `bil-032` | Billing is #1 |
| `postal_code`    | `bil-032` | Billing is #1 (all agree) |
| `date_of_birth`  | `crm-030` | CRM is #1 (all agree) |

```cypher
MATCH (:Entity {id: 'crm-030'})-[:RESOLVED_TO]->(g:GoldenRecord)
MATCH (m:Entity)-[:RESOLVED_TO]->(g)
RETURN m.id AS member_id, m.source AS source,
       m.email AS email, m.phone AS phone, m.address_line AS address,
       g.email AS golden_email, g.phone AS golden_phone, g.address_line AS golden_address
ORDER BY m.id;
```

### Cluster 6 — 3 rows, two CRM rows + one Marketing
**Members:** `crm-040`, `crm-041`, `mkt-042`
Demonstrates two things:
1. **Priority sources can have multiple rows.** Both CRM rows have identical priority-merged values, so the output is deterministic — CRM wins all priority fields, Marketing loses every one.
2. **Consensus on non-priority fields with disagreement.** `first_name` is *Frank* (1) vs *Franklin* (2) → **Franklin** wins by majority.

```cypher
MATCH (:Entity {id: 'crm-040'})-[:RESOLVED_TO]->(g:GoldenRecord)
MATCH (m:Entity)-[:RESOLVED_TO]->(g)
RETURN m.id AS member_id, m.source AS source,
       m.first_name AS first_name, m.email AS email, m.phone AS phone,
       g.first_name AS golden_first_name, g.email AS golden_email, g.phone AS golden_phone
ORDER BY m.id;
```

### Cluster 7 — 4 rows, all four sources
**Members:** `crm-050`, `mkt-051`, `sup-052`, `bil-053`
The full picture — every priority list resolves to a clear winner from a different source.
| Field            | Winner    |
|------------------|-----------|
| `email`          | `crm-050` |
| `phone`          | `sup-052` |
| `address_line`   | `bil-053` |
| `postal_code`    | `bil-053` (all agree) |
| `date_of_birth`  | `crm-050` (all agree) |

```cypher
MATCH (:Entity {id: 'crm-050'})-[:RESOLVED_TO]->(g:GoldenRecord)
MATCH (m:Entity)-[:RESOLVED_TO]->(g)
RETURN m.id AS member_id, m.source AS source,
       m.email AS email, m.phone AS phone, m.address_line AS address,
       g.email AS golden_email, g.phone AS golden_phone, g.address_line AS golden_address
ORDER BY m.id;
```

### Cluster 8 — 4 rows, with **empty values that trigger fallback**
**Members:** `crm-060`, `mkt-061`, `sup-062`, `bil-063`
Two intentional gaps:
- `crm-060.email` is empty → email priority skips CRM and lands on Marketing (`mkt-061`).
- `bil-063.address_line` is empty → address priority skips Billing and lands on CRM (`crm-060`).

| Field            | Winner    | Why |
|------------------|-----------|-----|
| `email`          | `mkt-061` | CRM #1 is empty → Marketing #2 wins |
| `phone`          | `sup-062` | Support is #1 |
| `date_of_birth`  | `crm-060` | CRM is #1 |
| `address_line`   | `crm-060` | Billing #1 is empty → CRM #2 wins |
| `postal_code`    | `bil-063` | Billing is #1 (all agree) |

The query uses `coalesce(nullif(...))` so empty source values render as `<empty>` instead of disappearing.

```cypher
MATCH (:Entity {id: 'crm-060'})-[:RESOLVED_TO]->(g:GoldenRecord)
MATCH (m:Entity)-[:RESOLVED_TO]->(g)
RETURN m.id AS member_id, m.source AS source,
       coalesce(nullif(m.email, ''), '<empty>') AS email,
       coalesce(nullif(m.address_line, ''), '<empty>') AS address,
       g.email AS golden_email, g.address_line AS golden_address
ORDER BY m.id;
```

### Cluster 9 — 3 rows, only Marketing present
**Members:** `mkt-070`, `mkt-071`, `mkt-072`
Marketing is the **lowest-priority source** for every field, but it's the only source present — so it wins everything by fallthrough. Demonstrates that priority lists describe *preference*, not *requirement*: if no preferred source contributes, the lowest-ranked source still produces the golden record.

```cypher
MATCH (:Entity {id: 'mkt-070'})-[:RESOLVED_TO]->(g:GoldenRecord)
MATCH (m:Entity)-[:RESOLVED_TO]->(g)
WITH g, collect(DISTINCT m.source) AS sources_present, collect(m) AS members
UNWIND members AS m
RETURN m.id AS member_id, m.source AS source,
       m.email AS email, m.phone AS phone, m.address_line AS address,
       g.email AS golden_email, g.phone AS golden_phone, g.address_line AS golden_address,
       sources_present
ORDER BY m.id;
```

### Cluster 10 — 5 rows, mixed sources, **consensus tiebreaker on `first_name`**
**Members:** `crm-080`, `crm-081`, `sup-082`, `mkt-083`, `mkt-084`
The largest cluster. Priority fields resolve straightforwardly:
| Field            | Winner    | Why |
|------------------|-----------|-----|
| `email`          | `crm-080` or `crm-081` | CRM is #1; both have non-empty emails |
| `phone`          | `sup-082` | Support is #1 |
| `address_line`   | `crm-080`/`crm-081` | Billing absent → CRM #2; both CRM rows agree |

Consensus on `first_name`: *Jonathan* (×2: `crm-080`, `sup-082`), *Jon* (×2: `crm-081`, `mkt-083`), *Jonny* (×1: `mkt-084`) → tie between Jonathan and Jon → **length tiebreaker → Jonathan** (8 chars beats 3).

```cypher
MATCH (:Entity {id: 'crm-080'})-[:RESOLVED_TO]->(g:GoldenRecord)
MATCH (m:Entity)-[:RESOLVED_TO]->(g)
RETURN m.id AS member_id, m.source AS source,
       m.first_name AS first_name, m.email AS email, m.phone AS phone,
       g.first_name AS golden_first_name, g.email AS golden_email, g.phone AS golden_phone
ORDER BY m.id;
```

## Running it end-to-end

The default standalone path runs locally through the CLI and writes artifacts plus outputs under `samples\people-multi-source\output`.

```powershell
.\scripts\Run-Scenario.ps1 -Scenario samples\people-multi-source -Mode Cli
```

Expected: 28 input records, 10 golden records, and all checks in `expectations.json` pass.

You can also run the CLI directly:

```powershell
dotnet run --project src\Linkuity.Cli -- run `
  --input samples\people-multi-source\sample.csv `
  --config samples\people-multi-source\match-config.json `
  --output samples\people-multi-source\output `
  --neo4j-export
```

The legacy API job path remains available for regression coverage. It requires the API to be running locally at `http://localhost:5017`.

```powershell
$cfg = Get-Content samples\people-multi-source\match-config.json -Raw
$job = curl -s -X POST http://localhost:5017/jobs `
  -H "Content-Type: application/json" `
  -d $cfg | ConvertFrom-Json

curl -X POST "http://localhost:5017/jobs/$($job.id)/upload" `
  -F "file=@samples\people-multi-source\sample.csv;type=text/csv"
curl -X POST "http://localhost:5017/jobs/$($job.id)/upload-complete"

do {
  Start-Sleep -Seconds 2
  $status = curl -s "http://localhost:5017/jobs/$($job.id)" | ConvertFrom-Json
  Write-Host "State: $($status.state)"
} while ($status.state -ne "complete" -and $status.state -ne "failed")

curl "http://localhost:5017/jobs/$($job.id)/golden-records" -o golden-records.csv
Get-Content golden-records.csv
```

The job is configured with `autoStart: true`, so processing begins as soon as the upload completes — no separate `/start` call is needed.

## Loading into Neo4j

In CLI mode, the Neo4j export is written to `output\neo4j-export\`. It contains one CSV per node/relationship type plus a `load.cypher` script with constraints and `LOAD CSV` statements.

```powershell
# Copy the CSVs into your Neo4j instance's import/ directory, then run load.cypher
# from Neo4j Browser or cypher-shell:
#   :source output/neo4j-export/load.cypher
```

After loading, run any of the per-cluster Cypher queries above. They're written to be readable in **Neo4j Browser** (table view) — each row is one cluster member, with the merged `golden_*` columns repeated alongside so the winner is obvious at a glance.

## Notes on normalization

Linkuity normalizes match-field values before matching. For this sample:

- **Email** — lowercased and trimmed. Sample emails are already lowercase.
- **Phone** — parsed by libphonenumber and reformatted to E.164. Sample input `(212) 555-5000` becomes `+12125555000` in the golden output.
- **`date_of_birth`** — normalized to `yyyy-MM-dd`. Sample input is already in that form.
- **Names** — leading honorifics (`Mr.`, `Mrs.`, `Dr.`, etc.) are stripped. The sample uses no honorifics.
- **Address / postal / company** — trimmed only.

The per-cluster "Winner" tables above identify rows by ID, so they hold regardless of how normalization rewrites the underlying string.
