# Linkuity

[![CI](https://github.com/linkuity/linkuity/actions/workflows/ci.yml/badge.svg)](https://github.com/linkuity/linkuity/actions/workflows/ci.yml)

Linkuity is an open-source private-runtime golden-record engine for data and MDM teams. It deduplicates people or organization records, links matching source records into clusters, and produces golden records that can stay inside your environment.

The codebase includes a local CLI batch runner, durable MDM projects with incremental matching, a Docker Compose private-server batch path, and an HTTP batch API that completes a match synchronously. Azure Blob Storage is optional adapter infrastructure selected with `Linkuity:RuntimeMode=Azure`, not the default runtime.

## Product Direction

- Linkuity is open source.
- Customer data does not need to leave the customer's environment.
- The target runtime is local-first, private-server friendly, and suitable for customer-managed cloud accounts.
- Azure Blob Storage remains a useful deployment adapter, but it is not the default architecture.
- The durable MDM model will support projects, sources, ingest batches, entity relationships, clusters, golden records, and incremental matching over time.

See:

- `docs/tutorials/` for hands-on, step-by-step guides — start with `docs/tutorials/cli-durable-mdm-quickstart.md` to build a durable MDM project with the CLI.
- `docs/private-runtime.md` for the private-runtime direction and current limitations.
- `docs/private-server-deployment.md` for the Docker Compose private-server batch path.
- `docs/how-matching-works.md` for how matching, blocking, scoring, merging, and tuning work end to end (start here to understand the engine).
- `docs/architecture.md` for current and target architecture.

## What Linkuity Does

Linkuity currently supports batch deduplication:

1. Accept a CSV containing people or organization records.
2. Normalize configured fields such as names, emails, phone numbers, dates, postal codes, and source identifiers.
3. Block records to avoid full pairwise comparison.
4. Score likely matches with the .NET matching engine.
5. Cluster linked records with the .NET post-processing pipeline.
6. Merge each cluster into a golden record.
7. Optionally export a Neo4j-ready graph bundle.

The target private runtime keeps this pipeline but removes the assumption that every run depends on hosted infrastructure or customer uploads to a Linkuity-operated service.

## Current Batch API

The HTTP API completes a batch match end to end, synchronously:

| Method | Path | Description |
|--------|------|-------------|
| POST | `/run` | Run a batch match synchronously: multipart `config` (JSON) + `file` (`text/csv`); returns merged golden records as `text/csv`. Max input 400 KiB (synchronous limit; use the CLI for larger sets). |
| GET | `/health` | Health check |

`POST /run` shares the same normalize -> match -> cluster -> merge pipeline as the
CLI (`BatchRunService`), so a request that fits under the size cap completes with the
golden-records CSV in the response body — no polling, no job state machine. The 400 KiB
cap exists because within-batch matching is O(n^2)-ish, so larger inputs risk exceeding a
synchronous request's time budget; use `linkuity run` (or the durable/incremental CLI
path) for larger inputs. An asynchronous variant of this endpoint (for larger inputs,
polled to completion) is a planned, additive future addition — it does not exist today.

Linkuity also exposes durable project/source/batch metadata endpoints (`/projects`,
`/projects/{id}/sources`, `/projects/{id}/batches`, etc.) used by the durable MDM CLI
path — see `docs/architecture.md` for the full list.

This API is one interface to Linkuity. The local CLI (`linkuity run` and
`linkuity ingest-incremental`) remains the default end-to-end path and drives durable MDM
project persistence today.

## Project Structure

```text
src/
  Linkuity.Cli/             Local CLI batch runner and durable MDM commands (default path)
  Linkuity.Api/             ASP.NET Core Web API (synchronous POST /run plus durable metadata endpoints)
  Linkuity.AppHost/         Optional .NET Aspire host for API + Azurite blob-emulator development
  Linkuity.ServiceDefaults/ Shared telemetry and health check defaults
  Linkuity.Core/            Shared domain models and interfaces
  Linkuity.Matching/        Native .NET matching engine (blocking, scoring, retrieval)
  Linkuity.Mdm/             Durable MDM model: projects, sources, batches, clusters, golden records
  Linkuity.Pipeline/        End-to-end batch pipeline orchestration (normalize -> match -> cluster -> merge)
  Linkuity.Infrastructure.Local/
                            Local filesystem artifact storage and JSON metadata store
  Linkuity.Infrastructure.Postgres/
                            PostgreSQL durable metadata-store backend
  Linkuity.Infrastructure.Lucene/
                            Lucene.NET incremental candidate-retrieval index
  Linkuity.Infrastructure.Azure/
                            Optional Azure Blob Storage artifact-store adapter
docs/
  architecture.md           Current and target architecture
  how-matching-works.md     How matching, blocking, scoring, and merging work
  private-runtime.md        Private-runtime direction and current limitations
  tutorials/                Hands-on, step-by-step guides
```

## Running On A Private Server

The first private-server mode runs Linkuity as a Docker Compose batch job with bind-mounted local storage. It uses the local CLI pipeline, writes artifacts to the server filesystem, and does not require Azure emulators.

```powershell
Copy-Item .env.example .env
New-Item -ItemType Directory -Force ./data/output/people-multi-source,./data/artifacts
docker compose --env-file .env -f docker-compose.local.yml run --rm linkuity-runner
```

Expected sample output:

```text
./data/output/people-multi-source/golden-records.csv
```

See `docs/private-server-deployment.md` for server directory layout, health checks, and backup responsibilities.

## Running The Current Azure-Compatible Stack Locally

Local development for the current Azure-compatible API path can use .NET Aspire to orchestrate the API and the Azurite blob emulator.

This setup does not require an Azure account, but it opts the API into `Linkuity:RuntimeMode=Azure` and mirrors the Azure Blob Storage artifact-store adapter through Azurite. It is optional developer tooling, not the default private-server deployment path.

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) running locally

