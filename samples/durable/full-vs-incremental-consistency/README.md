# full-vs-incremental-consistency

Demonstrates that two records loaded via different paths converge on the same canonical (golden) value when the same merge policy is applied.

## What it proves

Two projects are created with an identical merge policy (`CRM` beats `Marketing` for email):

- **Project A** loads both records — a Marketing record (`mkt-400`) and a CRM record (`crm-400`) — in a single full `persist-batch`.
- **Project B** loads only the Marketing record as a seed (`persist-batch`), then adds the CRM record later via `ingest-incremental`.

Both projects end with the **same canonical email** (`finn.fowler@example.com`), the CRM value, because source priority is re-evaluated on every merge regardless of ingestion path. The `ingest-incremental` step matches the CRM record to the existing Marketing seed via the shared phone number (exact match → 0.98 auto-link).

## Run

```pwsh
pwsh -File scripts/Run-DurableScenario.ps1 -ScenarioPath samples/durable/full-vs-incremental-consistency
```

Expected output: `All 12 checks passed.`

## Key assertions

Both `golden list` assertions check the identical CRM email:

```
row.email = 'finn.fowler@example.com'   # Project A (full batch)
row.email = 'finn.fowler@example.com'   # Project B (seed + incremental)
```

The `ingest-incremental` step also asserts `Auto matches: 1`, confirming the phone-based auto-link fired before the golden record was recomputed.
