# Samples

Small, self-contained datasets that double as teaching examples and as starting points
for tuning your own matching rules. Each has a README walkthrough and expected results
pinned by tests, so they stay honest as the engine evolves.

There are two kinds:

- **Flat samples** — one `sample.csv` plus a `match-config.json`. Run them with
  `linkuity run` (or the [scenario harness](../scripts/Run-Scenario.ps1)); their
  outcomes are pinned by `SampleScenarioTests`.
- **Durable samples** — ordered command sequences described by a `scenario.json`
  manifest and run by [`scripts/Run-DurableScenario.ps1`](../scripts/Run-DurableScenario.ps1).

## Catalog

| Sample | Kind | What it shows |
|--------|------|---------------|
| [people-multi-source](people-multi-source/README.md) | flat | 28 rows → 10 golden records. Source-priority merging across a range of cluster shapes — the main teaching example. |
| [people-phone-noise](people-phone-noise/README.md) | flat | Declaring a field but excluding it from matching (`participatesInMatching: false`), for when phone numbers are unreliable for identity (shared landlines, recycled numbers). |
| [organizations-multi-source](organizations-multi-source/README.md) | flat | Multi-source merge mechanics on org data — legal-suffix variation, article/prefix name noise, and the `organization_name` / `domain_name` semantic types. |
| [organizations-name-noise](organizations-name-noise/README.md) | flat | Org name-noise corner cases: ampersand variants (`A & B` vs `A and B`) and disambiguating same-named firms by `domain_name`. |
| [durable](durable/README.md) | durable | Durable MDM as scenario scripts: incremental auto-join, golden-record versioning, the review queue, and full-vs-incremental consistency. |

## Run a flat sample

```powershell
dotnet run --project src/Linkuity.Cli -- run `
  --input samples/people-multi-source/sample.csv `
  --config samples/people-multi-source/match-config.json `
  --output ./data/output/people-multi-source
```

Or verify one end to end (asserts the expected clusters):

```powershell
.\scripts\Run-Scenario.ps1 -ScenarioPath samples\people-multi-source -Mode Cli
```

## Run a durable sample

```powershell
pwsh -File scripts/Run-DurableScenario.ps1 -ScenarioPath samples/durable/customer-360-hub
```

New to Linkuity? Start with the [durable MDM quick start](../docs/tutorials/cli-durable-mdm-quickstart.md),
then explore these samples and the [architecture overview](../docs/architecture.md).
