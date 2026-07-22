# Durable mode on PostgreSQL

Durable mode keeps a persistent store so Linkuity matches new data against what it
has already seen, with golden-record version history and a review queue. The store
backs onto either a local JSON file or **PostgreSQL**. This page shows the Postgres
path end to end.

For the concepts (projects, sources, batches, incremental matching, review) work
through the [durable MDM quick start](tutorials/cli-durable-mdm-quickstart.md) — it
uses a JSON-file store, but **every command works the same on Postgres by adding two
flags**:

```text
--metadata-store postgres --connection-string <conn>
```

A JSON-file store uses `--metadata <file>` instead. Linkuity creates its schema on
first use (via DbUp), so all you need is a reachable, empty database.

## Prerequisites

- .NET 10 SDK
- A reachable PostgreSQL instance (any recent version). The connection string below
  assumes local defaults; adjust host/port/credentials to yours.

## Walkthrough

```powershell
$conn = "Host=localhost;Port=5432;Database=linkuity;Username=postgres;Password=postgres"
$data = "docs/tutorials/cli-durable-mdm-quickstart/data"

# Helper so every command targets the same Postgres store
# (don't name it `cli` — that's a built-in PowerShell alias)
function linkuity { dotnet run --project src/Linkuity.Cli -- @args --metadata-store postgres --connection-string $conn }

# 1. Create a durable project (prints a project id; schema is created on first use)
$projectId = (linkuity project create --name "Customer 360" --content-type person --merge-policy "$data/merge-policy.json").Trim()

# 2. Register the CRM source, open a batch, and load its rows
$crm   = (linkuity source create --project-id $projectId --name "CRM").Trim()
$batch = (linkuity batch create  --project-id $projectId --source-id $crm --record-count 4).Trim()
linkuity ingest-incremental --project-id $projectId --source-id $crm --batch-id $batch --input "$data/crm.csv"

# 3. Read the golden records back out of Postgres
linkuity golden list --project-id $projectId
```

The first load has nothing to match against, so the four CRM rows land as singletons.
Now load a second source and Linkuity matches it against the records already in Postgres:

```powershell
$mkt   = (linkuity source create --project-id $projectId --name "Marketing").Trim()
$batch = (linkuity batch create  --project-id $projectId --source-id $mkt --record-count 3).Trim()
linkuity ingest-incremental --project-id $projectId --source-id $mkt --batch-id $batch `
  --input "$data/marketing.csv" --auto-threshold 0.90 --review-threshold 0.75

linkuity golden list --project-id $projectId
```

The marketing record that shares an email with a CRM record auto-merges into that
cluster (its golden record advances to version 2 and its phone updates per the merge
policy); records that match nobody stay separate. Inspect the results with the
read-back commands:

```powershell
linkuity golden list    --project-id $projectId   # merged golden records
linkuity cluster list   --project-id $projectId   # which source records grouped together
linkuity review list    --project-id $projectId   # uncertain matches awaiting a human
linkuity golden history --project-id $projectId --cluster-id <cluster_id>   # version history
```

## Notes

- **Use a dedicated database per project.** Point `Database=` at an empty database;
  Linkuity manages its own schema inside it.
- **Thresholds.** `--auto-threshold` and `--review-threshold` set the auto-merge and
  review decision bands for an ingest. See
  [how-matching-works.md](how-matching-works.md) for what the bands mean.
- **Backend parity.** The Postgres backend produces the same outcomes as the JSON-file
  store; this is enforced by the conformance test suite. See
  [architecture.md](architecture.md#metadatastore-backends).
