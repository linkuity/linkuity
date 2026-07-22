# Organizations — Multi-Source Sample

A 28-row organization dataset that exercises Linkuity's source-priority merging across a range of cluster shapes. It covers the same merge-priority mechanics as the `people-multi-source` sample, applied to org-shaped data, and adds org-specific corner cases: legal-suffix variation (`Carbon Labs Corp` vs `Carbon Labs`), article/prefix name noise (`The Globex Corporation` clusters with `Globex Corporation` via a shared last-name token and shared `domain_name`), and the org-only semantic types `organization_name` and `domain_name`.

## Files

- `sample.csv` — 28 input rows representing 10 distinct organizations, ingested from 4 different source systems.
- `organizations-multi-source.profile.json` — matching profile. Declares the fields, semantic types, and matching strategy (its `contentType: "organization"` is byte-equivalent to the built-in `organization` profile — see `SampleScenarioTests.OrgMultiSource_BuiltInProfileByName_MatchesFileProfile`).
- `organizations-multi-source.merge.json` — merge policy. Declares per-field source-priority merge rules.
- `expectations.json` — asserted golden values for each cluster, used by `Run-Scenario.ps1` and `SampleScenarioTests`.

## Sources and merge priorities

Four sources are present: **CRM**, **Marketing**, **Support**, **Finance**. Each priority-merged field uses a different ranking — and unlike many sample datasets, every source is the system of record for at least one field:

| Field               | Priority order                                  | "System of record" story                                                                                          |
|---------------------|-------------------------------------------------|-------------------------------------------------------------------------------------------------------------------|
| `organization_name` | Finance → CRM → Support → Marketing             | Finance owns the legal name with proper suffix (it is on every invoice).                                          |
| `domain_name`       | Marketing → CRM → Finance → Support             | Marketing automation captures the canonical web domain via tracking pixels; CRM-entered domains often have typos. |
| `email`             | CRM → Marketing → Support → Finance             | CRM owns the sales/contact email — same as `people-multi-source`.                                                 |
| `phone`             | Support → CRM → Finance → Marketing             | Support keeps the most current contact number — same as `people-multi-source` (Billing→Finance).                  |
| `address_line`      | Finance → CRM → Support → Marketing             | Finance has the legal billing address.                                                                            |
| `postal_code`       | Finance → CRM → Support → Marketing             | Same as `address_line`.                                                                                           |

Fields **not** listed in `mergeFields` (`legal_form`, `industry`, `employee_count`, `account_owner`, `lifecycle_stage`, `founded_year`) fall back to **consensus merge**: most-frequent value wins; ties broken by longest string. Enrichment fields like `account_owner` (CRM-only) and `lifecycle_stage` (Marketing-only) flow through to the golden record without being declared in `mergeFields`. The `source` column itself is excluded from the golden output.

## How merging works

For each field with a priority rule, the merger walks the priority list and takes the **first non-empty value from the highest-ranked source** present in the cluster. If no priority-listed source contributes a value, the merger falls back to consensus. When multiple rows in the same priority source have non-empty values, one of them is picked (the order is not contractual — design rows so they agree on the value, or accept either as correct).

## Worked example: Cluster 7 — Pinnacle Holdings

Cluster 7 (Pinnacle Holdings) is the cleanest illustration — all four sources contribute one row each, and four different sources own four different priority fields. The four input rows from `sample.csv`:

| id        | source    | organization_name          | domain_name             | email                        | phone           | address_line                      |
|-----------|-----------|----------------------------|-------------------------|------------------------------|-----------------|-----------------------------------|
| crm-050   | CRM       | Pinnacle Holdings          | pinnacleholdings.com    | sales@pinnacleholdings.com   | (312) 555-5050  | 1100 Lake Shore Drive             |
| mkt-051   | Marketing | Pinnacle Holdings LLC      | pinnacleholdings.com    | info@pinnacleholdings.com    | (312) 555-5051  | 1100 Lake Shore Drive             |
| sup-052   | Support   | Pinnacle Holdings LLC      | pinnacleholdings.com    | support@pinnacleholdings.com | (312) 555-5052  | 1100 Lake Shore Drive             |
| fin-053   | Finance   | Pinnacle Holdings, LLC     | pinnacleholdings.com    | ap@pinnacleholdings.com      | (312) 555-5053  | 1100 Lake Shore Drive Suite 1500  |

