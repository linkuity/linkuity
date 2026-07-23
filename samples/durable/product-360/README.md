# product-360 (durable sample)

Demonstrates that the matching engine resolves a **product** dataset end to end
through a **configuration-only `product.profile.json`** — no engine code changes
beyond the identifier generalisation (`FieldRole.Identifier`). Product is not a
built-in content type, so `--profiles product.profile.json` is passed on every
`ingest-incremental` step.

## What it shows

- **Config-only profile** — `product.profile.json` declares `sku` and `gtin` as
  `FieldRole.Identifier` fields with `exact` similarity and weight 3.0. No C# code
  changes are required to add a new domain; the profile drives everything.
- **SKU auto-merge across sources** — `shop-010` (source: Shop) shares SKU
  `ALPHA-100` with `cat-001` (source: Catalog). The `exact-value` blocking strategy
  produces a shared blocking key on the identifier field and the `identifier-weighted`
  scorer boosts the pair above the 0.90 auto-match threshold.
- **GTIN auto-merge when SKU differs** — `supp-020` (source: Supplier) uses a
  region-variant SKU `ALPHA-100-EU` but shares GTIN `00012345600012` with `cat-001`.
  The GTIN identifier key is enough to auto-merge even though the SKU key misses.
- **Same name, different identifiers → kept separate, not a false merge** — `web-030`
  (source: Web) has a completely different SKU (`ZETA-900`) and GTIN
  (`00077777700030`) but the same `product_name` (`Widget Alpha`). The `token-name`
  strategy creates a shared name block so the pair is evaluated; the identifier-weighted
  scorer sees two identifier misses against a single fuzzy name hit and scores the pair
  below the review band. It does **not** merge — it becomes its own distinct cluster. The
  differing SKU and GTIN correctly keep a knockoff from being merged into the real product.

## Final state

| Cluster | Members | How |
|---------|---------|-----|
| Alpha cluster | cat-001, shop-010, supp-020 | SKU match, then GTIN match |
| Beta singleton | cat-002 | no matching record |
| Gamma singleton | cat-003 | no matching record |
| Web singleton | web-030 | different identifiers → kept separate |

4 clusters total, no false merge — `web-030` is correctly kept as its own entity.

## Run it

```powershell
pwsh scripts/Run-DurableScenario.ps1 -ScenarioPath samples/durable/product-360
```

All checks should pass. Edit `product.profile.json` to retune weights, evaluators,
blocking, or thresholds without touching any engine code.
