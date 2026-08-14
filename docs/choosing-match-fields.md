# Choosing which fields to match on

`match corpus fields` measures how much each of your columns is actually worth for matching, on
your data, and tells you what your data can never resolve.

It exists to replace guessing. Without it, building a matching profile means picking some fields,
assigning weights, running it, looking at the results, adjusting, and repeating — a loop that can
absorb weeks and still leave you unsure whether the configuration is right or merely familiar.

Whether a column is good evidence is a property of **your data**, not of the domain. A postal
address may be decisive in one dataset and close to worthless in another. Nothing in Linkuity
decides that in advance; this report measures it.

---

## A story

Priya runs data engineering at an insurance company. They are merging customer records from three
systems — policy, claims, and a broker feed. About 2 million records. Nobody knows how many actual
customers that is.

She has 40,000 pairs that colleagues have already confirmed by hand as "yes, same person".

Her only option used to be guessing which columns matter. Instead she runs the report and gets:

| field | filled | same customer agrees | different people agree | verdict |
|---|---|---|---|---|
| email | 34% | 91% | 0.02% | very strong |
| phone | 78% | 62% | 0.4% | strong |
| postcode | 96% | 71% | 3% | moderate |
| city | 99% | 88% | 22% | weak |
| country | 100% | 99% | 87% | nearly useless |

The two middle columns are the whole story, and **neither means anything alone**.

Email: the same customer agrees 91% of the time, and unrelated people essentially never do. That
is decisive. Country: everyone agrees, so agreeing tells you nothing at all.

City is the trap. 88% looks reassuring — until you notice a fifth of unrelated people share a city
too. A high same-entity rate is not evidence of anything by itself.

**What Priya changes:**

- **Drops `country` from matching.** It was going to contribute to every score and mean nothing.
- **Stops treating `city` as real evidence.** The number that looked good was the wrong number.
- **Learns `email` is decisive but present on only a third of records.** So it cannot carry the
  system alone — and that reframes a business conversation. "If we raise email coverage from 34% to
  70%, matching improves dramatically" is a concrete argument to take to whoever owns the broker
  feed.
- **Sees the records that are genuinely impossible.** They are not a tuning problem, and no
  configuration will fix them. They are a data-collection problem — now a sized, known backlog item
  rather than something that surfaces months later as a customer complaint.

Total elapsed: one run.

**Six months later** a fourth system is connected. Priya re-runs the report before turning matching
on for it. `postcode` has fallen from *moderate* to *nearly useless* — the new system stores
postcodes with spaces and the others do not, so records that genuinely match no longer look like
they do.

She catches that before it merges anything, rather than three weeks later.

---

## Running it

```powershell
dotnet run --project src/Linkuity.Cli -- match corpus fields `
  --input records.csv `
  --ground-truth ground-truth.csv `
  --profile my.profile.json
```

Or with a published binary:

```powershell
Linkuity.Cli.exe match corpus fields --input records.csv --ground-truth ground-truth.csv --profile my.profile.json
```

| Flag | Meaning |
|------|---------|
| `--input` | Records CSV. Required. |
| `--ground-truth` | CSV with header `record_id,canonical_key`. Records sharing a `canonical_key` are the same real entity. Required. |
| `--profile` | Built-in profile name or path to a `*.profile.json`. Required. |
| `--max-block-size` | Overrides the profile's `maxBlockSize`. Optional. |

**You need ground truth.** The report works by comparing how often confirmed-same records agree
against how often confirmed-different records agree, so it cannot run without knowing which is
which. It does not need to cover everything — a hand-confirmed sample is enough, and the report
tells you how many pairs it actually observed so you can judge whether to trust it.

---

## Reading the output

```text
=== field usefulness ===
600 records, 600 labeled, 291 confirmed same-entity pairs observed

