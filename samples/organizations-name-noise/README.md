# Organizations — Name-Noise Sample

An 8-row dataset pinning two org-specific name-noise corner cases that the broader `organizations-multi-source` sample defers: ampersand variants and common-name disambiguation by `domain_name`. The contrasts are pinned by `SampleScenarioTests.OrgNameNoise_AcmesStaySeparateWithDomain` and `SampleScenarioTests.OrgNameNoise_AmpersandVariantsCluster` in `src/Linkuity.Cli.Tests`.

## What this sample exercises

- **Common-name disambiguation by `domain_name`.** Two near-twin records — `Acme Corp` (`acmecorp.com`) and `Acme Corporation` (`acmecorporation.com`) — share the same address (`100 Wall Street Suite 500`, postal `10005`) and email-local-part convention (`info@...`), but differ on `domain_name`, `email`, and `phone`, and end in different last-name tokens (`corp` vs `corporation`). They share **no blocking key**: `exact-value` blocking (keyed on `domain_name`/`email`/`phone`) finds nothing in common, and `token-name` blocking (keyed on the last token of the org name) sees `corp` vs `corporation` — also no match. With no shared blocking key, the pair is never co-blocked and never scored against each other, so they stay as **two separate golden records**. Domain (along with email, phone, and the differing name token) is the disambiguating signal for this near-duplicate pair — if the pair also shared a blocking key (e.g. domain), they would be scored together and could floor to a false-merge.
- **Ampersand-variant robustness.** Three rows of the same law firm written three different ways — `Smith & Jones LLP` / `Smith and Jones` / `Smith Jones` — all merge into one golden record. They share an exact `domain_name` (`smithjoneslaw.com`) — that shared strong identifier floors the trio to a match regardless of the punctuation/conjunction variation in the name. Source priority resolves the canonical name to `Smith & Jones LLP` (CRM wins).
- **Routine clustering still works alongside the noise.** A clean multi-source duplicate (`Northbridge Capital`, CRM + Marketing rows that agree on every field) merges normally, and a lonely `Quartz Analytics` singleton passes through to its own golden record. These control entries confirm the corner cases above haven't broken anything else.

Note: in `matches.csv`, any pair sharing an exact strong-identifier field (e.g. `domain_name`, like the Smith & Jones trio) scores in the auto-match band at the identifier floor (~0.98), so don't be surprised to see 0.98 rather than a "perfect" 1.0.

## Files

- `sample.csv` — 8 input rows, 5 distinct organizations, 3 sources (CRM + Marketing + Support).
- `match-config.json` — standalone CLI/API job configuration. All six match fields participate in matching with uniform `["CRM", "Marketing", "Support"]` source priority.
- `expectations.json` — assertions for `Run-Scenario.ps1`, including the `neo4jExports.nonEmpty` block that pins the org export populated.

## Cluster catalog

The sample produces 5 golden records:

| Cluster | Members | Sources | What it proves |
|---|---|---|---|
| Acme Corp | `crm-001` | CRM | Common-name disambiguation: near-twin record of `Acme Corporation`, distinguished by domain, email, phone, and a different last-name token (`corp` vs `corporation`). No shared blocking key means the pair is never co-blocked or scored together, so it stays separate. |
| Acme Corporation | `mkt-002` | Marketing | Same as above viewed from the other side of the near-twin pair. |
| Smith & Jones LLP | `crm-010`, `mkt-011`, `sup-012` | CRM, Marketing, Support | ampersand-variant robustness — `&` / `and` / nothing all cluster because the trio shares an exact `domain_name` identifier; CRM's `Smith & Jones LLP` wins by source priority |
| Northbridge Capital | `crm-020`, `mkt-021` | CRM, Marketing | clean control cluster — no name noise, no domain ambiguity |
| Quartz Analytics | `crm-030` | CRM | control singleton — distinct everything |

## What to check in the output

After running the pipeline:

1. **Smith & Jones canonical name.** `output/golden-records.csv` has one row for the law firm and its `organization_name` is `Smith & Jones LLP` (not `Smith and Jones` or `Smith Jones`) — proves source priority resolved across the three variant spellings.
2. **Two distinct Acme golden records.** `output/golden-records.csv` has separate rows for `Acme Corp` and `Acme Corporation` — proves the pair stays disambiguated despite the shared address and email-local-part convention, since the pair shares no blocking key (differing identifiers, differing last-name token) and so is never compared.
3. **Neo4j export populated.** `output/neo4j-export/entities.csv` lists the 5 distinct organizations; `output/neo4j-export/emails.csv` and `output/neo4j-export/has-email.csv` are populated. Confirms the org-shape export pipeline ran end-to-end.

