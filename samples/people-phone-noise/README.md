# People — Phone-Noise Sample

A 10-row dataset demonstrating Linkuity's `participatesInMatching: false` flag. Phone numbers are declared (so they are normalized to E.164 and exported to the Neo4j graph) but **excluded from matching**, modeling a real-world case where phones are unreliable for identity (shared landlines, recently-recycled numbers, port confusion).

## What this sample exercises

- **Phone genuinely changes the cluster boundary.** The James/Marcus pair (`crm-001`, `crm-002`) are twins sharing last name, household phone, and address, but they differ on `date_of_birth` and every other field. With phone **excluded** from matching (`participatesInMatching: false`), no strong identifier is shared between the pair — the weighted field-similarity score stays below the auto-match cut (**0.90**) and they stay separate. Flip `phone.participatesInMatching` to `true` (the default) and the shared phone becomes a strong identifier that **floors the pair to a match**, false-merging the twins into one golden record. The flag is doing real work on this row pair.
- **Phone is still normalized.** Raw `(212) 555-9999` becomes `+12125559999` in the golden record output, even though it doesn't participate in matching.
- **Phone is still exported to Neo4j.** `phones.csv` and `has-phone.csv` populate in the Neo4j ZIP — the per-entity phone relationship is preserved.
- **Other clusters form on the remaining signal.** Name + email + address + DOB are sufficient to merge the three legitimate duplicate pairs (Emma, Frank, Iris) regardless of whether their phones agree.

Note: in `matches.csv`, a pair sharing an exact strong-identifier field (e.g. `date_of_birth` for Emma/Frank/Iris) scores in the auto-match band at the identifier floor (~0.98), not a "perfect" 1.0.

