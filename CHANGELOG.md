# Changelog

All notable changes to Linkuity are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project aims to follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
While Linkuity is pre-1.0 (beta), minor versions may include breaking changes.

## [Unreleased]

### Added
- Record corrections (F6 milestone 1): resending a record through
  `ingest-incremental` with the same `(project, source record id)` but different
  field values now supersedes the prior record and re-enters matching — an
  unclustered record is simply re-scored, a clustered record detaches from its
  cluster (dissolving it if it was the only member) with the golden record
  recomputed from the remaining survivors, and an identical resend is a safe
  no-op rather than an error. `IncrementalIngestResult.RecordsCorrected` reports
  how many corrections a batch applied. Scope is intentionally narrow for this
  milestone: only the file metadata store's non-Lucene-indexed path supports it
  (an indexed store throws `NotSupportedException` instead of silently leaving a
  stale candidate searchable); the PostgreSQL backend does not yet apply
  corrections either, and throws rather than dropping them silently. Deletion is
  not addressed by this milestone.
- Record deletion (F6 milestone 2): a new `record delete` CLI command marks a
  `(project, source record id)` record `DeletedAt` and detaches it from its
  cluster — an unclustered record is simply tombstoned, a clustered record's
  cluster recomputes its golden record from the remaining survivors
  (dissolving the cluster if it was the only member) — reusing the same
  `DetachFromCluster` primitive corrections use. `IMetadataStore.ListRecordDeletedEventsAsync`
  exposes the audit trail. Same scope as corrections: the file metadata
  store's non-Lucene-indexed path only (an indexed store throws
  `NotSupportedException`, including via the CLI, which always attaches an
  index for durable commands); the PostgreSQL backend does not yet support
  deletion either. `--source-record-id` accepts a comma-separated list to
  delete multiple records in one call.
- PostgreSQL backend for record corrections and deletion (F6 milestone 3): both
  now work on Postgres on the same terms as the file store — an index-attached
  store throws `NotSupportedException` for either, so the CLI (which always
  attaches an index) still can't reach either through it yet; real usage is
  against a non-indexed store until Lucene reindexing lands. Getting there
  required removing a duplicate-source-record-id check in
  `PostgresMetadataStore.ValidateIncrementalRequestAsync` that was the actual
  mechanism blocking every correction (not `PostgresMutationApplier`'s guard,
  as previously documented), and fixing a genuine Postgres-specific gap: since
  Postgres tracks cluster membership as a foreign key on the record rather
  than a list on the cluster, a corrected-away or deleted record's stale
  `cluster_id` had to be explicitly cleared — nothing before this milestone
  ever needed to, since every prior mutation path always moved a departing
  record onto some new cluster. `record_corrected_events` and
  `record_deleted_events` tables back `ListRecordCorrectedEventsAsync`/
  `ListRecordDeletedEventsAsync` on Postgres.
