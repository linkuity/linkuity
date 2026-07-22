# Tutorial: Build a durable MDM project from scratch with the CLI

> New to how matching works under the hood? Read [`docs/how-matching-works.md`](../how-matching-works.md) first for the concepts (blocking, scoring, decisions, merging, tuning); this tutorial is hands-on.

Welcome! This is a hands-on, beginner-friendly walkthrough. By the end you'll have used the
Linkuity command-line tool to build a small **durable MDM project**, watch it merge duplicate
customers automatically, send an uncertain match to a review queue, and inspect the results —
including a record's full history. An optional last section shows the same data as a graph in
Neo4j.

You don't need to know anything about Linkuity's internals to follow along. Just copy the
commands, read the explanations, and watch what happens.

## What is "MDM", in plain words?

Lots of systems store information about the same person. Your CRM knows "Ada Lovelace", and so
does your marketing tool — but the email might be spelled differently, or the phone number
might be newer in one system than the other. **Master Data Management (MDM)** is the job of
spotting that these records describe the *same* person and combining them into one trusted
record — a **golden record**.

Two terms you'll see throughout:

- **Cluster** — a group of source records that Linkuity believes are the same real-world person.
- **Golden record** — the single, merged "best" version built from a cluster.

### What does "durable" mean here?

A one-shot batch run reads a file, produces golden records, and forgets everything. A
**durable** project is different: it *remembers*. It keeps a small database of everything it
has seen, so when you add more data next week, it matches the new records against what already
exists and **updates** the golden records instead of starting over. That "remembering" is what
makes incremental, real-world MDM possible — and it's what this tutorial builds.

## Prerequisites

For the main tutorial you only need:

- **.NET SDK 10.0** or newer (check with `dotnet --version`).
- This repository, cloned locally. All commands below are run from the repository root.

The durable commands do all their matching inside .NET — no database server, no external
services, no Docker required. (The **optional** Neo4j appendix at the end needs Neo4j; it's
clearly marked.)

## The data you'll use

This tutorial ships with a tiny, ready-made dataset under
`docs/tutorials/cli-durable-mdm-quickstart/data/`. There are two sources — a CRM export and a
marketing export — designed so you'll see every interesting outcome.

`crm.csv` — four customers, all different people:

```csv
id,source,first_name,last_name,email,phone
crm-1,CRM,Ada,Lovelace,ada@analytical.example,555-0101
crm-2,CRM,Grace,Hopper,grace@navy.example,555-0102
crm-3,CRM,Alan,Turing,alan@bletchley.example,555-0103
crm-4,CRM,Katherine,Johnson,katherine@nasa.example,555-0104
```

`marketing.csv` — three customers, each chosen to trigger a different behavior:

```csv
id,source,first_name,last_name,email,phone
mkt-1,Marketing,Ada,Lovelace,ada@analytical.example,555-9001
mkt-2,Marketing,Thomas,Hopper,thomas@hopper-mail.example,555-7777
mkt-3,Marketing,Margaret,Hamilton,margaret@apollo.example,555-3333
```

A couple of column notes: the `id` column is each record's identifier within its own source,
and the `source` column tells Linkuity where the row came from (this drives the merge rules
below). Every other column is regular data.

## A note on the commands

The CLI is invoked as `dotnet run --project src/Linkuity.Cli -- <command> ...`. That's a lot to
retype, so first define a short helper in your terminal. **Run everything below in the same
terminal session** so the helper and your captured IDs stick around.

> **PowerShell tip:** don't name the helper `cli` — that's a built-in alias. We use `linkuity`.

```powershell
# PowerShell
function linkuity { dotnet run --project src/Linkuity.Cli -- @args }
```

```bash
# bash
linkuity() { dotnet run --project src/Linkuity.Cli -- "$@"; }
```

## Step 1 — Set up a working folder

Your durable project lives in a single metadata file. Pick a scratch location for it, and point
a variable at the bundled data folder so the commands stay short.

```powershell
# PowerShell
$work = "$env:TEMP\linkuity-tutorial"
New-Item -ItemType Directory -Force $work | Out-Null
$metadata = "$work\metadata.json"
$data = "docs/tutorials/cli-durable-mdm-quickstart/data"
```

