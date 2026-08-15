# customer-360-hub

A person-MDM hub for "Customer 360" that starts from a prior standalone match run and
then evolves over three incremental ingests. It is the flagship durable scenario: it
exercises seeding from pre-baked artifacts, clean auto-joins with a stable cluster id,
canonical-value changes that bump the golden version, and the human-review queue.

![customer-360-hub: seed a hub, then evolve it over three incremental ingests — an auto-join by exact phone that resolves a same-tier disagreement into golden version 2, a priority promotion that cuts golden version 3, and a borderline record that opens a review task](assets/demo.gif)

> Generated from [`assets/demo.tape`](assets/demo.tape) with [VHS](https://github.com/charmbracelet/vhs).
> **Stale as of the F54 golden-record-merge fix:** the recording predates the fix that
> makes same-tier disagreements resolve deterministically (see step 2 above) — the
> terminal output it shows still displays the old "version 2 at the CRM step" numbering.
> Re-record with the command in [Run it](#run-it) below to refresh it.

## The story

The subject is one real person — Carol Chen — whose contact details were recorded
slightly differently across four source systems (CRM, Marketing, Support, Web). Because
the surname spelling drifts between systems (`Chen`, `Chenn`, `Cheng`, `Chenh`), the hub
links records by reliable contact keys (exact **phone** / **email**) rather than by name.

1. **Seed from a prior run.** `persist-batch` loads pre-baked artifacts for job
   `…b01`: two records (`mkt-011` from Marketing, `sup-012` from Support) already
   clustered into `…c01`. The project merge policy prefers the CRM email, then
   Marketing, then Support, then Web. There is no CRM record yet, so the golden email
   resolves to the Marketing value `carol.chen+m@example.com` at **version 1**.

2. **Clean auto-join, and a same-tier disagreement resolves.** A new Marketing record
   `mkt-013` arrives. It shares an exact **phone** (`2125551001`) with `mkt-011` and
   nothing else, so it auto-matches that one record at 0.98 and joins the existing
   cluster. The cluster grows from 2 to **3** members under the same stable id. This is
   the first point where two records at the *same* priority tier disagree: `mkt-011`
   carries `carol.chen+m@example.com`, `mkt-013` carries `carol.chen+m2@example.com`.
   With no CRM record yet, both are still the top-priority tier present, so the merge
   resolves the disagreement the same way any tie resolves — by majority, then longest
   value — and `carol.chen+m2@example.com` wins (one character longer, otherwise tied).
   The surname consensus shifts too: `Chen` (`mkt-011`), `Chenn` (`sup-012`), `Cheng`
   (`mkt-013`) are all single-occurrence, and `Chenn`/`Cheng` tie for longest, so the
   alphabetically-first `Cheng` wins. Both fields changing means the golden record is
   rewritten and a **version 2** is recorded — a real, deterministic outcome of resolving
   a genuine disagreement, not an artifact of which record happened to arrive first.

3. **Priority promotion + versioning.** A CRM record `crm-010` arrives sharing an exact
   phone (`2125551002`) with `sup-012`. It auto-joins the same cluster. Now that a CRM
   record exists, the email merge policy promotes the CRM email
   `carol.chen@example.com`, which differs from the current golden value — so the golden
   record is rewritten again and a **version 3** is recorded.

4. **Review queue.** A Web record `web-099` (Caroline Chen) arrives. Its **name** strongly
   matches the Carol Chen cluster — the matcher scores `Caroline`/`Carol` as an equal
   first-name match and the surname matches `mkt-011` — but its phone and email match no
   existing member. The project's matching profile (see below) **down-weights exact-contact
   disagreement**, so a strong name match whose contact fields simply differ lands in the
   borderline band (>= 0.75 review, < 0.90 auto) instead of being dropped. Rather than
   auto-merging (which could be wrong) or discarding it (which could miss a real match), the
   hub opens a **review task** with reason `review_threshold` for a human to adjudicate.

## Matching profile

The project resolves incremental batches with
[`customer.profile.json`](customer.profile.json) (passed via `--profiles` on each
`ingest-incremental` step). It is the built-in `person` profile with one deliberate change:
the `email` and `phone` weights are lowered to `0.3`. In a Customer 360 hub, contact details
drift constantly across systems, so a *matching* phone or email is still decisive — an exact
identifier match floors the pair straight into the auto band regardless of weight, which is
what joins `mkt-013` and `crm-010` — but a *differing* phone or email should not, on its own,
veto an otherwise strong name match. Lowering those weights lets `web-099` surface for review
rather than being silently discarded. Edit the weights to see the band shift.

## What the assertions prove

| Step | Asserted fact |
| --- | --- |
| seed `golden list` | golden record is at version 1 with the Marketing email |
| Marketing `ingest-incremental` | exactly 1 record added, 1 auto match, and 1 golden version created (the same-tier email/surname disagreement resolving) |
| `cluster list` | the auto-join grew the cluster to record_count 3 (stable id) |
| CRM `ingest-incremental` | 1 auto match and 1 golden version created (priority promotion) |
| CRM `golden list` | golden record advanced to version 3 with the CRM email |
| Web `ingest-incremental` | exactly 1 review task |
| `review list` | an open task exists with reason `review_threshold` |

## Run it

```pwsh
pwsh -File scripts/Run-DurableScenario.ps1 -ScenarioPath samples/durable/customer-360-hub
```

Expect `All 19 checks passed.` Add `-KeepArtifacts` to leave the temp working directory
and its `metadata.json` on disk for inspection (clusters, goldenRecords,
goldenRecordVersions, reviewTasks):

```pwsh
pwsh -File scripts/Run-DurableScenario.ps1 -ScenarioPath samples/durable/customer-360-hub -KeepArtifacts
```

## Source-priority resolution

The merge policy's `sourcePriority` is resolved against **each record's own `source`
field value** — i.e. the CSV `source` column (`CRM`, `Marketing`, `Support`, `Web`) — and
**not** the durable `Source.Name` the record was ingested under. (Confirmed in
`FileMetadataStore.MergeByPriority`, which filters cluster members by
`record.Fields["source"]`.) That is why every CSV here carries an explicit `source`
column and why the whole sample can run through a single durable source named `Import`:
the canonical winner is chosen from the row data, not the ingest source.

A practical consequence: a value flips either when a higher-priority `source` value
appears in the cluster (step 3, CRM promotion), or when two records already sharing the
top priority tier disagree and the disagreement resolves by majority/longest value
(step 2, the two Marketing emails). The golden email is Marketing-sourced throughout
steps 1–2 and only becomes CRM-sourced once the CRM record joins in step 3 — but
"Marketing-sourced" does not mean "unchanging": which Marketing record's value wins can
itself shift as more same-tier evidence arrives.
