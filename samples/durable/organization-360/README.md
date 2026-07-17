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
- **Same name, different domain → review, not a false merge.** A different
  `Acme Industries` (acme.io, Web) shares the name block but has no shared
  identifier, so it lands in review and stays its own cluster — domain is the
  deciding identity signal.
- **Shared email overrides differing name/domain.** `Globex International`
  (globex-intl.com) auto-merges with `Globex LLC` because the email matches.

## Override demonstration

A self-contained second project ingests `data/override-acme.csv` (two records,
both named "Acme Industries" but with distinct domain/email/phone) with
`--profiles data/organization-override.profile.json`, which raises `reviewThreshold`
to **0.85**.

Under the **built-in** profile (`reviewThreshold` 0.75) the same pair would score
0.80 and land in review (one review task). With the override profile the 0.80
score falls **below** the raised 0.85 threshold — no review task is created and
both records remain singleton clusters. This observable difference proves that
the loaded profile replaced the built-in rather than merging with it.

## Run it

```powershell
pwsh scripts/Run-DurableScenario.ps1 -ScenarioPath samples/durable/organization-360
```

All checks should pass. The `organization.profile.json` at the sample root is
the canonical documentation worked example referenced by the architecture docs.
Edit it to retune weights, evaluators, blocking, or thresholds without touching
code.
