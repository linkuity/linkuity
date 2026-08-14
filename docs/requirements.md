# Linkuity — Product Requirements

> **Status: draft list.** The requirements are listed but not yet expanded. Open
> questions are recorded at the end and must be settled before expansion begins.

**Scope:** this describes the product as it must eventually be, not version 1. Some
items will not be built for a long time. Deciding what comes first is a separate
exercise and does not belong here.

**What the product does, in one sentence:** Linkuity takes records from several
systems, works out which of them describe the same real-world thing, and produces one
trusted version of that thing — and keeps doing it as the data changes.

Throughout, **entity** means the real-world thing: a customer, a company, a product.

Requirement numbers are identifiers, not an order. New requirements take the next free
number and sit in whichever section they belong to, so existing numbers never shift.

---

## Functional requirements

### Getting data in

- **F1** — Must accept records from files and from live system feeds.
- **F2** — Must accept records from many different source systems and remember which system each record came from.
- **F3** — Must accept new and changed records at any time, without reprocessing everything already loaded.
- **F4** — Must not require source systems to change their data, formats, or processes.
- **F5** — Must work with records that are missing fields.
- **F6** — Must handle records that are corrected or deleted at source.

### Data arriving late or out of order

- **F52** — Must not let an older version of a record overwrite a newer one in the trusted record, except where the customer has chosen source preference to win under F22.
- **F53** — Must decide which record is newer from a date the customer nominates, and must state what it does when a record carries no such date.
- **F54** — Must reach the same final state — both groupings and trusted-record values — regardless of the order in which records arrived.

### Deciding what matches

- **F7** — Must identify which records describe the same entity.
- **F8** — **Must never merge records that are not the same entity.**
- **F9** — Must give one of three answers for any pair: same, not the same, or needs a person to decide.
- **F10** — Must match values that are written differently but mean the same thing — abbreviations, typos, spacing, punctuation, name order.
- **F11** — Must treat filler values such as "UNKNOWN" or "N/A" as missing, not as a real value that two records can agree on.
- **F12** — Must work with data in any language and any alphabet.
- **F13** — Must find likely matches without comparing every record against every other record.
- **F14** — Must flag entities that are connected but not the same — same address, same owner — without merging them.

### Setting it up for a customer's data

- **F15** — Must be configurable for a new dataset without writing code.
- **F16** — Must measure, from the customer's own data, how useful each field is for matching.
- **F17** — Must tell the customer which of their records can never be told apart, and what extra information would separate them.
- **F18** — Must support different kinds of entity — people, companies, products — through the same mechanism, not special-case handling per kind.
- **F19** — Must let a customer test a configuration change before it affects live data.
- **F20** — Must warn before a configuration change would alter groupings that already exist.
- **F44** — Must produce a usable first result from a new dataset without anyone hand-writing a configuration.
- **F45** — Must ship a starting configuration for each kind of entity, set cautiously enough that it leaves duplicates unmerged rather than merging anything wrongly.
- **F46** — Must turn reviewers' decisions into the confirmed examples that F16 measures from, so the configuration improves as the queue is worked.
- **F47** — Must be operable by a data engineer. No statistical training may be assumed.
- **F48** — Must state up front what it needs from the customer, rather than discovering it partway through a project.

### Producing one trusted record

- **F21** — Must produce a single best version of each entity from all the records that describe it.
- **F22** — Must let the customer decide, for each field, which source system wins — and whether that preference or the newer value wins when the two disagree.
- **F23** — Must record where every value in the best version came from.

### Human review

- **F24** — Must give people a queue of uncertain matches to decide.
- **F25** — Must let a person confirm or reject a match, and must honour that decision permanently.
- **F26** — Must let a person separate records that were grouped together wrongly.
- **F27** — Must warn when the review queue grows beyond a size the customer sets. This is a warning, not a limit: the customer is told and processing continues. The default must be no threshold, and therefore no warning.
- **F41** — Must let a reviewer see the queue grouped by what its items have in common, so a shared underlying cause is visible rather than having to be worked out one item at a time.
- **F42** — Must let a reviewer correct the underlying data that put items in the queue, without changing the source system.
- **F43** — Must let a reviewer resubmit queued items for reprocessing after a correction, so every item the fix resolves clears without a separate manual decision.