- Lucene exclusion for corrected/deleted records (F6 milestone 4, closing the F6
  series): the `NotSupportedException` guards from milestones 1-3 are lifted on
  both the file and PostgreSQL backends, so correction and deletion now work on
  an index-attached store — the case the CLI always exercises, since it always
  attaches a Lucene index for durable commands. A correction removes the
  superseded record's Lucene document and indexes the correcting record; a
  deletion removes the deleted record's document; neither leaves a stale
  candidate searchable. Also fixes a drift-check bug this exposed: both
  backends' "is the index current?" recovery check compared the live index's
  document count against every stored record including tombstoned ones, which
  would have made the very next ingest call rebuild the index from all records
  — corrected/deleted ones included — silently undoing the exclusion. The
  comparison (and the rebuild it triggers) now counts only live records
  (`SupersededAt`/`DeletedAt` both null). Two crash-consistency gaps found in
  review are also closed: the durable write (JSON save / SQL commit) now always
  happens before the corresponding Lucene mutation, not after — for a
  correction specifically, superseding one record while adding another is a
  net-zero change in live document count, which the drift check cannot detect
  either way, so committing Lucene first risked it durably serving a
  "corrected" record that, had the durable write then failed, never actually
  existed in the store, with no self-healing path back; committing the durable
  write first means the worst case is a stale-but-real index entry the drift
  check can still detect and repair. `DeleteRecordsAsync` on both backends now
  also runs the drift check before mutating the index, matching
  `SaveIncrementalIngestAsync` — previously only the ingest path could
  self-heal a pre-existing drift. A second review round found and fixed two
  more gaps unlocked by lifting the guard: `IncrementalResolver.
  CreateBatchReviewTasks` had no liveness check on its candidate endpoint
  (unlike the auto-match path, which already skips edges whose endpoints
  aren't in `clusterByRecord`), so a correction whose changed field landed the
  comparison against its own not-yet-removed Lucene predecessor in the
  review band — not the auto band — created a permanent `ReviewTask`
  referencing a dead record; this path was unreachable before an index-backed
  store could apply corrections, and now gets the same liveness check the
  auto-match path already had. And `PostgresMetadataStore` had no equivalent
  of `FileMetadataStore`'s write-serializing gate around its shared,
  singleton `LuceneCandidateRetrieval`, so the newly-reachable
  `DeleteRecordsAsync`/correction calls could race concurrent Lucene
  mutations against each other and against `Retrieve` — both against that
  class's own documented "mutations must not run concurrently with Retrieve"
  invariant; `PostgresMetadataStore` now has the equivalent gate.
- `match corpus fields` CLI command: measures how much each of your columns is
  actually worth for matching, on your own data, so a matching profile is built
  from measurement rather than guesswork. Per matchable field it reports fill
  rate (declared `nullEquivalents` count as unfilled), how often records of the
  SAME entity agree, how often records of DIFFERENT entities agree anyway, the
  resulting evidence in bits, and a plain verdict. The two agreement rates are
  printed side by side because neither means anything alone — a column the same
  entity agrees on 99.8% of the time is worthless if unrelated records agree
  99.9% of the time too. Also reports what the data can never resolve: records
  identical on every matchable field that belong to different entities, and
  which fields were empty throughout the largest such group — the direct answer
  to "what would we have to collect to separate these". Rates come from the same
  calibration service the engine scores with, never measured separately. See
  [docs/choosing-match-fields.md](docs/choosing-match-fields.md).
- `canonical-jaccard` similarity evaluator: token-set Jaccard computed on
  canonicalized organization names (leading articles dropped, trailing legal
  suffixes stripped, ampersand initials collapsed — the same canonicalizer
  blocking uses), so `THE BOEING COMPANY` vs `BOEING CO` scores 1.0 instead
  of 0.25, while unrelated names sharing only noise tokens (`THE`, `COMPANY`)
  drop toward 0. Names that are canonically identical but differ only in
  token boundaries (e.g. `AMAZON.COM` vs `AMAZON COM`, both compressing to
  `AMAZONCOM`) score 1.0 via a compressed-form equality check — the same
  multiple-representation practice used by commercial entity-resolution
  engines. Fields whose semantic type has no registered canonicalizer fall
  back to plain token Jaccard.
- `match corpus audit` CLI command: measures recall and precision at corpus scale
  against a labelled ground truth without materializing a full candidate pair set —
  reachability, direct auto recall, post-cluster pairwise recall, and cluster
  pairwise precision, stratified by canonical-name overlap — plus a frozen-baseline
  gate (`--write-baseline` / `--compare-baseline`) that pins every evaluation input
  by SHA-256 and exits non-zero when a comparable run regresses.
- `match scoring audit` and `match scoring explain` CLI commands: score every
  blocked candidate pair under a profile (batch blocking-linear fidelity) and
  report band outcomes, direct-edge precision/recall/F1 against a held-out
  ground truth, a threshold sweep over distinct observed scores, a
  blocking-miss vs scoring-miss decomposition, and per-field diagnostics for
  under-scored true pairs and near-threshold false pairs. `--format csv` emits
  a pair-identity-sorted table built for diffing runs across a config change.
- `match blocking audit` and `match blocking explain` CLI commands: measure the
  blocking-recall ceiling against a held-out ground truth (raw and, when
  `maxBlockSize` is set, suppression-adjusted "effective" ceiling), with
  per-strategy attribution, missed-pair listing, and a `--min-recall` CI gate.
- Three new organization blocking strategies — `fingerprint` (canonicalized,
  sorted token-set key), `token` (per-token rare-key blocking), and `acronym`
  (initials-based key, e.g. `SOUTHWESTERN BELL CORP` ↔ `SBC`) — plus an
  organization-name canonicalizer (drops leading articles, strips a curated
  list of trailing legal-entity suffixes) feeding `fingerprint` and `token`.
- `maxBlockSize` profile field: an absolute cap on how many records a single
  blocking key may match before it's suppressed entirely (distinct from
  `MaxCandidates`, which caps ranked retrieval per query, not key frequency).
  Off by default; the built-in `organization` profile and the
  company-resolution showcase are the first consumers (`50`).

### Changed
- `match corpus audit` in report-only mode now exits **1** when a quality gate
  fails, instead of printing the failure and exiting 0. A run that merged records
  belonging to different entities was previously indistinguishable from a clean
  one to anything reading exit codes, CI included. Exit 2 still means "could not
  run", and `--write-baseline` still exits 0 deliberately — recording a
  known-bad run is how a reference point gets established at all.