```bash
# bash
work="${TMPDIR:-/tmp}/linkuity-tutorial"
mkdir -p "$work"
metadata="$work/metadata.json"
data="docs/tutorials/cli-durable-mdm-quickstart/data"
```

That `metadata.json` file *is* your durable database. Everything you do from here on is stored
there, and it survives between commands — that's the whole point of "durable".

## Step 2 — Create the project

```powershell
# PowerShell
$projectId = (linkuity project create --metadata $metadata --name "Customer 360" --content-type person --merge-policy "$data/merge-policy.json").Trim()
$projectId
```

```bash
# bash
projectId=$(linkuity project create --metadata "$metadata" --name "Customer 360" --content-type person --merge-policy "$data/merge-policy.json")
echo "$projectId"
```

The command prints a new **project ID** (a long GUID). We capture it because almost every later
command needs it.

Two options to understand:

- `--content-type person` tells Linkuity these records describe people (as opposed to
  organizations).
- `--merge-policy` points at a small rules file that decides, when two records disagree, which
  source wins for each field. Here's the policy this tutorial uses:

  ```json
  {
    "mergeFields": [
      { "fieldName": "email", "sourcePriority": ["CRM", "Marketing"] },
      { "fieldName": "phone", "sourcePriority": ["Marketing", "CRM"] }
    ]
  }
  ```

  In plain terms: for **email**, trust the CRM first; for **phone**, trust Marketing first
  (imagine marketing has the most up-to-date phone numbers). Any field not listed is decided by
  simple consensus. Keep this rule in mind — you'll see it take effect in Step 6.

## Step 3 — Add your first source and load it

A *source* is just a named origin for data, like "CRM". Create one, then register an *ingest
batch* (one delivery of records from that source), then load the CSV into it.

```powershell
# PowerShell
$crmSource = (linkuity source create --metadata $metadata --project-id $projectId --name "CRM").Trim()
$crmBatch  = (linkuity batch create --metadata $metadata --project-id $projectId --source-id $crmSource --record-count 4).Trim()
linkuity ingest-incremental --metadata $metadata --project-id $projectId --source-id $crmSource --batch-id $crmBatch --input "$data/crm.csv"
```

```bash
# bash
crmSource=$(linkuity source create --metadata "$metadata" --project-id "$projectId" --name "CRM")
crmBatch=$(linkuity batch create --metadata "$metadata" --project-id "$projectId" --source-id "$crmSource" --record-count 4)
linkuity ingest-incremental --metadata "$metadata" --project-id "$projectId" --source-id "$crmSource" --batch-id "$crmBatch" --input "$data/crm.csv"
```

The ingest command prints a short summary:

```text
Records added: 4
Auto matches: 0
Review tasks: 0
Singleton clusters: 4
Golden versions created: 4
```

Because the project was empty, there was nothing for these four to match against, so each
became its own one-record cluster (a **singleton**) with its own golden record. That's expected
for the very first load.

> **Good to know:** within a single batch, records are matched against what's *already stored*,
> not against each other. So your first load always lands as singletons — duplicates get caught
> as later batches arrive, which is exactly what we'll do next.

## Step 4 — Review what you have so far

Two read-back commands let you inspect the project. They print CSV to your screen.

```powershell
# PowerShell
linkuity golden list  --metadata $metadata --project-id $projectId
linkuity cluster list --metadata $metadata --project-id $projectId
```

```bash
# bash
linkuity golden list  --metadata "$metadata" --project-id "$projectId"
linkuity cluster list --metadata "$metadata" --project-id "$projectId"
```

`golden list` shows the merged records (your cluster IDs will differ):

```text
cluster_id,version,record_count,member_ids,email,first_name,last_name,phone
048dbc1f-...,1,1,crm-1,ada@analytical.example,Ada,Lovelace,555-0101
65620f75-...,1,1,crm-2,grace@navy.example,Grace,Hopper,555-0102
d5d10a21-...,1,1,crm-4,katherine@nasa.example,Katherine,Johnson,555-0104
f004eeb1-...,1,1,crm-3,alan@bletchley.example,Alan,Turing,555-0103
```

Read it as: four golden records, each at **version 1**, each containing one source record
(`member_ids` shows `crm-1`, `crm-2`, …). Nothing has been merged yet.

## Step 5 — Add a second source (the interesting part)

Now load the marketing data. We pass two thresholds that control how confident a match must be:

