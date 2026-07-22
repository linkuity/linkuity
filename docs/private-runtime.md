# Private Runtime

This document describes Linkuity's private-runtime direction and the current limits of the codebase. It is a roadmap guide, not a claim that every private-runtime feature is already implemented.

## Direction

Linkuity is an open-source runtime that data and MDM teams run where their data already lives.

The product principles are:

- No Linkuity-hosted customer data is required.
- No hosted Linkuity SaaS is the default product.
- Production code is open source.
- Customers can run Linkuity locally, on private servers, or in their own cloud accounts.
- Azure is available as an optional adapter.

## Target Runtime Modes

### Local CLI Mode

Local CLI mode will run a full deduplication and golden-record job from local files:

```text
input CSV + match config -> local artifacts -> matching -> clustering -> golden records
```

This mode is intended for evaluation, samples, consultants, one-off jobs, and teams that want a simple private path before operating a server deployment.

### Private Server Mode

Private server mode will run Linkuity as services on customer-managed infrastructure. The first target is a Docker Compose deployment with explicit data directories and backup responsibilities.

This mode is intended for teams that want internal APIs, repeatable jobs, private operations, and durable MDM projects.

### Customer Cloud Mode

Customer cloud mode means the customer runs Linkuity in their own cloud account. Azure support can remain available, but it should be an adapter choice rather than a required architecture.

Future adapters may support other object stores, queues, or database-backed storage depending on demand.

## Data Ownership

In the private-runtime model:

- Customers control where input data, job artifacts, metadata, and golden records are stored.
- Customers control backup, retention, deletion, and access policies.
- Linkuity does not need to receive or store customer datasets.
- Paid support can focus on operational help, implementation packages, certified builds, connectors, training, and advisory work.

## What Works Today

The default, end-to-end private path is the **local CLI**. `linkuity run` normalizes,
matches with the native .NET matching engine, clusters, merges golden records, and can
optionally export a Neo4j-ready bundle — with no server, queue, or external process:

```powershell
dotnet run --project src/Linkuity.Cli -- run --input <input.csv> --profile <person|organization|profile.json> [--merge-policy <merge.json>] --output <dir>
```

See [`docs/configuration.md`](configuration.md) for the profile and merge-policy schema.

On top of that, the current codebase supports durable local MDM metadata (JSON-backed or a
first-class **PostgreSQL** backend), incremental ingest (`linkuity ingest-incremental`),
durable project merge policy, and a Docker Compose private-server batch path.

The HTTP API also completes a batch match end to end today, synchronously: `POST /run`
takes a multipart request (a `profile` part, an optional `merge-policy` part, and a `file`
CSV part) and streams back the merged golden records as `text/csv`, sharing the same
normalize -> match -> cluster -> merge pipeline (`BatchRunService`) that the CLI uses — no
polling, no separate dispatch step. Because within-batch matching is O(n^2)-ish, synchronous input is capped at 400 KiB;
larger inputs get a 400 response with guidance to use the CLI instead. An asynchronous
variant of this endpoint (for larger inputs) is planned as an additive future addition —
it does not exist today. Azure Blob Storage remains available as an artifact-store adapter
when `Linkuity:RuntimeMode=Azure` — it changes only where job artifacts are read and
written, not how matching runs. Local development can still exercise the Azure Blob
adapter wiring with .NET Aspire and Azurite, without an Azure account.

## Scale And Performance

For projects that grow beyond a few tens of thousands of records, use the **PostgreSQL** durable backend (`--metadata-store postgres --connection-string <connstr>`, or `Linkuity:MetadataStore=Postgres`). It keeps incremental ingest bounded — per-batch time and memory do not grow with total project size — because candidate retrieval is index-backed and the ingest transaction touches only the selected candidates and their clusters. The JSON (`File`) store rewrites the whole database file on every mutation (O(total-records) time and memory), so it is best kept for samples, evaluation, and small/development projects.

Operational tuning knobs:

- **`--max-candidates` (default 50)** — the cap on candidates considered per incoming record.
- **`ingest-incremental --batch-size <n>`** — ingest a large CSV in `n`-record chunks. This bounds the working set of each `SaveIncrementalIngestAsync` call (candidate loads, resolution, mutations, DB round-trips) to `n` records; note the input CSV itself is currently read into memory up front, so it does not yet bound the raw-file read.
- **`--ingest-parallelism <n>` (default = all cores; on)** — degree of parallelism for the per-record matching loop on the Postgres path. Concurrent Lucene retrieval uses per-thread committed readers and leaner candidate reconstruction (measured 3.33× vs sequential at 20 cores), so parallel edge production is on by default; set to 1 to force sequential. Outcome-neutral (conformance parity at DOP=8).

For what these knobs *do* — the candidate limit's recall/work trade-off, hot blocking keys, and the two-levers point — see [`docs/how-matching-works.md`](how-matching-works.md) (Tuning and troubleshooting).

To reproduce or extend the scale numbers, use the `Linkuity.Mdm.Benchmarks` harness (`generate` + `measure`). Note that absolute per-batch throughput measured on a Windows/Docker dev box is dominated by PostgreSQL checkpoint/fsync I/O variance; measure on a native Linux PostgreSQL for production-representative throughput. The *scaling shape* (flat per-batch time, bounded memory) is environment-independent and is the property to validate.

## Merge Policy Modes

Standalone `run` is a self-contained local batch job. It takes a matching profile
(`--profile`) and an optional merge policy (`--merge-policy`) directly, and writes job
artifacts plus `golden-records.csv` to the requested output directory. This path
remains useful for samples, evaluation jobs, and one-off analysis because it does not
require a durable project or metadata database.

Durable MDM projects store merge policy on the project. Project create/read/update surfaces expose that policy, and durable workflows use it when creating current golden records and versions. `persist-batch` imports completed batch artifacts into durable metadata, but durable canonical values are computed from the project merge policy. `ingest-incremental` uses the same project policy when new records update affected clusters, keeping source-priority fields consistent across full durable imports and incremental ingests.

## What Is Planned

Planned work:

- **Enterprise readiness** — release process, checksummed/signed artifacts, SBOM,
  backup/restore, security, upgrade, and support-policy documentation.
- **Reporting and export at scale** — bounded, paginated, filtered read-back and export so
  large durable projects can be reported on beyond the current interim read-back guardrail.

## Current Limitations

Current limits:

- The local CLI and Docker Compose private-server batch path are the default private-runtime paths.
- The HTTP API completes a batch match end to end via synchronous `POST /run`, but is capped at 400 KiB of input because within-batch matching is O(n^2)-ish; larger inputs need the CLI. An asynchronous variant for larger inputs is planned, not yet implemented.
- Azure Blob Storage is available as an artifact-store adapter through `Linkuity.Infrastructure.Azure` when `Linkuity:RuntimeMode=Azure`.
- Aspire remains optional tooling for API + Azurite blob-emulator development.
- Durable MDM metadata runs on either a JSON-backed local store (best for samples, evaluation, and small projects) or the PostgreSQL backend (recommended beyond a few tens of thousands of records; `--metadata-store postgres`).

## Azure Compatibility

Azure support is useful for teams that want it. Azure is optional infrastructure:

- Azure Blob Storage is one artifact-store adapter in `Linkuity.Infrastructure.Azure`.
- Azure Container Apps is one deployment option.

The private runtime does not require Azure connection strings, Azure emulators, or Azure deployment resources for the default path.
