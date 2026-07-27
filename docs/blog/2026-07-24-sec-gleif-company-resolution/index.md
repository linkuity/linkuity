# Resolving Companies Across SEC EDGAR and GLEIF When There Is No Shared Key

*What it takes to link two authoritative company registries that were never designed to be joined — and how to prove you got it right, on real public data, with zero false merges on this held-out 49-company benchmark.*

> **Updated 2026-07-25.** Since this was first published, Linkuity's blocking layer
> went through three follow-on rounds (an organization-name canonicalizer, frequency-aware
> key suppression, and looser rare-token/acronym/n-gram keys). The numbers, the blocking
> walkthrough, and the screen recording and Neo4j graphs below have all been re-captured
> against that current state — including a corrected Boeing story: it's no longer a pure
> blocking miss, it's a pair that blocking now *reaches* but scoring still correctly holds
> apart (see "What a left-separate case looks like in the graph" below).

![Linkuity resolving 107 SEC EDGAR + GLEIF company records into 59 golden organizations, then scoring 100% precision / 79.2% recall / F1 88.4% with zero incorrect merges against a held-out CIK/LEI crosswalk](demo.gif)

## The JOIN you can't write

Here is a problem that looks trivial until you try it. The U.S. Securities and Exchange Commission publishes EDGAR, a registry of every company that files with it, keyed on a **CIK** (Central Index Key). The Global Legal Entity Identifier Foundation publishes GLEIF, a global registry of legal entities, keyed on an **LEI** (Legal Entity Identifier). Both describe Apple, Microsoft, IBM, and thousands of other companies. Both are free, current, and redistributable.

Now put them in the same database and write the query that says "these two rows are the same company."

You can't. There is no shared key. CIK and LEI are issued by different authorities that never coordinated, and neither the SEC data nor the basic GLEIF fields used here give you a ready-made CIK-to-LEI join key. The only thing the two registries agree on is a **company name** (spelled differently in each) and a **postal address** (formatted differently in each). That's it. `SELECT ... JOIN ... ON` has nothing to join on.

This is the essence of **entity resolution**: deciding that two records refer to the same real-world thing when no identifier tells you so. It shows up everywhere — customer masters, supplier consolidation, healthcare identity, deduplicating a CRM after an acquisition. Company resolution across SEC and GLEIF is a particularly honest instance of it, because the ground truth exists (both registries *do* have identifiers) but you're forbidden from using it to match — so you can measure exactly how well name-and-address resolution actually works.

