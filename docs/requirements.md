# Linkuity — Product Requirements

> **Status: expanded.** Every requirement below carries an implementation status and an
> elaboration, current as of 2026-08-14, based on a direct read of the codebase (not the
> roadmap's self-reporting, though the two mostly agree). Status is a snapshot, not a
> commitment — it will drift out of date as work continues and should be re-checked
> against the code before being relied on for a release decision.

**Scope:** this describes the product as it must eventually be, not version 1. Some
items will not be built for a long time. Deciding what comes first is a separate
exercise and does not belong here.

**What the product does, in one sentence:** Linkuity takes records from several
systems, works out which of them describe the same real-world thing, and produces one
trusted version of that thing — and keeps doing it as the data changes.

Throughout, **entity** means the real-world thing: a customer, a company, a product.

Requirement numbers are identifiers, not an order. New requirements take the next free
number and sit in whichever section they belong to, so existing numbers never shift.

**How to read a status:**

- **Fully implemented** — the requirement is met today, in the shipped code, for the
  general case it describes.
- **Partially implemented** — some real, working part of the requirement exists, but
  either the mechanism is narrower than the requirement asks for, or it only covers
  some of the cases the requirement describes.
- **Not implemented** — nothing in the codebase does this yet. This does not mean it
  was overlooked; several of these are explicitly out of scope for the first release
  (noted where that's the case), and one whole area (Milestone 11, "Enterprise
  Readiness" — access control, audit, erasure, backup/ops) has not been started at all.

---

## Functional requirements

### Getting data in

- **F1** — Must accept records from files and from live system feeds.

  "Live system feed" means a source system pushing or streaming changes as they
  happen — a webhook call, a message queue, a change-data-capture stream — as opposed
  to someone periodically handing Linkuity a file. Both are legitimate ways for a
  customer's data to arrive, and a production MDM deployment typically needs the
  live path eventually so golden records stay current without a human running an
  export job.

  **Status: Partially implemented.** File ingestion is solid and is the only path
  today: CSV in, via the CLI's `run` (one-shot batch) and `ingest-incremental`
  (durable, incremental) commands. There is no live-feed path — no webhook receiver,
  message-queue consumer, or polling connector exists anywhere in the API or CLI.
  The API's only ingest-shaped endpoint is `POST /run`, a synchronous CSV upload
  capped at 400 KB; it is not a feed, it's a small-file upload. Every load today is
  something a person or a scheduled job explicitly triggers by handing Linkuity a
  file.

- **F2** — Must accept records from many different source systems and remember which
  system each record came from.

  This is provenance at the record level: for every record Linkuity holds, it should
  always be answerable which source system (CRM, ERP, a specific file feed, etc.)
  it came from, independent of which entity it eventually got grouped into.

  **Status: Fully implemented.** `Source` is a first-class, durable model with its
  own create/list operations, and every `EntityRecord` carries a required `SourceId`
  and `IngestBatchId`. A project can have any number of sources registered against
  it, and every record's origin is recoverable at any time.

- **F3** — Must accept new and changed records at any time, without reprocessing
  everything already loaded.

  The distinction that matters here is between "add ten new records" and "re-run
  matching over the entire multi-million-record project because ten records
  arrived." The second is what most legacy dedupe tools do, and it's the reason
  they can't run continuously — a project of any real size makes that too slow and
  too expensive to run often.

  **Status: Fully implemented.** The `ingest-incremental` CLI command routes through
  a resolver that reads only the clusters and records actually touched by the
  incoming batch (via targeted candidate retrieval against the durable index), not
  the whole project. This is a structural property of the resolution code, not just
  a claim — the same design is what makes the scale requirements below (S8–S10)
  possible at all on the PostgreSQL backend.

- **F4** — Must not require source systems to change their data, formats, or
  processes.

  In practice this means Linkuity has to meet the data where it is: whatever column
  names, field structures, and quirks a source system already produces, without
  asking that system's owners to change how they export or format anything.

  **Status: Partially implemented.** Field mapping exists: a project's matching
  profile maps arbitrary incoming column names to Linkuity's semantic field types
  (name, email, address, and so on), so a source doesn't need to use Linkuity's
  vocabulary. The gap is format, not naming — ingestion is CSV-only today. A source
  system that only exports JSON, XML, or a database extract in some other shape
  would need an out-of-band conversion step before it reaches Linkuity.

- **F5** — Must work with records that are missing fields.

  Real-world source data is never complete — a phone number here, an address there.
  The requirement is that a record with holes in it should still be usable for
  matching on whatever fields it does have, rather than being rejected or breaking
  the comparison.

  **Status: Fully implemented.** Missing values are treated as simply absent
  throughout normalization and scoring — similarity evaluators explicitly
  short-circuit on blank fields, and the scorer takes a `comparable` flag for pairs
  that have nothing to compare on that field rather than treating "missing" as a
  disagreement. No code path throws on a missing field.

- **F6** — Must handle records that are corrected or deleted at source.

  Two distinct situations: a source system fixes a typo in a record it already sent
  ("corrected"), or a source system removes a record entirely, e.g. the customer
  closed their account ("deleted"). Both need to flow through to Linkuity's picture
  of the entity, ideally without the customer having to manually intervene.

  **Status: Partially implemented.** Corrections now work on one path: resending a
  record through `ingest-incremental` with the same `(project, source record id)`
  but different field values updates the stored record (superseding, not
  overwriting, the prior one) and flows through matching, clustering, and the
  golden record exactly as new evidence would — an unclustered record simply gets
  re-scored, while a clustered record detaches from its cluster (dissolving it if
  it was the only member) and the golden record recomputes from the survivors. An
  identical resend (no field changed) is a safe no-op. This is supported on the
  file metadata store's non-Lucene-indexed path only: attach an index and the same
  call throws `NotSupportedException` rather than silently leaving a stale,
  still-searchable candidate behind.

  Deletion (the "customer closed their account" half of this requirement) now
  works the same way: a new `record delete` CLI command marks the targeted
  record(s) `DeletedAt` and detaches them from their cluster via the same
  `DetachFromCluster` primitive corrections use — an unclustered record is
  simply tombstoned, a clustered record's cluster recomputes its golden record
  from the remaining survivors (dissolving the cluster if it was the only
  member). Every `RecordDeletedEvent` is queryable via
  `IMetadataStore.ListRecordDeletedEventsAsync`.

  Both now also work on the PostgreSQL backend, on the same terms as the file
  store: an index-attached Postgres store throws `NotSupportedException` for
  either, so `record delete`/a correcting resend run through the CLI (which
  always attaches a Lucene index) still fails gracefully rather than
  succeeding — real correction/deletion is reachable only against a
  non-indexed store, for both backends, until Lucene reindexing lands.
  Porting corrections to Postgres required removing the mechanism that
  actually blocked every correction there: not `PostgresMutationApplier`'s
  guard (the earlier, inaccurate attribution), but a duplicate-source-record-id
  check in `PostgresMetadataStore.ValidateIncrementalRequestAsync` that
  rejected any resend before it reached the resolver. It also required fixing
  a genuine Postgres-specific gap: Postgres tracks cluster membership as a
  foreign key on the record (`entity_records.cluster_id`), the opposite of the
  file store's list-on-the-cluster model, and nothing before this needed to
  *clear* that key when a record left a cluster without joining another —
  every prior mutation path (merge, dissolution) always moved a departing
  record onto some new cluster. `PostgresMutationApplier` now clears it
  explicitly as part of writing the correction/deletion tombstone.

  One thing remains unimplemented: excluding a superseded or deleted record
  from a Lucene-indexed store's retrieval, for either backend.

### Data arriving late or out of order

- **F52** — Must not let an older version of a record overwrite a newer one in the
  trusted record, except where the customer has chosen source preference to win
  under F22.

  This guards against a specific failure mode: two updates to the same fact arrive
  out of sequence (network retry, batch replay, a source system's own backlog), and
  the trusted record ends up holding the stale value because it happened to be
  processed last. The requirement is that recency — not processing order — decides,
  unless the customer has explicitly said a source's own priority should override
  recency for that field.

  **Status: Not implemented.** Golden-record survivorship today supports exactly two
  rules: pick the value from the highest-priority source (`MergeByPriority`), or pick
  whichever value is most common across records (`MergeByConsensus`). Neither reads
  a timestamp. There is no date/recency comparison anywhere in the merge logic — the
  concept this requirement depends on doesn't exist yet.

- **F53** — Must decide which record is newer from a date the customer nominates, and
  must state what it does when a record carries no such date.

  Two parts: (1) the customer should be able to say "the `updated_at` column is what
  tells you which record is newer" on a per-project or per-field basis, since
  different source systems name and populate this differently; (2) since not every
  record will carry that field, the behavior when it's missing needs to be a defined,
  documented rule — not an accident of whatever the code happens to do.

  **Status: Not implemented.** The merge-policy model (`MergeField`) has only a field
  name and a source-priority list — there is no concept of a customer-nominated
  recency field anywhere in it. Because the feature doesn't exist, there is also no
  defined behavior for the no-date case; this isn't a bug so much as ground that
  hasn't been built on yet.

- **F54** — Must reach the same final state — both groupings and trusted-record
  values — regardless of the order in which records arrived.

  This is a correctness property, not a performance one: load a dataset in one
  order, then load the identical dataset in a shuffled order, and the two runs
  should produce identical clusters *and* identical golden-record field values.
  Getting only groupings right (which two records belong to the same entity) but
  not values (which value survives into the trusted record) would still leave the
  trusted record dependent on load order — a subtler but real violation of the same
  guarantee.

  **Status: Fully implemented.** Clustering order-independence is proven by an
  actual test that feeds the same three records in three different orderings and
  asserts identical cluster membership every time. Golden-record *value*
  order-independence was not tested, and on inspection was a real bug:
  `MergeByPriority` used a first-match lookup over cluster members when more than
  one record shared the same highest-priority source with different values for a
  field, and `MergeByConsensus` broke a full count/length tie the same way — both
  sensitive to member-list order, which in every caller tracked arrival order.
  Fixed by making every tie resolve on field content (majority, then longest, then
  alphabetical) rather than position, with permutation tests proving it
  (`Linkuity.Core.Merge.GoldenRecordMerge`). The fix also closed a related gap: the
  batch and durable paths used to carry two independently-maintained copies of this
  logic that had quietly drifted apart (case-sensitivity, field-universe scope,
  source-field configurability, blank-value checking); both now call the same
  implementation, so they cannot drift apart again.

### Deciding what matches

- **F7** — Must identify which records describe the same entity.

  This is the core of the product: given two or more records, decide whether they
  describe the same real-world person, company, or product.

  **Status: Fully implemented.** This is what the matching engine does end to end —
  normalize, generate blocking keys, retrieve candidates, score, and classify —
  and it is the best-developed part of the codebase by a wide margin (this is the
  subject of most of the roadmap's Milestones 12–27).

- **F8** — **Must never merge records that are not the same entity.**

  This is the single requirement everything else is built to protect. A false merge
  is qualitatively worse than a missed one: an unmerged duplicate is an
  inconvenience; a wrong merge silently corrupts a customer's trusted data and can
  propagate everywhere that data is used. "Never" is meant literally here, not as
  "rarely" — the acceptance philosophy recorded elsewhere in the codebase is
  explicit that there is no tolerable rate of wrong merges, only zero.

  **Status: Partially implemented — with an important nuance.** The scoring design
  is built around this goal: an exact-identifier floor for auto-merges, a
  review-floor gate that keeps weak, low-evidence pairs out of the auto-merge band
  in the first place, and a "wrong-merge gate" tool that fails outright — no
  tolerance, no configurable threshold — if it finds even one wrong merge against a
  labeled dataset. What's *not* in place is a live, runtime enforcement of this
  guarantee against a customer's actual, unlabeled data: the wrong-merge gate only
  runs when someone explicitly points `match corpus audit` at a corpus with known-
  correct answers, which in practice means a developer validating a profile before
  it goes live, not something the system checks automatically while ingesting real
  customer records it doesn't have ground truth for. In other words: the tools to
  *verify* this guarantee holds exist and are taken seriously, but the guarantee
  itself is not something the running system polices on its own — it's something a
  human confirms in advance by testing.

- **F9** — Must give one of three answers for any pair: same, not the same, or needs
  a person to decide.

  No pair should fall through the cracks into an undefined state — every comparison
  Linkuity makes has to land in exactly one of these three buckets.

  **Status: Fully implemented.** The scoring pipeline ends in a threshold-based
  classifier with three explicit bands — auto-match, review, no-match — configurable
  per matching profile, with a default posture of 0.90 for auto and 0.75 for review.

- **F10** — Must match values that are written differently but mean the same thing —
  abbreviations, typos, spacing, punctuation, name order.

  This is what makes matching useful at all rather than a glorified exact-string
  comparison: "Bob Smith" and "Robert Smith," "123 Main St" and "123 Main Street,"
  "Smith, Robert" and "Robert Smith" all need to be recognized as potentially the
  same thing.

  **Status: Fully implemented.** Multiple similarity strategies exist and can be
  assigned per field in a matching profile: fuzzy/edit-distance text comparison
  (typos, punctuation), token-based comparison (name order, spacing), n-gram
  comparison, and phonetic matching (sound-alike spellings) via a Double Metaphone
  implementation. Which strategy applies to which field is profile configuration,
  not hardcoded per entity type.

- **F11** — Must treat filler values such as "UNKNOWN" or "N/A" as missing, not as a
  real value that two records can agree on.

  Source data is full of placeholder values that look like real data but aren't:
  "UNKNOWN," "N/A," a legal-form code meaning "not provided," a national-ID
  placeholder like "000-00-0000." If two records both carry "UNKNOWN" in the same
  field, that is not evidence they're the same entity — it's evidence neither
  record actually says anything on that field, and treating it as agreement is a
  direct path to false merges.

  **Status: Fully implemented.** Every profile field can declare a list of "null
  equivalent" values, compared case- and whitespace-insensitively, and this is
  enforced through one single predicate used consistently everywhere a field's
  value is read — for similarity comparison and for blocking-key generation alike —
  specifically so a sentinel value can't slip past one of those paths while still
  being (wrongly) treated as real by the other. Which literal strings count as
  fillers is customer data, declared per field in the profile, not hardcoded in the
  engine — the same string can be a real value on one field and a sentinel on
  another.

- **F12** — Must work with data in any language and any alphabet.

  A customer's data will not always be English, and won't always be in a Latin
  script — accented Western European names, Cyrillic, Arabic, Chinese/Japanese/
  Korean characters, and everything in between all need to compare sensibly.

  **Status: Partially implemented.** Nothing in the matching or normalization code
  special-cases English or assumes Latin script for the *numeric/structural* parts
  of the pipeline (exact and token comparisons work on whatever Unicode text is
  fed in). The gap is on the technique side: the one phonetic ("sounds like")
  strategy shipped is Double Metaphone, which is an English-pronunciation algorithm
  and produces meaningless results on non-Latin or non-English text — it's an
  opt-in strategy a profile can choose, not the default, but a customer wanting
  phonetic matching on, say, Cyrillic names has no equivalent available. There's
  also no explicit Unicode normalization (accent-folding, script-aware
  normalization) visible in the codebase, and no evidence the matching pipeline has
  been tested against non-Latin-script corpora at all.

- **F13** — Must find likely matches without comparing every record against every
  other record.

  Comparing every record to every other record is quadratic — fine for thousands of
  records, impossible for millions. The system needs some form of pre-filtering
  ("blocking") that narrows the field of candidates a given record is actually
  compared against, without missing genuine matches in the process.

  **Status: Fully implemented.** Candidate retrieval runs through an embedded
  Lucene.NET index keyed on profile-driven blocking strategies (exact-value,
  token-name, phonetic, n-gram, prefix, acronym, fingerprint, and a composite of
  these), so a record is only ever compared against records sharing some blocking
  signal with it — not the entire corpus.

- **F14** — Must flag entities that are connected but not the same — same address,
  same owner — without merging them.

  Two companies at the same address, or two people sharing a phone number, are
  meaningfully related to each other in a way worth surfacing — but "related" is not
  "the same entity," and the two must not be conflated. This is a distinct kind of
  output from a match: not a merge decision at all, but a separate relationship
  worth recording between two entities that stay separate.

  **Status: Not implemented.** No relationship-edge concept exists between distinct
  entities in the data model — nothing analogous to a graph edge that says "these
  two clusters share an address" or "these two clusters share an owner" while
  remaining two clusters. This has been discussed and deliberately deferred rather
  than overlooked.

### Setting it up for a customer's data

- **F15** — Must be configurable for a new dataset without writing code.

  A customer standing up Linkuity against a new dataset — a new source system, a new
  kind of entity — should be able to describe how that data should be matched
  through configuration, not by a developer writing and shipping new matching
  logic.

  **Status: Fully implemented.** A matching profile is a JSON document — field
  names, semantic types, roles, weights, which similarity/blocking strategies to
  use, thresholds — loaded and validated against the live set of registered
  strategies at load time (an unknown strategy name fails loudly, naming the
  offending value, rather than silently falling back to something else). No
  recompilation or code change is required to add or modify a profile.

- **F16** — Must measure, from the customer's own data, how useful each field is for
  matching.

  Not every field a customer has is equally useful for telling entities apart — a
  postal code might be highly discriminating in one dataset and nearly useless in
  another. The requirement is that Linkuity should be able to tell the customer
  which of *their* fields actually carry matching evidence, measured against their
  own data rather than assumed in advance.

  **Status: Fully implemented.** `match corpus fields` measures, for every matchable
  field, how often records of the *same* entity agree on it versus how often
  records of *different* entities agree on it by chance, expresses the gap between
  those two rates in bits of evidence, and translates that into a plain-language
  verdict ("very strong" down to "nearly useless") so a reader doesn't need to
  think in log-odds to get the right conclusion. It requires a labeled ground-truth
  file to measure against, though — it isn't something Linkuity can compute from
  unlabeled data alone.

- **F17** — Must tell the customer which of their records can never be told apart,
  and what extra information would separate them.

  Some duplicates in a dataset are unresolvable with the fields available — two
  different people who share every field the data collects on them. Rather than
  Linkuity silently failing to resolve these or a customer discovering them by
  accident, the product should surface them directly, along with a concrete answer
  to "what would we need to collect to tell these apart?"

  **Status: Fully implemented.** The same `match corpus fields` measurement produces
  a distinct "indistinguishable groups" report: groups of two or more different
  real entities whose every matchable field is identical under the profile's own
  normalization, how many records and groups are affected, and — for the largest
  such group — which matchable fields were unfilled on every record in it, which is
  the direct, actionable answer to "what's missing." This output is explicitly
  framed in the code as a data-collection signal, not a tolerance for merging
  ambiguous records — an unresolvable pair still must not be merged.

- **F18** — Must support different kinds of entity — people, companies, products —
  through the same mechanism, not special-case handling per kind.

  The alternative — a person-matching code path, a separate company-matching code
  path, and so on — doubles maintenance cost for every new entity kind and
  guarantees the kinds drift apart in quality over time. The requirement is one
  matching mechanism, driven entirely by configuration data, that resolves any
  kind of entity.

  **Status: Fully implemented.** Person, organization, and product entity kinds all
  resolve through the identical `MatchingEngine` with zero entity-kind-specific
  code — this was explicitly proven by adding organization and product support
  purely through new profile configuration and semantic field types, with the
  matching/scoring/decision code itself left untouched (verified by diffing those
  files against the pre-change baseline).

- **F19** — Must let a customer test a configuration change before it affects live
  data.

  Before a customer rolls out a change to their matching profile or merge policy —
  a new threshold, a re-weighted field — they need a way to see its effect without
  it actually changing their live, already-grouped data.

  **Status: Not implemented.** There is no "dry run against my live project" or
  preview mechanism. The closest existing tool, `match corpus audit`/`match corpus
  calibrate`, measures a profile's behavior — but only against a separately supplied
  labeled corpus file, not against a specific project's actual current data and
  existing groupings. It answers "is this profile accurate in general," not "what
  would change in my project if I applied this."

- **F20** — Must warn before a configuration change would alter groupings that
  already exist.

  A step further than F19: not just letting the customer test a change on request,
  but proactively telling them a pending change would alter existing groupings
  before it's applied, so it can't happen by surprise.

  **Status: Not implemented.** Nothing in the codebase compares a proposed
  configuration against current groupings and computes or surfaces a diff. Changing
  a project's merge policy today simply overwrites the stored configuration with no
  before/after comparison at all.

- **F44** — Must produce a usable first result from a new dataset without anyone
  hand-writing a configuration.

  Zero-to-first-value: a customer should get *something* useful the moment they
  point Linkuity at their data, without a project kickoff spent hand-tuning a
  profile first.

  **Status: Fully implemented.** Person and organization ship as built-in profiles
  that a project can use with no configuration at all — `ingest-incremental` and
  `run` both work zero-config against these built-ins, and a loaded custom profile
  silently overrides a built-in of the same content type rather than requiring one.

- **F45** — Must ship a starting configuration for each kind of entity, set
  cautiously enough that it leaves duplicates unmerged rather than merging anything
  wrongly.

  The default configuration's job is to fail safe: an under-tuned profile should
  produce more manual review work, not more wrong merges. This is a deliberate,
  named trade-off elsewhere in the project's own decisions: rough but honest beats
  smooth but wrong.

  **Status: Fully implemented.** The built-in person and organization profiles use
  a review threshold below the auto-merge threshold (0.75 vs 0.90) plus an
  identifier-aware floor that requires an exact identifier match (email, phone,
  domain, etc.) before anything auto-merges on weighted similarity alone — a
  conservative posture consistent with "leave it in the queue rather than guess."

- **F46** — Must turn reviewers' decisions into the confirmed examples that F16
  measures from, so the configuration improves as the queue is worked.

  This is the feedback loop that's supposed to make Linkuity's calibration get
  better over time without a separate, disconnected data-labeling exercise: every
  time a human resolves an item in the review queue, that decision becomes a
  labeled example the field-usefulness measurement can learn from.

  **Status: Not implemented.** Two separate gaps compound here. First, the
  measurement tools (`match corpus fields`, `match corpus calibrate`) only accept
  ground truth from an externally supplied labeled CSV file — there is no code path
  that reads it from the project's own review-decision history. Second, and more
  fundamentally, there currently isn't a review-decision history to read from at
  all: no command records a reviewer's confirm/reject decision anywhere (see F25).
  The loop this requirement describes has no data source yet, on either end.

- **F47** — Must be operable by a data engineer. No statistical training may be
  assumed.

  This sets the bar for who can run Linkuity day to day: someone comfortable with
  CLIs, config files, and CSVs, but not someone expected to understand precision/
  recall curves or Bayesian probability to get useful results out of it.

  **Status: Partially implemented — a matter of judgment rather than a clean yes/no.**
  Authoring a basic profile (naming fields, picking semantic types, choosing a
  similarity strategy) is declarative and doesn't require statistics. But some of
  the more advanced tooling leans on statistical framing in its raw form —
  `match corpus fields` reports "bits of evidence" and internal probability
  estimates alongside the plain-language verdict it also produces, and getting the
  most out of calibration/evidence-scoring features arguably still benefits from
  some comfort with the underlying concepts, even though the tools try to translate
  the numbers into a plain read for someone who doesn't want to dig into the math.

- **F48** — Must state up front what it needs from the customer, rather than
  discovering it partway through a project.

  Before a project starts, the customer should be able to see a clear, complete
  list of what Linkuity needs from them — source formats, expected fields, any
  ground-truth data required for calibration — rather than that list emerging
  piecemeal as surprises during implementation.

  **Status: Not implemented.** There is no onboarding checklist, requirements
  wizard, or up-front "here's what we need from you" artifact generated by the
  product itself. Reference documentation exists (architecture and matching-engine
  guides) describing how the system works, but nothing gathers or states a
  project's specific requirements before work begins.

### Producing one trusted record

- **F21** — Must produce a single best version of each entity from all the records
  that describe it.

  Once records are grouped into an entity, the customer needs one authoritative
  version of that entity's data — the "golden record" — not just the grouping
  itself.

  **Status: Fully implemented.** `GoldenRecord` (current state) and
  `GoldenRecordVersion` (history) are both durable, first-class models, computed
  from cluster membership using the project's configured merge rules every time a
  cluster's membership or an underlying value changes.

- **F22** — Must let the customer decide, for each field, which source system wins
  — and whether that preference or the newer value wins when the two disagree.

  Two separate customer choices bundled into one requirement: (1) per field, rank
  which source system is authoritative when sources disagree (email always comes
  from the CRM, revenue always comes from the ERP, etc.); and (2) per field, decide
  whether that source ranking or simple recency should win when both apply and
  disagree with each other.

  **Status: Partially implemented.** The first half exists: a merge policy can
  declare, per field, an ordered list of which source wins. The second half does
  not — there is no recency concept in merge policy at all (this is the same gap
  described under F52/F53), so there is nothing for source preference to be
  weighed against, and no customer-facing choice between the two exists yet.

- **F23** — Must record where every value in the best version came from.

  For every value sitting in a golden record, it should be traceable back to the
  specific source record — and by extension the source system — it came from. This
  is what makes a golden record trustworthy rather than an opaque black box.

  **Status: Partially implemented.** Provenance exists at a coarse grain — a
  project's merge policy states which source *should* win per field, and which
  scoring profile produced a given match decision is recorded on the match edge —
  but the golden record itself stores only a flat field-name-to-value dictionary,
  with no per-value stamp saying "this specific value came from this specific
  source record." Reconstructing exact per-value provenance today would mean
  cross-referencing the merge policy against the underlying entity records
  yourself, rather than reading it directly off the golden record.

### Human review

- **F24** — Must give people a queue of uncertain matches to decide.

  Pairs that land in the "needs a person" band (F9) need somewhere to go — a real,
  inspectable queue, not just a score that's discarded.

  **Status: Fully implemented.** A `ReviewTask` is durably created for every pair
  landing in the review band (and for weak cluster-merge suggestions), and the
  queue is readable via `review list` and exportable via `review export`.

- **F25** — Must let a person confirm or reject a match, and must honour that
  decision permanently.

  This is the other half of F24 — a queue nobody can act on isn't useful. A person
  needs to be able to say "yes, same entity" or "no, different entities," and that
  decision needs to stick: it must not be silently reopened or overridden by a
  later automated run.

  **Status: Not implemented.** `ReviewTask` has a `Status` field in its data model,
  but nothing in the CLI or API ever writes a human decision into it — there is no
  `review confirm` or `review reject` command anywhere. The queue can be viewed and
  exported, but a human cannot yet act on an item through the product; the decision
  half of the human-review loop doesn't exist yet.

- **F26** — Must let a person separate records that were grouped together wrongly.

  The opposite failure mode from a missed match: two records got clustered
  together as one entity, and a reviewer needs to be able to pull them back apart.

  **Status: Not implemented.** No split, unmerge, or reversal command exists in the
  CLI or API. The data model was deliberately built to make this possible later —
  when clusters get merged automatically, the losing cluster's history is
  preserved (tombstoned, not deleted) rather than discarded, specifically so a
  future unmerge operation could reconstruct the pre-merge state — but that
  operation itself has not been built.

- **F27** — Must warn when the review queue grows beyond a size the customer sets.
  This is a warning, not a limit: the customer is told and processing continues.
  The default must be no threshold, and therefore no warning.

  This exists to catch a specific operational failure mode: a profile that's
  too cautious (or a dataset that's genuinely hard to resolve) can flood the review
  queue faster than a team can work it. The customer should find out about that as
  it's happening, not discover a six-figure backlog later — but this is advisory,
  never a hard stop on processing.

  **Status: Not implemented.** No queue-size threshold, warning, or related
  configuration exists anywhere in the codebase.

- **F41** — Must let a reviewer see the queue grouped by what its items have in
  common, so a shared underlying cause is visible rather than having to be worked
  out one item at a time.

  If a hundred review items all exist because of the same root cause — a shared
  surname, a data-quality problem in one source feed — a reviewer working the queue
  one row at a time has no way to notice that pattern and address it once. Grouping
  by shared cause turns a hundred individual decisions into (potentially) a handful
  of pattern-level ones.

  **Status: Not implemented.** `review list` and `review export` produce flat lists
  of review tasks; nothing groups them by shared blocking key, shared cause, or any
  other common attribute.

- **F42** — Must let a reviewer correct the underlying data that put items in the
  queue, without changing the source system.

  Sometimes what belongs in the queue is really a data-quality problem — a
  malformed field that's confusing the matcher — and the fix belongs in Linkuity's
  copy of the record, not in the source system (which the customer may not control,
  or which shouldn't be touched for a Linkuity-side correction).

  **Status: Not implemented.** No command or endpoint edits an ingested record's
  field values in place.

- **F43** — Must let a reviewer resubmit queued items for reprocessing after a
  correction, so every item the fix resolves clears without a separate manual
  decision.

  The payoff of F42: once a shared underlying problem is fixed once (per F41), every
  review item that problem caused should be able to clear automatically by being
  re-run through matching, rather than requiring a separate manual decision on each
  one.

  **Status: Not implemented.** No resubmit-for-reprocessing operation exists; this
  depends on F42 existing first, which it doesn't.

### Staying correct as data changes

- **F28** — Must keep each entity's identifier stable over time, so downstream
  systems can rely on it.

  Once a downstream system has recorded "customer entity ID X," that ID needs to
  keep meaning the same entity indefinitely — even as more records join it, even
  across a cluster merge — or every system that stored that ID breaks.

  **Status: Fully implemented.** Cluster and golden-record IDs are stable GUIDs.
  When two clusters merge, the surviving cluster's ID is chosen deterministically
  (oldest creation time, tie-broken by smallest ID) and kept — a merge never mints
  a brand-new ID that downstream systems would have to learn about.

- **F29** — Must update existing groupings when new data arrives — including
  splitting a group that turns out to be wrong.

  Groupings aren't static: new records need to be able to join existing groups
  (and existing groups need to be able to merge into each other when new evidence
  connects them) — and, just as importantly, a group that turns out to have been
  wrong needs to be splittable, not stuck.

  **Status: Partially implemented.** The "join and merge as new evidence arrives"
  half is solid — new records join existing clusters, and clusters bridge-merge
  automatically when strong evidence connects them across a batch. The "split a
  group that's wrong" half does not exist (this is the same gap as F26) — there is
  currently no way to reverse or undo a grouping decision once made.

- **F30** — Must keep a history of how each entity changed and why, except where
  erasure has been requested under S17.

  A full change history — not just the current state — for every entity: when a
  new record joined, when a value changed and to what, when clusters merged and
  why. This is what makes "how did we end up with this golden record" answerable
  after the fact.

  **Status: Fully implemented (for the "keep history" half).** Every canonical
  change to a golden record creates a new, retained `GoldenRecordVersion`; cluster
  merges are recorded as `ClusterMergeEvent`s naming the surviving and absorbed
  clusters, the triggering records, and the evidence involved. The erasure
  exception this requirement carves out doesn't currently create a conflict in
  practice, because S17 (erasure) has not been built yet either — there's nothing
  yet that would need to override this history-keeping.

- **F31** — Must be able to show the state of an entity as it was at an earlier
  date.

  Not just "list the versions that existed" but "tell me what this entity looked
  like as of March 3rd" — a genuine point-in-time reconstruction, which is a
  different and more specific capability than a plain version list.

  **Status: Partially implemented.** `golden history` lists every version of a
  golden record with its timestamp, which contains all the information needed to
  work out what was current as of a given date — but there is no command or query
  that does that reconstruction for you. Today, answering "what did this entity
  look like on March 3rd" means manually scanning the version list and finding the
  right one yourself.

### Explaining itself, and answering for itself

- **F32** — Must explain why any two records were matched, in terms a non-technical
  person can follow.

  Two distinct bars here: the explanation needs to *exist* (the raw why), and it
  needs to be *readable by someone who isn't an engineer* — a business stakeholder
  asking "why do you think these are the same customer" shouldn't need a data
  scientist to translate the answer for them.

  **Status: Partially implemented.** The underlying explanation data is genuinely
  rich and complete: `match explain` produces a full per-signal breakdown for any
  match decision — which fields agreed, by how much, weighted how, contributing how
  much to the final score. What it does not yet do is translate that into plain
  language — the output is a CSV of signal names, raw values, weights, and
  numerical contributions, which is exactly the right underlying data but requires
  some technical fluency to read as a "why" a non-technical stakeholder could
  restate in their own words.

- **F33** — Must explain why two records a user expected to match did not.

  The mirror image of F32, and arguably the harder, more common support question:
  a user looking at two records that seem obviously the same, asking why Linkuity
  *didn't* connect them.

  **Status: Not implemented.** `match explain` only surfaces breakdowns for pairs
  Linkuity already compared and recorded a decision for (an auto-match edge or a
  review task). There is no on-demand "compare these two specific records and tell
  me why they didn't match" query for an arbitrary pair the system never happened
  to compare — which, depending on blocking behavior, could itself be part of the
  answer (they may never have been retrieved as candidates at all), but there's no
  tooling to discover or explain that today.

- **F34** — Must record who or what made each decision, and when.

  Every match decision, merge, and review outcome needs an actor and a timestamp
  attached — "the system, automatically, at 14:32 on the 3rd" or "reviewer Jane, at
  09:10 on the 4th" — as the basis for any later audit or dispute.

  **Status: Partially implemented.** The "what and when" half is solid for
  automated decisions — every match edge records its decision (auto/review/
  no-match) and the evidence behind it, and cluster merges are timestamped events.
  The "who" half is largely absent: there is no user-identity concept in the
  product at all (see S15), so there's no one to attribute a decision *to* beyond
  "the system" — and since human review decisions aren't recorded yet either
  (F25), there currently isn't a human actor's decision to attribute in the first
  place.

- **F49** — Must let someone find everything a decision affected, once that decision
  turns out to be wrong — including what was sent out and when.

  When a bad decision is discovered, the blast radius needs to be answerable
  precisely: which entities, which golden-record values, and — critically — which
  downstream systems were told about the bad decision and when, so those systems
  can be corrected too.

  **Status: Partially implemented.** The internal half is well covered: a cluster
  merge event records which clusters and trigger records were involved, and every
  version a golden record ever held is retained, so it's possible to trace what a
  given decision touched inside Linkuity's own data. The external half — what was
  sent to other systems, and when — has nothing to point to yet, because there is
  no export/notification mechanism in the first place (F37, explicitly deferred);
  there's no delivery log because there's no delivery.

- **F50** — Must let a wrong merge be reversed. The reversal reaches Linkuity's own
  data; telling other systems is the customer's to do, using F49's record and,
  later, F37's hooks.

  A narrower, more specific case of F29's "split a wrong group": specifically
  undoing a merge, inside Linkuity, once it's known to be wrong.

  **Status: Not implemented.** No reverse/undo-merge command exists. As with F26/
  F29, the data model was deliberately built to make this feasible later — a
  merged-away cluster's version history and membership are preserved rather than
  deleted specifically so a future unmerge could reconstruct the prior state — but
  the operation that would actually perform the reversal hasn't been written.

- **F51** — Must be able to reproduce a past decision exactly, using the
  configuration that was in force when it was made.

  Configuration changes over a project's life — thresholds get tuned, profiles get
  adjusted. To defend or re-examine a decision made six months ago, it has to be
  reproducible using *that* moment's configuration, not whatever configuration
  happens to be active today.

  **Status: Partially implemented.** Real progress exists on the scoring side: every
  match edge stores which scoring strategy and which specific profile
  "fingerprint" produced it, so a past scoring decision can be tied to the exact
  profile content that was live at the time, even across multiple edits to a
  profile with the same name. The gap is the merge policy: a project's merge
  configuration (which drives golden-record survivorship, not match scoring) is
  stored as a single mutable value with no version history — changing it overwrites
  the only copy — so a past *golden-record* outcome cannot be reliably reproduced
  against the exact policy that was in force when it was computed, only a past
  *match* decision can.

### Getting data out

- **F35** — Must export trusted records and their groupings in standard formats.

  Golden records and their cluster memberships need to leave Linkuity in formats
  other tools can actually consume — not locked inside Linkuity's own storage.

  **Status: Partially implemented.** Two real export paths exist: a CSV export of
  golden records, and a Neo4j-specific graph export package (a zip a customer can
  load directly into Neo4j to explore entity relationships visually). There is no
  JSON, XML, Parquet, or other general-purpose structured export, and no
  general-purpose graph format beyond the Neo4j-specific package.

- **F36** — Must let another system ask "which entity does this record belong to"
  and get an answer immediately.

  A specific, narrow, high-value query: given a record identifier, answer
  instantly which entity (cluster/golden record) it currently belongs to — the kind
  of lookup another application would make in real time, e.g. as part of handling
  a live transaction.

  **Status: Not implemented.** No such lookup endpoint exists in the API. The
  nearest things that exist — `golden list` and `cluster list` — are batch
  read-back commands over an entire project (with a row-count guardrail to prevent
  loading something unbounded), not a single-record, real-time lookup.

- **F37** — Must provide hooks the customer can use to notify other systems when an
  entity changes, including when a merge is reversed. Linkuity supplies the hook;
  the customer decides what it does and who it tells. *Not required for the first
  release.*

  This requirement is explicitly deferred by its own text, so its absence is
  expected rather than a gap — noted here only for completeness and because F49
  and F50 both reference it as the future mechanism for propagating corrections
  downstream.

  **Status: Not implemented — deliberately, per the requirement's own scope note.**
  No webhook, event-notification, or hook mechanism of any kind exists in the
  codebase today.

### Proving it works

- **F38** — Must be able to measure its own accuracy against a set of known-correct
  answers.

  Linkuity needs a way to check its own work: given a dataset with known-correct
  answers (which records really are the same entity), measure how well matching
  performed against that ground truth.

  **Status: Fully implemented, but currently developer/engineering-facing rather
  than customer-facing.** `match corpus audit` is a real, shipped CLI command that
  computes recall, precision, and reachability against a labeled corpus at scale.
  A separate script (`tools/corpus/corpus_variation.py`) exists purely as internal
  developer tooling for validating a corpus's quality before using it for
  calibration — it isn't part of the shipped product surface at all. The shipped
  command is genuinely useful to a customer who has (or is willing to build)
  labeled data, but nothing packages this as a customer-facing "here's your
  accuracy" workflow independent of preparing that labeled input themselves.

- **F39** — Must fail a run, not merely warn, when that run produces wrong merges.

  Distinguishes an accuracy check that just reports a number from one with teeth:
  when wrong merges are found, the run should be treated as a failure — with the
  operational consequences that implies (blocking a release, failing a CI check) —
  not just logged as a warning someone might not read.

  **Status: Partially implemented.** The mechanism itself is genuinely uncompromising
  where it exists: `match corpus audit` returns a hard failure exit code the moment
  it finds even one wrong merge against labeled data, with no configurable
  tolerance. What's missing is the "when that run" scope — this gate only fires
  when a human explicitly invokes it against a corpus with known answers (a
  pre-production or CI-time check). It is not wired into a live customer's actual
  `run` or `ingest-incremental` — those will complete and report success even if,
  unbeknownst to the system, they made a wrong merge, because there's no ground
  truth available to check against in that context.

- **F40** — Must report its accuracy in terms a business owner can act on.

  Precision, recall, and reachability are the right numbers for an engineer
  tuning a profile — but a business owner needs something closer to "how many
  customer records are duplicated right now" or "how much would a wrong merge
  cost us," phrased in terms that lead directly to a decision, not a statistics
  lesson.

  **Status: Not implemented, in the form this requirement describes.** The
  existing accuracy reporting (`match corpus audit`'s output) is written in
  engineering vocabulary throughout — reachability percentages, precision/recall,
  blocking-strategy internals, cohesion metrics. The one exception is the
  wrong-merge gate's failure message, which is deliberately phrased as a pair
  count rather than a percentage (specifically because "10,000 of 1,000,000 pairs
  wrongly merged" reads as alarming in a way "99% precision" doesn't) — but that
  one message is the exception, not the general shape of the reporting.

---

## System requirements

### Where it runs

- **S1** — Must run entirely inside the customer's own environment. No customer data
  leaves it.

  No customer record, or anything derived from one, should ever be transmitted to
  a server Linkuity's makers control — this is the foundation the whole
  private-runtime direction is built on.

  **Status: Fully implemented.** No telemetry, analytics, or "phone home" code
  exists anywhere in the solution. The only outbound network calls in the codebase
  are inert documentation links inside code comments, not anything executed. Azure
  connectivity — the one path that does talk to an external service — is entirely
  optional, isolated to its own adapter project, and off by default.

- **S2** — Must run on one laptop and on a production server using the same
  product, not two different builds.

  A developer's laptop and a customer's production server should be running the
  same artifact, with behavior differences controlled by configuration, not by
  shipping and maintaining two separate codebases or build pipelines.

  **Status: Fully implemented.** There is one CLI executable and one API
  executable; which storage backend and runtime mode they use is a configuration
  switch, not a different build. The same Docker image used for local development
  is what a private-server deployment runs.

- **S3** — Must run on Windows, Linux, and macOS.

  Customers' environments vary, and the product shouldn't dictate which operating
  system they run it on.

  **Status: Partially implemented.** Nothing in the code is Windows-, Linux-, or
  macOS-specific — the whole solution targets a single, OS-neutral .NET version,
  and no OS-specific system calls or hardcoded path assumptions were found. In
  practice it has genuinely been run on Windows (all of the roadmap's scale
  measurements were performed there). What's missing is verification, not
  necessarily function: automated continuous-integration testing currently only
  runs on Linux, so there's no automated proof it works correctly on Windows or
  macOS today — only that it isn't written in an OS-specific way.

- **S4** — Must not require a cloud account, subscription, or vendor-hosted service
  to function.

  The default way of running Linkuity should need nothing beyond what the
  customer already controls — no signup, no managed cloud dependency, no
  subscription to a third party.

  **Status: Fully implemented.** The default configuration (local runtime mode,
  file-based storage) needs no cloud account of any kind. Even the PostgreSQL
  backend, when a customer chooses it, is a self-hosted database, not a managed
  cloud service Linkuity depends on.

### Storage

- **S5** — Must store its data in PostgreSQL.

  PostgreSQL is meant to be *the* place Linkuity's durable data lives — the
  primary, expected storage engine, not one option among several.

  **Status: Partially implemented.** A complete, well-tested PostgreSQL backend
  exists and is a real, production-quality option a customer can select. But it is
  not what a new project gets by default — the default storage today remains the
  older JSON-file-based store, which this requirement's spirit (PostgreSQL as *the*
  store) argues against continuing to ship as the default.

- **S6** — Must not require any additional database, search engine, or message
  broker to be installed.

  Beyond whatever database holds Linkuity's durable data, nothing else — no
  separate search engine, no message queue — should need to be stood up and
  operated for the product to function.

  **Status: Fully implemented, for the default/PostgreSQL path.** The search-engine
  component (Lucene.NET, used for candidate retrieval) runs embedded in-process as
  a set of index files on disk — there is no separate search server to install or
  run. A message broker (Azure Service Bus) exists only inside the optional Azure
  adapter and plays no role in local or PostgreSQL-backed operation.

- **S7** — Must not lose work in progress if the machine restarts.

  A crash or restart mid-operation should never leave Linkuity's data in a
  half-written, corrupted state.

  **Status: Fully implemented.** PostgreSQL-backed ingest runs each bounded batch
  inside a single transaction, so an interruption rolls back cleanly rather than
  leaving partial writes. The file-based store writes via a temp-file-then-atomic-
  replace pattern for the same reason — a crash mid-write cannot corrupt the
  existing file.

### Scale and speed

- **S8** — Must handle tens of millions of records.

  This is a specific, quantified bar — not "handle a large dataset" in the
  abstract, but tens of millions of individual records in a single project.

  **Status: Not implemented, as currently validated.** The largest scale actually
  measured and documented is 100,000 records on the PostgreSQL backend. The
  project's own scale-validation work explicitly states that a full millions-scale
  run was not executed — the claim that behavior extrapolates cleanly from 100,000
  up to tens of millions is a reasonable engineering inference given the design
  (see S9), but it is an inference, not a measurement. This requirement is not met
  as *proven* today, regardless of how likely it is to hold.

- **S9** — Must process new data in time proportional to the amount of new data,
  not the total already stored.

  As a project accumulates more historical records, ingesting a new batch should
  keep taking roughly the same amount of time — not get progressively slower
  because there's more history to wade through.

  **Status: Partially implemented — and backend-dependent.** On PostgreSQL, this is
  measured and holds: per-batch ingest time stayed essentially flat (a 1.03×
  ratio between the first and last tenth of a 100,000-record run). On the older
  file-based store, the opposite is true and is documented as such — per-batch time
  rose roughly 5.4× as the stored corpus grew tenfold, because that store rewrites
  its entire file on every save. Since the file store remains the default (S5),
  this requirement does not hold for a project run under default settings.

- **S10** — Must have predictable memory use regardless of how much data is stored.

  The same idea as S9, applied to memory rather than time: memory use during
  ingest shouldn't climb as the total stored dataset grows.

  **Status: Partially implemented — same backend split as S9.** PostgreSQL-backed
  ingest measured a flat memory plateau as the corpus grew. The file-based store
  measured memory rising roughly 6.4× over the same growth, because it holds the
  whole dataset in memory to rewrite it. Same caveat as S9: this doesn't hold under
  the current default backend.

- **S11** — Must state its limits openly rather than slowing down or degrading
  silently.

  When Linkuity is operating near a real limit — a candidate cap, a row-count
  ceiling — the customer should be told, rather than the system quietly getting
  slower or truncating results without saying so.

  **Status: Partially implemented.** Two real, explicit guardrails exist and are
  enforced with clear failure messages: a maximum candidate count per record during
  matching, and a maximum row count for read-back/export commands that refuses to
  run rather than silently loading something unbounded. What's missing: the
  candidate cap doesn't warn when it actually binds and silently caps work for that
  record rather than flagging it, and the review-queue-size warning this same
  principle would call for (F27) doesn't exist at all yet.

### Reliability

- **S12** — Must resume an interrupted run without duplicating or losing records.

  If an ingest is interrupted partway and re-run, the result should be exactly as
  if it had run once cleanly — no records counted twice, none dropped.

  **Status: Fully implemented.** Both storage backends key each record on its
  project and source-record identifier and reject an attempt to insert the same
  key twice, rather than silently duplicating it. Combined with the
  transactional/atomic batch commit (S7), retrying an interrupted batch doesn't
  produce duplicates. There isn't a distinct "resume this exact batch" operation —
  retrying means resubmitting the batch, and the idempotency guarantee is what
  makes that safe.

- **S13** — Must produce the same result every time from the same data and the same
  configuration.

  Determinism: the same inputs and configuration should always produce the same
  clusters and golden records, run after run.

  **Status: Partially implemented.** This is proven for two specific dimensions —
  running with different degrees of parallelism produces byte-identical results,
  and running the identical scenario against either storage backend produces
  identical results — which is real, meaningful evidence of a deterministic design.
  It has not been proven as a fully general claim across every dimension the
  requirement could reasonably cover, such as records arriving in different
  batches across separate ingest calls over time, or arbitrary configuration
  variations.

- **S14** — Must never lose a human's decision, under any failure.

  Once a person has made a review decision, that decision needs to survive
  crashes, restarts, anything short of catastrophic data loss.

  **Status: Not implemented.** This is currently a non-question rather than an
  unmet bar, because there's no human decision-recording feature yet to protect —
  no `review confirm`/`review reject` command exists (F25), so there is nothing
  written that could be lost.

### Security and privacy

- **S15** — Must control who can view data, who can make decisions, and who can
  change configuration.

  Access control across three distinct capabilities: viewing customer data,
  making review/match decisions, and changing how matching or merging is
  configured. These are meaningfully different permissions a real deployment would
  want to separate (a reviewer shouldn't necessarily be able to reconfigure the
  matching engine, for instance).

  **Status: Not implemented.** There is no authentication or authorization code
  anywhere in the API or CLI — no login, no API key, no role concept, no user
  identity model at all. Anyone who can reach the API process or run the CLI can
  do anything the product supports.

- **S16** — Must keep an audit trail of every change to data and to configuration.

  Every change — a record's data changing, a configuration setting being edited —
  needs a durable trail: what changed, and (per F34) who or what changed it, and
  when.

  **Status: Partially implemented.** Data-level changes are genuinely well audited
  — every match decision and its evidence, and every cluster merge event, is
  durably recorded. Configuration changes are the gap: updating a project's merge
  policy overwrites the stored configuration in place with no history record of
  what it changed from or when — the "and to configuration" half of this
  requirement is currently unmet.

- **S17** — Must be able to permanently remove an individual's data on request.

  A hard, genuine deletion capability for an individual's data across everywhere
  it lives — records, version history, search index, any exports or backups — in
  response to a legal or policy request (e.g. a privacy regulation's right to
  erasure). This requirement, per the decisions recorded later in this document,
  is meant to override F30's history-keeping when the two are in tension.

  **Status: Not implemented.** No delete, erase, forget, or purge operation exists
  anywhere in the storage interface, CLI, or API — every operation available today
  adds or reads data, never removes it. There is consequently no path through
  entity records, version history, the search index, or exports that would
  actually satisfy an erasure request.

### How people and systems use it

- **S18** — Must be fully usable from a command line.

  Every capability the product offers should be reachable from the CLI, without
  requiring a GUI or a direct API call for anything.

  **Status: Fully implemented.** The CLI is, in practice, the primary and most
  complete interface to the product today — broader than the API (see S19). It
  covers project/source/batch setup, one-shot and incremental ingest, golden-
  record and cluster read-back, review-queue read-back, match explanation, and the
  full diagnostic/calibration toolset (field usefulness, corpus audit,
  calibration, blocking/scoring characterization).

- **S19** — Must be usable by another program through an API.

  A separate, non-interactive system should be able to drive Linkuity
  programmatically — not just a human at a terminal.

  **Status: Partially implemented.** A real REST API exists, covering project,
  source, and batch setup, merge-policy updates, and a synchronous small-file
  `run`. It is materially narrower than the CLI, though: incremental ingest,
  review-queue access, golden-record/cluster read-back, and match explanation are
  all CLI-only today, with no API equivalent. There is also no OpenAPI/Swagger
  definition published for the endpoints that do exist.

- **S20** — Must be fully operable without a graphical interface.

  Nothing about running Linkuity should require a browser or desktop application.

  **Status: Fully implemented, trivially.** No graphical interface of any kind
  exists in the product today — only the CLI and the headless API — so there is
  nothing GUI-dependent to route around.

### Change over time

- **S21** — Must upgrade to a new version without requiring everything to be
  reprocessed.

  Installing a new version of Linkuity shouldn't force a customer to re-run
  matching over their entire historical dataset just to keep using it.

  **Status: Partially implemented.** For the PostgreSQL backend, schema changes are
  versioned migrations that are consistently additive — new columns arrive with
  safe defaults, and the corresponding data models are written so older stored
  records still load correctly even if they predate a given field. This is a real
  and deliberately applied pattern, not an accident, but it's scoped to additive
  schema changes only; there's no tested path yet for a genuinely breaking schema
  change, and the file-based store has no equivalent versioning discipline at all.

- **S22** — Must retain past configurations so that past results can still be
  explained.

  As configuration changes over a project's lifetime, old configurations need to
  stay retrievable — this is what makes F51 (reproducing a past decision using the
  configuration in force at the time) possible at all.

  **Status: Partially implemented.** Match-scoring configuration is well covered —
  every match decision is stamped with the exact profile content that produced it.
  Merge/survivorship configuration is not — a project's merge policy is a single
  mutable value with no retained history, so past golden-record outcomes can't be
  matched back to the exact policy that was live when they were computed if that
  policy has since changed. Same underlying gap as F51.

### Openness

- **S23** — Must be open source under a licence permitting commercial use.

  The product's source needs to be published under a license that doesn't just
  allow reading the code, but allows a business to actually use it commercially.

  **Status: Fully implemented.** The repository carries an Apache License 2.0
  `LICENSE` file, which permits commercial use.

- **S24** — Must not depend on any component that restricts commercial use.

  Beyond Linkuity's own license, none of its dependencies should carry a license
  that would restrict a customer's commercial use of the product built on them
  (e.g. a "copyleft" license requiring derivative works to also be open-sourced).

  **Status: Fully implemented, based on a quick check rather than an exhaustive
  audit.** The major, directly referenced dependencies (the PostgreSQL driver, the
  micro-ORM used for data access, the schema-migration tool, the embedded search
  library, the CSV parser, and the core .NET framework libraries) all carry
  permissive licenses (MIT, Apache 2.0, or the PostgreSQL license) — none are
  GPL/AGPL or otherwise commercially restrictive. This check covered the packages
  referenced directly in project files, not a full audit of every indirect,
  transitive dependency those packages themselves pull in.

### Running it day to day

- **S25** — Must report progress on long-running work.

  A long-running ingest or batch run shouldn't sit silent until it either finishes
  or fails — the person running it should be able to tell it's making progress.

  **Status: Not implemented.** Long-running CLI operations (a full `run`, an
  `ingest-incremental` with many chunks) currently print only a final summary once
  everything completes — no progress bar, percentage, record count, or periodic
  status line appears while the work is in flight.

- **S26** — Must record enough detail to diagnose a bad result after the fact.

  When something goes wrong — an unexpected merge, a run that behaved oddly —
  there should be enough of an operational record to work out why, after the fact,
  without having to reproduce the problem live.

  **Status: Partially implemented.** Where a decision is concerned, the detail is
  genuinely excellent — the per-signal match-decision breakdown (F32) is exactly
  the kind of record needed to diagnose a specific bad match. General operational
  logging, though, is nearly absent: across the entire solution there is
  essentially one meaningful application log statement outside of the match-
  decision data itself. The CLI — the primary way the product is run day to day —
  is wired to discard its logging output entirely, so a CLI-driven run produces no
  operational log trail beyond whatever it prints to the console.

---

## Decisions taken

Recorded so the reasoning survives into the expanded requirements.

**Erasure beats history.** Where keeping a full record of change collides with erasing a
person on request, erasure wins. `S17` overrides `F30`.

**The review queue warns, it does not cap.** A customer can say how large the queue may
grow before being told about it, but processing continues either way. There is no default
threshold. Silently dropping items was ruled out: an item that never reaches the queue is
a decision nobody made. `F27`.

**Onboarding starts rough and tightens itself.** A customer is not required to supply
confirmed examples before Linkuity produces anything. It starts on shipped defaults set to
under-merge, the reviewers' decisions become the confirmed examples, and the configuration
is re-measured from those. The cost accepted here is that the first review queue is larger
and noisier than a measured configuration would produce. `F44`–`F48`.

**A reversal reaches our own data, plus a record of what it touched.** Undoing a wrong
merge inside Linkuity, and being able to say exactly what went out and when, is ours.
Acting on that in other systems is the customer's — informed by `F49` now, and by `F37`
hooks later. Not chosen: pushing reversals downstream ourselves, which would have pulled
`F37` into the first release. `F49`, `F50`.

**Arrival order must not change the outcome, in full.** Both groupings and trusted-record
values. This is stronger than guaranteeing groupings alone, and deliberately so: it forces
every value-selection rule to be stated explicitly rather than falling out of timing. It is
also directly testable — load the same data twice in different orders and compare. `F54`.

**Source preference versus recency is the customer's call, per field.** Some fields are
authoritative by source, some by recency, and no single rule fits both. `F22` carries that
choice and `F52` defers to it.

---

## Open questions

None. Every requirement above is accepted. The gap between what's decided here and what's
built is now tracked requirement-by-requirement in the Status lines above, not left open.
