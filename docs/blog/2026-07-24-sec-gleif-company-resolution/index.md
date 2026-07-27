# Resolving Companies Across SEC EDGAR and GLEIF When There Is No Shared Key

*What it takes to link two authoritative company registries that were never designed to be joined — and how to prove you got it right, on real public data, with zero false merges on this held-out 49-company benchmark.*

> **Updated 2026-07-27.** Since this was first published, Linkuity's blocking layer went
> through three follow-on rounds (an organization-name canonicalizer, frequency-aware key
> suppression, and looser rare-token/acronym/n-gram keys), and the *scoring* layer has now
> had one of its own: organization names are compared on the same canonical form blocking
> already computes. The numbers, the walkthrough, and the screen recording and Neo4j graphs
> below have all been re-verified against that current state.
>
> Boeing has now been corrected twice, and the sequence is the lesson. It was first
> published as a blocking failure. The blocking rounds turned it into a pair that blocking
> *reached* but scoring then threw away on filler words. The scoring round below finally
> merges it. Two different layers of the same pipeline were wrong, one after the other, and
> each looked like the whole story at the time.

![Linkuity resolving 107 SEC EDGAR + GLEIF company records into 52 golden organizations, then scoring 100% precision / 88.9% recall / F1 94.1% with zero incorrect merges against a held-out CIK/LEI crosswalk](demo.gif)

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
      "roles": ["Searchable","Matchable","Blocking"], "similarityEvaluator": "canonical-jaccard", "weight": 4.0 },
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

So does that mean Boeing merges? For two published versions of this article, no — and *why* is the most instructive part of the whole exercise, because it is the quietest way a pipeline can waste its own blocking work.

Canonicalization, as originally built, was a **blocking-only** concept. The `jaccard` evaluator scored the *raw* field value, article and suffix words included. So blocking saw through `THE BOEING COMPANY` vs `BOEING CO` and handed the pair to the scorer — and the scorer then looked at those same two strings and found one shared token, `boeing`, out of four distinct ones (`the`, `boeing`, `company`, `co`), for a name similarity of **0.25**. Meanwhile `jaccard` on `address_line` scored SEC's real Arlington, VA headquarters against GLEIF's registered-agent address in Wilmington, DE at **0.0** — no shared tokens at all. Weighted: `(4.0×0.25 + 2.5×0 + 0.5×0) / 7.0 ≈ 0.14`, under even the 0.31 review floor. Blocking had done its job and handed scoring a pair that scoring was structurally incapable of accepting.

The fix is to **score the same canonical form you blocked on.** The `canonical-jaccard` evaluator runs the identical canonicalizer — same code, same curated suffix list — over both names before taking the token-set overlap. `THE BOEING COMPANY` and `BOEING CO` both reduce to `{BOEING}`: identical sets, similarity **1.0**. `match scoring explain` prints the pair as:

```
organization_name (canonical-jaccard, w 4): 'BOEING CO' vs 'THE BOEING COMPANY'
address_line (jaccard, w 2.5): '929 LONG BRIDGE DRIVE, ARLINGTON, VA' vs '2711 Centerville Road Suite 400, Wilmington, US-DE'
postal_code (exact, w 0.5): '22202' vs '19808'
  organization_name: sim 1.0000 x w 4   -> 0.5714
  address_line:      sim 0.0000 x w 2.5 -> 0.0000
  postal_code:       sim 0.0000 x w 0.5 -> 0.0000
score 0.5714 -> auto
```

0.14 to **0.5714**: a clean auto-merge, carried entirely by the name, because the two addresses still agree on precisely nothing. Six more pairs cross with it — Walt Disney, Intel, Ford, 3M, Texas Instruments, and Starbucks were all blocked, compared, and then discarded on filler words.

Now the objection you should be forming: *isn't deleting words from names an excellent way to manufacture false merges?* It is the right instinct, and the measured answer is the opposite — canonicalization **deflates** false pairs by the very same mechanism. GLEIF's `THE WALT DISNEY COMPANY` and `THE BOEING COMPANY` are two different companies that share two raw tokens, `the` and `company`. That's 0.4 raw name similarity, and because both are registered at Wilmington, DE agent addresses that happen to share postal code `19808`, the pair scored **0.3893** — inside the review band, where a human had to look at it. Canonicalize both and the filler evaporates: `{WALT, DISNEY}` against `{BOEING}`, similarity **0.0**, total **0.1607**, a clean no-match. Raw-token overlap was inflating true and false pairs alike; stripping the words that carry no identity pulls them apart in both directions. It is also worth noting what propped that false pair up on the address side: both companies list a Delaware registered-agent address, which is the hazard the data-preparation section above described, showing up exactly as predicted.