### Staying correct as data changes

- **F28** — Must keep each entity's identifier stable over time, so downstream systems can rely on it.
- **F29** — Must update existing groupings when new data arrives — including splitting a group that turns out to be wrong.
- **F30** — Must keep a history of how each entity changed and why, except where erasure has been requested under S17.
- **F31** — Must be able to show the state of an entity as it was at an earlier date.

### Explaining itself, and answering for itself

- **F32** — Must explain why any two records were matched, in terms a non-technical person can follow.
- **F33** — Must explain why two records a user expected to match did not.
- **F34** — Must record who or what made each decision, and when.
- **F49** — Must let someone find everything a decision affected, once that decision turns out to be wrong — including what was sent out and when.
- **F50** — Must let a wrong merge be reversed. The reversal reaches Linkuity's own data; telling other systems is the customer's to do, using F49's record and, later, F37's hooks.
- **F51** — Must be able to reproduce a past decision exactly, using the configuration that was in force when it was made.

### Getting data out

- **F35** — Must export trusted records and their groupings in standard formats.
- **F36** — Must let another system ask "which entity does this record belong to" and get an answer immediately.
- **F37** — Must provide hooks the customer can use to notify other systems when an entity changes, including when a merge is reversed. Linkuity supplies the hook; the customer decides what it does and who it tells. *Not required for the first release.*

### Proving it works

- **F38** — Must be able to measure its own accuracy against a set of known-correct answers.
- **F39** — Must fail a run, not merely warn, when that run produces wrong merges.
- **F40** — Must report its accuracy in terms a business owner can act on.

---

## System requirements

### Where it runs

- **S1** — Must run entirely inside the customer's own environment. No customer data leaves it.
- **S2** — Must run on one laptop and on a production server using the same product, not two different builds.
- **S3** — Must run on Windows, Linux, and macOS.
- **S4** — Must not require a cloud account, subscription, or vendor-hosted service to function.

### Storage

- **S5** — Must store its data in PostgreSQL.
- **S6** — Must not require any additional database, search engine, or message broker to be installed.
- **S7** — Must not lose work in progress if the machine restarts.

### Scale and speed

- **S8** — Must handle tens of millions of records.
- **S9** — Must process new data in time proportional to the amount of new data, not the total already stored.
- **S10** — Must have predictable memory use regardless of how much data is stored.
- **S11** — Must state its limits openly rather than slowing down or degrading silently.

### Reliability

- **S12** — Must resume an interrupted run without duplicating or losing records.
- **S13** — Must produce the same result every time from the same data and the same configuration.
- **S14** — Must never lose a human's decision, under any failure.

### Security and privacy

- **S15** — Must control who can view data, who can make decisions, and who can change configuration.
- **S16** — Must keep an audit trail of every change to data and to configuration.
- **S17** — Must be able to permanently remove an individual's data on request. **This overrides F30:** history is kept except where erasure has been requested.

### How people and systems use it

- **S18** — Must be fully usable from a command line.
- **S19** — Must be usable by another program through an API.
- **S20** — Must be fully operable without a graphical interface.

### Change over time

- **S21** — Must upgrade to a new version without requiring everything to be reprocessed.
- **S22** — Must retain past configurations so that past results can still be explained.

### Openness

- **S23** — Must be open source under a licence permitting commercial use.
- **S24** — Must not depend on any component that restricts commercial use.

### Running it day to day

- **S25** — Must report progress on long-running work.
- **S26** — Must record enough detail to diagnose a bad result after the fact.

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

None. Every requirement above is accepted and ready to be expanded.
