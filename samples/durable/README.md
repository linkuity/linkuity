# Durable MDM samples

These samples exercise Linkuity's **durable MDM** features — a persistent
metadata database that lives across CLI invocations, incremental matching,
golden-record version history, stable clusters, a review queue, and durable
project merge policy.

Unlike the standalone samples under `samples/` (one CSV in, one golden-record set
out via `scripts/Run-Scenario.ps1`), each durable sample is an **ordered
sequence** of CLI commands described by a `scenario.json` manifest and run by
`scripts/Run-DurableScenario.ps1`. The runner creates a fresh temporary metadata
database, executes the steps, and verifies assertions on command output and
read-back CSV. Seeds use committed pre-baked match artifacts (representing a
prior standalone `run`) so the samples are deterministic.

## Scenarios

| Scenario | Shows |
|---|---|
| `customer-360-hub` | Flagship storyline: seed → clean auto-join → canonical change + versioning → review task |
| `golden-versioning` | A higher-priority record bumps the golden record to version 2 |
| `review-queue` | Auto / review / no-match threshold bands in one incremental batch |
| `full-vs-incremental-consistency` | Full import and incremental ingest converge on the same canonical value |

(`_smoke` is an internal scenario used to test the runner itself.)

## Run a scenario

```
pwsh -File scripts/Run-DurableScenario.ps1 -ScenarioPath samples/durable/customer-360-hub
```

Add `-KeepArtifacts` to keep the temporary metadata database for inspection.

## How a scenario works

A `scenario.json` lists ordered `steps`. Each step runs one Linkuity CLI command:

- `args` become `--key value` pairs. A value of `{name}` is replaced by a value
  captured from an earlier step; a value beginning with `data/` resolves against
  the scenario folder.
- `capture` stores the first GUID a command prints (e.g. a new project/source/
  batch id) under a name for later `{name}` interpolation.
- `assert` checks the step:
  - `stdout` — each `"Name": "Value"` must appear as a `Name: Value` line.
  - `rows` — the command's CSV output is parsed; `where` selects exactly one row
    (a key ending in `~` matches as a substring), then `expect` checks fields.
  - `error` — the step is expected to exit non-zero.

The runner injects `--metadata <temp-db>` for every command except `run`.

## Read-back commands

The samples (and your own durable projects) inspect durable state with:

- `golden list --metadata <db> --project-id <id>` — current golden records
- `golden history --metadata <db> --project-id <id> [--cluster-id <id>]` — version history
- `cluster list --metadata <db> --project-id <id>` — clusters and membership
- `review list --metadata <db> --project-id <id>` — open review tasks

Each prints CSV to stdout and accepts an optional `--output <file>`.
