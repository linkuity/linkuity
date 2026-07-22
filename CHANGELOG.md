# Changelog

All notable changes to Linkuity are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project aims to follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
While Linkuity is pre-1.0 (beta), minor versions may include breaking changes.

## [Unreleased]

### Changed
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
