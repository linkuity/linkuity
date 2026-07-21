# Linkuity

[![CI](https://github.com/linkuity/linkuity/actions/workflows/ci.yml/badge.svg)](https://github.com/linkuity/linkuity/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4.svg)](https://dotnet.microsoft.com/)

Open-source entity resolution and golden-record engine.

Linkuity finds records that refer to the same person or organization, links them into clusters, and produces explainable golden records you can keep inside your own environment.

- Detect duplicates across CSVs, systems, and incremental loads
- Merge records into source-aware golden records
- Explain why records matched
- Run locally with the CLI
- Run privately with Docker Compose
- Export Neo4j-ready graph data
- Avoid sending customer data to a hosted matching service

## Try It In Under Five Minutes

Prerequisites:

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

Run the bundled people sample:

```powershell
git clone https://github.com/linkuity/linkuity.git
cd linkuity
dotnet run --project src/Linkuity.Cli -- run `
  --input samples/people-multi-source/sample.csv `
  --config samples/people-multi-source/match-config.json `
  --output ./data/output/people-multi-source `
  --neo4j-export
```

Expected result:

```text
Job <id> completed.
Golden records: <repo>\data\output\people-multi-source\golden-records.csv
```

The sample starts with 28 records from CRM, Marketing, Support, and Billing. Linkuity resolves them into 10 golden records.

## Before And After

Input rows can disagree across systems:

| id | source | first_name | last_name | email | phone | address_line |
|----|--------|------------|-----------|-------|-------|--------------|
| crm-050 | CRM | Grace | Garcia | grace.garcia@example.com | (212) 555-5000 | 700 Walnut Dr |
| mkt-051 | Marketing | Grace | Garcia | grace.garcia+m@example.com | (212) 555-5001 | 700 Walnut Dr |
| sup-052 | Support | Grace | Garcia | grace.garcia+s@example.com | (212) 555-5002 | 700 Walnut Dr Apt 1 |
| bil-053 | Billing | Grace | Garcia | grace.garcia+b@example.com | (212) 555-5003 | 700 Walnut Dr Suite 100 |

Linkuity clusters the records and produces one golden record:

| record_count | member_ids | email | phone | address_line |
|--------------|------------|-------|-------|--------------|
| 4 | crm-050\|mkt-051\|sup-052\|bil-053 | grace.garcia@example.com | +12125555002 | 700 Walnut Dr Suite 100 |

That output is explainable:

| Field | Winner | Why |
|-------|--------|-----|
| email | CRM | CRM is the highest-priority source for email |
| phone | Support | Support is the highest-priority source for phone |
| address_line | Billing | Billing is the highest-priority source for address |

Golden records are composites. They do not have to be copied from a single source row.

## What Linkuity Does

Linkuity currently supports batch and durable incremental entity resolution:

1. Accept CSV records for people or organizations.
2. Normalize fields such as names, emails, phone numbers, dates, postal codes, and source identifiers.
3. Block records to avoid full pairwise comparison.
4. Score likely matches with the native .NET matching engine.
5. Cluster linked records with Union-Find.
6. Merge each cluster into a golden record.
7. Export artifacts, score breakdowns, and optional Neo4j graph files.

The default path is local-first. Azure Blob Storage is available as an optional adapter when `Linkuity:RuntimeMode=Azure`, but it is not required for local or private-server use.

## Quick Starts

### CLI Batch Run

Use the CLI when you want the shortest path from CSV to golden records:

```powershell
dotnet run --project src/Linkuity.Cli -- run `
  --input samples/people-multi-source/sample.csv `
  --config samples/people-multi-source/match-config.json `
  --output ./data/output/people-multi-source `
  --neo4j-export
```

Outputs:

```text
data/output/people-multi-source/golden-records.csv
data/output/people-multi-source/artifacts/
data/output/people-multi-source/neo4j-export.zip
```

For a guided walkthrough of the sample data, see [samples/people-multi-source](samples/people-multi-source/README.md).

### Build a Standalone CLI

`dotnet run` rebuilds on every invocation and is best for contributors. To produce a compiled `Linkuity.Cli.exe` you can run directly, publish it.

Framework-dependent build (smaller; requires the .NET 10 runtime on the machine that runs it):

```powershell
dotnet publish src/Linkuity.Cli -c Release -o dist
.\dist\Linkuity.Cli.exe run `
  --input samples/people-multi-source/sample.csv `
  --config samples/people-multi-source/match-config.json `
  --output ./data/output/people-multi-source
```

Self-contained single-file build (no .NET runtime required on the target machine; larger binary):

```powershell
dotnet publish src/Linkuity.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o dist
.\dist\Linkuity.Cli.exe run --input ... --config ... --output ...
```

The self-contained build targets one platform via the runtime identifier (`-r`). Use `linux-x64` or `osx-arm64` instead of `win-x64` to produce binaries for those platforms.

### Verified Sample Scenario

Run the sample harness when you want Linkuity to execute the scenario and assert the expected clusters:

```powershell
.\scripts\Run-Scenario.ps1 -ScenarioPath samples\people-multi-source -Mode Cli
```

Expected summary:

```text
Input records:   28
Golden records:  10
All checks passed.
```

### Private Server With Docker Compose

Run Linkuity as a private batch job with bind-mounted local storage:

```powershell
Copy-Item .env.example .env
New-Item -ItemType Directory -Force ./data/output/people-multi-source,./data/artifacts
docker compose --env-file .env -f docker-compose.local.yml run --rm linkuity-runner
```

Expected output:

```text
./data/output/people-multi-source/golden-records.csv
```

See [docs/private-server-deployment.md](docs/private-server-deployment.md) for server directory layout, health checks, and backup responsibilities.

### HTTP Batch API

Start the local API:

```powershell
dotnet run --project src/Linkuity.Api
```

Check health:

```powershell
curl http://localhost:5017/health
```

Run a synchronous batch match through `POST /run`:

```powershell
curl.exe -X POST http://localhost:5017/run `
  -F "config=@samples/people-multi-source/match-config.json;type=application/json" `
  -F "file=@samples/people-multi-source/sample.csv;type=text/csv" `
  -o golden-records.csv
```

`POST /run` returns the golden-records CSV directly. The endpoint accepts small synchronous inputs today; use the CLI for larger files.

## Why Developers Use Linkuity

| Need | Linkuity gives you |
|------|-------------------|
| Deduplicate records | Native matching, blocking, scoring, and clustering |
| Build golden records | Configurable field merge policies and source priority |
| Explain matches | Persisted score breakdowns and read-back commands |
| Run privately | CLI, local filesystem storage, Docker Compose, optional Postgres |
| Visualize clusters | Neo4j-ready graph export |
| Grow over time | Durable projects, sources, ingest batches, and incremental matching |

## Project Structure

```text
src/
  Linkuity.Cli/             Local CLI batch runner and durable MDM commands
  Linkuity.Api/             ASP.NET Core Web API with synchronous POST /run
  Linkuity.AppHost/         Optional .NET Aspire host for API + Azurite development
  Linkuity.ServiceDefaults/ Shared telemetry and health check defaults
  Linkuity.Core/            Shared domain models and interfaces
  Linkuity.Matching/        Native .NET matching engine
  Linkuity.Mdm/             Durable projects, sources, batches, clusters, golden records
  Linkuity.Pipeline/        Normalize -> match -> cluster -> merge orchestration
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
  how-matching-works.md     Matching, blocking, scoring, merging, and tuning
  private-runtime.md        Private-runtime direction and current limitations
  private-server-deployment.md
                            Docker Compose private-server batch path
  tutorials/                Hands-on guides
samples/
  people-multi-source/      28-row, 10-golden-record sample scenario
```

## Documentation

- [How matching works](docs/how-matching-works.md): blocking, scoring, clustering, merging, and tuning.
- [CLI durable MDM quick start](docs/tutorials/cli-durable-mdm-quickstart.md): create durable projects and ingest incremental data.
- [Architecture](docs/architecture.md): current components and target architecture.
- [Private runtime](docs/private-runtime.md): local-first and private-server direction.
- [Private server deployment](docs/private-server-deployment.md): Docker Compose deployment details.

## Current API

The HTTP API completes a batch match end to end, synchronously:

| Method | Path | Description |
|--------|------|-------------|
| POST | `/run` | Run a batch match synchronously with multipart `config` JSON and CSV `file`; returns merged golden records as `text/csv`. Max input 400 KiB. |
| GET | `/health` | Health check. |

`POST /run` shares the same normalize -> match -> cluster -> merge pipeline as the CLI. An asynchronous API for larger inputs is planned as an additive future feature.

Linkuity also exposes durable project/source/batch metadata endpoints used by the durable MDM CLI path. See [docs/architecture.md](docs/architecture.md) for the full endpoint list.

## Optional Azure-Compatible Local Stack

Local development for the Azure-compatible API path can use .NET Aspire to orchestrate the API and the Azurite blob emulator.

Prerequisites:

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) running locally

Start the stack:

```powershell
dotnet run --project src/Linkuity.AppHost
```

Aspire starts the Azure Storage blob emulator in Docker, waits for it to be healthy, and launches the API. The Aspire dashboard opens at `http://localhost:15888`.

Azure mode changes where artifacts are stored. It does not change the matching pipeline.

## Tests

```powershell
dotnet test
```

## Roadmap

Near-term work focuses on making Linkuity easier to try, explain, benchmark, and extend:

- Faster first-run onboarding and packaged CLI distribution
- More sample datasets for people, organizations, CRM cleanup, and customer 360
- Reproducible benchmark harnesses
- Better match explanations and review workflows
- SDK, connector, and plugin guidance
- Larger-scale private-server deployment examples

## Contributing

Contributions are welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md), which includes development guidance and the Contributor License Agreement requirement in [CLA.md](CLA.md).

Please also review:

- [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)
- [SECURITY.md](SECURITY.md)
- [CHANGELOG.md](CHANGELOG.md)

## License

Linkuity is licensed under the [Apache License 2.0](LICENSE).