A field is useful when the two middle columns are FAR APART. A field both
the same entity and unrelated records agree on tells you almost nothing.

  field                     filled   same entity   different   evidence   verdict
                                         agrees     agree      per match
  ------------------------------------------------------------------------------
  name                       100 %     99.83 %      9.65 %    3.4 bits   strong
  email                       40 %     99.58 %      0.13 %    9.6 bits   very strong
  city                       100 %     99.83 %     27.97 %    1.8 bits   moderate
  country                    100 %     99.83 %     99.97 %   -0.0 bits   nearly useless

--- what this data cannot resolve ---
  12 record(s) in 3 group(s) are identical on every matchable field
  yet belong to different entities. Largest group: 4 record(s) spanning 2 entities.
  Unfilled throughout that group: email - collecting it is what would separate them.

  No threshold can fix these, and they are not permission to merge them:
  a pair nothing distinguishes belongs in review, or apart.
```

### filled

How many records carry a real value for this column.

A column that is only 40% populated can still be your best field — it just cannot be your only one.
A column that is 100% populated may be worthless; see `country` above.

Values you have declared as meaning "missing" count as **unfilled**. If your data uses `UNKNOWN` or
`N/A` as a placeholder, declare it in the profile's `nullEquivalents` for that field; otherwise the
report will tell you the column is fully populated when in practice it holds nothing the matcher
can use. See [configuration.md](configuration.md).

### same entity agrees / different agree

The two rates the whole report turns on. **Read them together, never separately.**

A field is useful when they are far apart. It is useless when they are close, however high they
both are.

### evidence per match

The gap between the two rates, expressed in bits. Higher means one agreement on this field tells
you more. It is what the matcher uses internally, and it is what the verdict column summarises.

Negative means agreeing on this field is very slightly evidence the records are *different* — which
in practice means the field is noise.

### verdict

A plain reading of the evidence column, for people who do not think in bits:

| verdict | meaning |
|---|---|
| very strong | An agreement here is close to conclusive on its own. |
| strong | Substantial evidence. |
| moderate | Real but not decisive; needs support from other fields. |
| weak | Barely narrows anything. |
| nearly useless | Agreeing tells you essentially nothing. Consider removing it. |
| not measured | Not enough observations to say. **Not** the same as "worthless" — see below. |

These bands are presentation only. Nothing in the matching engine scores off them, and changing
them cannot change a match.

### "not measured"

Reported when there were too few observations to estimate a rate. It is deliberately distinct from
a low score: *we could not tell* and *this is worthless* are different answers, and treating the
first as the second retires a column nobody actually checked.

The usual cause is that nothing ever puts two **different** entities side by side for comparison.
Chance agreement cannot be measured from pairs that are never compared. If several fields come back
unmeasured at once, look at your profile's blocking configuration — it is probably too narrow for
the report to see anything.

---

## What this data cannot resolve

The second half of the report counts records that are **identical on every matchable field** and yet
belong to different real entities.

The classic case in company data is fund share classes: several legally distinct entities with the
same registered name, at the same administrator's address, with nothing else on file. No matching
configuration separates them, because the data does not contain anything that distinguishes them.

Two things to understand about this number:

**It is not permission to merge them.** A pair that nothing distinguishes should not be merged. It
belongs in a review queue, or apart. This figure never relaxes any quality gate.

**It is a shopping list.** The report names the fields that were empty across the whole of the
largest such group — those are the fields that, if collected, would separate them. That turns "our
matching is imperfect" into "we need registration numbers on these 400 records", which is
actionable.

---

## What to do next

1. **Remove fields whose verdict is `nearly useless`.** They add noise to every score.
2. **Check that `not measured` fields are genuinely unmeasurable** rather than a symptom of narrow
   blocking.
3. **Look at coverage, not just strength.** A very strong field on 30% of records needs a
   moderate field alongside it for the other 70%.
4. **Declare your placeholder values** as `nullEquivalents` and re-run — a column reporting 100%
   filled that is really full of `UNKNOWN` will otherwise mislead everything above.
5. **Re-run when a new source arrives.** A field's usefulness is a property of the data, and new
   data changes it. This is cheaper than discovering the change through bad merges.

Then take the fields that survived into [configuration.md](configuration.md) to build the profile,
and [how-matching-works.md](how-matching-works.md) for how they are combined into a decision.
