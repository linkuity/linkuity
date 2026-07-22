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

Real systems rarely store a person the same way twice. The same customer shows up as a nickname in one app, a fat-fingered typo in another, and a differently formatted phone number in a third:

| id | source | first_name | last_name | email | phone | address_line |
|----|--------|------------|-----------|-------|-------|--------------|
| crm-050 | CRM | Joseph | Martinez | joseph.martinez@example.com | (312) 555-0147 | 1420 Maple Avenue |
| mkt-051 | Marketing | Joe | Martinez | joe.martinez@example.com | 312-555-0147 | 1420 Maple Ave |
| sup-052 | Support | Joesph | Martinez | joseph.martinez@example.com | 312.555.0147 | 1420 Maple Avenue Apt 3B |
| bil-053 | Billing | Joseph | Martinez | joseph.martimez@example.com | 3125550147 | 1420 Maple Ave Apt 3B |

Every mismatch here is one you have probably seen in production:

- **Nickname vs. legal name** — `Joe` in Marketing, `Joseph` in the others.
- **A typo in the name** — Support saved `Joesph`.
- **A typo in the email** — Billing has `joseph.martimez` (an `m` where the `n` should be), so an exact-match join silently misses it.
- **One phone, four formats** — parentheses, dashes, dots, and bare digits.
- **Addresses that drifted apart** — `Avenue` shortened to `Ave`, and the apartment number only reached two of the four systems.

Linkuity normalizes, blocks, scores, and clusters these into one golden record:

| record_count | member_ids | first_name | email | phone | address_line |
|--------------|------------|------------|-------|-------|--------------|
| 4 | crm-050\|mkt-051\|sup-052\|bil-053 | Joseph | joseph.martinez@example.com | +13125550147 | 1420 Maple Ave Apt 3B |

That output is explainable:

| Field | Winner | Why |
|-------|--------|-----|
| first_name | Consensus | `Joseph` is the most common spelling across the sources, so the nickname and the typo lose |
| email | CRM | CRM is the highest-priority source for email — and its address has no typo |
| phone | Support | Support is the highest-priority source for phone; every format normalizes to the same number |
| address_line | Billing | Billing is the highest-priority source for address and carries the apartment number |

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

## How Matching Works

Comparing every record against every other is `N²` and doesn't scale, so Linkuity only compares records that share a cheap-to-compute **blocking key**, then scores just those candidate pairs field by field — name, email, phone, address — into a single similarity score. That score falls into one of three **decision bands**: high scores **auto-merge**, low scores stay **separate**, and the uncertain middle becomes a **review task** for a human. Accepted pairs are grouped transitively into clusters, and each cluster collapses into one golden record using your source-priority merge rules. Every pair keeps a per-field score breakdown, so you can always see *why* two records did or didn't merge.

You steer all of this with a matching profile and a few score thresholds. The two failure modes are **under-merging** (real duplicates left separate) and **over-merging** (distinct entities collapsed together) — tuning the thresholds and which fields participate in matching is how you correct each. For the full treatment — normalization, blocking, similarity scoring, the decision bands, clustering, and merge policy, with a worked example computed from real engine output — see [docs/how-matching-works.md](docs/how-matching-works.md).

## Two Ways to Run Linkuity

Linkuity has two execution modes, and the quick start above used the first one.

**Batch mode** (`linkuity run`) is stateless. It reads a CSV, resolves it in one pass, writes `golden-records.csv`, and forgets everything. Reach for it when you want a one-off dedupe, an export, or a CI check — it's exactly what the [quick start](#try-it-in-under-five-minutes) did.

**Durable mode** (`linkuity project` + `ingest-incremental`) keeps a persistent store. It *remembers* every record it has seen, so when new data arrives it matches against what already exists and updates the golden records incrementally — with full version history and a review queue for uncertain matches. The store is backed by a **local JSON file** or **PostgreSQL**.