After the pipeline runs, the merger applies each field's priority list to produce the golden record:

| Field               | Priority list                           | Winner    | Golden value                       |
|---------------------|-----------------------------------------|-----------|------------------------------------|
| `organization_name` | **Finance** → CRM → Support → Marketing | `fin-053` | `Pinnacle Holdings, LLC`           |
| `domain_name`       | **Marketing** → CRM → Finance → Support | `mkt-051` | `pinnacleholdings.com`             |
| `email`             | **CRM** → Marketing → Support → Finance | `crm-050` | `sales@pinnacleholdings.com`       |
| `phone`             | **Support** → CRM → Finance → Marketing | `sup-052` | `+13125555052`                     |
| `address_line`      | **Finance** → CRM → Support → Marketing | `fin-053` | `1100 Lake Shore Drive Suite 1500` |

Four different sources contribute four different fields — the golden record is a *composite*, not a copy of any single input row. Phone is shown post-normalization (libphonenumber → E.164); see [Notes on normalization](#notes-on-normalization) below.

## Cluster catalog

Member IDs are prefixed by source (`crm-`, `mkt-`, `sup-`, `fin-`) so you can read them straight off the `member_ids` column in the golden output.

Each cluster below includes a Cypher query to inspect it in Neo4j after loading the export (see [Loading into Neo4j](#loading-into-neo4j)). The graph schema is:

- `(:Entity {id, source, organization_name, domain_name, email, phone, address_line, postal_code, legal_form, industry, employee_count, account_owner, lifecycle_stage, founded_year})` — one node per input row.
- `(:GoldenRecord {cluster_id, record_count, …merged fields})` — one node per cluster.
- `(:Entity)-[:RESOLVED_TO {cluster_size}]->(:GoldenRecord)` — links a member to its cluster.

Each query anchors on a known member id and pivots through `RESOLVED_TO` to surface every member alongside the merged golden values, so you can eyeball who contributed what.

### How clusters form

Two records end up in the same cluster when the native matcher's output graph connects them:

1. **Blocks** — blocking-gated candidate retrieval partitions records into buckets keyed by two blocking strategies: `exact-value` (keys on identifier-role/exact-typed fields such as `domain_name`, `email`, `phone`) and `token-name` (keys on the **last raw token** of `organization_name` — no article/leading-word removal, no sound-alike encoding). For example, `The Globex Corporation` and `Globex Corporation` both end in the token `corporation`, and both rows also share `domain_name=globex.com` — so they co-block on both keys. Only rows sharing a blocking key are compared.
2. **Scores** — identifier-weighted scoring compares each candidate pair: a shared exact strong-identifier field (e.g. domain, email, phone) floors the pair to a match (~0.98, the identifier floor); otherwise a weighted average of per-field similarity is computed. Pairs whose score reaches the auto-match cut (**0.90** by default) become edges in `matches.csv`.
3. **Unions** — a Union-Find clustering strategy merges connected components over those edges.

Step 3 makes cluster membership **transitive**: two records can land in the same cluster even if their *direct* similarity is below threshold, as long as a chain of above-threshold edges connects them.

**Verifying the sample:**

- `.\scripts\Run-Scenario.ps1 -Scenario samples\organizations-multi-source -Mode Cli` — runs the full pipeline locally without Azure, AppHost, or a hosted API, then asserts every cluster against `expectations.json`.
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
Singleton clusters return the source row unchanged. Priority logic is moot. **Expected golden:** Northbridge Capital Inc. with all `crm-001` values.

```cypher
MATCH (e:Entity {id: 'crm-001'})-[:RESOLVED_TO]->(g:GoldenRecord)
RETURN e.id AS member_id, e.source AS source,
       e.organization_name AS organization_name, e.domain_name AS domain_name,
       e.email AS email, e.phone AS phone,
       g.organization_name AS golden_org, g.domain_name AS golden_domain,
       g.email AS golden_email, g.phone AS golden_phone,
       g.record_count AS record_count;
```

### Cluster 2 — singleton, Marketing only
**Members:** `mkt-002`
Same as above but from a low-priority source. Confirms that priority is *not* a filter — Marketing-only data still appears in the golden output. **Expected golden:** Sycamore Press Group with all `mkt-002` values.

```cypher
MATCH (e:Entity {id: 'mkt-002'})-[:RESOLVED_TO]->(g:GoldenRecord)
RETURN e.id AS member_id, e.source AS source,
       e.organization_name AS organization_name, e.domain_name AS domain_name,
       e.email AS email, e.phone AS phone,
       g.organization_name AS golden_org, g.domain_name AS golden_domain,
       g.email AS golden_email, g.phone AS golden_phone,
       g.record_count AS record_count;
```

### Cluster 3 — 2 rows, CRM + Marketing
**Members:** `crm-010`, `mkt-011`

This cluster demonstrates **legal-suffix variation**: CRM has `Carbon Labs Corp` while Marketing dropped the suffix entirely (`Carbon Labs`). The records still cluster because they share an exact `domain_name` (`carbonlabs.io`) — a shared strong identifier floors the pair to a match even though the organization name differs by suffix (name normalization does not strip suffixes). Because Finance is absent, CRM steps into the #2 slot for `organization_name` and wins the name with the suffix intact. Marketing wins `domain_name` as the #1 source.

| Field               | Winner    | Why |
|---------------------|-----------|-----|
| `organization_name` | `crm-010` | Finance absent → CRM is #2 in `[Finance, CRM, Support, Marketing]` |
| `domain_name`       | `mkt-011` | Marketing is #1 |
| `email`             | `crm-010` | CRM is #1 |
| `phone`             | `crm-010` | Support absent → CRM is #2 in `[Support, CRM, Finance, Marketing]` |
| `address_line`      | `crm-010` | Finance absent → CRM is #2; both rows agree |
| `postal_code`       | (same)    | Both rows agree |

```cypher
MATCH (:Entity {id: 'crm-010'})-[:RESOLVED_TO]->(g:GoldenRecord)
MATCH (m:Entity)-[:RESOLVED_TO]->(g)
RETURN m.id AS member_id, m.source AS source,
       m.organization_name AS organization_name, m.domain_name AS domain_name,
       m.email AS email, m.phone AS phone,
       g.organization_name AS golden_org, g.domain_name AS golden_domain,
       g.email AS golden_email, g.phone AS golden_phone
ORDER BY m.id;
```

### Cluster 4 — 2 rows, Support + Finance
**Members:** `sup-020`, `fin-021`

This cluster demonstrates **article/prefix name noise surviving blocking**: the Support row has `The Globex Corporation` while the Finance row has `Globex Corporation`. Both values end in the same last token (`corporation`), so `token-name` blocking co-blocks them; they also share `domain_name` (`globex.com`), so `exact-value` blocking co-blocks them too. Finance wins `organization_name` (dropping the article), and Support wins `phone` as the #1 source.

| Field               | Winner    | Why |
|---------------------|-----------|-----|
| `organization_name` | `fin-021` | Finance is #1; value is `Globex Corporation` (no article) |
| `domain_name`       | `fin-021` | Marketing/CRM absent → Finance is #3; both rows agree on `globex.com` |
| `email`             | `sup-020` | CRM/Marketing absent → Support is #3 in `[CRM, Marketing, Support, Finance]` |
| `phone`             | `sup-020` | Support is #1 |
| `address_line`      | `fin-021` | Finance is #1; both rows agree |
| `postal_code`       | `fin-021` | Finance is #1; both rows agree |

```cypher
MATCH (:Entity {id: 'sup-020'})-[:RESOLVED_TO]->(g:GoldenRecord)
MATCH (m:Entity)-[:RESOLVED_TO]->(g)
RETURN m.id AS member_id, m.source AS source,
       m.organization_name AS organization_name, m.domain_name AS domain_name,
       m.email AS email, m.phone AS phone,
       g.organization_name AS golden_org, g.domain_name AS golden_domain,
       g.email AS golden_email, g.phone AS golden_phone
ORDER BY m.id;
```

### Cluster 5 — 3 rows, CRM + Support + Finance (each owns one field)
**Members:** `crm-030`, `sup-031`, `fin-032`

This is the three-source showcase cluster — each present source is the top-ranked source for at least one priority field. Marketing is absent, so `domain_name` falls through to CRM at position #2.

| Field               | Winner    | Why |
|---------------------|-----------|-----|
| `organization_name` | `fin-032` | Finance is #1; value is `Bluestone Logistics Inc.` (with period) |
| `domain_name`       | `crm-030` | Marketing absent → CRM is #2; all three rows agree on `bluestonelog.com` |
| `email`             | `crm-030` | CRM is #1 |
| `phone`             | `sup-031` | Support is #1 |
| `address_line`      | `fin-032` | Finance is #1; value is `1500 Mountain View Drive Suite 200` |
| `postal_code`       | `fin-032` | Finance is #1; all agree |

```cypher
MATCH (:Entity {id: 'crm-030'})-[:RESOLVED_TO]->(g:GoldenRecord)
MATCH (m:Entity)-[:RESOLVED_TO]->(g)
RETURN m.id AS member_id, m.source AS source,
       m.organization_name AS organization_name, m.domain_name AS domain_name,
       m.email AS email, m.phone AS phone, m.address_line AS address_line,
       g.organization_name AS golden_org, g.domain_name AS golden_domain,
       g.email AS golden_email, g.phone AS golden_phone, g.address_line AS golden_address
ORDER BY m.id;
```

### Cluster 6 — 3 rows, two CRM rows + one Marketing
**Members:** `crm-040`, `crm-041`, `mkt-042`

Demonstrates two things:
1. **Priority sources can have multiple rows.** Both CRM rows have identical values for priority-merged fields, so the output is deterministic — CRM wins all priority fields, Marketing loses every one.
2. **Consensus on non-priority fields with disagreement.** `legal_form` is `LLC` (×1: `crm-040`) vs `Limited Liability Co.` (×2: `crm-041`, `mkt-042`) → **majority wins → `Limited Liability Co.`**

| Field               | Winner    | Why |
|---------------------|-----------|-----|
| `organization_name` | `crm-040`/`crm-041` | Finance absent → CRM is #2; both CRM rows agree on `Quantum Robotics LLC` |
| `domain_name`       | `mkt-042` | Marketing is #1 |
| `email`             | `crm-040`/`crm-041` | CRM is #1; both CRM rows agree on `sales@quantumrobotics.ai` |
| `phone`             | `crm-040`/`crm-041` | Support absent → CRM is #2; both CRM rows agree on `+12065554040` |
| `legal_form`        | consensus | `Limited Liability Co.` wins by majority (2 vs 1) |

```cypher
MATCH (:Entity {id: 'crm-040'})-[:RESOLVED_TO]->(g:GoldenRecord)
MATCH (m:Entity)-[:RESOLVED_TO]->(g)
RETURN m.id AS member_id, m.source AS source,
       m.organization_name AS organization_name, m.legal_form AS legal_form,
       m.email AS email, m.phone AS phone,
       g.organization_name AS golden_org, g.legal_form AS golden_legal_form,
       g.email AS golden_email, g.phone AS golden_phone
ORDER BY m.id;
```

### Cluster 7 — 4 rows, all four sources
**Members:** `crm-050`, `mkt-051`, `sup-052`, `fin-053`

The full picture — every priority list resolves to a clear winner from a different source. See the [Worked example](#worked-example-cluster-7--pinnacle-holdings) above for the full breakdown.

| Field               | Winner    |
|---------------------|-----------|
| `organization_name` | `fin-053` |
| `domain_name`       | `mkt-051` |
| `email`             | `crm-050` |
| `phone`             | `sup-052` |
| `address_line`      | `fin-053` |
| `postal_code`       | `fin-053` (all agree) |

```cypher
MATCH (:Entity {id: 'crm-050'})-[:RESOLVED_TO]->(g:GoldenRecord)
MATCH (m:Entity)-[:RESOLVED_TO]->(g)
RETURN m.id AS member_id, m.source AS source,
       m.organization_name AS organization_name, m.domain_name AS domain_name,
       m.email AS email, m.phone AS phone, m.address_line AS address_line,
       g.organization_name AS golden_org, g.domain_name AS golden_domain,
       g.email AS golden_email, g.phone AS golden_phone, g.address_line AS golden_address
ORDER BY m.id;
```

### Cluster 8 — 4 rows, a clean 4-source merge with **one empty value that triggers fallback**
**Members:** `crm-060`, `mkt-061`, `sup-062`, `fin-063`

All four rows share `domain_name=meridiandx.com`, so the cluster forms as a single clean 4-source merge (CRM/Marketing/Support/Finance) into one golden record — the same shape as Cluster 7.

One intentional gap:
- `crm-060.email` is empty → email priority skips CRM #1 and lands on Marketing #2 (`mkt-061`).

Note: gaps intentionally avoid `organization_name` because it is the blocking key — an empty org name would prevent the row from joining any cluster.

| Field               | Winner    | Why |
|---------------------|-----------|-----|
| `organization_name` | `fin-063` | Finance is #1; value is `Meridian Diagnostics Inc.` |
| `domain_name`       | `mkt-061` | Marketing is #1; all rows agree on `meridiandx.com` |
| `email`             | `mkt-061` | CRM #1 is empty → Marketing #2 wins |
| `phone`             | `sup-062` | Support is #1 |
| `address_line`      | `fin-063` | Finance is #1; all rows agree on `400 Research Park Way` |
| `postal_code`       | `fin-063` | Finance is #1; all agree |

The query uses `coalesce(nullif(...))` so the empty `email` source value renders as `<empty>` instead of disappearing.

```cypher
MATCH (:Entity {id: 'crm-060'})-[:RESOLVED_TO]->(g:GoldenRecord)
MATCH (m:Entity)-[:RESOLVED_TO]->(g)
RETURN m.id AS member_id, m.source AS source,
       coalesce(nullif(m.email, ''), '<empty>') AS email,
       coalesce(nullif(m.domain_name, ''), '<empty>') AS domain_name,
       g.email AS golden_email, g.domain_name AS golden_domain
ORDER BY m.id;
```

### Cluster 9 — 3 rows, only Marketing present
**Members:** `mkt-070`, `mkt-071`, `mkt-072`

Marketing is the **lowest-priority source** for `organization_name`, `address_line`, and `postal_code`, but it is the only source present — so it wins everything by fallthrough. Demonstrates that priority lists describe *preference*, not *requirement*: if no preferred source contributes, the lowest-ranked source still produces the golden record. Because `account_owner` is a CRM-only enrichment field and CRM is absent, `account_owner` is empty in the golden record.

```cypher
MATCH (:Entity {id: 'mkt-070'})-[:RESOLVED_TO]->(g:GoldenRecord)
MATCH (m:Entity)-[:RESOLVED_TO]->(g)
WITH g, collect(DISTINCT m.source) AS sources_present, collect(m) AS members
UNWIND members AS m
RETURN m.id AS member_id, m.source AS source,
       m.organization_name AS organization_name, m.domain_name AS domain_name,
       m.email AS email, m.phone AS phone,
       g.organization_name AS golden_org, g.domain_name AS golden_domain,
       g.email AS golden_email, g.phone AS golden_phone,
       sources_present
ORDER BY m.id;
```

### Cluster 10 — 5 rows, mixed sources, **`legal_form` length tiebreaker**
**Members:** `crm-080`, `crm-081`, `sup-082`, `mkt-083`, `mkt-084`

The largest cluster. Priority fields resolve straightforwardly:

| Field               | Winner    | Why |
|---------------------|-----------|-----|
| `organization_name` | `crm-080`/`crm-081` | Finance absent → CRM is #2; both CRM rows agree on `Arcadia Software` |
| `domain_name`       | `mkt-083`/`mkt-084` | Marketing is #1; all rows agree on `arcadiasoft.com` |
| `email`             | `crm-080`/`crm-081` | CRM is #1; both CRM rows agree on `sales@arcadiasoft.com` |
| `phone`             | `sup-082` | Support is #1; value is `+16505558081` |
| `address_line`      | `crm-080`/`crm-081` | Finance absent → CRM is #2; all rows agree on `3000 Hanover Street` |

Consensus on `legal_form`: `Inc` (×2: `crm-080`, `crm-081`), `Incorporated` (×2: `sup-082`, `mkt-083`), `Inc.` (×1: `mkt-084`) → 2-2 tie between `Inc` and `Incorporated` → **length tiebreaker → `Incorporated` wins** (12 chars beats 3).

```cypher
MATCH (:Entity {id: 'crm-080'})-[:RESOLVED_TO]->(g:GoldenRecord)
MATCH (m:Entity)-[:RESOLVED_TO]->(g)
RETURN m.id AS member_id, m.source AS source,
       m.organization_name AS organization_name, m.legal_form AS legal_form,
       m.email AS email, m.phone AS phone,
       g.organization_name AS golden_org, g.legal_form AS golden_legal_form,
       g.email AS golden_email, g.phone AS golden_phone
ORDER BY m.id;
```

## Running it end-to-end

The default standalone path runs locally through the CLI and writes artifacts plus outputs under `samples\organizations-multi-source\output`.

```powershell
.\scripts\Run-Scenario.ps1 -Scenario samples\organizations-multi-source -Mode Cli
```

Expected: 28 input records, 10 golden records, and all checks in `expectations.json` pass.

You can also run the CLI directly:

```powershell
dotnet run --project src\Linkuity.Cli -- run `
  --input samples\organizations-multi-source\sample.csv `
  --profile samples\organizations-multi-source\organizations-multi-source.profile.json `
  --merge-policy samples\organizations-multi-source\organizations-multi-source.merge.json `
  --output samples\organizations-multi-source\output `
  --neo4j-export
```

Because this sample's profile is byte-equivalent to the built-in `organization` profile, `--profile organization` works identically in place of the file path above.

The HTTP API completes the same run synchronously via `POST /run`. It requires the API
to be running locally at `http://localhost:5017`.

```powershell
curl.exe -X POST http://localhost:5017/run `
  -F "profile=<samples\organizations-multi-source\organizations-multi-source.profile.json" `
  -F "merge-policy=<samples\organizations-multi-source\organizations-multi-source.merge.json" `
  -F "file=@samples\organizations-multi-source\sample.csv;type=text/csv" `
  -o golden-records.csv
Get-Content golden-records.csv
```

See [`docs/http-api.md`](../../docs/http-api.md) for the full `/run` contract.

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
- **Domain** — lowercased and trimmed. Sample domains are already lowercase.
- **Phone** — parsed by libphonenumber and reformatted to E.164. Sample input `(212) 555-0101` becomes `+12125550101` in the golden output.
- **`organization_name`** — trimmed only. No suffix stripping, no case folding. `Carbon Labs Corp` and `Carbon Labs` produce different normalized values; they cluster because a shared strong identifier (`domain_name`) floors the pair to a match, not because of `organization_name` normalization.
- **Address / postal** — trimmed only.

The per-cluster "Winner" tables above identify rows by ID, so they hold regardless of how normalization rewrites the underlying string.