The contrast is pinned by `SampleScenarioTests.PhoneNoise_TwinsStaySeparateWhenPhoneExcluded` and `SampleScenarioTests.PhoneNoise_TwinsMergeWhenPhoneIncluded` in `src/Linkuity.Cli.Tests` — the tests load this sample (the second with phone's `participatesInMatching` flag flipped to `true`) and assert both halves of the boundary flip.

## Files

- `sample.csv` — 10 input rows, 7 distinct people, 2 sources (CRM + Marketing).
- `match-config.json` — standalone CLI/API job configuration. `phone` and `source` carry `participatesInMatching: false`.
- `expectations.json` — assertions for `Run-Scenario.ps1`, including the `neo4jExports.nonEmpty` block that verifies Neo4j export populated.

## Cluster catalog

The sample produces 7 golden records:

| Cluster | Members | Phone scenario | What it proves |
|---|---|---|---|
| James Smith | `crm-001` | twins — same household phone (`+12125559999`) as Marcus, but different `date_of_birth` and no other shared identifier | flag prevents false-merge: with phone excluded, no shared identifier floors the pair and the weighted score stays below the 0.90 auto-match cut → separate; with phone included, the shared phone identifier floors the pair to a match → false-merge |
| Marcus Smith | `crm-002` | twins — same household phone as James | same boundary flip, viewed from Marcus's side |
| Emma Edwards | `crm-010`, `mkt-011` | phones disagree wildly (`0300` vs `9999`) | the pair shares an exact `date_of_birth` — that identifier alone floors the pair to a match, so phone disagreement is irrelevant |
| Frank Foster | `crm-020`, `mkt-021` | phones agree (`0400`) but don't participate | the pair shares an exact `date_of_birth` identifier — clean cluster regardless of phone |
| Iris Ito | `crm-030`, `mkt-031` | phones differ by 1 digit (`0700` vs `0701`) | shared `date_of_birth` identifier floors the match; the one-digit phone variation is moot since phone doesn't participate |
| Greg Green | `crm-040` | unique phone | CRM-only singleton |
| Henry Hill | `mkt-041` | unique phone | Marketing-only singleton |

## What to check in the output

After running the pipeline:

1. **Golden records phone field** — `output/golden-records.csv` shows phones in E.164 form (`+12125550100`, not `(212) 555-0100`). This proves normalization ran on the non-matching field.
2. **Neo4j export** — `output/neo4j-export/phones.csv` lists distinct phone values; `output/neo4j-export/has-phone.csv` links each entity to its phone. Both are non-empty. This proves the export resolved phone via semantic-type lookup, regardless of `participatesInMatching`.

## Run the scenario

From the repo root:

```powershell
.\scripts\Run-Scenario.ps1 -Scenario samples\people-phone-noise -Mode Cli
```

Expected: all checks pass — `goldenRecordCount = 7`, 7 cluster member checks, ~14 `expectedFields.*` checks (phone in E.164 across clusters), 2 `neo4jExports.nonEmpty` checks.

## Inspecting the result in Neo4j

After the standalone CLI run completes, `output/neo4j-export/load.cypher` plus the sibling CSVs can be loaded into a Neo4j instance (drop the CSVs into Neo4j's `import/` directory, then run `:source load.cypher`). Because the job's `contentType` is `person`, every entity and golden-record node carries a `:Person` label alongside the umbrella `:Entity` / `:GoldenRecord` label. The queries below use the umbrella labels so they also work on graphs imported from an `organization` job.

### Overview — all 7 golden records and their members

Confirms the cluster topology of the sample at a glance.

```cypher
MATCH (g:GoldenRecord)<-[:RESOLVED_TO]-(m:Entity)
RETURN g.cluster_id AS cluster_id,
       g.first_name AS golden_first,
       g.last_name AS golden_last,
       g.phone AS golden_phone,
       count(m) AS member_count,
       collect(m.id) AS member_ids
ORDER BY member_count DESC, golden_last;
```

Expected: 7 rows. Emma, Frank, and Iris are 2-member clusters; the rest (James, Marcus, Greg, Henry) are singletons.

### Pin #1 — James and Marcus stay in separate clusters despite the shared household phone

The core assertion of the sample: `crm-001` (James) and `crm-002` (Marcus) share the same E.164 phone (`+12125559999`) but are not duplicates — they differ on `date_of_birth` and every other field. With `phone.participatesInMatching: false`, no field shared between them floors the pair to a match, and the weighted field-similarity score stays below the 0.90 auto-match cut, so they stay apart. If this query returns `clustered_together: true`, the flag has stopped working.

```cypher
MATCH (a:Entity {id: 'crm-001'})-[:RESOLVED_TO]->(g1:GoldenRecord)
MATCH (b:Entity {id: 'crm-002'})-[:RESOLVED_TO]->(g2:GoldenRecord)
MATCH (a)-[:HAS_PHONE]->(p:Phone)
MATCH (b)-[:HAS_PHONE]->(p)
RETURN a.first_name AS left_first, a.last_name AS left_last,
       b.first_name AS right_first, b.last_name AS right_last,
       p.value AS shared_phone,
       g1.cluster_id = g2.cluster_id AS clustered_together,
       g1.cluster_id AS left_cluster, g2.cluster_id AS right_cluster;
```

Expected: one row, `shared_phone: "+12125559999"`, `clustered_together: false`. The `MATCH (a)-[:HAS_PHONE]->(p:Phone) MATCH (b)-[:HAS_PHONE]->(p)` pattern verifies they really do connect to the same `:Phone` node.

### Pin #2 — Emma's two rows merged despite phone disagreement

`crm-010` and `mkt-011` are the same person but have wildly different phones (`+12125550300` vs `+12125559999`). They still cluster because name, email, address, and DOB agree — the phone column is normalized and exported but doesn't drive matching.

```cypher
MATCH (:Entity {id: 'crm-010'})-[:RESOLVED_TO]->(g:GoldenRecord)
MATCH (m:Entity)-[:RESOLVED_TO]->(g)
OPTIONAL MATCH (m)-[:HAS_PHONE]->(p:Phone)
RETURN m.id AS member_id, m.source AS source,
       m.first_name AS first_name, m.last_name AS last_name,
       p.value AS source_phone,
       g.first_name AS golden_first, g.last_name AS golden_last,
       g.phone AS golden_phone
ORDER BY m.id;
```

Expected: 2 rows (`crm-010`, `mkt-011`), each with a different `source_phone`, both sharing the same `golden_phone` (Support priority resolves the winner from the configured priority list).

### Phone graph — every entity links to its phone, including the non-matching ones

Confirms that `phone.participatesInMatching: false` does NOT prevent the phone from being exported. James and Marcus both connect to the same shared `:Phone` node; everyone else has their own.

```cypher
MATCH (e:Entity)-[:HAS_PHONE]->(p:Phone)
RETURN p.value AS phone,
       count(e) AS entity_count,
       collect(e.id) AS entity_ids
ORDER BY entity_count DESC, phone;
```

Expected: 7 distinct phone values total. `+12125559999` has 2 entities (`crm-001`, `crm-002` — the shared household phone); all others have 1 entity each.
