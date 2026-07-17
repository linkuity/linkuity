# Linkuity Performance Testing Plan

**Status:** Active. Created 2026-07-05; last updated 2026-07-06.
**Owner:** MDM scale track.
**Scope:** End-to-end ingest performance and outcome-correctness-at-scale validation, following Milestone 26.

> **Current state (2026-07-06):** the review-task write-amplification bottleneck that the first A/B run uncovered is **resolved** — Milestone 27 gated the review floor on real similarity (per-batch ingest fell ~7×; review_tasks 5.97M → 0 on the 100k run), and Milestone 27.1 made the gate profile-configurable (`reviewFloorGate`). The remaining, incremental throughput headroom is catalogued in **[Future Improvements](#future-improvements--remaining-ingest-throughput-levers)** at the end of this document.

---

## Testing environment (REQUIRED for every test in this plan)

**All tests use the native local PostgreSQL instance — not Docker:**

```
Host=localhost;Port=54320;Username=postgres;Password=postgres
```

- Measurements must run on an **idle box** (verify CPU is low; no competing dotnet/test/build processes). Absolute per-batch throughput on a Windows box is dominated by PostgreSQL checkpoint/fsync variance — the **scaling shape** (flat per-batch time, bounded memory) is the environment-independent property to validate, not the absolute milliseconds.
- Use dedicated databases per run (e.g. `linkuity_perf_a`, `linkuity_perf_b_dop1`, `linkuity_perf_b_dopN`) so runs never clobber each other and the default `postgres` database stays clean.
- **Large runs must report occasional progress.** Any test ingesting large volumes runs as a background process that streams per-batch progress to an output file; the operator checks in and surfaces interim progress (batches done / cumulative records / elapsed) at intervals rather than going dark until completion.
- CPU-only matching micro-benchmarks (no Postgres) use `measure-matching`; end-to-end pipeline tests (with Postgres writes/commits/fsync) use `measure --backend postgres`.

---

## Where we are now

- **Ingest correctness is proven** across backends: `Linkuity.Mdm.ConformanceTests` passes 37/37 with the Postgres store at `IngestParallelism = 8` versus the File store at DOP=1 (run against the local Postgres). Parallel edge production is byte-identical to sequential.
- **Per-record matching cost is understood and improved.** Milestone 25 localized the ingest bottleneck to Lucene candidate stored-field retrieval (>90% of per-record cost at `maxCandidates=50`). Milestone 26 fixed it: leaner candidate reconstruction (1.27× sequential) plus per-thread committed readers turned the parallel path from 0.26× (a ~4× regression) into **3.33× at 20 cores**. `IngestParallelism` now defaults to `ProcessorCount`.
- **What we have measured:** the CPU-only `measure-matching` instrument at **corpus = 100,000 records** with an incoming **batch = 1,000 records**, dop=1 vs dop=20, best-of-5, reproduced twice, before vs after M26 in the same session.

### The gap this plan closes

We have **not** yet exercised the *real* pipeline at volume — actual Postgres COPY/upserts, per-batch commits, fsync, incremental Lucene indexing, and working-set growth across many batches — with parallel ingest **on by default**. Nor have we validated outcome-identity between sequential and parallel end-to-end (beyond the small conformance facts) or tested on realistically skewed data. That is what the options below address.

## What we have done to get to this point (history)

- **Milestone 23** — delivered the scalable PostgreSQL durable backend (index-backed candidate retrieval; bounded per-batch ingest) and the `Linkuity.Mdm.Benchmarks` harness (`generate` + `measure`).
- **Milestone 25** — built parallel edge production in `IncrementalResolver` (deterministic index-ordered reduce), concurrency-safe Lucene retrieval, the `IngestParallelism` knob, the CPU-only `measure-matching` instrument, and a permanent conformance parity gate at DOP=8. Measurement showed the naive parallel path *regressed* ~4-5× (shared-reader stored-field serialization), so it shipped **dormant** (default sequential). Artifact: `docs/roadmap/measurements/2026-07-03-parallel-ingest/`.
- **Milestone 26** — unlocked the win. Stage 1: retrieved candidates became a scoring projection (stopped storing blocking keys; read only `id/project_id/source_record_id/fields` via a field-limited visitor). Stage 2: per-thread committed readers so concurrent stored-field reads stop serializing. Measured 3.33× parallel scaling; flipped `IngestParallelism` on by default; wired the conformance gate to the local Postgres. Artifact: `docs/roadmap/measurements/2026-07-05-ingest-retrieval-cost/`.

---

## Option A — Throughput scaling end-to-end (do first, with B)

**Goal:** validate the *shape* of end-to-end Postgres ingest under the now-default parallel path — per-batch time stays flat as the corpus grows, and working-set memory stays bounded — using the full pipeline (matching + DB writes + commits + incremental Lucene indexing).

**Method:** `generate` synthetic datasets at increasing sizes in fixed 1k batches, then `measure --backend postgres --ingest-parallelism <cores>` against the local instance, capturing the per-batch `elapsed_ms` and `peak_ws_mb` the harness reports. Read the curve, not the absolute ms.

- [ ] Confirm the box is idle (CPU low, no competing processes) and Postgres :54320 is reachable.
- [ ] Generate datasets at increasing sizes in 1k batches (e.g. 50k, 100k, 200k; extend to 400k+ if the curve is clean), seed fixed for reproducibility.
- [ ] For each size, create a dedicated database and run `measure --backend postgres --ingest-parallelism <ProcessorCount>` (background, streaming per-batch progress to a file; report interim progress).
- [ ] Record per-batch `elapsed_ms` and `peak_ws_mb` per size; plot/inspect the curve.
- [ ] Verify **flat per-batch time** (per-batch elapsed does not grow with cumulative corpus size) and **bounded memory** (peak working set does not grow unbounded across batches).
- [ ] Capture a headline (largest clean size, per-batch median, memory ceiling) and note fsync-variance caveats honestly.
- [ ] Write results to `docs/roadmap/measurements/2026-07-05-e2e-scale/` (or a dated sibling).

## Option B — Outcome correctness at scale (do first, with A)

**Goal:** prove the on-by-default parallel path produces **identical outcomes** to sequential at volume — beyond the small conformance facts, where hot blocking keys and large cluster merges actually occur.

**Method:** ingest the same large dataset twice — once at `--ingest-parallelism 1`, once at `ProcessorCount` — into two dedicated databases, then compare **ID-independent invariants** (record IDs differ per run, so compare content/shape, not raw IDs).

- [ ] Pick a dataset size with meaningful duplication and hot keys (e.g. 100k, duplicate-rate ~0.2).
- [ ] Create two dedicated databases: `linkuity_perf_b_dop1` and `linkuity_perf_b_dopN`.
- [ ] Ingest the identical dataset into each — one at `--ingest-parallelism 1`, one at `ProcessorCount` (background, progress reported).
- [ ] Compare ID-independent invariants between the two runs and assert identical:
  - [ ] golden record count
  - [ ] cluster count and cluster-size distribution (histogram)
  - [ ] review-task count
  - [ ] a canonical-content hash of all golden records (sorted by content, volatile IDs stripped)
- [ ] If any invariant differs, treat it as a correctness defect (systematic-debugging), not a perf note.
- [ ] Optionally, measure matching precision/recall against the generator's known duplicate ground truth at this scale.
- [ ] Record the comparison in the same measurement artifact as A.

## Option C — Realistic data (later, separate effort)

**Goal:** stress matching quality and performance on non-uniform, skewed, messy data (hot names, missing fields, format variance) closer to production than the uniform synthetic generator.

- [ ] Select a representative public entity dataset (people or organizations) with license suitable for the repo/samples.
- [ ] Add an adapter/importer to load it through the ingest pipeline (or convert to the `generate` DTO shape).
- [ ] Run end-to-end ingest on the local Postgres; capture throughput curve and memory (as in A).
- [ ] Assess matching quality on realistic skew (spot-check clusters, hot-blocking-key behavior, false merges/splits).
- [ ] Decide whether findings warrant tuning work (blocking strategy, `--max-candidates`, decision bands).

---

## Relationship to Milestone 19

Milestone 19 (Performance And Scale Validation) in `docs/roadmap/PLAN.md` is essentially **A + B formalized at millions-of-records scale** with bounded-memory exit criteria, incremental Lucene indexing behavior, and configurable-limit validation. The runs in this plan are the immediate, smaller-scale execution of that milestone's intent; if the curves are clean, scaling A/B to millions is the Milestone 19 deliverable.

## Progress-reporting convention (for all large runs)

1. Run the ingest/measure command as a background process; stream its per-batch output to a named file.
2. The harness emits a `[progress]` line to stderr every 25 batches (`batch N/total  cumulative=…  lastBatchMs=…  peakWsMb=…`) plus the final per-batch table.
3. While a long run is in flight, check the output file at intervals and surface interim progress (batches completed, cumulative records, elapsed, current memory) — do not go dark until completion.
4. On completion, report the full curve and the pass/fail against the flat-time / bounded-memory / outcome-identity criteria.

---

## Findings — 2026-07-05 (first A/B run)

**Run:** 100,000 records, 1,000-record batches, backend=postgres against local :54320, `--ingest-parallelism 1` (sequential baseline), db `linkuity_perf_b_dop1`.
**Status:** Option A (throughput scaling) — shape validated on the dop=1 run. Option B (dop=1 vs dop=20 outcome parity) — **pending** (the dop=20 run and the invariant comparison were not completed; a bottleneck was found first and prioritized).

### Headline

Matching is **not** the bottleneck (Milestone 26 fixed it). End-to-end ingest cost is dominated by **review-task write amplification**: the 100k-record ingest produced **5,967,681 review tasks (3.0 GB)** — ~60k rows written per 1,000-record batch. That write volume, plus its WAL and the checkpoints it forces, is what makes each batch take ~17s (spiking to ~40s during forced checkpoints). M26's parallel matching win is real but end-to-end-invisible: it sped up the ~30% that was CPU; the ~70% that is I/O/writes was untouched.

### Shape (Option A) — healthy

Per-batch elapsed: warm-up 5s → steady **16–18s** → **28–40s bursts** when a forced checkpoint lands → back to ~18s. It does **not** grow with cumulative corpus. Memory bounded ~400–480 MB. So **scaling is fine (flat per-batch, bounded memory); the constant is just high.**

### Evidence (100k dop=1 run)

| table | rows | size | note |
|---|---|---|---|
| **review_tasks** | **5,967,681** | **3053 MB** | the problem — 50× the entity data |
| entity_records | 100,000 | 57 MB | the actual data |
| golden_record_versions | 81,846 | 26 MB | |
| golden_records | 79,323 | 22 MB | ~79k clusters (mostly singletons + small; max cluster 30) |
| match_edges | 35,131 | 20 MB | **all ≥0.90 (auto)**; zero edges in the review band |
| Lucene index | — | 17 MB | single segment, committed every batch |

### Root cause of the review-task volume

- **~60 review tasks per incoming record.** Each batch's 1,000 records generate ~60k review-band candidate pairs against the durable corpus, each persisted as an `open` row. It **plateaus** at ~60k/batch (min 7,392 first batch → ~60k steady) because durable retrieval is capped at `maxCandidates=50` — the cap bounds it, which is why per-batch time is flat rather than climbing.
- **Every review task scored exactly 0.800** (avg = min = max = 0.800 across all 5.97M). One matching signal — records sharing a single hot blocking key/field — produces a constant 0.800, which sits inside the review band (0.75–0.90). Hot blocking keys mean each record collides weakly with ~50–60 others, and every collision becomes a persisted review task.
- **Not a dedup bug** — all 5.97M pairs are distinct `(new_entity_record_id, candidate_entity_record_id)`. It is genuine over-generation: the pipeline persists *every* review-band pair, uncapped on the within-batch side (the "within-batch fan-out cap" deferred in the M25/M26 spec). It is also a **precision problem** — 6M open tasks is not human-triageable.

### Secondary factors (PostgreSQL config, amplifying — not causing)

- `synchronous_commit = on` → every batch commit fsyncs WAL (~30 MB of review rows per batch).
- `max_wal_size = 1GB` vs 3 GB of writes → **15 forced checkpoints** (plus 188 timed) → the 28–40s batch spikes.
- `shared_buffers = 128MB` (stock default, tiny).
- Lucene `Commit()` every batch → a second per-batch fsync alongside Postgres's.
- Windows fsync is slow; every one of the above pays it.

### Per-batch attribution (~17s steady, evidence-backed estimate; no pg_stat_statements)

- Matching (dop=1) ~5s (~30%); ~1.6s at dop=20.
- Review-task writes + their WAL: the largest share (~60k rows/batch, jsonb `breakdown` + 2 FK indexes).
- Other DB writes (entity COPY, edges, goldens, versions): small (<130 MB combined).
- Per-batch fsyncs (Postgres WAL commit + Lucene commit) + periodic checkpoint storms: the rest and the spikes.

### Highest-leverage levers (in rough impact order)

1. **Stop generating ~60 review tasks per record** — the big one, and a correctness lever. Cap within-batch fan-out; and/or do not persist every review-band pair (aggregate/dedupe or keep top-K per record); and/or revisit why a lone shared key yields a review-worthy 0.800. This collapses the 3 GB and most of the WAL/checkpoint load at once. **→ subject of the follow-up investigation.**
2. Batch/COPY review-task inserts if still wanted at volume.
3. PostgreSQL config tuning (no app code): raise `max_wal_size`, raise `shared_buffers`, optionally relax `synchronous_commit` for bulk ingest.
4. Lucene commit cadence — commit the index less often than per batch to remove one per-batch fsync.

### Root cause of the review-task explosion (confirmed 2026-07-05, systematic-debugging)

The explosion is a **design/precision issue, not an arithmetic bug** — a three-part interaction, evidenced against the 100k `linkuity_perf_b_dop1` run:

1. **The scoring floor (primary).** The person profile uses `ScoringStrategy = "identifier-weighted"` (`IdentifierAwareWeightedScoringStrategy`), which returns `max(0.80, weighted)` for any candidate without an exact-identifier (email/phone) match — `ReviewFloor = 0.80`. Its stated assumption is *"every scored candidate already shares a blocking key, so a shared blocking key alone reaches the review band."* So **every blocking-key-sharer that is not an auto-match is floored into the review band (exactly 0.80), regardless of how weak the real similarity is.** There is no path from "shares a blocking key but is clearly a different entity" to NoMatch. This is why every one of the 5.97M review tasks scores exactly 0.800, and why the stored `score` (0.80) ≠ the breakdown sum (~0.12) — the floor overrides the weighted average, by design (the class comment says so).
2. **Low-cardinality blocking keys.** Retrieval returns candidates *because* they share a blocking key. A surname-only key like `name:lopez` is shared by ~60 people in 100k records. Blocking is a *recall* filter (cast wide); the floor treats every catch as review-worthy *confidence*. That conflation is the flaw.
3. **Uncapped review-task persistence.** Every review-band pair is persisted (`CreateBatchReviewTasks`). Retrieval fan-out (`maxCandidates=50` durable + uncapped within-batch mates) × the floor = ~50–60 review tasks per incoming record → 5.97M.

**Evidence.** A representative task pairs *Daniel Lopez* (daniel.lopez26@yahoo.com / (240) 803-6638) with *Robert Lopez* (robert.lopez5@gmail.com / (722) 119-3118) — different people whose only shared blocking key is `name:lopez`; weighted similarity 0.118, floored to 0.80. Of 181,604 sampled review tasks, **all score exactly 0.80 and zero fall in (0.80, 0.90)** — the entire review queue is floor-driven, not genuine medium-confidence matches. So review-band precision here is effectively zero, and the queue (6M "same surname, different person") is untriageable — a correctness problem as much as a performance one.

This subsumes the M25/M26 deferred *"within-batch fan-out cap"* item, but the deeper lever is the floor semantics themselves.

### Fix option space (to be brainstormed — changes matching outcomes, needs a deliberate design)

- **Don't floor on a single common blocking key** — require ≥2 shared blocking keys, or a minimum weighted similarity, before flooring to review; or make the floor conditional on blocking-key *selectivity* (rare key → review, common surname → not).
- **Separate recall from review-worthiness** — keep retrieval wide but only *persist* review tasks above a real similarity bar (or top-K per record) instead of every floored pair.
- **Cap within-batch fan-out** (the deferred item) — bounds volume but does not fix precision on its own.
- **Down-weight / drop low-cardinality blocking keys** (e.g., surname-only) as review evidence.

### Resolution (2026-07-05)

Fixed by **gating the review floor on real similarity** (`IdentifierAwareWeightedScoringStrategy`: the 0.80 review floor now applies only when weighted similarity `≥ 0.75`; below that a pair keeps its raw score → NoMatch). Same 100k ingest, re-run: **review_tasks 5,967,681 → 0**, while **clusters / golden_records / match_edges are byte-identical** (79,323 / 79,323 / 35,131) — perfectly surgical. Per-batch time fell **~7×** (21,099 ms → 3,027 ms mean); the 3.0 GB review_tasks table is gone. Zero review tasks is the correct result for this dataset (all 5.97M pre-fix tasks were the exact-0.80 floor with none in (0.80, 0.90); the synthetic data has no genuinely-ambiguous pairs). Full evidence: `docs/roadmap/measurements/2026-07-05-review-task-precision/` (Milestone 27).

**Milestone 27.1 (2026-07-06)** then made the gate **profile-configurable**: an optional `reviewFloorGate` matching-profile key (default `0.75`, range `[0,1]`). Lower it below a profile's `reviewThreshold` to re-enable promotion of strongly-evidenced sub-threshold pairs into review; leave it at the default for maximum review precision. The value is read end-to-end through `IncrementalResolver` (a wiring gap that would have made the knob a no-op on the real ingest path was caught in review and is guarded by an end-to-end regression test).

---

## Future Improvements — remaining ingest throughput levers

After Milestone 27 collapsed the review-task write amplification (the dominant cost — per-batch ingest dropped ~7×), the remaining wall-clock is mostly PostgreSQL I/O overhead. So the biggest wins now are **configuration**, not application code, and all of these are incremental relative to the review-task fix. Listed in rough bang-for-buck order; each is a *candidate* for a future measurement/design pass, not a committed task.

### 1. PostgreSQL configuration tuning — no app code; per-deployment; biggest immediate win

These are the secondary factors measured during the 100k diagnosis (`synchronous_commit=on`, `max_wal_size=1GB`, `shared_buffers=128MB` — all stock defaults):

- **`shared_buffers`** (default 128 MB, tiny) → raise toward ~25% of host RAM for write caching.
- **`max_wal_size`** (default 1 GB) → raise (e.g. 4–8 GB) so bulk ingest triggers far fewer *forced* checkpoints (the 100k run forced 15 — the source of the per-batch spikes).
- **`synchronous_commit`** (`on`) → for a bulk load, `off` (or grouped commit) removes the per-batch WAL fsync. **Durability tradeoff:** a crash can lose the last few *committed* batches — acceptable for a re-runnable bulk ingest, not as a permanent steady-state default. Best applied as an ingest-session setting, not baked in.

Effort: near-zero (config, or a documented "bulk-ingest" tuning profile). Risk: low, except the deliberate durability choice on `synchronous_commit`.

### 2. Batch-size tuning — existing `--batch-size` knob; measurement-driven

Batches are 1k today. Larger batches (5k–10k) amortize the fixed per-batch costs (transaction commit, checkpoint pressure, DB round-trips, the per-batch Lucene commit) over more records → higher throughput, at the cost of a larger per-batch working set and transaction. The right size is empirical — cheap to measure by re-running the `measure` harness at a few sizes. Effort: low (measurement only). Risk: low.

### 3. Lucene commit cadence — real code lever, but needs its own design

`IndexRecords` commits the Lucene index **every batch**, a second per-batch fsync alongside Postgres's. Committing less often would remove one fsync per batch — but it is **not** a trivial change: each batch's records must be visible to the *next* batch's candidate retrieval, and Milestone 26 switched to committed-directory readers precisely for that guarantee. Deferring commits within a run would need a within-run visibility strategy (e.g. a near-real-time reader for the in-progress run plus a periodic durable commit). A genuine lever, but it warrants its own brainstorm rather than a config tweak. Effort: medium. Risk: medium (correctness of cross-batch matching).

### 4. Native Linux PostgreSQL — environment, not code

The Windows dev box's fsync is slow; the *scaling shape* (flat per-batch, bounded memory) is environment-independent, but absolute throughput would be materially better on a native Linux PostgreSQL (noted since M19/M23). Not a code lever — a deployment choice for production-representative numbers.

### 5. Deferred / high-risk — listed for completeness; not recommended yet

- **Multi-batch / concurrent-batch ingest** — breaks the sequential corpus-visibility contract (concurrent batches see different corpus states) and still serializes on a single Postgres's fsync. High risk, uncertain gain. Deferred in M25/M26.
- **Further matching-cost reduction** (lower `--max-candidates`, leaner candidate reconstruction) — matching is only ~30% of the end-to-end batch now, and lowering `max-candidates` trades recall. Diminishing returns.

**Recommended first step:** a low-risk measurement pass combining **#1 + #2** (tune `shared_buffers`/`max_wal_size`, test a couple of batch sizes, optionally an ingest-only `synchronous_commit=off`) on the local Postgres — no production code, and it quantifies how much headroom is actually left. Pursue **#3** only if #1+#2 are insufficient.
