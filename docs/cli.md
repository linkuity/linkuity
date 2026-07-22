# CLI reference

The `linkuity` CLI is the default way to run Linkuity. It has one stateless batch
command (`run`) and a set of durable-MDM commands (`project`, `source`, `batch`,
`ingest-incremental`, `golden`, `cluster`, `review`). This page covers batch usage
and how to build a standalone binary. For the durable commands and every flag, see
[architecture.md](architecture.md#durable-cli-commands); for a guided durable
walkthrough see the [durable MDM quick start](tutorials/cli-durable-mdm-quickstart.md).

## Running from source

During development the shortest path is `dotnet run`, which rebuilds on each call:

```powershell
dotnet run --project src/Linkuity.Cli -- run `
  --input samples/people-multi-source/sample.csv `
  --config samples/people-multi-source/match-config.json `
  --output ./data/output/people-multi-source `
  --neo4j-export
```

`run` flags:

| Flag | Meaning |
|------|---------|
| `--input` | Path to the source CSV. |
| `--config` | Path to the match configuration JSON (fields + merge policy). |
| `--output` | Output directory for `golden-records.csv` and `artifacts/`. |
| `--neo4j-export` | Also write a `neo4j-export.zip` graph bundle (optional). |

Outputs land under `--output`:

```text
golden-records.csv      # the merged golden records
artifacts/              # normalized input, matches, and score breakdowns
neo4j-export.zip        # only with --neo4j-export
```

## Building a standalone binary

`dotnet run` is best for contributors. To produce a compiled `Linkuity.Cli.exe`
you can run directly, publish it.

**Framework-dependent** (smaller; requires the .NET 10 runtime on the target machine):

```powershell
dotnet publish src/Linkuity.Cli -c Release -o dist
.\dist\Linkuity.Cli.exe run `
  --input samples/people-multi-source/sample.csv `
  --config samples/people-multi-source/match-config.json `
  --output ./data/output/people-multi-source
```

**Self-contained single file** (no .NET runtime required on the target; larger binary):

```powershell
dotnet publish src/Linkuity.Cli -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o dist
.\dist\Linkuity.Cli.exe run --input ... --config ... --output ...
```

The self-contained build targets one platform via the runtime identifier (`-r`).
Use `linux-x64` or `osx-arm64` instead of `win-x64` for those platforms.

> Packaged CLI releases (so users don't build from source) are on the
> [roadmap](roadmap/PLAN.md).

## Durable commands

The durable path stores state across runs and matches incrementally. The store is a
local JSON file (`--metadata <file>`) or PostgreSQL
(`--metadata-store postgres --connection-string <conn>`, see
[durable-postgres.md](durable-postgres.md)). Start with the
[durable MDM quick start](tutorials/cli-durable-mdm-quickstart.md), which walks through
`project create`, `source create`, `batch create`, `ingest-incremental`, and the
`golden` / `cluster` / `review` read-back commands.
