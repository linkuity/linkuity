# Changelog

All notable changes to Linkuity are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project aims to follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
While Linkuity is pre-1.0 (beta), minor versions may include breaking changes.

## [Unreleased]

### Added
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
- Blocking-audit suppression boundary aligned with the engine's corpus-frequency
  count: the engine (`blocking-linear`) counts a key's frequency over a corpus
  that excludes the query record, so a full block of exactly `maxBlockSize + 1`
  records stays active. The audit previously suppressed on whole-block size and
  wrongly reported such blocks as suppressed. Report wording now states the
  corpus-frequency criterion. (Showcase effective ceiling unchanged at 88.9%.)

### Removed
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