- `--auto-threshold 0.90` — at or above this score, link automatically.
- `--review-threshold 0.75` — between the two thresholds, don't link; flag it for a human.
  Below this, treat it as a brand-new record.

```powershell
# PowerShell
$mktSource = (linkuity source create --metadata $metadata --project-id $projectId --name "Marketing").Trim()
$mktBatch  = (linkuity batch create --metadata $metadata --project-id $projectId --source-id $mktSource --record-count 3).Trim()
linkuity ingest-incremental --metadata $metadata --project-id $projectId --source-id $mktSource --batch-id $mktBatch --input "$data/marketing.csv" --auto-threshold 0.90 --review-threshold 0.75
```

```bash
# bash
mktSource=$(linkuity source create --metadata "$metadata" --project-id "$projectId" --name "Marketing")
mktBatch=$(linkuity batch create --metadata "$metadata" --project-id "$projectId" --source-id "$mktSource" --record-count 3)
linkuity ingest-incremental --metadata "$metadata" --project-id "$projectId" --source-id "$mktSource" --batch-id "$mktBatch" --input "$data/marketing.csv" --auto-threshold 0.90 --review-threshold 0.75
```

The summary now tells a richer story:

```text
Records added: 3
Auto matches: 1
Review tasks: 1
Singleton clusters: 2
Golden versions created: 3
```

Here's what each marketing record did:

- **`mkt-1` (Ada Lovelace)** has the *same email* as `crm-1`. An exact email match is strong
  evidence, so it scored above 0.90 and was **auto-linked** into Ada's existing cluster.
- **`mkt-2` (Thomas Hopper)** shares only a *last name* with `crm-2` (Grace Hopper) — different
  first name, different email, different phone. That's a weak, ambiguous match (score 0.80), so
  instead of guessing, Linkuity created a **review task** for a human to decide. Until someone
  decides, Thomas stands on his own (one of the two new singletons).
- **`mkt-3` (Margaret Hamilton)** matches nobody, so she became a clean new record (the other
  singleton).

## Step 6 — Review again, and look at history

Let's see how the picture changed.

```powershell
# PowerShell
linkuity golden list --metadata $metadata --project-id $projectId
```

```bash
# bash
linkuity golden list --metadata "$metadata" --project-id "$projectId"
```

```text
cluster_id,version,record_count,member_ids,email,first_name,last_name,phone
048dbc1f-...,2,2,crm-1|mkt-1,ada@analytical.example,Ada,Lovelace,555-9001
5600c0c9-...,1,1,mkt-3,margaret@apollo.example,Margaret,Hamilton,555-3333
65620f75-...,1,1,crm-2,grace@navy.example,Grace,Hopper,555-0102
c37fc433-...,1,1,mkt-2,thomas@hopper-mail.example,Thomas,Hopper,555-7777
d5d10a21-...,1,1,crm-4,katherine@nasa.example,Katherine,Johnson,555-0104
f004eeb1-...,1,1,crm-3,alan@bletchley.example,Alan,Turing,555-0103
```

Look at the first row: Ada's golden record now has **two members** (`crm-1|mkt-1`), is at
**version 2**, and her phone changed to `555-9001`. That's the merge policy at work — we said
phone should prefer Marketing, and the marketing record had the newer number.

### The review queue

```powershell
# PowerShell
linkuity review list --metadata $metadata --project-id $projectId
```

```bash
# bash
linkuity review list --metadata "$metadata" --project-id "$projectId"
```

```text
new_entity_record_id,candidate_entity_record_id,score,reason,status
3fe31aba-...,4faba283-...,0.8,review_threshold,open
```

This is the Thomas-vs-Grace Hopper decision waiting for a human: `score` 0.80, `reason`
`review_threshold` (it landed in the uncertain band), `status` `open`. The two IDs are
Linkuity's internal record identifiers for the new record and the candidate it might match.

### A record's history

Because the project is durable, every change to a golden record is versioned. Pick the merged
Ada cluster's ID from the `golden list` output above and ask for its history:

```powershell
# PowerShell — replace the GUID with your merged cluster's cluster_id
linkuity golden history --metadata $metadata --project-id $projectId --cluster-id 048dbc1f-6555-4d3c-aa7d-49f58934cf16
```