### Start The Current Development Stack

```powershell
dotnet run --project src/Linkuity.AppHost
```

Aspire starts the Azure Storage (blob) emulator in Docker, waits for it to be healthy, then launches the API. The Aspire dashboard opens at `http://localhost:15888`.

In this mode the API still completes matches synchronously through `POST /run` — Azure mode only changes where job artifacts are stored (Azure Blob Storage / Azurite instead of the local filesystem).

### Verify The API

```powershell
curl http://localhost:5017/health
```

Expected result: `200 OK`.

## Running A Current Batch Job

`linkuity run` is the default end-to-end batch path: normalize, match natively, cluster,
merge, and optionally export a Neo4j-ready bundle — no server, queue, or external process
required.

```powershell
dotnet run --project src/Linkuity.Cli -- run --input samples/people-multi-source/sample.csv --config samples/people-multi-source/match-config.json --output ./data/output/people-multi-source --neo4j-export
```

The sample should produce 10 golden records. See `samples/people-multi-source/README.md`
for the full walkthrough of which records merge and why.

For a durable project that remembers matches across incremental loads, see
`docs/tutorials/cli-durable-mdm-quickstart.md`; use `linkuity ingest-incremental` there.

## Tests

```powershell
dotnet test
```

## Optional Azure Deployment Path

The Azure deployment option runs `Linkuity.Api` against Azure-managed infrastructure:

- `Linkuity.Api`
- Azure Blob Storage
- Azure Container Apps

In this mode `POST /run` still completes matches synchronously, in-process; Azure mode
only changes the artifact-store backend from the local filesystem to Azure Blob Storage.

Set `Linkuity__RuntimeMode=Azure` for the API to use the Azure Blob Storage adapter. Current environment variables:

| Service | Required Variables |
|---------|--------------------|
| Linkuity.Api | `Linkuity__RuntimeMode=Azure`, `BlobStorage__ConnectionString`, `BlobStorage__ContainerName` |

This deployment path remains documented for teams that want Azure infrastructure. Local CLI and private-server batch execution remain the default paths.

## License

Linkuity is licensed under the [Apache License 2.0](LICENSE). Contributions are
welcome — see [CONTRIBUTING.md](CONTRIBUTING.md), which requires signing the
[Contributor License Agreement](CLA.md).