All of which is the remedy for a pair blocking *did* deliver. The reverse case has no remedy at all: no amount of clever scoring saves a pair that blocking never presents to it, which remains the core lesson — it's just that fewer pairs hit that wall now. **Your blocking keys are a hard ceiling on your recall**, and Linkuity ships an instrument for measuring exactly where that ceiling sits: `match blocking audit` reports the reachable/unreachable pairs against a held-out ground truth, per-strategy attribution, and the busiest blocks, so "the matcher missed an obvious pair" stops being a guessing game. Widening blocking has a real cost, though — the candidate-pair workload for this dataset rises from 71 pairs (the original 3-strategy set) to 2,563 (6 strategies) — so the profile also sets `maxBlockSize: 50`: any blocking key shared by more than 50 records is suppressed entirely rather than left to blow up the candidate set (the busiest offender here, `ngram:inc`, shared by dozens of `Inc`/`Incorporated` records, is exactly what this caps). That trades a small amount of raw reachability (94.4%) for a bounded, still-high **effective** ceiling (88.9%) — the honest number after the cap is applied.

Of the 72 true-match pairs in this dataset, the 8 that remain genuinely unreachable by any name-based key are pure corporate renames with zero token overlap — AT&T/SBC ↔ Southwestern Bell, Meta ↔ Facebook, Verizon ↔ Bell Atlantic. Closing those needs an identifier field (CIK/LEI), which this showcase deliberately withholds; see the "Precision-first tuning" section below.

### Stage 2 — Similarity and scoring

For every candidate pair, each matchable field produces a similarity in [0, 1]:

- **`canonical-jaccard`** on `organization_name` — token-set overlap, |A∩B| / |A∪B|, taken over the *canonical* tokens rather than the raw ones (Stage 1's canonicalizer, run again here at scoring time). `MICROSOFT CORPORATION` and `MICROSOFT CORP` both reduce to `{MICROSOFT}` → 1.0. Jaccard is the right shape for company names — it rewards shared distinctive words and is unbothered by word order, where an edit-distance ratio would be dragged down by `corp` vs `corporation`. Canonicalizing first is what stops the legal suffix from counting as evidence at all.
- **`jaccard`** on `address_line` — the same token-set overlap on the raw value. There is no address canonicalizer, so `ONE MICROSOFT WAY, REDMOND, US-WA` against `ONE MICROSOFT WAY, REDMOND, WA` scores 0.83: the `US-WA`/`WA` spelling is the entire difference.
- **`exact`** on `postal_code` — 1.0 or 0.0.

These combine into a **weighted average**: `Σ(weightᵢ · similarityᵢ) / Σ(weightᵢ)`, over the fields present on both records. Microsoft lands at **0.8690** — canonically identical names (1.0 × 4.0) plus that near-identical street address (0.83 × 2.5) — and clears the bar comfortably even though its postal codes *disagree*: GLEIF carries the ZIP+4 `98052-8300` against SEC's `98052-6399`, and `exact` is unforgiving about it. IBM is subtler still, at **0.7078**: SEC writes `1 NEW ORCHARD ROAD` and GLEIF writes `ONE NORTH CASTLE DRIVE` for the same Armonk campus, so the address lines score only 0.18 — they share the town and the state and nothing else — but the names are canonically identical and both records carry postal code `10504`.

Canonicalizing at scoring time also has a sharp edge, and the honest way to find one is to measure rather than to reason. The canonicalizer deletes `.` without inserting a delimiter, deliberately, so that initials like `J.P.` collapse to `JP` instead of fragmenting. That means `AMAZON.COM, INC.` canonicalizes to the single fused token `AMAZONCOM`, against `AMAZON COM INC`'s `{AMAZON, COM}` — zero overlap, where raw Jaccard had split on the `.` and scored them identical. Amazon had been merging comfortably at ~0.87 and the switch to canonical tokens silently broke it, down to 0.29 and out of the cluster. The audit instrument caught that before the change shipped, and the fix is the one commercial resolution engines have used for years: keep a second, **compressed** representation of each name and check that too. If the canonical token sequences are character-identical once concatenated — `AMAZONCOM` against `AMAZON` + `COM` — the pair scores 1.0 outright. Amazon is back at 0.8661, and the check is strict enough not to reopen the Disney/Boeing case, since `WALTDISNEY` and `BOEING` are not equal under it either.

Crucially, **the score is not a black box.** Linkuity records each field's contribution — signal, value, weight, and its share of the total — and `match explain` will print them one row per factor. In master data management, "the system merged them" is not an acceptable answer to a data steward; "they scored 0.8690: name 1.0 × 4.0, address 0.83 × 2.5, postal 0 × 0.5" is.

### Stage 3 — Decision, clustering, and the golden record

The weighted score falls into one of three bands: **auto-merge** (≥ 0.41), **review** (≥ 0.31), or **no-match**. Auto-merge edges are fed to a **union-find** clustering pass (connected components with path compression and union-by-rank) that turns pairwise matches into entities: if SEC-Apple matches GLEIF-Apple and GLEIF-Apple matches SEC-Apple-former-name, all three land in one cluster transitively, even if some pairs never scored directly.

Each cluster is then merged into one **golden record** via a field-level source-priority policy. Here `organization_name` prefers GLEIF's legal name, while `address_line` and `postal_code` prefer SEC's business address — so the golden Apple record reads `Apple Inc.` (from GLEIF) at `ONE APPLE PARK WAY, CUPERTINO, CA` (from SEC). The golden record is a **composite, not a copy** of any single source.

![Apple Inc. golden organization resolved from five source records across SEC and GLEIF, each linked to the shared golden record by a RESOLVED_TO edge](golden-graph.png)

## The part most demos skip: proving it

It is easy to build a resolver that produces confident-looking output. It is the *validation* that makes it engineering. Because both registries actually have identifiers, we can hold out a **CIK↔LEI crosswalk** — a mapping the matching profile never sees — and score the golden records against it after the fact.

```
==> Company-resolution scorecard
    golden records : 52  (expected 52)
    companies      : 49
    correctly unified : 46
    left separate     : 3
    incorrect merges  : 0
    precision 100.0%  recall 88.9%  F1 94.1%
```

Read that carefully. 107 records collapsed into **52 golden organizations** (not the ideal 49 — 3 companies were left split). Of the pairs the matcher *did* merge on this held-out set, **zero were wrong** (100% pairwise precision). Of the pairs it *should* have merged, it caught 88.9% (recall). Because the crosswalk is never referenced by the profile, a passing scorecard proves genuine name-and-address resolution — not an ID lookup wearing a fuzzy-matching costume.

That recall figure has moved three times, and which way and why is more informative than the number itself. The first version of this pipeline reported 80.6%. It went *down* to 79.2% when a coarse blocking setup — one that had been giving two unrelated rename pairs an accidental shared bucket — was tightened: a correction, not a regression, and exactly the kind of thing you only notice with an audit instrument pointed at held-out truth. It went up to 88.9% with the canonical scoring change above. Incorrect merges stayed pinned at zero through all three.

The three left-separate companies are reported by name, honestly, every run — and they are now all of a single kind: **the genuine rebrands.** Verizon (from Bell Atlantic), Meta (from Facebook), and AT&T (from SBC / Southwestern Bell). SEC carries retired filer names at the same address as the current legal name, but a rebrand shares *no* name tokens with its former identity — `BELL ATLANTIC` and `VERIZON COMMUNICATIONS`, `FACEBOOK` and `META PLATFORMS` — and every blocking key in this profile is derived from the name. So these pairs are never bucketed together and never scored at all: the matching address both records carry never gets the chance to speak. That is the Stage 1 recall ceiling in concrete form, and bridging a rebrand from the name alone would be a hallucination.

AT&T is the instructive half-success among them. The acronym strategy genuinely fires — `acr:sbc` bridges `SOUTHWESTERN BELL CORP` to `SBC COMMUNICATIONS INC`, so AT&T's two retired SEC names correctly land in one cluster. Nothing bridges *that* cluster to `AT&T INC.`, so the company still resolves to two golden records rather than one. A blocking strategy can be working exactly as designed and still not be enough.

The class that used to dominate this list — blocked, compared, and then left under the scoring threshold: Boeing, Walt Disney, Intel, 3M, Ford, Texas Instruments, Starbucks — is gone, closed by the canonical scoring change in Stage 2. Exactly one scoring miss survives it, and it's worth knowing: `WALMART INC.` against the retired filer name `WAL MART STORES INC` scores **0.0** on name, because canonicalization normalizes spelling and suffixes but does nothing about a word that has been split in two. Walmart still resolves correctly — its other records connect it transitively — but that pair is a real miss, and generating joined-token variants is a future round. The audit's decomposition states the whole position in one line: of 72 true pairs, 63 auto-merge, 8 are unreachable by blocking, and 1 is reachable but under-scored.

### What the fix looks like in the graph

Pull Boeing's records out of the resolved graph now and the two islands are gone. GLEIF's `THE BOEING COMPANY` and SEC's `BOEING CO` both point at one shared golden organization:

![Boeing resolved in Neo4j: SEC's BOEING CO and GLEIF's THE BOEING COMPANY both linked by RESOLVED_TO edges to a single shared golden organization](boeing-graph.png)

Earlier versions of this article ran the same query and got two disconnected nodes, and used that picture to make a point worth keeping even now that it has changed: **the empty picture is identical whether the pair was never compared or was compared and rejected.** The export only draws an edge for pairs that clear the auto-merge threshold, so "no edge" on its own cannot tell you which stage failed. When Boeing looked like that, it was the second case — bucketed, scored at 0.14, rejected — and the only way to learn that was `match explain`, not this graph. The three rebrands above still render exactly that way today, and for them it is the *first* case: never bucketed, never scored.

That is the practical form of the lesson. A resolution graph shows you outcomes; it does not show you which stage produced them. Picking the wrong stage is how you spend a week tuning thresholds on a pair that blocking never delivered — or, as here, a week widening blocking keys for a pair that blocking had already delivered and scoring was quietly throwing away.

## Precision-first tuning is a decision, not a default

Why is the auto-merge threshold **0.41** — a number that looks alarmingly low if you expect matches to score near 1.0? Because in master data management the two kinds of error are not equal. A **false merge** silently fuses two different companies into one golden record and corrupts every downstream report; unwinding it later is expensive and often undetected. A **missed merge** leaves two records where there should be one — visible, safe, fixable. So the threshold was tuned down until recall was as high as it could go **while incorrect merges stayed at exactly zero.** That's the tradeoff made explicit: 88.9% recall bought at 100% precision, on purpose.

Notice which lever moved to buy the recall gain described above: none of them. The thresholds are the same 0.41 and 0.31 they have always been. The gain came from making the *similarity* more honest, not from lowering the bar — and that distinction shows up in the score distribution, which is the diagnostic worth watching. Before the change, the highest-scoring false pair on the entire dataset was that Disney-vs-Boeing collision at 0.3893, sitting **0.02 below the auto-merge cut**; true pairs were scattered down through the same range. There was no headroom left to tune. After it, no false pair anywhere scores above 0.20, the lowest true auto-merge is 0.41, and the corridor between them is empty. Same threshold, radically more margin behind it. A threshold is only as good as the separation underneath it, and separation is bought in the similarity function, not the cut.

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
4. **Normalize once, then use that form everywhere.** If blocking canonicalizes a field and scoring doesn't, blocking will faithfully deliver pairs that scoring is structurally unable to accept, and nothing in either stage looks broken. Nearly ten points of recall were sitting in that gap here. Check that every stage sees the same view of a field.
5. **Tune precision-first when a false merge is the expensive error** — and state the tradeoff out loud instead of chasing a single accuracy number. Then watch the separation between your true and false score distributions, not just the threshold you drew between them.
6. **You haven't resolved anything until you've validated against held-out truth.** Confident output is not correct output. If you can hold out an identifier and score against it, do — and report your failures by name.

The full, reproducible showcase — data, profile, validator, and honest scorecard — is on [GitHub](https://github.com/linkuity/linkuity/tree/main/showcases/company-resolution).