| | Batch mode | Durable mode |
|---|---|---|
| Entry point | `linkuity run` | `linkuity project` / `ingest-incremental` |
| State | Stateless — resolves and forgets | Persistent store that remembers |
| New data | Reprocesses the whole file each time | Matched incrementally against stored records |
| History & review | — | Versioned golden records + review queue |
| Store backend | Output files on disk | Local JSON file **or** PostgreSQL |
| Reach for it when | One-off dedupe, exports, CI | Ongoing MDM as data arrives over time |

### Durable mode on PostgreSQL

Every durable command targets a Postgres store by adding `--metadata-store postgres --connection-string <conn>` (a local JSON store uses `--metadata <file>` instead). Linkuity creates its schema on first use, so all you need is a reachable database:

```powershell
$conn = "Host=localhost;Port=5432;Database=linkuity;Username=postgres;Password=postgres"
$data = "docs/tutorials/cli-durable-mdm-quickstart/data"

# Helper so every command targets the same Postgres store
# (don't name it `cli` — that's a built-in PowerShell alias)
function linkuity { dotnet run --project src/Linkuity.Cli -- @args --metadata-store postgres --connection-string $conn }

# Create a durable project (prints a project id)
$projectId = (linkuity project create --name "Customer 360" --content-type person --merge-policy "$data/merge-policy.json").Trim()

# Register the CRM source, open a batch, and load its rows
$crm   = (linkuity source create --project-id $projectId --name "CRM").Trim()
$batch = (linkuity batch create  --project-id $projectId --source-id $crm --record-count 4).Trim()
linkuity ingest-incremental --project-id $projectId --source-id $crm --batch-id $batch --input "$data/crm.csv"

# Read the golden records back out of Postgres
linkuity golden list --project-id $projectId
```

Load a second source later and Linkuity matches it against the records already in Postgres — auto-merging confident duplicates, versioning the golden records, and queuing uncertain matches for review. For the full step-by-step walkthrough (it uses a JSON-file store, but every command works the same on Postgres by adding the two flags above), see [docs/tutorials/cli-durable-mdm-quickstart.md](docs/tutorials/cli-durable-mdm-quickstart.md).

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

## Samples

The [`samples/`](samples/) directory holds small, self-contained datasets — each with a README walkthrough and expected results — that double as teaching examples and as starting points for tuning your own rules. The flat samples are one CSV plus a `match-config.json`, pinned by `SampleScenarioTests`; the durable samples are ordered command sequences run by `scripts/Run-DurableScenario.ps1`.

| Sample | What it shows |
|--------|---------------|
| [people-multi-source](samples/people-multi-source/README.md) | 28 rows → 10 golden records. Source-priority merging across a range of cluster shapes — the main teaching example, used throughout this README. |
| [people-phone-noise](samples/people-phone-noise/README.md) | Declaring a field but excluding it from matching (`participatesInMatching: false`), for when phone numbers are unreliable for identity (shared landlines, recycled numbers). |
| [organizations-multi-source](samples/organizations-multi-source/README.md) | The multi-source merge mechanics applied to org data — legal-suffix variation, article/prefix name noise, and the `organization_name` / `domain_name` semantic types. |
| [organizations-name-noise](samples/organizations-name-noise/README.md) | Org name-noise corner cases: ampersand variants (`A & B` vs `A and B`) and disambiguating same-named firms by `domain_name`. |
| [durable](samples/durable/README.md) | Durable MDM as scenario scripts: incremental auto-join, golden-record versioning, the review queue, and full-vs-incremental consistency. |

Run a flat sample with the [CLI batch command](#cli-batch-run) (swap in that sample's `sample.csv` and `match-config.json`), verify one end to end with the [scenario harness](#verified-sample-scenario), or work through the guided [durable MDM quick start](docs/tutorials/cli-durable-mdm-quickstart.md).

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
  people-multi-source/      28-row, 10-golden-record teaching scenario
  people-phone-noise/       Excluding an unreliable field from matching
  organizations-multi-source/  Org-shaped multi-source merging
  organizations-name-noise/ Org name-noise corner cases
  durable/                  Durable MDM scenario scripts
```

See [Samples](#samples) for what each one demonstrates and how to run it.

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