- Wrong merges are now gated absolutely rather than relative to a previous run.
  A run fails if ANY merged pair joins records that ground truth says are
  different entities; there is no threshold and none is configurable, because a
  threshold answers "how many wrong merges are acceptable" and the answer is
  none. Records that cannot be told apart from the available fields are not an
  allowance to merge them wrongly — such a pair belongs in review, or apart.
- The built-in `organization` matching profile now scores `organization_name`
  with `canonical-jaccard` (previously `fuzzy`; weight unchanged). This is a
  **behavior change** for anyone relying on the built-in organization
  profile's scoring, in both batch and durable matching (custom
  `*.profile.json` files are unaffected). On the company-resolution showcase
  (which also adopts it), 7 previously under-scored true pairs graduate to
  auto-match: recall 79.2% → 88.9% at unchanged 100% precision.
- The built-in `organization` matching profile now blocks on
  `["exact-value", "fingerprint", "phonetic", "token", "acronym", "ngram"]`
  with `maxBlockSize: 50`, replacing the previous
  `["exact-value", "token-name"]`. This is a **behavior change** for anyone
  relying on the built-in organization profile's default blocking (custom
  `*.profile.json` files are unaffected). The built-in `person` profile is
  unchanged. See `docs/how-matching-works.md` and
  `showcases/company-resolution/README.md` for the measured effect (recall
  ceiling 87.5% → 88.9% effective / 94.4% raw on the company-resolution
  benchmark).
- **Breaking:** `match-config.json` is retired. Batch `linkuity run` and
  `POST /run` now take a **matching profile** (`--profile` / `profile` — a
  built-in name like `person`/`organization`, or a `*.profile.json` file) and an
  optional **merge policy** (`--merge-policy` / `merge-policy`, a `*.merge.json`
  file). This is the same profile/merge-policy format durable ingest already used
  (`--content-type`/`--profiles`/`--merge-policy`), so there is now exactly one
  configuration format across the whole product — and custom taxonomies (not just
  the built-in `person`/`organization`) are now supported in batch. See
  [`docs/configuration.md`](docs/configuration.md) for the full schema.

  Before (retired): a single `--config` flag pointed at one `match-config.json`
  bundling both matching fields and merge rules. After, a profile plus an
  optional merge policy:
  ```powershell
  linkuity run --input sample.csv `
    --profile sample.profile.json --merge-policy sample.merge.json `
    --output ./out
  ```
- HTTP API now completes batch matches end-to-end via synchronous `POST /run`
  (multipart `profile` + optional `merge-policy` + `file` → golden records CSV),
  sharing the CLI's batch engine.

