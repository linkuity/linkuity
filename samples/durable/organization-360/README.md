# organization-360 (durable sample)

Demonstrates that the matching engine resolves an **organization**
dataset end to end through the **built-in organization profile** — no `--profiles`
option, no external JSON required (zero-config). A second sub-flow then loads an
override profile to demonstrate that loaded profiles replace the built-in.

## What it shows

- **Zero-config resolution** — the main flow passes no `--profiles` argument.
  The CLI resolves the built-in `organization` profile automatically, applying
  the same 0.90 auto-match / 0.75 review thresholds as the canonical JSON.
- **Strong non-person identifiers** — `domain_name`, `email`, and `phone` drive
  auto-matches (these are already in the engine's identifier set).
- **Same name, same domain → auto-match.** A second `Acme Industries` (acme.com,
  Marketing) auto-merges with `Acme Industries` (acme.com, CRM) on the shared
  domain.
- **Same name, different domain → kept separate, not a false merge.** A different
  `Acme Industries` (acme.io, Web) shares only the name block; with a different domain and
  no shared identifier it scores below the review band, so the hub does **not** merge it —
  it becomes its own distinct cluster. Domain is the deciding identity signal.
- **Shared email overrides differing name/domain.** `Globex International`
  (globex-intl.com) auto-merges with `Globex LLC` because the email matches.

## Override demonstration

A self-contained second project ingests `data/override-acme.csv` — two records,
`Acme East Corp` and `Acme West Corp`, that share the **same domain** `acme-corp.com` —
with `--profiles data/organization-override.profile.json`, which raises `autoMatchThreshold`
from 0.90 to **0.99**.

The shared domain is a strong identifier, so the pair scores exactly **0.98**. Under the
**built-in** profile (`autoMatchThreshold` 0.90) that clears the bar and the two records
**auto-merge** into one cluster. With the override profile the same 0.98 now falls **below**
the raised 0.99 auto bar and lands in the review band instead — one review task and two
singleton clusters rather than one merged cluster. This observable difference proves that the
loaded profile replaced the built-in rather than merging with it.

## Run it

```powershell
pwsh scripts/Run-DurableScenario.ps1 -ScenarioPath samples/durable/organization-360
```

All checks should pass. The `organization.profile.json` at the sample root is
the canonical documentation worked example referenced by the architecture docs.
Edit it to retune weights, evaluators, blocking, or thresholds without touching
code.
