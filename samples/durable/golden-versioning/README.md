# golden-versioning

**What it proves:** Golden-record version history driven by source priority. A seed Marketing record
establishes a cluster at version 1. An incremental CRM record with a shared phone number auto-joins
the cluster and, because CRM ranks above Marketing in the merge policy, flips the canonical email to
the CRM address — producing version 2.

## Scenario at a glance

| Step | Action | Key assertion |
|------|--------|---------------|
| Seed | `persist-batch` loads one Marketing record (`mkt-201`) | `golden list` → version 1, email `dana.doe+m@example.com` |
| Ingest | `ingest-incremental` with `crm-upgrade.csv` (shared phone) | `Auto matches: 1`, `Golden versions created: 1` |
| Verify | `golden list` after ingest | version 2, email `dana.doe@example.com` |
| History | `golden history` after ingest | both versions present: v1 Marketing email, v2 CRM email |

## Run it

```powershell
pwsh -File scripts/Run-DurableScenario.ps1 -ScenarioPath samples/durable/golden-versioning
```

Expected output: `All 13 checks passed.`