```bash
# bash — replace the GUID with your merged cluster's cluster_id
linkuity golden history --metadata "$metadata" --project-id "$projectId" --cluster-id 048dbc1f-6555-4d3c-aa7d-49f58934cf16
```

```text
cluster_id,version,created_at,email,first_name,last_name,phone
048dbc1f-...,1,2026-...,ada@analytical.example,Ada,Lovelace,555-0101
048dbc1f-...,2,2026-...,ada@analytical.example,Ada,Lovelace,555-9001
```

There's the audit trail: version 1 was Ada from CRM alone (phone `555-0101`); version 2 is the
merged record after the marketing data arrived (phone `555-9001`). Nothing was overwritten and
lost — the history is preserved.

## Step 7 — Visualizing your results (with the CLI)

You've actually been visualizing all along — the read-back commands *are* the view into your
durable store:

- **`cluster list`** answers *"which records were grouped together?"*
- **`golden list`** answers *"what is the single best version of each record?"*
- **`golden history`** answers *"how did this record change over time?"*
- **`review list`** answers *"what needs a human decision?"*

Tip: every read-back command also takes `--output <file.csv>` to save the table to a file (it
still prints to the screen too), so you can open the results in a spreadsheet.

That's the complete loop: **build** (create project, ingest sources), **review** (golden /
cluster / review / history), and **visualize** (read it back). Everything lives in your
`metadata.json`, ready for the next batch whenever it arrives.

---

## Appendix (optional) — See it as a graph in Neo4j

Sometimes a picture helps. Linkuity can produce a Neo4j-ready bundle so you can explore your
data as a graph of people, sources, emails, and the links between them.

> **Heads-up — this is a slightly different path.** The durable store doesn't export to Neo4j
> directly today. Instead we use Linkuity's one-shot `run` command on a combined CSV of the same
> records. It produces the same six clusters you built durably, just rendered as a graph.
> This step additionally needs **Neo4j**.

This tutorial ships a combined file (`data/combined.csv`, all seven records), a matching
profile (`data/people.profile.json`), and a merge policy (`data/people.merge.json`). Generate
the graph bundle:

```powershell
# PowerShell
linkuity run --input "$data/combined.csv" --profile "$data/people.profile.json" --merge-policy "$data/people.merge.json" --output "$work/neo4j-out" --neo4j-export
```

```bash
# bash
linkuity run --input "$data/combined.csv" --profile "$data/people.profile.json" --merge-policy "$data/people.merge.json" --output "$work/neo4j-out" --neo4j-export
```

You'll get a `neo4j-out/` folder containing `golden-records.csv` and `neo4j-export.zip`. Unzip
the bundle — it contains node and relationship CSVs plus a ready-to-run `load.cypher` script:

```text
entities.csv  golden-records.csv  sources.csv  emails.csv  phones.csv
matched-to.csv  resolved-to.csv  from-source.csv  has-email.csv  has-phone.csv
load.cypher
```

To load it:

1. Start Neo4j (Neo4j Desktop or a Docker container).
2. Copy the unzipped CSV files into that database's `import/` directory.
3. Open Neo4j Browser and run the contents of `load.cypher` (it creates the constraints, nodes,
   and relationships).
4. Explore. For example, to see entities resolved to their golden records:

   ```cypher
   MATCH (e:Entity)-[:RESOLVED_TO]->(g:GoldenRecord)
   RETURN e, g
   ```

You'll see Ada's two source records pointing at a single golden record, while the Hoppers stay
separate — the same outcome you reached with the CLI, now as a graph.

For a deeper, graph-focused sample (with more data and example queries), see
[`samples/people-multi-source/README.md`](../../samples/people-multi-source/README.md).

---

## Recap & next steps

You built a durable MDM project from nothing: created a project with merge rules, loaded two
sources, watched Linkuity auto-merge a confident duplicate, route an uncertain one to review,
and keep a brand-new record on its own — then reviewed and visualized all of it, including full
version history.

Where to go next:

- [`samples/`](../../samples/) — more worked scenarios (multi-source merges, name noise, phone
  noise) with detailed walkthroughs.
- [`docs/architecture.md`](../architecture.md) — how the pieces fit together.
- [`docs/private-runtime.md`](../private-runtime.md) — Linkuity's private-runtime direction and
  what's planned next.
