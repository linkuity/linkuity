# How Matching Works

This guide explains how Linkuity decides that two records describe the same
real-world entity — end to end, from raw input to a merged golden record — and
how you steer that behavior with a matching profile and a few tuning knobs. It is
written for the person configuring and operating Linkuity, not for someone reading
the engine source. After reading it you should be able to predict what the engine
will do with your data, read an explanation of why two records did or didn't
merge, and diagnose the two things that usually go wrong: **under-merging**
(duplicates left separate) and **over-merging** (distinct entities collapsed
together).

Everything here describes the current .NET resolution engine used by the durable
MDM projects and the CLI. Concrete numbers in the worked example were produced by
running the engine over the sample data below, not hand-computed.

## The problem, and the pipeline at a glance

The naive way to deduplicate `N` records is to compare every record against every
other — `N²/2` comparisons. At a million records that is 500 billion comparisons;
it does not scale. Linkuity avoids it by only ever comparing records that already
share a cheap-to-compute **blocking key**, then scoring just those candidate pairs.

A record travels through these stages, in order:

1. **Normalization** — clean each field to a canonical form so trivial formatting
   differences don't matter.
2. **Blocking** — reduce each record to a set of blocking keys; only records that
   share a key will ever be compared.
3. **Candidate retrieval** — for an incoming record, fetch the best-ranked records
   that share a key (capped at `MaxCandidates`).
4. **Similarity & scoring** — compare the incoming record against each candidate
   field by field and combine the field scores into one pair score.
5. **Decision bands** — turn the score into one of three outcomes: auto-match,
   review, or no-match.
6. **Clustering** — group all the accepted pairs transitively into entities.
7. **Within-batch resolution & bridge-merge** — resolve a new batch against itself
   and the existing data in one order-independent pass.
8. **Golden records & merge policy** — collapse each cluster into one
   representative record.
9. **Versioning** — record a new golden-record version only when the merged
   fields actually change.
10. **Review tasks** — surface borderline pairs to a human.
11. **Explainability** — show, per field, exactly why a pair scored the way it did.

The rest of this guide walks each stage with its intent, its inputs and outputs, a
step of the running worked example, where it is configured in the profile, and —
where a knob exists — a tuning note.

## The worked example

We'll follow these six person records, ingested from three sources, through every
stage. They use the built-in `person` profile.

| id | source    | first_name | last_name | email           | phone     | postal_code |
|----|-----------|------------|-----------|-----------------|-----------|-------------|
| r1 | crm       | Jane       | Smith     | jane@acme.com   | 555-0100  | 94105       |
| r2 | marketing | Jane       | Smith     | jane@acme.com   | *(none)*  | 94105       |
| r3 | support   | J          | Smith     | jane@acme.com   | 555-0100  | 94105       |
| r4 | crm       | John       | Smith     | john@acme.com   | 555-0200  | 94110       |
| r5 | crm       | Robert     | Jones     | bob@example.com | 555-0300  | 94107       |
| r6 | marketing | Rob        | Jones     | *(none)*        | *(none)*  | 94107       |

There are really three entities here: **Jane Smith** (r1, r2, r3 — the same person
from three systems), **John Smith** (r4 — a different person who merely shares the
surname), and **Robert "Rob" Jones** (r5, r6).

### Verified results (produced by running the engine)

These are the actual outputs the sections below refer back to.

**Blocking keys** (note: keys are normalized — punctuation stripped, lowercased):

| id | blocking keys |
|----|---------------|
| r1 | `email:janeacmecom`, `name:smith`, `phone:5550100` |
| r2 | `email:janeacmecom`, `name:smith` |
| r3 | `email:janeacmecom`, `name:smith`, `phone:5550100` |
| r4 | `email:johnacmecom`, `name:smith`, `phone:5550200` |
| r5 | `email:bobexamplecom`, `name:jones`, `phone:5550300` |
| r6 | `name:jones` |

**Scored pairs** (only pairs that share a blocking key are ever compared):

| pair | shared block(s) | score | decision |
|------|-----------------|-------|----------|
| r1–r2 | `email:janeacmecom`, `name:smith` | 1.00 | **auto-match** |
| r1–r3 | `email:janeacmecom`, `name:smith`, `phone:5550100` | 1.00 | **auto-match** |
| r2–r3 | `email:janeacmecom`, `name:smith` | 1.00 | **auto-match** |
| r1–r4 | `name:smith` | 0.25 | **no-match** |
| r2–r4 | `name:smith` | 0.25 | **no-match** |
| r3–r4 | `name:smith` | 0.25 | **no-match** |
| r5–r6 | `name:jones` | 1.00 | **auto-match** |

Pairs that share no blocking key (for example r1 and r5) are never retrieved as
candidates, so they are never compared — an implicit no-match.

**Clusters** (from the auto-matched pairs): `{r1, r2, r3}`, `{r4}`, `{r5, r6}`.

**Review tasks**: none. John Smith shares only the coarse `name:smith` block with the
Janes, and his low field agreement keeps him below the review-floor gate (see below),
so no review task is created.

**Golden records**: one per cluster — a single "Jane Smith", a single "John Smith",
and a single "Robert Jones".

Two outcomes here are worth holding onto, because they capture how the engine
actually behaves and they recur in the tuning section:

- **John Smith (r4) did not merge with the Janes, and lands in no-match**, not
  review. Sharing the coarse `name:smith` block is enough to be *compared*, but
  because his email and phone are present and *different*, his weighted field
  agreement (0.25) falls below the **review-floor gate** (default 0.75), so the
  review floor never applies. A coarse shared block alone does not flood the review
  queue; John stays separate on the engine's own judgment.
- **Robert and Rob Jones (r5, r6) auto-merged with no shared identifier at all** —
  purely on strong overall field agreement (same surname, same postal code, and
  `Rob` matching `Robert`). Auto-match is not identifier-only.

## Normalization

**Intent.** Before anything is compared, each configured field is cleaned to a
canonical form so that trivial formatting differences — casing, surrounding
whitespace, punctuation — never cause a miss. `"Jane@Acme.com "` and
`"jane@acme.com"` should be treated as the same email, and they are.

**Inputs → outputs.** A raw field value in, a normalized value out, driven by each
field's *semantic type* (an `Email` is cleaned differently from a `DateOfBirth`):
phones to a canonical E.164-style form, emails and domains lowercased, dates to ISO
form, honorifics stripped from names. In the durable MDM path this cleaning happens
once, **at ingest**, so the records the engine later scores are already canonical.