## Run the scenario

From the repo root:

```powershell
.\scripts\Run-Scenario.ps1 -Scenario samples\organizations-name-noise -Mode Cli
```

Expected: all checks pass — `goldenRecordCount = 5`, 5 cluster member checks, 15 `expectedFields.*` checks, 3 `neo4jExports.nonEmpty` checks.

## Inspecting the result in Neo4j

After the standalone CLI run completes, `output/neo4j-export/load.cypher` plus the sibling CSVs can be loaded into a Neo4j instance (drop the CSVs into Neo4j's `import/` directory, then run `:source load.cypher`). Because the job's `contentType` is `organization`, every entity and golden-record node carries an `:Organization` label alongside the umbrella `:Entity` / `:GoldenRecord` label. The queries below use the umbrella labels so they also work on graphs imported from a `person` job.

### Overview — all 5 golden records and their members

Confirms the cluster topology of the sample at a glance.

```cypher
MATCH (g:GoldenRecord)<-[:RESOLVED_TO]-(m:Entity)
RETURN g.cluster_id AS cluster_id,
       g.organization_name AS golden_name,
       g.domain_name AS golden_domain,
       count(m) AS member_count,
       collect(m.id) AS member_ids
ORDER BY member_count DESC, golden_name;
```

Expected: 5 rows. Smith & Jones has 3 members, Northbridge has 2, the two Acme rows and Quartz are singletons.

### Pin #1 — the two Acme records stay in separate clusters

The core assertion of the sample: `Acme Corp` and `Acme Corporation` share no blocking key — they differ on `domain_name`, `email`, and `phone` (no `exact-value` match) and end in different last-name tokens, `corp` vs `corporation` (no `token-name` match) — so they are never co-blocked or scored against each other. If this query returns `clustered_together: true`, the disambiguation has broken.

```cypher
MATCH (a:Entity {id: 'crm-001'})-[:RESOLVED_TO]->(g1:GoldenRecord)
MATCH (b:Entity {id: 'mkt-002'})-[:RESOLVED_TO]->(g2:GoldenRecord)
RETURN a.organization_name AS left_org, a.domain_name AS left_domain,
       b.organization_name AS right_org, b.domain_name AS right_domain,
       g1.cluster_id = g2.cluster_id AS clustered_together,
       g1.cluster_id AS left_cluster, g2.cluster_id AS right_cluster;
```

Expected: `clustered_together: false`. Two different `cluster_id`s, one row each in their respective clusters.

### Pin #2 — the Smith & Jones triplet merged into one golden record

Three different ampersand spellings (`Smith & Jones LLP` / `Smith and Jones` / `Smith Jones`) all resolved to the same cluster. The CRM row's name (`Smith & Jones LLP`) wins source priority and becomes the canonical golden value.

```cypher
MATCH (:Entity {id: 'crm-010'})-[:RESOLVED_TO]->(g:GoldenRecord)
MATCH (m:Entity)-[:RESOLVED_TO]->(g)
RETURN m.id AS member_id, m.source AS source,
       m.organization_name AS source_name,
       g.organization_name AS golden_name,
       g.domain_name AS golden_domain
ORDER BY m.id;
```

Expected: 3 rows (`crm-010`, `mkt-011`, `sup-012`), each with a different `source_name`, all sharing `golden_name: "Smith & Jones LLP"` and `golden_domain: "smithjoneslaw.com"`.

### Control — Northbridge clean cluster

A routine 2-source merge with no name noise and no domain ambiguity. Confirms the corner-case handling above hasn't broken normal clustering.

```cypher
MATCH (:Entity {id: 'crm-020'})-[:RESOLVED_TO]->(g:GoldenRecord)
MATCH (m:Entity)-[:RESOLVED_TO]->(g)
RETURN m.id AS member_id, m.source AS source,
       m.organization_name AS organization_name,
       g.organization_name AS golden_name,
       g.domain_name AS golden_domain;
```

Expected: 2 rows (`crm-020`, `mkt-021`), both with `golden_name: "Northbridge Capital"` and `golden_domain: "northbridgecap.com"`.
