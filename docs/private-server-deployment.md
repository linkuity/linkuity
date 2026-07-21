# Private Server Deployment

This guide runs Linkuity's first private-server mode on a VM or internal server with Docker Compose. It uses the local batch runner, local filesystem artifacts, and bind-mounted data directories. It does not require Azure, Azurite, Azure Storage, or .NET Aspire.

The current private-server path is a one-shot batch job runner. The HTTP API also
completes a batch match synchronously via `POST /run` (capped at 400 KiB of input; use
the CLI for larger inputs) and defaults to local filesystem artifacts; the Azure Blob
Storage artifact-store adapter remains an opt-in compatibility and deployment path.

## Prerequisites

- Docker Engine or Docker Desktop with Docker Compose.
- Enough local disk for input files, job artifacts, and output CSVs.
- A CSV input file and a Linkuity match configuration JSON file.

## Directory Layout

The default `.env.example` uses these paths:

```text
./samples/people-multi-source/sample.csv       sample input CSV
./samples/people-multi-source/match-config.json sample match configuration
./data/artifacts                               local job artifacts
./data/output/people-multi-source             golden-record output
```

For a real private server, keep operational data under a server-owned directory such as `/srv/linkuity`:

```text
/srv/linkuity/input      source CSV files supplied by operators or jobs
/srv/linkuity/config     match configuration JSON files
/srv/linkuity/artifacts  normalized CSVs, metadata, matches, and job internals
/srv/linkuity/output     golden-record CSVs and optional exports
```

`LINKUITY_ARTIFACTS` is bind-mounted into the container as `/linkuity/output/artifacts`. The CLI writes `golden-records.csv` under `LINKUITY_OUTPUT` and writes inspectable job artifacts under `LINKUITY_ARTIFACTS`.

## Configure

Copy the example environment file:

```powershell
Copy-Item .env.example .env
```

Edit `.env` for your server paths:

```dotenv
LINKUITY_INPUT=./samples/people-multi-source/sample.csv
LINKUITY_CONFIG=./samples/people-multi-source/match-config.json
LINKUITY_OUTPUT=./data/output/people-multi-source
LINKUITY_ARTIFACTS=./data/artifacts
```

The default file intentionally contains no Azure connection strings. Do not add Azure settings for the default private-server batch path.

Create the output and artifact directories before running Compose:

```powershell
New-Item -ItemType Directory -Force ./data/output/people-multi-source,./data/artifacts
```

On Linux:

```bash
mkdir -p ./data/output/people-multi-source ./data/artifacts
```

## Run

Build and run the private-server batch job:

```powershell
docker compose --env-file .env -f docker-compose.local.yml run --rm linkuity-runner
```

The sample run writes:

```text
./data/output/people-multi-source/golden-records.csv
./data/artifacts/<job-id>/metadata.json
./data/artifacts/<job-id>/input.csv
./data/artifacts/<job-id>/normalized.csv
./data/artifacts/<job-id>/matches.csv
./data/artifacts/<job-id>/golden_records.csv
```

To rerun with another dataset, update `LINKUITY_INPUT`, `LINKUITY_CONFIG`, `LINKUITY_OUTPUT`, and `LINKUITY_ARTIFACTS` in `.env`.

## Health Checks

For the first private-server mode, health is a successful batch smoke run and the presence of the output CSV:

```powershell
docker compose --env-file .env -f docker-compose.local.yml run --rm linkuity-runner
Test-Path ./data/output/people-multi-source/golden-records.csv
```

Expected result: the Compose command exits with code `0`, and `Test-Path` returns `True`.

On Linux:

```bash
docker compose --env-file .env -f docker-compose.local.yml run --rm linkuity-runner
test -f ./data/output/people-multi-source/golden-records.csv
```

The `/health` HTTP endpoint belongs to the current API path. It is not required for this default private-server batch path.

## Backup Responsibilities

Private-server operators own all data retention and backup decisions. Linkuity does not copy private-server data to a hosted service.

Back up these paths according to your organization's retention policy:

- `LINKUITY_OUTPUT`: golden-record CSVs and exports that users consume.
- `LINKUITY_ARTIFACTS`: job metadata, normalized CSVs, match files, and intermediate artifacts when auditability or reruns matter.
- Source input directories: original CSVs if they are not already retained in an upstream system.
- Match configuration directories: JSON configuration files used to produce outputs.
- Durable database volumes when running a durable MDM project on the PostgreSQL backend.

If job artifacts are temporary in your environment, you may delete old `LINKUITY_ARTIFACTS/<job-id>` folders after confirming the output has been retained. Do not delete `LINKUITY_OUTPUT` unless the golden-record outputs have been backed up or are no longer needed.

## Aspire And Azure Emulators

.NET Aspire remains optional developer tooling for the current Azure Blob Storage compatibility path: it orchestrates the API plus an Azurite blob emulator. It is not part of the default private-server deployment described here.

The Aspire profile sets `Linkuity__RuntimeMode=Azure` for the API. The default private-server Compose file starts no Azurite container.