There is a second, narrower normalization used only when a value becomes a
*blocking key* or is compared for an *exact* match: it keeps just letters and
digits and lowercases them. That is why r1's email `jane@acme.com` produces the key
`email:janeacmecom` and its phone `555-0100` produces `phone:5550100` (see
Blocking, next) — the punctuation is dropped for keying.

**Worked-example step.** `jane@acme.com` and a hypothetical `"Jane@Acme.com "`
normalize to the same email, so r1, r2, and r3 share it exactly; `J` versus `Jane`
are left as distinct first-name values (normalization cleans, it does not guess at
abbreviations — that job falls to fuzzy scoring later).

**Where it's configured.** Each field's `semanticType` selects its cleaning rules,
and the profile's `normalizationStrategy` selects when the engine applies them. The
built-in `person` and `organization` profiles use `identity` — a deliberate no-op
at match time — because durable records are already normalized upstream at ingest
and the evaluators normalize internally where needed; the alternative
`semantic-field` strategy applies the field cleaning at match time for callers that
feed raw records. The exhaustive per-type rules live in the reference table in
[`docs/architecture.md`](architecture.md) under "Normalization"; this guide does
not repeat them.

## Blocking

**Intent.** Blocking is the first cost-control step and the reason Linkuity scales.
Instead of comparing an incoming record against every existing record, each record
is reduced to a small set of **blocking keys**, and only records that share at least
one key ever become candidates for scoring. A blocking key is a cheap, exact string
— think of it as "records that agree on *this* are worth a closer look."