### Fixed
- `SaveCompletedBatchAsync` indexed Lucene before its durable write committed, on
  both the file and PostgreSQL backends (#85). This is the same crash-consistency
  defect class F6 milestone 4 fixed on the correction and deletion paths, but on
  the bulk "completed batch" import path, which that milestone never touched — so
  it predates it rather than being introduced by it. The discriminator on this
  path is not detectability — a completed batch is a pure insert, so either
  ordering leaves a live-count mismatch the drift check could see, unlike a
  correction's net-zero supersede-plus-add — but what the index serves in the
  meantime. Retrieval reconstructs candidates from index documents rather than
  looking them up in the store, so indexing first and then failing the durable
  write (JSON save / SQL commit) makes Lucene hand out records that never reached
  the store at all, which then get scored into edges and clusters referencing ids
  nothing can resolve. Committing the durable write first inverts that into the
  conservative failure: the records exist but are temporarily unsearchable,
  costing missed matches rather than phantom ones. Both backends now commit first
  and index after, matching `SaveIncrementalIngestAsync`/`DeleteRecordsAsync`.
  `SaveCompletedBatchAsync` also now runs the drift check (`EnsureIndexCurrent`/
  `EnsureIndexCurrentAsync`) before mutating the index, which it previously never
  did on either backend — so the bulk-import path could not self-heal at all, and
  a batch-only workflow (which never calls the ingest or deletion paths) would
  carry a drift left by an earlier crash indefinitely. It runs before the batch's
  own mutations are applied, so the batch's not-yet-indexed records don't
  themselves read as drift and trigger a rebuild on every call. A codebase-wide scan for the
  same pattern found no other occurrences: the only remaining index mutations are
  those two already-fixed paths and the drift check's own recovery rebuild, which
  has no paired durable write. Both fixed call sites are now pinned by a
  regression test that observes the durable store from inside the index mutation
  itself (reading the committed JSON file on the file backend, querying over a
  separate connection outside the still-open transaction on Postgres) — asserting
  index contents after the call returns cannot distinguish the two orderings,
  since both end in identical state on the success path.
- Record correction bugs found in post-merge review of F6 milestone 1 (#63): (1)
  `FileMetadataStore.ApplyMutations` could orphan a golden record against a
  tombstoned cluster when one ingest batch corrected both members of a
  2-member cluster — the clear-by-cluster-id ran before, not after, the
  golden-record upsert loop, so it couldn't catch an upsert queued earlier in
  the same batch for the same now-dead cluster; (2) `DetachFromCluster`'s
  tombstone branch wrote a cluster's stale, pre-batch membership instead of
  the batch's pending-reduced membership, inconsistent with its sibling
  "survivors keep cluster id" branch; (3) `IncrementalIngestResult.RecordsAdded`
  double-counted corrected records (a correction was reported as both "added"
  and "corrected"); (4) `FileMetadataStore.UpdateBatchRecordCount` over-reported
  a batch's stored `RecordCount` by including no-op resends that are dropped
  before resolution.
- `GoldenRecordMerge.MergeFields` could silently drop a cluster member's field
  value during consensus/priority merge: it looks up each member's value by one
  canonical field-name casing, which only works if every member's `Fields`
  dictionary is case-insensitive — true for freshly-normalized records, false
  for any record reloaded from the JSON file store (`System.Text.Json`
  deserializes dictionaries with the default, case-sensitive comparer
  regardless of the original). Field lookups are now case-insensitive
  regardless of the caller's dictionary comparer. Also fixed: the merged
  record's field key order was arrival-order-dependent; it's now sorted
  deterministically.
- `ingest-incremental` crashed with an unhandled `NotSupportedException` (raw
  stack trace, no top-level handler) on any correcting resend through the CLI.
  The CLI always attaches a Lucene index to the metadata store so durable
  commands exercise indexed retrieval like production, but index-backed
  correction isn't supported yet (tracked separately); the resulting guard's
  exception type just wasn't in the CLI's list of gracefully-handled
  exceptions. Now fails cleanly with an error message and exit code 2, matching
  every other "not yet supported on this backend" case.
- Golden-record field values could depend on the order records arrived in (F54): when
  multiple cluster members shared the highest-priority source but disagreed on a
  field's value, or a consensus vote tied on both count and length, the merge picked
  whichever value happened to be enumerated first rather than resolving on content.
  Fixed by breaking every tie on field content (majority, then longest, then
  alphabetical), proven with permutation tests. This also closed a related gap: the
  batch (`run`, `POST /run`) and durable (`ingest-incremental`, `persist-batch`)
  paths used to carry two independently-maintained copies of this merge logic that
  had quietly drifted apart on case-sensitivity of consensus grouping, corpus-wide
  vs. cluster-local field scope, a hardcoded vs. configurable source-field name, and
  blank-value checking. Both paths now share one implementation
  (`Linkuity.Core.Merge.GoldenRecordMerge`), so they cannot drift apart again.
- Blocking-audit suppression boundary aligned with the engine's corpus-frequency
  count: the engine (`blocking-linear`) counts a key's frequency over a corpus
  that excludes the query record, so a full block of exactly `maxBlockSize + 1`
  records stays active. The audit previously suppressed on whole-block size and
  wrongly reported such blocks as suppressed. Report wording now states the
  corpus-frequency criterion. (Showcase effective ceiling unchanged at 88.9%.)

### Removed
- F1 from `match scoring audit` output, its threshold sweep, and the
  `ScoringMetrics`/`ThresholdSweepRow` records. F1 weights precision and recall
  equally, which is the wrong objective when a wrong merge is not an acceptable
  trade for a found match: an F1 of 0.30 reads as a tuning dial when it is in
  fact two thirds of merges being wrong. Replaced by the review-queue size and
  recall including review, reported alongside precision and recall separately.
- The multi-step job API (`/jobs/*`), the in-process/Azure Service Bus dispatch
  machinery, the two-lane queue, and the `Linkuity.Worker` post-processing host.
  Azure Blob Storage remains an optional artifact-store backend.

## [0.1.0-beta.1]

Initial public beta.

- Local CLI batch runner (`linkuity run`): normalize, match, cluster, merge
  golden records, and optionally export a Neo4j-ready bundle — no server or
  external services required.
- Durable MDM projects with incremental matching (`linkuity ingest-incremental`)
  and project-level merge policy.
- First-class PostgreSQL durable metadata-store backend, plus a JSON-backed
  local store for samples and small projects.
- Native .NET matching engine: blocking, phonetic strategies,
  similarity-weighted scoring, Lucene candidate retrieval, and persisted score
  breakdowns with `match explain`.
- Configurable matching profiles for person and organization domains.
- Docker Compose private-server batch path.
- Optional Azure-compatible batch API and adapters
  (`Linkuity:RuntimeMode=Azure`).
