# review-queue

A focused durable scenario that proves the three incremental-matching **threshold bands**
in a single batch: one record auto-merges, one lands in the human-review queue, and one is
left as a brand-new singleton.

## The story

The hub is seeded (via `persist-batch`) with one prior CRM record, `crm-300`
(Erin Estrada), in its own cluster. A single incremental batch of three Web records then
arrives, each engineered to land in a different band against that one existing record:

1. **Auto-match (>= 0.90).** `web-300` shares an exact **email**
   (`erin.estrada@example.com`) with `crm-300`, scoring 0.98. It auto-merges into the
   existing cluster.
2. **Review (>= 0.75, < 0.90).** `web-301` has the **same name** (Erin Estrada) as
   `crm-300` but a **different email and phone**. The scenario's matching profile
   ([`review-queue.profile.json`](review-queue.profile.json), the built-in `person` profile
   with the `email`/`phone` weights lowered to `0.3`) down-weights exact-contact
   *disagreement*, so a strong name match whose contact simply differs scores 0.80 — the
   review band. The hub does **not** merge it; it opens a `review_threshold` task and leaves
   the record as its own cluster for a human to adjudicate.
3. **Singleton (no shared key).** `web-302` (Zoe Zimmer) shares no email, phone, or
   surname token with anything, so it becomes an unmatched singleton cluster.

## What puts each record in its band

| Record | Shared key with `crm-300` | Score | Band |
| --- | --- | --- | --- |
| `web-300` | exact email | 0.98 | auto-merge |
| `web-301` | same name, different contact | 0.80 | review task |
| `web-302` | none | — | singleton |

## Why `Singleton clusters: 2`

Candidate matches are generated only against **existing persisted** records, so the three
incoming records never match each other — only `crm-300`. A record that lands in the
review band is **not** merged: it stays in its own cluster while a review task is opened.
So the singleton count is 2 — the review record (`web-301`) plus the genuine no-match
record (`web-302`) — even though only one of them is an "unrelated" singleton.

## Run it

```pwsh
pwsh -File scripts/Run-DurableScenario.ps1 -ScenarioPath samples/durable/review-queue
```

Expect `All 10 checks passed.` Add `-KeepArtifacts` to leave the temp working directory
and its `metadata.json` (entityRecords, matchEdges, clusters, reviewTasks) on disk for
inspection.