The essential trade-off to understand up front: blocking keys must be **loose
enough** that true duplicates land in the same block, but **selective enough** that
blocks don't become huge. A key every record shares would put you back at
all-to-all. Most tuning problems trace back to this balance (see
[Tuning and troubleshooting](#tuning-and-troubleshooting)).

**Inputs → outputs.** A normalized record in; the set of its blocking keys out.

**How a record's keys are produced.** Key creation is entirely **profile-driven** —
there are no hardcoded field names in the engine. The engine loops over the strategy
names the profile lists in `blockingStrategies`, runs each one, and takes the
**union** of the keys they produce (duplicates removed). The built-in `person`
profile uses `["exact-value", "token-name"]`. The built-in `organization` profile
uses a wider set — `["exact-value", "fingerprint", "phonetic", "token", "acronym",
"ngram"]` plus `maxBlockSize: 50` — because organization names carry more variation
(legal suffixes, abbreviations, acronyms, rebrand history) than person names do; see
below. A few more strategies — `prefix` and `dob-lastname-phonetic` — ship
registered but **off by default** for both built-ins; any profile opts into one by
naming it.

Every strategy chooses which fields it operates on through a three-part test. A
field participates only when **all three** hold:

1. the field carries the **`Blocking`** role in the profile,
2. its **semantic type** is one the strategy handles, and
3. the record has a **non-empty value** for it.

So as the profile author you control, per field, *whether* it blocks (the role) and
*what kind* of value it is (the semantic type). The built-in strategies:

- **`exact-value`** keys off fields that are an exact-identifier semantic type
  (`Email`, `Phone`, `DomainName`, `DateOfBirth`) **or** carry the `Identifier`
  role (this is how a `Sku` or `Gtin` blocks in other domains, with no engine
  change). It emits `"{field}:{normalized}"`.
- **`token-name`** keys off name-ish types (`LastName`, `FullName`,
  `OrganizationName`, `ProductName`), takes the **last token** of the value, and
  emits `"name:{lastToken}"`.
- **`fingerprint`**, **`token`**, and **`acronym`** key off `OrganizationName`
  specifically, and share an **organization-name canonicalizer**: uppercase, drop a
  leading article (`THE`/`A`/`AN`), and strip a curated list of trailing
  legal-entity suffixes (`INC`, `CORP`, `CORPORATION`, `CO`, `COMPANY`, `LLC`, and
  ~40 more — deliberately excluding `GROUP`/`GRUPO`/`INTERNATIONAL`/`GLOBAL`, which
  carry real meaning). This is what lets `THE BOEING COMPANY` and `BOEING CO` — an
  article plus two different suffix spellings — resolve to the same canonical form,
  `BOEING`:
  - **`fingerprint`** emits one key per sorted, deduped set of canonical tokens
    (`fp:{tokens}`), plus a hyphen-collapsed variant (so `WAL-MART` and `WALMART`
    key alike).
  - **`token`** emits one key **per canonical token** (`token:{t}`, minimum length
    2) — deliberately loose (the "many strict keys" model), safe only because
    `maxBlockSize` (below) caps how large any single token's block can grow.
  - **`acronym`** emits initials-based keys (`acr:{initials}`) in both directions —
    generating initials from the name (`SOUTHWESTERN BELL CORP` → `sbc`) and
    recognizing short all-alphabetic tokens as potential acronyms already present
    (`SBC` → `sbc`) — so a company and its own acronym co-block.
- **`ngram`** emits trigrams **per token** (never spanning a word boundary — a gram
  that straddles two words, like the `ein` at the end of `SERVICE` and the start of
  `INC`, is meaningless and only manufactures false candidates), tolerant of typos
  and minor misspellings.
- **`phonetic`** groups names that sound alike regardless of spelling.

Values are run through the match-key normalizer (lowercase, keep only letters and
digits) before becoming keys, which is why `Jane@Acme.com` and `jane@acme.com`
land on the same block, and a value that is empty after normalization produces no
key.

**Blocking-key suppression (`maxBlockSize`).** Looser strategies like `token` and
`ngram` buy recall at a cost: a common token (`ngram:inc`, shared by every `Inc`/
`Incorporated` record) can generate an enormous block, which is expensive to score
and rarely useful (a block that big is not selective evidence of anything). A
profile can set `maxBlockSize` to an absolute cap: any blocking key shared by more
than that many records is **suppressed entirely** — it contributes zero candidates,
rather than being ranked and truncated like `MaxCandidates` does. This is a
different lever from the candidate cap (see [Candidate retrieval and the candidate
limit](#candidate-retrieval-and-the-candidate-limit)): `MaxCandidates` bounds *how
many* candidates a query returns, ranked best-first; `maxBlockSize` decides whether
a *specific key* is trustworthy enough to contribute at all. A record usually
produces several keys, so losing one suppressed key rarely means losing all its
candidates — it means falling back on its other keys. `match blocking audit`
reports both the raw ceiling (ignoring suppression) and the **effective** ceiling
(what's actually reachable after it), plus which keys got suppressed and the pairs
that suppression alone put out of reach, so you can see exactly what the cap cost.

**Worked-example step.** With the `person` profile, `email`, `phone`, and
`date_of_birth` are exact-identifier fields with the `Blocking` role, and
`last_name`/`full_name` carry `Blocking` too; `first_name` does **not** (it isn't
marked `Blocking`, and `FirstName` isn't a `token-name` type), so it produces no
key. Our six records therefore block like this:

| id | blocking keys |
|----|---------------|
| r1 | `email:janeacmecom`, `name:smith`, `phone:5550100` |
| r2 | `email:janeacmecom`, `name:smith` |
| r3 | `email:janeacmecom`, `name:smith`, `phone:5550100` |
| r4 | `email:johnacmecom`, `name:smith`, `phone:5550200` |
| r5 | `email:bobexamplecom`, `name:jones`, `phone:5550300` |
| r6 | `name:jones` |

Reading the blocks the other way — which records share a key:

- `name:smith` → **{r1, r2, r3, r4}** — a *coarse* key. All four Smiths will be
  compared to each other, including John (r4), who is a different person. This is
  blocking doing its job (grouping candidates), not matching (deciding they're the
  same); the decision comes later.
- `email:janeacmecom` → **{r1, r2, r3}** — a *selective* identifier key that
  isolates the real Jane Smith duplicates.
- `phone:5550100` → **{r1, r3}**.
- `name:jones` → **{r5, r6}**.

Notice r4 (John Smith) shares only the coarse `name:smith` key with the Janes, and
shares no email or phone key with them — that is exactly why, later, it will be a
*no-match* rather than *auto-merged*: the shared block is too coarse to clear the
review-floor gate on its own.

**Where it's configured.** Per field: the `Blocking` role and the `semanticType`.
Per profile: the `blockingStrategies` list and, optionally, `maxBlockSize`. See
[The profile model](#the-profile-model).

**Tuning note.** A key shared by a large crowd (a "hot" or coarse key like a common
surname) is the usual root cause of both wasted work and missed or noisy matches;
the fix is usually a more selective blocking strategy or a `maxBlockSize` cap, not
a bigger candidate cap. This is developed in [Tuning and
troubleshooting](#tuning-and-troubleshooting).

## Candidate retrieval and the candidate limit

**Intent.** Blocking tells you *which* records might be worth comparing; candidate
retrieval actually *fetches* them for an incoming record, ranked best-first, and
caps how many come back. The full pipeline for one incoming record is:
**blocking → candidate retrieval (top-N) → score each candidate → decide.**

**Inputs → outputs.** An incoming record's blocking keys in; up to `MaxCandidates`
ranked candidate records out — the ones the engine will actually score.

**The `MaxCandidates` cap.** `MaxCandidates` (default **50**) is a top-N limit on
retrieval: for each incoming record the index returns at most the N best-ranked
records that share a key, and only those get scored. It is a **cap, not a target** —
if only three records share a key, you get three. Per-record cost is roughly
`O(MaxCandidates × cost-to-score-one-candidate)`, so the cap is what keeps per-batch
time flat as the project grows, and it is what protects you from a *hot* blocking
key: if 40,000 records shared `name:smith`, without a cap every new Smith would be
compared against all 40,000 — blocking would have quietly turned back into
all-to-all. The cap bounds that blow-up.

**Ranking — which candidates survive the cap.** Retrieval ranks candidates by
relevance before applying the cap, and the signals are boosted unequally: an exact
**blocking-key** match carries the highest boost (weight **4**), a **phonetic**
match less (**2**), and a **fuzzy** name match least (**1**). So a record that shares
a strong exact key with the incoming record tends to be near the top of the list and
survive even a low cap; the record most at risk of being cut is one that shares only
a weak, common signal with a large crowd.

**Recall vs. work — the trade-off, and why it's asymmetric.** The cap trades recall
for work, and the two directions are not symmetric:

- Set it **too low** and, on a *hot* key, a genuine duplicate can be dropped from
  the candidate list before it is ever scored. A record that never becomes a
  candidate cannot match, so it silently forms its own cluster — a missed merge
  (a **correctness** problem, and a silent one).
- Set it **too high** and you only pay to score candidates that were never going to
  match — a **cost** problem, never a correctness one.

Two things soften the low-cap risk in practice: the relevance ranking above keeps
records sharing a strong exact identifier near the top, and a record usually
produces several blocking keys, so a duplicate only has to survive the cap on *one*
shared key. The danger zone is narrow: a true duplicate that shares only a weak,
common key (a frequent surname) with a big crowd. Because the failure modes are
asymmetric, the sane default (50) sits comfortably above any realistic number of
genuine duplicates for a single entity.

**Worked-example step.** Our corpus is tiny, so the cap never binds: when r3 is
ingested, its keys `email:janeacmecom`, `name:smith`, and `phone:5550100` retrieve
r1, r2, and r4 (everyone who shares any of those keys), all far under 50, and every
one of them is scored. To see the cap matter, imagine `name:smith` had 40,000
members: retrieval would return only the 50 best-ranked — and because r1 and r2
share r3's *exact email key* (boost 4), they'd rank above the sea of unrelated
Smiths that share only `name:smith`, and would still be scored.

**Where it's configured / tuning note.** `--max-candidates` on the CLI, or
`Linkuity:Postgres:MaxCandidates` in configuration (default 50). Raise it for
legitimately high-multiplicity entities or very common blocking values; lower it
only to bound a blocking key you've *proven* is hot — or, for a single known-hot
key, reach for `maxBlockSize` instead (it suppresses that key specifically rather
than lowering the cap for every query; see [Blocking](#blocking)). Crucially, a
crowded candidate set has more than one possible fix — raise the cap, make the
blocking keys more selective, or suppress the one offending key — and the
blocking-key design is often the better one. This point is developed in [Tuning and
troubleshooting](#tuning-and-troubleshooting).

## Similarity and weighted scoring

**Intent.** For each candidate pair, decide *how alike* they are as a single number
in `[0, 1]`. Linkuity does this field by field — each field gets its own similarity
measure and its own importance (weight) — then combines the field scores into one
pair score, with a special rule for strong identifiers.

**Inputs → outputs.** An incoming record and one candidate in; a pair score plus a
per-field breakdown out (the breakdown is what powers explainability later).

**Per-field similarity evaluators.** Each field names a `similarityEvaluator` suited
to its kind of value. The built-ins are:

| evaluator | for | how it compares |
|-----------|-----|-----------------|
| `exact` | identifiers, codes, postal codes | 1.0 if equal after match-key normalization, else 0.0 |
| `fuzzy` | names, free text | the strongest of edit-distance, substring, and token-set ratios |
| `jaccard` | address-like multi-token text | overlap of the two token sets |
| `canonical-jaccard` | organization names | overlap of the two token sets after canonicalization (articles dropped, legal suffixes stripped, ampersand initials collapsed), falling back to plain token overlap for fields whose semantic type has no canonicalizer; names that are canonically identical but differ only in token boundaries (e.g. `AMAZON.COM` vs `AMAZON COM`, both compressing to `AMAZONCOM`) score 1.0 via a compressed-form equality check — the same multiple-representation practice used by commercial entity-resolution engines |
| `ngram` | short strings/typos | shared character n-grams |
| `numeric` | quantities | closeness on a numeric scale |
| `date` | dates | equal after parsing to a common form |

`jaccard` and `ngram` are two distinct evaluators, not one. A field is only scored
when **both** records have a value for it; a field missing on either side simply
doesn't contribute.

`canonical-jaccard` closes a gap the blocking canonicalizer opened: since the
blocking stage already treats `THE BOEING COMPANY` and `BOEING CO` as the same
fingerprint, scoring them at a raw-token Jaccard of 0.25 meant blocking could
find pairs that scoring then threw away. With `canonical-jaccard` the same
canonicalizer (same registration map, same rules) feeds both stages, so the
built-in `organization` profile now scores those two names at 1.0 — and,
symmetrically, two *different* companies no longer collect similarity credit
for sharing noise tokens like `THE` or `COMPANY`. The evaluator also
short-circuits on names that are **canonically identical but differ only in
token boundaries**: `AMAZON.COM, INC.` and `AMAZON COM INC` both compress to
the same token sequence, `AMAZONCOM`, once punctuation and delimiters are
squashed out, so they score 1.0 outright on a compressed-form equality check
rather than being penalized for how the tokenizer happened to split them — the
same multiple-representation practice commercial entity-resolution engines use
for company names with inconsistent punctuation.

One behavior of `fuzzy` is worth calling out because it drives a worked-example
outcome: it takes the **maximum** of an edit-distance ratio, a *partial* (substring)
ratio, and a token-set ratio. The partial ratio means a short value that is a
substring of the other scores **1.0** — so `Rob` matches `Robert`, and `J` matches
`John`, at full strength. That is powerful for abbreviations and nicknames, and it
is also a thing to watch when you tune for over-merging.

**Combining fields into a pair score.** The person and organization profiles use the
`field-weighted` similarity strategy with the `identifier-weighted` scorer. The
scorer:

1. computes a **weighted average** of the per-field scores, using each field's
   `weight` — so in the `person` profile, `email` and `phone` (weight 3) and
   `last_name` (weight 2) count more than `first_name` or `postal_code` (weight 1);
2. applies a **floor**: if any field with the `Identifier` role matched exactly
   (score 1.0) **and the weighted average clears the profile's identifier
   corroboration gate** (`identifierFloorGate`, default **0.35**), the pair score is
   floored to **0.98**; otherwise, **if the weighted average clears the profile's
   review-floor gate** (`reviewFloorGate`, default **0.75**), it is floored to
   **0.80** — below both gates no floor applies and the raw weighted average stands;
3. returns `max(floor, weighted-average)`, or `0` if the pair had no comparable
   fields at all.

The `0.80` floor is deliberate: because retrieval only ever hands the scorer
candidates that already **share a blocking key**, a shared block *plus* enough field
agreement to clear the **review-floor gate** (default `0.75`) is treated as enough
evidence to reach the **review** band. Stronger field agreement pushes the score up
from there; a **corroborated** exact identifier match jumps it to `0.98` (auto). But a
candidate that shares only a coarse block and otherwise *disagrees* stays below the gate
and keeps its low raw score — a **no-match**, and that stays true even when the coarse
block was an `Identifier` field, because the identifier floor has its own gate
(`identifierFloorGate`, default `0.35`). The practical consequence — **a shared block
reaches review only when field agreement clears the gate** — is the single most
important thing to internalize before reading decision bands and tuning.

**Worked-example step.**

- **r1–r2** (both Jane Smith, same `email`): `email` is an `Identifier` and matches
  exactly → floor `0.98`; every comparable field also matches, so the weighted
  average is `1.0` → score **1.00**.
- **r1–r4** (Jane vs John Smith): they share only `name:smith`. `first_name` scores
  `0.5` (Jane/John), `last_name` `1.0`, but `email` and `phone` are *present and
  different* → `0.0` at weight 3 each, dragging the weighted average to `0.25`. No
  identifier matched, and `0.25` is **below the review-floor gate (0.75)**, so no
  floor applies → score **0.25** (a no-match).
- **r5–r6** (Robert vs Rob Jones): `first_name` scores `1.0` (`Rob` is a substring
  of `Robert`), `last_name` `1.0`, `postal_code` `1.0`; `email`/`phone` are absent
  on r6 so they don't count. Weighted average `1.0`, no identifier needed → score
  **1.00**.

Note the contrast between r1–r4 and r5–r6: both share just one name-ish block, but
r4 carries *contradicting* identifiers (a different email and phone that are
present), holding it below the gate at its raw `0.25`, while r6 carries only
*corroborating* data (same postal, name substring) and no contradictions, lifting it
to auto.

**Where it's configured.** Per field: `similarityEvaluator` and `weight`. Per
profile: `similarityStrategy` (`field-weighted`) and `scoringStrategy`
(`identifier-weighted`), and which fields carry the `Identifier` role. See
[The profile model](#the-profile-model).

## Decision bands

**Intent.** Turn the pair score into one of three actionable outcomes.

**The three bands.** Two thresholds cut the `[0, 1]` score line into bands:

- **auto-match** — score ≥ `autoMatchThreshold` (default **0.90**): the pair is
  accepted automatically and the records will be clustered together.
- **review** — `reviewThreshold` ≤ score < `autoMatchThreshold` (default band
  **0.75–0.90**): the pair is plausible but not certain; it becomes a review task
  for a human and does **not** merge on its own.
- **no-match** — score < `reviewThreshold` (default **0.75**): the pair is rejected.

Recall from scoring that a candidate sharing a block floors at `0.80` **only when its
field agreement clears the review-floor gate** (default `0.75`), and an exact
identifier match floors at `0.98` **only when its field agreement clears the identifier
corroboration gate** (default `0.35`). So in practice, with the built-in profiles: a
corroborated identifier match lands in **auto**; a shared-block candidate with enough field
agreement lands in **review** (or **auto** if agreement is strong); and a
**no-match** comes both from records that never shared a block (never compared) *and*
from shared-block candidates whose field agreement falls below the gate — compared,
but too weak to reach review.

**Worked-example step.** r1–r2, r1–r3, r2–r3 (shared email identifier → `1.00`) and
r5–r6 (strong agreement → `1.00`) are **auto-matches**. r1–r4, r2–r4, r3–r4 (John
Smith sharing only the coarse `name:smith`, at a raw `0.25` below the gate) are
**no-matches** — compared, but too weak to reach review. Every cross-surname pair
(say r1 and r5) shares no block, is never retrieved, and is an implicit **no-match**
too.

**Where it's configured.** `autoMatchThreshold` and `reviewThreshold` on the profile
(`decisionStrategy` is `threshold`). Lowering thresholds merges more aggressively
(risking over-merge); raising them is more conservative (risking under-merge). See
[Tuning and troubleshooting](#tuning-and-troubleshooting).

## Clustering

**Intent.** A match decision is about a *pair*, but an entity can span many records
across many sources. Clustering turns the accepted pairs into groups: if r1 matches
r2 and r2 matches r3, then r1, r2, and r3 are one entity even if r1 and r3 were
never directly compared. This transitive grouping uses **Union-Find (connected
components)**.

**Inputs → outputs.** The accepted (auto-match) pairs in; a set of clusters
(groups of record ids) out.

**What clusters and what doesn't.** Only **auto-match** pairs are unioned into
clusters. **Review-band pairs are deliberately excluded** — a pair in the review
band creates a review task but does *not* pull its records together. This is why a
single uncertain pair can't silently merge two entities.

**Order-independence.** The result does not depend on the order records or pairs are
processed. Union-Find components are insensitive to union order by construction, the
pair set is symmetric (each unordered pair stored once, keeping its best score), and
where a merge must pick a winner it uses a fixed total order (see bridge-merge
below). Re-running the same batch in any order yields the same clusters.

**Worked-example step.** The auto-matched pairs are r1–r2, r1–r3, r2–r3, and r5–r6.
Union-Find turns these into components **{r1, r2, r3}** and **{r5, r6}**. r4 (John
Smith) has only *no-match* edges to the Janes (raw score 0.25, below the review-floor
gate), so nothing links it — r4 stays on its own as **{r4}**. Three entities, exactly
right, and John is separated on the engine's own judgment rather than a bad merge.

**Where it's configured.** `clusteringStrategy` (the built-ins use `union-find`).
For the durable ingest flow that drives this, see the "Incremental Ingest" section
of [`docs/architecture.md`](architecture.md).

## Within-batch resolution and bridge-merge

**Intent.** Real ingestion is incremental: a new batch arrives and must be resolved
both **against itself** (records in the same batch can be duplicates of each other)
and **against everything already stored** — in one pass, without depending on the
order records happen to arrive. This stage is where a new record either starts a new
entity, joins an existing one, or *bridges* two that were previously separate.

**Inputs → outputs.** A batch of new records plus the existing clusters they touch
in; updated clusters (new, extended, or merged) plus lineage records out.

**The three cases.** Each connected component from clustering is compared against the
existing clusters its records touch:

- **Touches no existing cluster** → a **new cluster** is created (a singleton if the
  component is one record, or several net-new records that matched each other).
- **Touches exactly one existing cluster** → the new records **join** that cluster.
- **Touches two or more existing clusters** → a **bridge-merge**: a new record's
  auto-match edges have linked clusters that used to be separate, so they collapse
  into one. A deterministic **survivor** is chosen — the oldest cluster, ties broken
  by smallest id — and it absorbs all the others' members. Each absorbed cluster is
  tombstoned (`status = merged`, pointing at the survivor), its current golden record
  removed while its version history is retained, and a **`ClusterMergeEvent`** is
  written recording the survivor, the absorbed members, the record that triggered the
  bridge, and the top edge's score and breakdown. The survivor choice uses the same
  fixed order regardless of arrival order, so bridge outcomes are reproducible.

**Weak bridges.** If the pair linking two existing clusters is only in the *review*
band (not auto), no merge happens; instead a `cluster_merge_suggestion` review task
is created, annotated with both cluster ids — a suggestion for a human, never an
automatic merge.

**Worked-example step.** Our six records arrive in a single batch and none of them
exists yet, so all three components are the "touches no existing cluster" case:
**{r1, r2, r3}**, **{r5, r6}**, and **{r4}** each become a brand-new cluster.

To see a real bridge, extend the story: suppose r1 and r2 had been ingested in an
*earlier* batch and — because at that moment they didn't yet share a strong enough
signal — ended up in two separate clusters. Later, r3 arrives sharing the exact
email `jane@acme.com` with both. r3's auto-match edges to r1 and to r2 put all three
in one Union-Find component that now touches *two* existing clusters, triggering a
bridge-merge: the older of the two clusters survives and absorbs the other, a
`ClusterMergeEvent` records the lineage, and the persisted corpus means the two Janes
are reconciled the moment a linking record shows up — even though they were never
directly compared to each other. This is why a missed merge is often self-healing:
a later related record bridges it.

**Where it's configured.** This behavior is intrinsic to durable incremental ingest;
the merge policy that then builds the surviving golden record is configured
separately (next). See "Incremental Ingest" in
[`docs/architecture.md`](architecture.md) for the full flow diagram.

## Golden records and merge policy

**Intent.** A cluster is a set of source records; downstream systems want one clean
record per entity. The **golden record** is that single representative, built by a
**merge policy** that decides, field by field, which value wins.

**Inputs → outputs.** A cluster's member records in; one merged record — a value per
field — out.

**How a field's value is chosen.** Two selection modes:

- **Consensus** (the default): the cluster agrees on a representative value for the
  field.
- **Source-priority**: for a configured field, take the first non-empty value
  following a configured order of sources — e.g. "trust CRM's phone over
  Marketing's."

The durable project stores its policy (`Project.MergeConfiguration`), and the *same*
policy is applied to full batch imports and to later incremental ingests, so a
field's chosen value doesn't drift as new data arrives.

**Worked-example step.** The cluster {r1, r2, r3} collapses to one golden **Jane
Smith**: `last_name` Smith and `email` jane@acme.com (all agree), `phone` 555-0100
(from r1 and r3; r2 was blank), `postal_code` 94105. Under consensus these are the
agreed values; under a source-priority policy you could, say, force the `phone` to
come from CRM (r1) regardless.

**Where it's configured.** The project's merge configuration. Exact modes,
validation, and CLI usage are in "Durable Merge Policy" and "Post-Processing" in
[`docs/architecture.md`](architecture.md).

## Versioning

**Intent.** Golden records are not static — as new source records merge into a
cluster, the golden record can change. Linkuity keeps a **version history** so you
can see how an entity's canonical record evolved and when.

**Key rule.** A new golden-record **version is written only when the merged fields
actually change.** An ingest that re-confirms existing values without adding or
altering any field does not create a new version — history records real changes, not
churn.

**Worked-example step.** If r1 and r2 (no phone) had formed the Jane cluster first,
its golden record would have no phone. When r3 (phone 555-0100) later merges in and
fills that field, a **new version** is written capturing the added phone. A
subsequent record that merely repeats jane@acme.com adds nothing and creates no new
version.

**Where it's configured.** Automatic; no configuration. Inspect history with the
`golden history` command — walked step by step in the
[quickstart tutorial](tutorials/cli-durable-mdm-quickstart.md).

## Review tasks

**Intent.** Not every pair is a confident yes or no. Pairs that land in the **review
band** are surfaced to a human as **review tasks** instead of being merged or
discarded — the engine's way of saying "plausible, but you decide."

**Inputs → outputs.** Review-band pairs (and cluster-spanning review suggestions) in;
review-queue entries out. Review tasks never move records on their own.

**Two kinds.** A plain review task flags an uncertain *pair*. When a review-band pair
spans two *existing clusters*, it becomes a **`cluster_merge_suggestion`** task
annotated with both cluster ids — a recommendation to merge two entities, still
pending a human.

**Worked-example step.** Our six records produce **no review tasks**. John Smith (r4)
shares only the coarse `name:smith` block with each Jane, but with a different email
*and* phone his field agreement (0.25) sits below the review-floor gate, so he is a
clean no-match rather than a review — the gate is what keeps a coarse block from
generating review noise. A review task *would* appear if John shared, say, a phone
with a Jane: agreement would then clear the gate without reaching auto. (The
[tuning section](#tuning-and-troubleshooting) covers the gate and review noise.)

**Where it's configured.** Automatic from the decision bands (`reviewThreshold`).
Inspect the queue with `review list` — shown in the
[quickstart tutorial](tutorials/cli-durable-mdm-quickstart.md).

## Explainability

**Intent.** Every score is auditable. When you need to know *why* two records did or
didn't merge, explainability shows the per-field breakdown behind the pair score —
which fields agreed, which didn't, and how each was weighted.

**Inputs → outputs.** A pair (or a record's edges) in; the stored score breakdown and
decision out.

**Worked-example step.** Running `match explain` on the r1↔r4 pair shows the story
behind the 0.25: they share the `name:smith` block; `last_name` scored 1.0 and
`first_name` 0.5 (Jane vs John), but `email` and `phone` were **present and
different**, each scoring 0.0 at weight 3 — pulling the weighted average down to
0.25. That sits below the review-floor gate (0.75), so no floor applies and 0.25
stands: a no-match. No `Identifier` field matched either. The breakdown makes "shared
surname, contradicting identifiers" legible at a glance.

**Where it's configured.** Automatic; breakdowns are persisted during ingest. Command
usage, filters, and output columns are documented under "`match explain`" in
[`docs/architecture.md`](architecture.md).

## The profile model

Everything above is steered from one place: the **matching profile**. A profile is
the single configuration surface that ties the stages together — change matching
behavior by editing a profile, not the engine. There are two built-in profiles,
`person` and `organization`; you can override them or add your own by loading a JSON
profile.

**What a profile declares:**

- **`contentType`** — the kind of entity this profile matches (e.g. `person`).
- **`fields[]`** — one entry per field, each with:
  - **`name`** — the column name in your data.
  - **`semanticType`** — what kind of value it is (`Email`, `LastName`,
    `DateOfBirth`, …); this drives normalization and which strategies apply.
  - **`roles`** — any of `Searchable`, `Matchable`, `Blocking`, `Identifier`. The two
    that change matching most: **`Blocking`** makes the field eligible to produce
    blocking keys, and **`Identifier`** marks it a *strong identifier* — an exact
    match on it auto-matches the pair (via the 0.98 floor, provided the pair also
    clears `identifierFloorGate`) and it also produces an
    exact blocking key. This is how `Email`/`Phone` act as identifiers for people,
    and how a `Sku`/`Gtin` does the same in other domains **with no engine change**.
  - **`similarityEvaluator`** and **`weight`** — how the field is compared and how
    much it counts (see [Similarity and weighted scoring](#similarity-and-weighted-scoring)).
- **`blockingStrategies`** — which blocking strategies run. Built-ins include
  `exact-value`, `token-name`, `fingerprint`, `token`, `acronym`, `ngram`,
  `phonetic`, `prefix`, and `dob-lastname-phonetic` — see [Blocking](#blocking)
  for what each does and which the built-in profiles use.
- **`maxBlockSize`** (optional) — caps how many records a single blocking key may
  match before it's suppressed entirely; see [Blocking](#blocking).
- **Strategy selections** — `normalizationStrategy`, `candidateRetrievalStrategy`,
  `similarityStrategy`, `scoringStrategy`, `decisionStrategy`, `clusteringStrategy`.
- **`autoMatchThreshold`** and **`reviewThreshold`** — the decision bands.
- **`reviewFloorGate`** (optional, default `0.75`) — the minimum weighted per-field similarity a
  non-identifier candidate must reach for the review floor to apply. Below it, a pair keeps its raw
  weighted score (usually a no-match), which stops a shared low-cardinality blocking key (e.g. a
  common surname) from flooding the review queue. Lower it below `reviewThreshold` to promote
  strongly-evidenced sub-threshold pairs into review; leave it at the default for maximum review
  precision.
- **`identifierFloorGate`** (optional, default `0.35`) — the minimum weighted per-field similarity a
  pair must reach on its own before an exact `Identifier` match is allowed to floor it to `0.98`.
  An identifier match promotes a plausible pair to auto; it does not rescue an implausible one.
  This matters because profiles routinely give the `Identifier` role to fields that are *not*
  unique to one entity — `date_of_birth`, a switchboard `phone`, a `domain_name` shared by sibling
  companies — and without the gate any such coincidence auto-merged two records that agreed on
  nothing else. Below the gate the identifier floor does not apply and the pair falls through to
  the review-floor logic. Raise it to demand more corroboration; set it to `0` for the old
  unconditional floor.

**The worked profile (`person`).** The example in this guide runs on the built-in
`person` profile. Its most relevant fields:

| field | semanticType | roles | evaluator | weight |
|-------|--------------|-------|-----------|-------:|
| first_name | FirstName | Searchable, Matchable | fuzzy | 1.0 |
| last_name | LastName | Searchable, Matchable, **Blocking** | fuzzy | 2.0 |
| email | Email | Searchable, Matchable, **Blocking**, **Identifier** | exact | 3.0 |
| phone | Phone | Matchable, **Blocking**, **Identifier** | exact | 3.0 |
| postal_code | PostalCode | Matchable | exact | 1.0 |

(The full profile also carries `full_name`, `name`, `date_of_birth`, `domain_name`,
`organization_name`, and `address_line`.) Its strategy selections:
`normalizationStrategy = identity`, `blockingStrategies = [exact-value, token-name]`,
`similarityStrategy = field-weighted`, `scoringStrategy = identifier-weighted`,
`decisionStrategy = threshold`, `clusteringStrategy = union-find`,
`autoMatchThreshold = 0.90`, `reviewThreshold = 0.75`. Its
`candidateRetrievalStrategy` is nominally `linear`, but that default is never used in
real matching: any actual run overrides retrieval with a **blocking-gated** strategy
— `blocking-linear` for the in-memory engine, and the Lucene index adapter (with the
`MaxCandidates` cap from
[Candidate retrieval](#candidate-retrieval-and-the-candidate-limit)) at durable
scale. This matters because the scorer's `0.80` review floor assumes every scored
candidate already shares a blocking key; the ungated `linear` strategy would violate
that assumption, which is why it isn't used.

This is the same profile format both entry points consume: `linkuity run`
(`--profile`) and durable ingest (`--content-type`/`--profiles`) resolve identically
— there is no separate "batch config" shape. The profile taught in this section is
literally the `*.profile.json` file (or built-in name) you'd pass to `linkuity run
--profile ...`.

**Where it's configured.** The exhaustive schema (every field and strategy name,
required vs. optional, and every value's meaning) is the field-by-field reference in
[`docs/configuration.md`](configuration.md); the built-in/override and validation
semantics are documented under "Authoring a matching profile" in
[`docs/architecture.md`](architecture.md#authoring-a-matching-profile-no-code-changes).
This guide covers the intuition and the worked profile, not the full schema.

## Tuning and troubleshooting

Almost every matching problem is one of two failures, and they pull in opposite
directions:

- **Under-merging** (false negatives): true duplicates are left as separate
  entities.
- **Over-merging** (false positives): distinct entities are collapsed together.

Tune toward the middle; over-correcting one just creates the other.

### Diagnosing under-merging

Symptoms: you can see duplicates in the output that stayed separate; the same person
appears as several golden records.

Most under-merges are a **blocking** problem, not a scoring one: if two true
duplicates never share a blocking key, they are never even compared, so no threshold
will save them. Common causes and fixes:

- **True duplicates don't share any key** (e.g. `Catherine` vs `Katherine` — the
  `token-name` key `name:catherine` ≠ `name:katherine`). Fix by adding a *looser*
  blocking strategy so the variants collide: **`phonetic`** (groups names that sound
  alike) or **`ngram`** (tolerant of typos and transpositions).
- **A hot key is cutting real duplicates before scoring** — only relevant when a
  single key is shared by more than `MaxCandidates` records. Fix by raising
  `--max-candidates`, *or* by making blocking more selective so the crowd shrinks
  (see [The levers](#the-levers) below). If the same key is *also* past
  `maxBlockSize`, it's suppressed rather than truncated — check `match blocking
  audit` before assuming a raised cap will help.
- **Thresholds too high.** Lower `autoMatchThreshold` cautiously — but prefer adding
  a reliable `Identifier` field, which auto-matches via the 0.98 floor without
  loosening everything. If a pair that agrees on an identifier still fails to
  auto-match, check `identifierFloorGate` — the rest of the record may not be
  corroborating it.

### Diagnosing over-merging

Symptoms: distinct entities share a golden record; a cluster contains records that
clearly aren't the same entity.

Common causes and fixes:

- **A non-unique field is marked `Identifier`.** An `Identifier` exact match floors
  a pair toward auto-match once the pair's weighted score clears `identifierFloorGate`
  (default `0.35`), so a shared value (a company-wide email domain, a household
  phone) marked `Identifier` can still fuse unrelated records that agree on little
  else. Fix: raise `identifierFloorGate` first — it demands more independent
  corroboration before the floor applies, at far less recall cost than removing the
  role (measured at −13 points of FEBRL recall). Only remove the `Identifier` role
  when the field isn't really an identifier at all (e.g. a display name), not when
  it's a real identifier that just needs corroboration.
- **Fuzzy generosity on short values.** Recall that `fuzzy` scores a substring at
  1.0 (`Rob` = `Robert`, `J` = `John`). Two genuinely different people with a
  substring-related name and otherwise agreeing fields can auto-merge. Fix: raise
  `autoMatchThreshold`, add a distinguishing field with real weight, or require an
  identifier.
- **Thresholds too low.** Raise `autoMatchThreshold` / `reviewThreshold`.

### Measuring both failures at corpus scale (`match corpus audit`)

`match blocking audit` and `match scoring audit` materialize every candidate pair
with a full breakdown, which is what you want on a showcase-sized corpus and what you
cannot afford on a million records. `match corpus audit` answers the same question
aggregate-only, so it runs over a whole labelled corpus without holding a pair set:
given records and a ground truth that assigns each record a canonical key, it reports
four metrics —

- **reachability** — true pairs that share at least one *unsuppressed* blocking key,
  i.e. the recall ceiling blocking leaves you;
- **direct auto recall** — true pairs scored at or above `autoMatchThreshold` on
  their own, before clustering;
- **post-cluster pairwise recall** — true pairs that ended up in the same cluster,
  including those joined transitively through a third record;
- **cluster pairwise precision** — the over-merging side: how many pairs sharing a
  predicted cluster are genuinely the same entity.

Pairs are also broken down by how similar the two names are after canonicalization
(identical, containment, strong/weak overlap, disjoint), so "we lost recall" can be
attributed to a cohort rather than guessed at.

```powershell
dotnet run --project src/Linkuity.Cli -- match corpus audit `
  --input corpus.csv `
  --ground-truth ground-truth.csv `
  --profile company.profile.json
```

`--input` is the corpus CSV, `--ground-truth` is a CSV of `record_id,canonical_key`,
and `--profile` takes a built-in name or a `*.profile.json` path (the same record
sources as the sibling verbs are available — `--metadata` / `--metadata-store`).

Run this way it just reports. It can also **gate**: `--write-baseline <dir>` freezes
the current numbers as an artifact, and `--compare-baseline <dir>` re-runs and refuses
to let the frozen numbers get worse. Both baseline modes additionally require
`--corpus-source <path>` — the snapshot or manifest the corpus was extracted from —
which is pinned by SHA-256 alongside the records, ground truth and profile, so a
comparison across two different corpora is rejected rather than reported as a
regression. Use it to keep a matching change honest: write a baseline before, compare
after. Because the gate pins every evaluation input by SHA-256, gate mode further
requires `--input` specifically (a store-backed source has nothing single to hash)
and requires `--profile` to be a file path rather than a built-in name (a name has
no bytes to hash either).

Exit codes are a contract: **0** report-only run, successful baseline write, or a gate
that passed; **1** gate failure — the two runs are comparable and something got worse;
**2** usage error, or gate *refusal* because the runs are not comparable at all. A
refusal is never reported as a failure; the two mean entirely different things.

### Hot and coarse blocking keys (and review-queue noise)

A key shared by a large crowd — a common surname's `name:smith`, a catch-all domain
— is the usual root of trouble. Beyond the recall risk above, a coarse key can
generate **review-queue noise**: candidates that share the block and agree *just
enough* to clear the review-floor gate, without being real duplicates, land in
review. The **review-floor gate** (default `0.75`) is the first line of defense —
it is exactly why John Smith (r4) produced *no* review tasks in our example: sharing
`name:smith` but contradicting on email and phone, his agreement (0.25) never cleared
the gate, so he was a clean no-match rather than three review chores.

For the pairs that *do* clear the gate on a coarse key, the cure is rarely a
threshold change; it is **more selective blocking, or capping the key**. Options:

- an **`Identifier`** field (email, phone, a code) so real duplicates share a
  precise key instead of only the surname, or
- the composite **`dob-lastname-phonetic`** strategy, which keys on date-of-birth
  *and* a phonetic surname together — two attributes must align, so the block is far
  smaller than surname alone, or
- **`maxBlockSize`**, which suppresses one specific key past a frequency threshold
  instead of redesigning blocking around it — the right tool when you've found one
  known-hot key (a common surname, a legal-suffix ngram) rather than a systemic
  coarseness problem. Use `match blocking audit` to find which keys are the biggest
  blocks before reaching for either fix.

Raising the gate itself (toward `reviewThreshold`) tightens review precision further,
at the cost of dropping weakly-evidenced true matches out of review; see the
`reviewFloorGate` note under [The profile model](#the-profile-model).

### The levers

When a candidate set is crowded, there are **three** ways to fix it, not one:

1. **Raise `MaxCandidates`** — consider more of the crowd (more recall, more work).
2. **Make the blocking keys more selective** — shrink the crowd itself (add an
   identifier, a composite strategy like `dob-lastname-phonetic`, or a `prefix`/
   `ngram` strategy so a big block splits into several small ones).
3. **Set `maxBlockSize`** — suppress one specific oversized key rather than redesign
   around it; the record falls back on its other keys instead of contributing this
   one to any candidate set. Cheaper than (2) when the problem is one hot key, not a
   systemically coarse strategy; unlike (2), it can *cost* recall on the pairs whose
   only shared key was the suppressed one — `match blocking audit`'s effective-vs-raw
   ceiling reports exactly that cost.

The candidate cap is the lever people reach for first, but the **blocking-key design
is usually the better fix**: a smaller, more precise block improves both recall
*and* cost, while raising the cap only trades cost for recall. Reach for the cap when
an entity legitimately has high multiplicity; reach for blocking (or a
`maxBlockSize` cap on the one offending key) when a key is merely coarse.

### Throughput and memory (`--batch-size`)

`ingest-incremental --batch-size <n>` splits a large input into `n`-record chunks,
each ingested as its own batch, which bounds the working set of each ingest call.
It's an operational knob, not a matching one — it doesn't change *which* records
match, only how much work is in flight at once. For the scaling properties, the
Postgres-vs-File choice, and measured numbers, see "Scale and performance" in
[`docs/architecture.md`](architecture.md).

## End-to-end worked example

Putting it all together, here is the full journey of the six records — every number
below was produced by running the engine.

**1. Records** (three real entities: Jane Smith = r1/r2/r3, John Smith = r4, Robert
"Rob" Jones = r5/r6):

| id | first_name | last_name | email | phone | postal_code |
|----|-----------|-----------|-------|-------|-------------|
| r1 | Jane | Smith | jane@acme.com | 555-0100 | 94105 |
| r2 | Jane | Smith | jane@acme.com | — | 94105 |
| r3 | J | Smith | jane@acme.com | 555-0100 | 94105 |
| r4 | John | Smith | john@acme.com | 555-0200 | 94110 |
| r5 | Robert | Jones | bob@example.com | 555-0300 | 94107 |
| r6 | Rob | Jones | — | — | 94107 |

**2. Blocking → keys** (punctuation stripped): r1/r2/r3 all carry
`email:janeacmecom` and `name:smith`; r4 carries `name:smith` (but
`email:johnacmecom`); r5/r6 carry `name:jones`.

**3. Candidate pairs** (only records sharing a key are compared): within the
`name:smith` block {r1, r2, r3, r4}, and within `name:jones` {r5, r6}. r-to-Jones vs
r-to-Smith pairs share nothing → never compared.

**4. Scores → 5. Decisions:**

| pair | score | decision | why |
|------|------:|----------|-----|
| r1–r2, r1–r3, r2–r3 | 1.00 | auto-match | shared exact `email` identifier |
| r5–r6 | 1.00 | auto-match | strong agreement (surname, postal, `Rob`≈`Robert`) |
| r1–r4, r2–r4, r3–r4 | 0.25 | no-match | share only coarse `name:smith`; identifiers differ, below the review-floor gate |

**6. Clustering** (auto edges only): `{r1, r2, r3}`, `{r5, r6}`, `{r4}`.

**7. Golden records + review tasks:** one golden **Jane Smith**, one **Robert
Jones**, one **John Smith** — and **no review tasks**: John's shared `name:smith`
block never clears the review-floor gate, so he is a clean no-match rather than a
review. Three input sources, six rows, resolved into exactly three entities with no
human in the loop.