This article walks through doing it end to end with [Linkuity](https://github.com/linkuity/linkuity), an open-source entity-resolution engine, and — more importantly — through the general lessons the exercise teaches: **blocking is the ceiling on your recall, precision-first tuning is a deliberate choice, and you have not resolved anything until you've validated against ground truth you held out.**

## The data: two registries, no bridge

The [showcase](https://github.com/linkuity/linkuity/tree/main/showcases/company-resolution) covers 49 well-known public companies.

| Source | Provides | Identifier | License |
|--------|----------|-----------|---------|
| SEC EDGAR | EDGAR-conformed name, former filer names, business address | CIK | US-gov public domain |
| GLEIF | legal name, legal / HQ address | LEI | CC0 |

For each company, one GLEIF record and one current SEC record are projected into the input, **plus one extra SEC record for every retired filer name** a company has. Apple, for instance, contributes four SEC rows — its current `Apple Inc.` plus the historical `APPLE INC`, `APPLE COMPUTER INC`, and `APPLE COMPUTER INC/ FA` — and one GLEIF row. That yields **107 input records that should resolve to 49 companies**: a gap the matcher has to close using fuzzy name and address alone.

One honesty note baked into the data preparation, because it materially changes what can match: GLEIF exposes both a `legalAddress` and a `headquartersAddress`. The legal address is frequently a shared registered-agent address — a single Delaware "C/O Corporation Trust Center" used by hundreds of unrelated companies. Matching on *that* would manufacture false positives by the dozen. The showcase deliberately uses GLEIF's **headquarters address** — usually more comparable to SEC's business address than the legal address, though not guaranteed to be a true operating HQ (Boeing's, as we'll see, is itself a Delaware registered-agent address) — falling back to the legal address only when no HQ address exists. Which address you pick is a modeling decision, not a detail — and it belongs in the open, not buried in a script.

## The pipeline

Entity resolution is not one algorithm; it's a pipeline of them. Linkuity's stages, and the exact configuration this showcase uses, are declared in a single JSON profile — no code. This is an abbreviated view; the [full profile](https://github.com/linkuity/linkuity/blob/main/showcases/company-resolution/run/company.profile.json) also declares the `source` field, `normalizationStrategy`, `candidateRetrievalStrategy`, and `decisionStrategy`:

```json
{
  "contentType": "organization",
  "fields": [
    { "name": "organization_name", "semanticType": "OrganizationName",
      "roles": ["Searchable","Matchable","Blocking"], "similarityEvaluator": "jaccard", "weight": 4.0 },
    { "name": "address_line", "semanticType": "AddressLine",
      "roles": ["Searchable","Matchable"], "similarityEvaluator": "jaccard", "weight": 2.5 },
    { "name": "postal_code", "semanticType": "PostalCode",
      "roles": ["Matchable"], "similarityEvaluator": "exact", "weight": 0.5 }
  ],
  "blockingStrategies": ["exact-value", "fingerprint", "phonetic", "token", "acronym", "ngram"],
  "maxBlockSize": 50,
  "similarityStrategy": "field-weighted",
  "scoringStrategy": "identifier-weighted",
  "clusteringStrategy": "union-find",
  "autoMatchThreshold": 0.41,
  "reviewThreshold": 0.31
}
```

Read top to bottom, that says: match organizations on name (heavily), address (moderately), and postal code (lightly, exact); generate candidate pairs by blocking; score each pair as a weighted average of per-field similarity; cluster the matches; and auto-merge anything scoring ≥ 0.41. **Note what's absent: there is no identifier field.** CIK and LEI are nowhere in this profile — they live only in the held-out ground truth. Everything below is name-and-address resolution.

### Stage 1 — Blocking, and why it's the ceiling on recall

You cannot compare all 107 records to each other and call it a day at this scale — but at real scale (millions of records) the O(n²) comparison is fatal, so every entity-resolution system starts with **blocking**: cheaply bucketing records by a key, and only comparing records that share a bucket. Linkuity's batch path always runs blocking-gated retrieval — a pair that shares no blocking key is **never scored, never even considered.**

That single sentence is the most important thing to understand about entity resolution, so let's make it concrete. This profile runs six blocking strategies over the organization name, most of them keying off a shared **canonicalizer**: uppercase, drop leading articles (`THE`), and strip a curated list of trailing legal-entity suffixes (`CO`, `COMPANY`, `INC`, `CORP`, `CORPORATION`, and ~40 more) before generating keys. The headline strategies:

- **`fingerprint`** — the sorted, deduped set of canonical tokens. `MICROSOFT CORP` and `MICROSOFT CORPORATION` both canonicalize to `MICROSOFT` (the suffix strips off), so they land on the identical key. `THE BOEING COMPANY` and `BOEING CO` both canonicalize to `BOEING` — the leading article and the trailing suffix are exactly what this strategy is built to see through.
- **`token`** — one key per canonical token (minimum length 2), deliberately loose — a Splink-style "many strict keys" model that's safe because a frequency cap (below) keeps any one token-key from exploding.
- **`acronym`** — generates and recognizes initials, so `SOUTHWESTERN BELL CORP` and `SBC` share a key even though they don't share a single token.
- **`ngram`**, **`phonetic`**, **`exact-value`** — trigram, phonetic, and exact-identifier keys, rounding out recall for typos, transliteration, and shared domains/emails/phones.

Between them, all five Apple records land together, Microsoft's two records meet on `fingerprint:MICROSOFT`, IBM's meet, and — the case the original version of this article used as the headline blocking *failure* — **Boeing's two records now land together too.** GLEIF's `THE BOEING COMPANY` and SEC's `BOEING CO` both canonicalize to `BOEING`, share the `fingerprint` key, and the matcher gets its chance to score them. The same fix reaches Coca-Cola, Procter & Gamble, and Walt Disney.

So does that mean Boeing merges? **No — and that's the more interesting lesson.** Boeing gets compared now, but it doesn't come close to merging: `jaccard` on `organization_name` scores `THE BOEING COMPANY` vs `BOEING CO` at **0.25** (one shared token, `boeing`, out of four distinct ones — `the`, `boeing`, `company`, `co`), and `jaccard` on `address_line` scores SEC's real Arlington, VA headquarters against GLEIF's registered-agent address in Wilmington, DE at **0.0** (no shared tokens at all). Weighted: `(4.0×0.25 + 2.5×0 + 0.5×0) / 7.0 ≈ 0.14` — well under even the 0.31 review floor, let alone the 0.41 auto-merge bar. Notice what canonicalization *didn't* do here: it's a **blocking-only** concept — the `jaccard` evaluator scores the raw field value, article and suffix words included, so `THE`/`COMPANY` on one side and `CO` on the other still count as real, unmatched tokens against the name similarity. Canonicalization got the pair *compared*; it did nothing for how similar they *score*. **This is no longer a blocking problem — it's a scoring/address problem**, and it's a cleaner diagnosis than "the pair was never compared": blocking did its job; the address signal (and the raw-token name similarity) is the honest bottleneck now. Walt Disney has the same shape. (More on this class of problem — and why a shared *registered-agent* address is closer to noise than signal — in the next section.)

No amount of clever scoring saves a pair that blocking never presents to it, which is still the core lesson — it's just that fewer pairs hit that wall now. **Your blocking keys are a hard ceiling on your recall**, and Linkuity ships an instrument for measuring exactly where that ceiling sits: `match blocking audit` reports the reachable/unreachable pairs against a held-out ground truth, per-strategy attribution, and the busiest blocks, so "the matcher missed an obvious pair" stops being a guessing game. Widening blocking has a real cost, though — the candidate-pair workload for this dataset rises from 71 pairs (the original 3-strategy set) to 2,563 (6 strategies) — so the profile also sets `maxBlockSize: 50`: any blocking key shared by more than 50 records is suppressed entirely rather than left to blow up the candidate set (the busiest offender here, `ngram:inc`, shared by dozens of `Inc`/`Incorporated` records, is exactly what this caps). That trades a small amount of raw reachability (94.4%) for a bounded, still-high **effective** ceiling (88.9%) — the honest number after the cap is applied.

Of the 72 true-match pairs in this dataset, the 8 that remain genuinely unreachable by any name-based key are pure corporate renames with zero token overlap — AT&T/SBC ↔ Southwestern Bell, Meta ↔ Facebook, Verizon ↔ Bell Atlantic. Closing those needs an identifier field (CIK/LEI), which this showcase deliberately withholds; see the "Precision-first tuning" section below.

### Stage 2 — Similarity and scoring

For every candidate pair, each matchable field produces a similarity in [0, 1]:

- **`jaccard`** on `organization_name` and `address_line` — token-set overlap, |A∩B| / |A∪B|. `MICROSOFT CORP` vs `MICROSOFT CORPORATION` share only the token `microsoft` out of three distinct tokens → 0.33. Jaccard is the right tool for company names: it rewards shared distinctive words and is unbothered by word order or a differing legal suffix, where a raw edit-distance ratio would be dragged down by `corp`/`corporation`.
- **`exact`** on `postal_code` — 1.0 or 0.0.

These combine into a **weighted average**: `Σ(weightᵢ · similarityᵢ) / Σ(weightᵢ)`, over the fields present on both records. So Microsoft — names only 0.33 alike, but a near-identical street address weighted 2.5× — clears the bar on the strength of the address. IBM is subtler still: SEC writes `1 NEW ORCHARD ROAD` and GLEIF writes `ONE NORTH CASTLE DRIVE` for the same Armonk campus (completely different strings), but the names are strongly alike **and both carry postal code `10504`** — the exact-matching postal signal plus name similarity bridges a pair whose address lines look nothing alike.

Crucially, **the score is not a black box.** Linkuity records each field's contribution — signal, value, weight, and its share of the total — and `match explain` will print them one row per factor. In master data management, "the system merged them" is not an acceptable answer to a data steward; "they scored 0.49: name 0.33 × 4.0, address 0.83 × 2.5, postal 0 × 0.5" is.

### Stage 3 — Decision, clustering, and the golden record

The weighted score falls into one of three bands: **auto-merge** (≥ 0.41), **review** (≥ 0.31), or **no-match**. Auto-merge edges are fed to a **union-find** clustering pass (connected components with path compression and union-by-rank) that turns pairwise matches into entities: if SEC-Apple matches GLEIF-Apple and GLEIF-Apple matches SEC-Apple-former-name, all three land in one cluster transitively, even if some pairs never scored directly.

Each cluster is then merged into one **golden record** via a field-level source-priority policy. Here `organization_name` prefers GLEIF's legal name, while `address_line` and `postal_code` prefer SEC's business address — so the golden Apple record reads `Apple Inc.` (from GLEIF) at `ONE APPLE PARK WAY, CUPERTINO, CA` (from SEC). The golden record is a **composite, not a copy** of any single source.

![Apple Inc. golden organization resolved from five source records across SEC and GLEIF, each linked to the shared golden record by a RESOLVED_TO edge](golden-graph.png)

## The part most demos skip: proving it

It is easy to build a resolver that produces confident-looking output. It is the *validation* that makes it engineering. Because both registries actually have identifiers, we can hold out a **CIK↔LEI crosswalk** — a mapping the matching profile never sees — and score the golden records against it after the fact.

```
==> Company-resolution scorecard
    golden records : 59  (expected 59)
    companies      : 49
    correctly unified : 39
    left separate     : 10
    incorrect merges  : 0
    precision 100.0%  recall 79.2%  F1 88.4%
```

Read that carefully. 107 records collapsed into **59 golden organizations** (not the ideal 49 — 10 companies were left split). Of the pairs the matcher *did* merge on this held-out set, **zero were wrong** (100% pairwise precision). Of the pairs it *should* have merged, it caught 79.2% (recall). Because the crosswalk is never referenced by the profile, a passing scorecard proves genuine name-and-address resolution — not an ID lookup wearing a fuzzy-matching costume.

(Recall reads slightly lower than the very first version of this pipeline, 79.2% vs. an earlier 80.6% — not a regression, but an honest correction. A coarser blocking setup used to give a couple of unrelated rename pairs an accidental shared bucket; closing that gap is exactly what the audit instrument below is for, and it's why the number moved down before the wider blocking strategies below moved it back up.)

The ten left-separate companies are reported by name, honestly, every run:

- **Blocked, but under the scoring threshold** — Boeing, Walt Disney, Intel, 3M, Ford, Texas Instruments, Starbucks. These now share a blocking key (the canonicalizer/fingerprint work described above sees past `THE X COMPANY` vs `X CO`, article words, and suffix abbreviations), so they *are* compared — they just don't clear the 0.41 auto-merge bar, usually because the SEC business address and the GLEIF address disagree. This is a scoring/address problem now, not a blocking one.
- **The genuine rebrands** — AT&T (from SBC), Meta (from Facebook), and Verizon (from Bell Atlantic). SEC carries retired filer names at the same address as the current legal name, but a rebrand shares *no* name tokens with its former identity — `BELL ATLANTIC` and `VERIZON COMMUNICATIONS`, `FACEBOOK` and `META PLATFORMS` — so even though the addresses match, the matcher correctly refuses to guess that a company rebranded. That's not a bug — bridging a rebrand from name and address alone would be a hallucination. (AT&T/SBC is the one exception that *does* close: `acr:sbc` — the acronym strategy — bridges `SOUTHWESTERN BELL CORP` to `SBC`, which is why it shows up as "correctly unified" rather than in this list.)

### What a left-separate case looks like in the graph

Pull Boeing's records out of the resolved graph and it still looks exactly like the original version of this article described — two disconnected islands, no edge between them:

![Boeing left separate in Neo4j: SEC's BOEING CO and GLEIF's THE BOEING COMPANY each resolve to their own separate golden organization, with no MATCHED_TO edge between them](boeing-graph.png)

That's worth pausing on, because it's a genuinely useful caveat about reading a resolution graph: **this picture is identical whether the pair was never compared or compared and correctly rejected** — the Neo4j export only draws a `MATCHED_TO` edge for pairs that clear the auto-merge threshold, so a "no edge" here doesn't distinguish "blocking never presented this pair" from "blocking presented it and scoring said no." Today it's the latter: Boeing's two records *do* share a blocking key (`fingerprint`, per Stage 1) and get scored — at **0.14**, nowhere near even the 0.31 review floor — so union-find still has nothing to connect. The *outcome* is unchanged from the original run (two golden organizations, not one), and so, coincidentally, is the graph; what changed is the *reason*, and you can only see that reason with `match explain`, not with this graph export. Set this beside Apple's fully-connected cluster from Stage 3: for Apple, the blocking-to-scoring pipeline runs all the way through to a merge; for Boeing, it runs all the way through and stops well short of even a review-band edge. Multiply the remaining gap across the ten left-separate companies above and you get 59 golden organizations instead of the ideal 49.

## Precision-first tuning is a decision, not a default

Why is the auto-merge threshold **0.41** — a number that looks alarmingly low if you expect matches to score near 1.0? Because in master data management the two kinds of error are not equal. A **false merge** silently fuses two different companies into one golden record and corrupts every downstream report; unwinding it later is expensive and often undetected. A **missed merge** leaves two records where there should be one — visible, safe, fixable. So the threshold was tuned down until recall was as high as it could go **while incorrect merges stayed at exactly zero.** That's the tradeoff made explicit: 79.2% recall bought at 100% precision, on purpose.

And when you genuinely need to close the recall gap? The lever is sitting right there, deliberately unused: feed the CIK↔LEI crosswalk into the profile as an **`Identifier` field**. An exact identifier match floors a pair straight into the auto band regardless of name or address, instantly unifying every hard case. This showcase withholds that strong key precisely so it can measure how far fuzzy resolution gets *without* it. In production you'd use every reliable key you have — the point of the exercise is to know what the fuzzy layer contributes underneath.

## Run it yourself

Offline, from committed data, in about fifteen seconds (needs only the .NET 10 SDK and PowerShell 7):

```powershell
git clone https://github.com/linkuity/linkuity.git
cd linkuity
./showcases/company-resolution/run-demo.ps1
```

It runs the resolution and prints the scorecard above. Add `-Neo4j` to emit a graph export you can load into Neo4j and explore the clusters visually; add `-Refresh -UserAgent "you@example.com"` to re-pull live SEC + GLEIF data and rebuild the input from scratch.

## Takeaways

Whether or not you ever touch SEC and GLEIF, the lessons port to any entity-resolution problem:

1. **When there's no shared key, resolution is your only option** — and it's a pipeline (normalize → block → score → decide → cluster → merge), not a single fuzzy-match call.
2. **Blocking sets your recall ceiling.** Records that share no blocking key are never compared. Most "the matcher missed an obvious pair" bugs are blocking bugs, and they fail silently — audit your blocking before you tune your scoring.
3. **Pick the right similarity for the field.** Token Jaccard for multi-word names, exact for codes, edit-distance for typos. One evaluator does not fit all fields.
4. **Tune precision-first when a false merge is the expensive error** — and state the tradeoff out loud instead of chasing a single accuracy number.
5. **You haven't resolved anything until you've validated against held-out truth.** Confident output is not correct output. If you can hold out an identifier and score against it, do — and report your failures by name.

The full, reproducible showcase — data, profile, validator, and honest scorecard — is on [GitHub](https://github.com/linkuity/linkuity/tree/main/showcases/company-resolution).
