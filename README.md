# Linkuity

[![CI](https://github.com/linkuity/linkuity/actions/workflows/ci.yml/badge.svg)](https://github.com/linkuity/linkuity/actions/workflows/ci.yml)

Linkuity is an open-source private-runtime golden-record engine for data and MDM teams. It deduplicates people or organization records, links matching source records into clusters, and produces golden records that can stay inside your environment.

The codebase includes a local CLI batch runner, durable MDM projects with incremental matching, a Docker Compose private-server batch path, and an HTTP batch API. Azure Blob Storage and Azure Service Bus are optional adapter infrastructure selected with `Linkuity:RuntimeMode=Azure`, not the default runtime.

## Product Direction

- Linkuity is open source.
- Customer data does not need to leave the customer's environment.
- The target runtime is local-first, private-server friendly, and suitable for customer-managed cloud accounts.
- Azure Blob Storage and Azure Service Bus remain useful deployment adapters, but they are not the default architecture.
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
5. Cluster linked records with the .NET post-processing worker.
6. Merge each cluster into a golden record.
7. Optionally export a Neo4j-ready graph bundle.

The target private runtime keeps this pipeline but removes the assumption that every run depends on hosted infrastructure or customer uploads to a Linkuity-operated service.

## Current Batch API

The existing application exposes an HTTP batch-job workflow:

| Method | Path | Description |
|--------|------|-------------|
| POST | `/jobs` | Create a new job |
| POST | `/jobs/{id}/upload` | Upload a CSV file (`text/csv`, max 50 MB) |
| POST | `/jobs/{id}/upload-complete` | Signal that upload is done |
| POST | `/jobs/{id}/start` | Dispatch the job for processing |
| GET | `/jobs/{id}` | Poll job status |
| GET | `/jobs/{id}/golden-records` | Download merged golden records as CSV after state is `Complete` |
| GET | `/jobs/{id}/neo4j-export` | Download Neo4j-ready CSVs plus a `load.cypher` import script |
| GET | `/health` | Health check |

Job states:

```text
Open -> Ingesting -> UploadComplete -> Processing -> MatchingComplete -> Complete
                                                        \-> Failed
```

This API is one interface to Linkuity. The local CLI (`linkuity run` and
`linkuity ingest-incremental`) is the default end-to-end path and drives durable MDM
project persistence today. Planned work will make the server API infrastructure-neutral.

## Azure-Compatible Batch Pipeline

```text
POST /jobs/upload -> Normalize -> Azure Service Bus queue
  -> matching stage (no Azure matching consumer)
  -> .NET worker (graph clustering + golden record merge)
  -> GET /jobs/{id} -> download results
```

This is the optional Azure-compatible API path. It is selected for the .NET services with `Linkuity__RuntimeMode=Azure`.

Azure-compatible infrastructure:

- Azure Blob Storage stores job artifacts.
- Azure Service Bus carries job IDs between processing stages.
- .NET Aspire starts local Azure emulators for development.
- Azure Container Apps is the documented Azure deployment option.

These are compatibility targets for teams that want Azure infrastructure. Local CLI and Docker Compose private-server batch execution are the default private-runtime paths.

## Project Structure

```text
src/
  Linkuity.Cli/             Local CLI batch runner and durable MDM commands (default path)
  Linkuity.Api/             ASP.NET Core Web API
  Linkuity.Worker/          Optional .NET background worker host for Azure post-processing
  Linkuity.AppHost/         Optional .NET Aspire host for Azure-emulator development
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
                            Optional Azure Blob and Azure Service Bus adapters
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

Local development for the current Azure-compatible API path can use .NET Aspire to orchestrate the API, the .NET worker, and Azure emulators.

This setup does not require an Azure account, but it opts the .NET services into `Linkuity:RuntimeMode=Azure` and mirrors the Azure-backed batch architecture through Azurite and the Azure Service Bus emulator. It is optional developer tooling, not the default private-server deployment path.

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) running locally

### Start The Current Development Stack

```powershell
dotnet run --project src/Linkuity.AppHost
```

Aspire starts the Azure Storage emulator and Service Bus emulator in Docker, waits for them to be healthy, then launches the API and .NET worker. The Aspire dashboard opens at `http://localhost:15888`.

The Azure-compatible path has no matching consumer (see "Azure-Compatible Batch Pipeline" above): jobs dispatched with `Linkuity:RuntimeMode=Azure` are normalized and queued, but nothing scores matches, so they will not progress past `Processing`. Use `linkuity run` or `linkuity ingest-incremental` for an end-to-end match.

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

The Azure deployment option uses independent service containers and Azure-managed infrastructure:

- `Linkuity.Api`
- `Linkuity.Worker`
- Azure Blob Storage
- Azure Service Bus
- Azure Container Apps

The Azure path has no matching consumer (see "Azure-Compatible Batch Pipeline" above);
`Linkuity.Worker` performs Azure-mode post-processing once a job reaches
`MatchingComplete`, but nothing currently produces that transition. Use `linkuity run` or
`linkuity ingest-incremental` for an end-to-end match.

Set `Linkuity__RuntimeMode=Azure` for the .NET API and .NET worker to use the Azure adapters. Current environment variables:

| Service | Required Variables |
|---------|--------------------|
| Linkuity.Api | `Linkuity__RuntimeMode=Azure`, `BlobStorage__ConnectionString`, `BlobStorage__ContainerName`, `AzureServiceBus__ConnectionString` |
| Linkuity.Worker | `Linkuity__RuntimeMode=Azure`, `BlobStorage__ConnectionString`, `BlobStorage__ContainerName`, `AzureServiceBus__ConnectionString`, `AzureServiceBus__PostProcessingQueueName` |

This deployment path remains documented for teams that want Azure infrastructure. Local CLI and private-server batch execution remain the default paths.
