# Tutorial: Build a durable product catalog MDM project with the CLI

> New to how matching works under the hood? Read [`docs/how-matching-works.md`](../how-matching-works.md) first for the concepts (blocking, scoring, decisions, merging, tuning); this tutorial is hands-on.

Welcome! This is a hands-on, beginner-friendly walkthrough. By the end you'll have used the
Linkuity command-line tool to build a small **durable product MDM project**, load product data
from three sources, watch confident matches merge automatically, send one uncertain match to a
review queue, and inspect the final trusted product catalog.

You don't need to know anything about Linkuity's internals to follow along. Copy the commands,
read the explanations, and watch how the catalog changes as each new source arrives.

## What is product MDM, in plain words?

Product data usually lives in many places. A catalog system might know the official product
name and category. A supplier feed might have the better price and warranty. A marketplace feed
might have the best image and description. Sometimes those rows describe the same real-world
product, but the names, IDs, or details do not line up perfectly.

**Product Master Data Management (MDM)** is the job of spotting when those records describe the
same product and combining them into one trusted product record - a **golden record**.

Two product terms you'll see throughout:

- **SKU** - a stock keeping unit, usually an internal or seller-specific product code.
- **GTIN** - a global trade item number, such as a UPC or EAN, meant to identify a product
  across systems.

Two Linkuity terms you'll see throughout:

- **Cluster** - a group of source records that Linkuity believes are the same real-world
  product.
- **Golden record** - the single, merged "best" version built from a cluster.

## What does "durable" mean here?

A one-shot batch run reads files, produces results, and forgets everything. A **durable**
project is different: it *remembers*. It keeps a durable store with the sources, batches,
source records, clusters, golden records, review tasks, and golden-record history it has seen.

That means the second and third ingests are matched against the product records already stored
in the project. Linkuity updates existing golden records instead of starting from scratch.

In this tutorial, the durable project also uses a product profile file:

`docs/tutorials/cli-durable-product-catalog/data/product.profile.json`

That profile tells Linkuity this is a **product** project and that fields like `sku`, `gtin`,
`product_name`, and `brand` should drive matching. This tutorial passes that profile on every
incremental ingest.

## Prerequisites

You need:

- **.NET SDK 10.0** or newer (check with `dotnet --version`).
- This repository, cloned locally.
- A terminal opened at the repository root.

**You can run this tutorial two ways, and you pick one in Step 1:**

- **File store (default)** - the durable project is a single local JSON file. No database,
  Docker, or Neo4j required.
- **PostgreSQL** - the exact same tutorial, stored in Postgres, the scalable durable backend.
  This option additionally needs **Docker** (to run a throwaway database).

The commands and the results are identical either way: choosing the backend is a one-line
change, and both stores produce the same golden records, versions, and review tasks. That
behavioral parity is guaranteed across backends.

## The data you'll use

This tutorial ships with a tiny, ready-made product dataset under
`docs/tutorials/cli-durable-product-catalog/data/`. There are three source files plus two
configuration files.

`catalog.csv` - the starting product catalog, with five products:

```csv
id,source,sku,gtin,product_name,brand,category,price,availability,warranty_months,image_url,description
cat-001,Catalog,ANK-20W-WHT,00810012345001,Anker 20W USB-C Charger,Anker,Chargers,19.99,In stock,18,,Compact wall charger for phones and tablets
cat-004,Catalog,DEL-U2724DE,00884116398011,Dell UltraSharp 27 USB-C Monitor,Dell,Monitors,529.99,Backorder,36,,27 inch USB-C hub monitor
```

`supplier.csv` - five supplier records. Three match catalog products, and two are new products:

```csv
id,source,sku,gtin,product_name,brand,category,price,availability,warranty_months,image_url,description
sup-101,Supplier,ANK-20W-WHT,00810012345002,Anker PowerPort 20W USB C Wall Adapter,Anker,Mobile Power,17.49,In stock,24,,Supplier listing for 20W Anker wall charger
sup-104,Supplier,SAM-T7-1TB-BLU,08872764107925,Samsung T7 Portable SSD 1TB Blue,Samsung,Storage,89.99,In stock,36,,Portable solid state drive
```

`marketplace.csv` - four marketplace records. Two match existing products, one becomes a new
product, and one is similar enough to need human review:

```csv
id,source,sku,gtin,product_name,brand,category,price,availability,warranty_months,image_url,description
mkt-201,Marketplace,ANK-20W-MP,00810012345001,Anker USB C Fast Charger 20W White,Anker,Phone Accessories,18.99,In stock,18,https://example.com/images/anker-20w-white.jpg,Fast compact charger with foldable plug
mkt-203,Marketplace,VLT-20W-WHT,00699900002001,20W USB-C Fast Wall Charger,VoltEdge,Phone Accessories,12.99,In stock,6,https://example.com/images/voltedge-20w.jpg,Budget 20W USB-C wall charger
```

Two configuration files control the behavior:

- `product.profile.json` tells Linkuity how to match product records. In this profile, `sku`
  and `gtin` are exact identifiers, while `product_name` and `brand` can use fuzzy matching.
- `merge-policy.json` tells Linkuity which source wins when matched records disagree on a
  field.

For example, the merge policy prefers:

- **Catalog** for `product_name`, `brand`, and `category`.
- **Supplier** for `price`, `availability`, and `warranty_months`.
- **Marketplace** for `image_url` and `description`.

Keep that in mind when you inspect the Anker charger later.

## A note on the commands

The CLI is invoked as `dotnet run --project src/Linkuity.Cli -- <command> ...`. That's a lot to
retype, so first define a short helper in your terminal. **Run everything below in the same
terminal session** so the helper and your captured IDs stick around.

> **PowerShell tip:** don't name the helper `cli` - that's a built-in alias. We use `linkuity`.

```powershell
# PowerShell
function linkuity { dotnet run --project src/Linkuity.Cli -- @args }
```

```bash
# bash
linkuity() { dotnet run --project src/Linkuity.Cli -- "$@"; }
```

> **Tip:** run `dotnet build src/Linkuity.Cli` once before you start. A few steps below capture a
> command's output into a variable (a project or source ID), and building first keeps that output
> clean.

## Step 1 - Set up a working folder and choose your store

First pick a scratch folder and point a variable at the bundled data folder so the commands stay
short:

```powershell
# PowerShell
$work = "$env:TEMP\linkuity-product-tutorial"
New-Item -ItemType Directory -Force $work | Out-Null
$data = "docs/tutorials/cli-durable-product-catalog/data"
```

```bash
# bash
work="${TMPDIR:-/tmp}/linkuity-product-tutorial"
mkdir -p "$work"
data="docs/tutorials/cli-durable-product-catalog/data"
```

Now **choose one of two durable stores**. Every command from here on uses a `$store` variable, so
you pick your backend once - right here - and the rest of the tutorial is byte-for-byte identical
either way.

### Option A - Local file store (default, no database)

Your durable project lives in a single JSON file. Nothing else to install.

```powershell
# PowerShell
$store = @("--metadata", "$work\metadata.json")
```

```bash
# bash
store=(--metadata "$work/metadata.json")
```

### Option B - PostgreSQL (the scalable backend)

The same tutorial, stored in PostgreSQL instead. You need Docker. Start a throwaway database,
then point `$store` at it - both the `linkuity` database and its schema are created automatically
on your first command.

We map the container to host port **55432** (not Postgres's default 5432) so it won't clash with
any PostgreSQL you might already be running locally. The leading `docker rm -f` clears any
leftover container from a previous run.

```powershell
# PowerShell
docker rm -f linkuity-pg 2>$null
docker run -d --name linkuity-pg -e POSTGRES_PASSWORD=postgres -p 55432:5432 postgres:16-alpine
$cs = "Host=localhost;Port=55432;Database=linkuity;Username=postgres;Password=postgres"
$store = @("--metadata-store", "postgres", "--connection-string", $cs, "--index-dir", "$work\pg-index")
```

```bash
# bash
docker rm -f linkuity-pg 2>/dev/null
docker run -d --name linkuity-pg -e POSTGRES_PASSWORD=postgres -p 55432:5432 postgres:16-alpine
cs="Host=localhost;Port=55432;Database=linkuity;Username=postgres;Password=postgres"
store=(--metadata-store postgres --connection-string "$cs" --index-dir "$work/pg-index")
```

When you finish the Postgres run, stop and remove the database with `docker rm -f linkuity-pg`.

> **Troubleshooting.** If `docker run` prints *port is already allocated*, something else is on
> port 55432 - pick another number and change it in both the `-p` mapping and the connection
> string. If a command reports *database "linkuity" does not exist*, your connection string is
> pointing at a different PostgreSQL than the container (usually a port clash); the CLI creates
> the `linkuity` database on demand, so this almost always means the connection is going to the
> wrong server.

Whichever store you chose, everything you do from here is saved in it and survives between
commands. In every command below, `@store` (PowerShell) / `"${store[@]}"` (bash) expands to the
backend flags you just set.

## Step 2 - Create the project

```powershell
# PowerShell
$projectId = (linkuity project create @store --name "Product 360" --content-type product --merge-policy "$data/merge-policy.json").Trim()
$projectId
```

```bash
# bash
projectId=$(linkuity project create "${store[@]}" --name "Product 360" --content-type product --merge-policy "$data/merge-policy.json")
echo "$projectId"
```

The command prints a new **project ID** (a long GUID). We capture it because almost every later
command needs it.

Two options to understand:

- `--content-type product` tells Linkuity these records describe products.
- `--merge-policy "$data/merge-policy.json"` points at the source-priority rules. Those rules
  decide which source wins for each field when records merge.

## Step 3 - Add the Catalog source and load it

A *source* is a named origin for data, like "Catalog". Create one, register an *ingest batch*
(one delivery of records from that source), then load the CSV into it.

```powershell
# PowerShell
$catalogSource = (linkuity source create @store --project-id $projectId --name "Catalog").Trim()
$catalogBatch  = (linkuity batch create @store --project-id $projectId --source-id $catalogSource --record-count 5).Trim()
linkuity ingest-incremental @store --project-id $projectId --source-id $catalogSource --batch-id $catalogBatch --input "$data/catalog.csv" --profiles "$data/product.profile.json"
```

```bash
# bash
catalogSource=$(linkuity source create "${store[@]}" --project-id "$projectId" --name "Catalog")
catalogBatch=$(linkuity batch create "${store[@]}" --project-id "$projectId" --source-id "$catalogSource" --record-count 5)
linkuity ingest-incremental "${store[@]}" --project-id "$projectId" --source-id "$catalogSource" --batch-id "$catalogBatch" --input "$data/catalog.csv" --profiles "$data/product.profile.json"
```

The ingest command prints a short summary:

```text
Records added: 5
Auto matches: 0
Review tasks: 0
Singleton clusters: 5
Golden versions created: 5
```

Because the project was empty, there was nothing for these five records to match against. Each
catalog row became its own one-record cluster with its own golden record.

## Step 4 - Review the catalog records

Two read-back commands let you inspect the project. They print CSV to your screen.

```powershell
# PowerShell
linkuity golden list  @store --project-id $projectId
linkuity cluster list @store --project-id $projectId
```

```bash
# bash
linkuity golden list  "${store[@]}" --project-id "$projectId"
linkuity cluster list "${store[@]}" --project-id "$projectId"
```

`golden list` shows the merged records. Your `cluster_id` values will differ:

```text
cluster_id,version,record_count,member_ids,availability,brand,category,description,gtin,image_url,price,product_name,sku,warranty_months
0c62f4d1-...,1,1,cat-001,In stock,Anker,Chargers,Compact wall charger for phones and tablets,00810012345001,,19.99,Anker 20W USB-C Charger,ANK-20W-WHT,18
57e3c0c2-...,1,1,cat-004,Backorder,Dell,Monitors,27 inch USB-C hub monitor,00884116398011,,529.99,Dell UltraSharp 27 USB-C Monitor,DEL-U2724DE,36
```

Read it as: five golden records, each at **version 1**, each containing one source record.
Nothing has been merged yet.

## Step 5 - Add the Supplier source

Now load the supplier feed. This source has three records that should match catalog products:
Anker, Logitech, and Sony. It also has two new products: Samsung and Ubiquiti.

```powershell
# PowerShell
$supplierSource = (linkuity source create @store --project-id $projectId --name "Supplier").Trim()
$supplierBatch  = (linkuity batch create @store --project-id $projectId --source-id $supplierSource --record-count 5).Trim()
linkuity ingest-incremental @store --project-id $projectId --source-id $supplierSource --batch-id $supplierBatch --input "$data/supplier.csv" --profiles "$data/product.profile.json"
```

```bash
# bash
supplierSource=$(linkuity source create "${store[@]}" --project-id "$projectId" --name "Supplier")
supplierBatch=$(linkuity batch create "${store[@]}" --project-id "$projectId" --source-id "$supplierSource" --record-count 5)
linkuity ingest-incremental "${store[@]}" --project-id "$projectId" --source-id "$supplierSource" --batch-id "$supplierBatch" --input "$data/supplier.csv" --profiles "$data/product.profile.json"
```

The summary now shows matching:

```text
Records added: 5
Auto matches: 3
Review tasks: 0
Singleton clusters: 2
Golden versions created: 5
```

Here's what happened:

- `sup-101` matched the Anker charger from Catalog. The SKU is the same, even though the GTIN
  and product name are different.
- `sup-102` matched the Logitech keyboard by GTIN.
- `sup-103` matched the Sony headphones by GTIN.
- `sup-104` and `sup-105` did not match anything already stored, so each became a new
  singleton cluster.

## Step 6 - Review merged products and source priority

Read the golden records again:

```powershell
# PowerShell
linkuity golden list @store --project-id $projectId
```

```bash
# bash
linkuity golden list "${store[@]}" --project-id "$projectId"
```

After the supplier load, you still have seven golden records: the five original catalog
products, plus the two new supplier-only products. Three of the original products now have two
members.

Look for the Anker charger row:

```text
cluster_id,version,record_count,member_ids,availability,brand,category,description,gtin,image_url,price,product_name,sku,warranty_months
0c62f4d1-...,2,2,cat-001|sup-101,In stock,Anker,Chargers,Compact wall charger for phones and tablets,00810012345001,,17.49,Anker 20W USB-C Charger,ANK-20W-WHT,24
```

This row shows the merge policy in action:

- `product_name` and `category` stayed from **Catalog**.
- `price`, `availability`, and `warranty_months` came from **Supplier**.
- `image_url` is still empty because neither Catalog nor Supplier has one.

That is source priority: when matched records disagree, the merge policy chooses the trusted
source for each field instead of blindly taking the newest value.

## Step 7 - Add the Marketplace source

Now load the marketplace feed. This source has four records:

- `mkt-201` should match the Anker charger.
- `mkt-202` should match the Dell monitor.
- `mkt-203` is a VoltEdge charger that looks similar to Anker, but is not the same product.
- `mkt-204` is a new Logitech webcam.

```powershell
# PowerShell
$marketplaceSource = (linkuity source create @store --project-id $projectId --name "Marketplace").Trim()
$marketplaceBatch  = (linkuity batch create @store --project-id $projectId --source-id $marketplaceSource --record-count 4).Trim()
linkuity ingest-incremental @store --project-id $projectId --source-id $marketplaceSource --batch-id $marketplaceBatch --input "$data/marketplace.csv" --profiles "$data/product.profile.json"
```

```bash
# bash
marketplaceSource=$(linkuity source create "${store[@]}" --project-id "$projectId" --name "Marketplace")
marketplaceBatch=$(linkuity batch create "${store[@]}" --project-id "$projectId" --source-id "$marketplaceSource" --record-count 4)
linkuity ingest-incremental "${store[@]}" --project-id "$projectId" --source-id "$marketplaceSource" --batch-id "$marketplaceBatch" --input "$data/marketplace.csv" --profiles "$data/product.profile.json"
```

The summary:

```text
Records added: 4
Auto matches: 2
Review tasks: 1
Singleton clusters: 2
Golden versions created: 4
```

Here's the important part:

- `mkt-201` auto-matched the Anker charger by GTIN.
- `mkt-202` auto-matched the Dell monitor by GTIN.
- `mkt-203` looked similar to the Anker charger, but not confident enough to merge. It became
  its own product and created one review task.
- `mkt-204` became a new product.

## Step 8 - Review the queue and history

First, list the final golden records:

```powershell
# PowerShell
linkuity golden list @store --project-id $projectId
```

```bash
# bash
linkuity golden list "${store[@]}" --project-id "$projectId"
```

The final golden list has **9 data rows**. That means the project now has nine product
clusters after all three ingests.

The Anker charger has three source members:

```text
member_ids
cat-001|mkt-201|sup-101
```

Its final golden record shows the source priority working across all three sources:

- **Catalog** keeps `product_name` (`Anker 20W USB-C Charger`) and `category` (`Chargers`).
- **Supplier** keeps `price` (`17.49`), `availability` (`In stock`), and `warranty_months`
  (`24`).
- **Marketplace** keeps `image_url` and `description`.

The Dell monitor has two source members:

```text
member_ids
cat-004|mkt-202
```

The VoltEdge charger is separate:

```text
member_ids
mkt-203
```

That is correct. It is a lookalike product, not the same product as the Anker charger.

Now inspect the review queue:

```powershell
# PowerShell
linkuity review list @store --project-id $projectId
```

```bash
# bash
linkuity review list "${store[@]}" --project-id "$projectId"
```

There is exactly one open review task:

```text
new_entity_record_id,candidate_entity_record_id,score,reason,status
..., ...,0.8,review_threshold,open
```

That row is the VoltEdge charger lookalike against the Anker charger. Its score is **0.80**:
similar enough to ask for a human decision, but below the automatic-match threshold.

Finally, inspect the Anker golden-record history. Copy the Anker `cluster_id` from
`golden list`, then run:

```powershell
# PowerShell - replace the GUID with your Anker cluster_id
linkuity golden history @store --project-id $projectId --cluster-id 0c62f4d1-0000-0000-0000-000000000000
```

```bash
# bash - replace the GUID with your Anker cluster_id
linkuity golden history "${store[@]}" --project-id "$projectId" --cluster-id 0c62f4d1-0000-0000-0000-000000000000
```

You should see **3 versions**:

```text
cluster_id,version,created_at,availability,brand,category,description,gtin,image_url,price,product_name,sku,warranty_months
0c62f4d1-...,1,2026-...,In stock,Anker,Chargers,Compact wall charger for phones and tablets,00810012345001,,19.99,Anker 20W USB-C Charger,ANK-20W-WHT,18
0c62f4d1-...,2,2026-...,In stock,Anker,Chargers,Compact wall charger for phones and tablets,00810012345001,,17.49,Anker 20W USB-C Charger,ANK-20W-WHT,24
0c62f4d1-...,3,2026-...,In stock,Anker,Chargers,Fast compact charger with foldable plug,00810012345001,https://example.com/images/anker-20w-white.jpg,17.49,Anker 20W USB-C Charger,ANK-20W-WHT,24
```

Read that as:

- Version 1 came from Catalog: price `19.99`, no image.
- Version 2 added Supplier: price changed to `17.49`, still no image.
- Version 3 added Marketplace: price stayed `17.49`, and the marketplace image arrived.

Nothing was overwritten and lost. The durable project kept the history of how the trusted
product record changed over time.

## Step 9 - Explore the durable store with SQL (PostgreSQL only)

This step only applies if you chose **Option B (PostgreSQL)** in Step 1. On the File store your
project is a JSON file; on PostgreSQL the exact same data lives in real, queryable tables, so you
can inspect the golden records, clusters, history, and review queue directly with SQL - a good
way to build intuition for what the durable store actually keeps.

Open a `psql` shell inside the database container you started earlier:

```bash
docker exec -it linkuity-pg psql -U postgres -d linkuity
```

Two things to know as you read the queries below:

- **Per-record data is stored in `jsonb` columns.** Pull a field out with `fields->>'product_name'`.
- **Cluster membership is the `entity_records.cluster_id` foreign key**, not a list on the
  cluster. To see which source records make up a product, join `entity_records` to the cluster.

### See what the project holds

```sql
-- Purpose: a quick census of the durable store after all three ingests.
select 'entity_records' as tbl, count(id) from entity_records
union all select 'clusters',       count(id) from clusters
union all select 'golden_records', count(id) from golden_records
union all select 'review_tasks',   count(id) from review_tasks;
-- You should see: 14 entity_records (5 + 5 + 4 across the three sources), 9 clusters and
-- 9 golden_records (one trusted product each), and 1 review_task (the VoltEdge lookalike).
```

### The trusted catalog (this is `golden list`, in SQL)

```sql
-- Purpose: the merged "best" record for every product, with its current version number and how
-- many source records were combined into it. The join to clusters filters out any merged-away
-- (tombstoned) clusters, exactly like the CLI's `golden list`.
select g.fields->>'product_name' as product,
       g.fields->>'brand'        as brand,
       g.fields->>'sku'          as sku,
       g.fields->>'price'        as price,
       v.version_number          as version,
       count(er.id)              as members
from golden_records g
join clusters c               on c.id = g.cluster_id and c.status <> 'merged'
join golden_record_versions v on v.id = g.current_version_id
left join entity_records er   on er.cluster_id = g.cluster_id
group by g.id, product, brand, sku, price, version
order by product;
-- You should see: 9 products. The Anker 20W USB-C Charger has 3 members (one per source) and is
-- at version 3 with price 17.49 - the Supplier price the merge policy preferred. The other rows
-- are single-source products at version 1.
```

### Who is inside each product

```sql
-- Purpose: expand every cluster into its source records, so you can see exactly which rows
-- Linkuity judged to be the same product, and which source each came from. Note that
-- entity_records.fields keeps the raw per-source values (including `source`), whereas the golden
-- record above holds the single merged value.
select g.fields->>'product_name' as product,
       er.fields->>'source'      as source,
       er.source_record_id       as source_record,
       er.fields->>'price'       as source_price
from golden_records g
join entity_records er on er.cluster_id = g.cluster_id
order by product, source;
-- You should see: the Anker charger backed by cat-001 (Catalog), sup-101 (Supplier) and
-- mkt-201 (Marketplace) with three different source prices, while its golden record kept the one
-- trusted 17.49. The VoltEdge charger stands alone as mkt-203 - correctly NOT merged into Anker.
```

### The history of one product

```sql
-- Purpose: golden records keep every version, so you can see how a trusted record evolved as new
-- sources arrived. Nothing is overwritten and lost.
select gv.version_number as version,
       gv.fields->>'price'     as price,
       gv.fields->>'image_url' as image_url,
       gv.created_at
from golden_record_versions gv
where gv.fields->>'product_name' like 'Anker%'
order by gv.version_number;
-- You should see: 3 rows for the Anker charger - v1 price 19.99 with no image (Catalog only),
-- v2 price 17.49 still no image (Supplier arrived), v3 price 17.49 with the Marketplace image.
```

### The review queue, resolved to product names

```sql
-- Purpose: the uncertain matches a human should decide on, joined back to the actual products on
-- each side so the row is readable.
select rt.score, rt.reason, rt.status,
       nr.fields->>'product_name' as new_product, nr.source_record_id as new_record,
       cr.fields->>'product_name' as candidate,    cr.source_record_id as candidate_record
from review_tasks rt
join entity_records nr on nr.id = rt.new_entity_record_id
join entity_records cr on cr.id = rt.candidate_entity_record_id;
-- You should see: 1 open task at score 0.80 - the VoltEdge "20W USB-C Fast Wall Charger"
-- (mkt-203) against the Anker charger: similar enough to flag, below the 0.90 auto-merge bar.
```

### Why records matched (explainability)

```sql
-- Purpose: the confident links Linkuity made, with a per-signal breakdown of how each score was
-- built. This is the data behind the CLI's `match explain`; `jsonb_array_elements` unpacks the
-- stored breakdown array into one row per contributing signal.
select l.source_record_id as left_rec, r.source_record_id as right_rec, me.score,
       f->>'Signal' as signal, f->>'Value' as value,
       f->>'Weight' as weight, f->>'Contribution' as contribution
from match_edges me
join entity_records l on l.id = me.left_entity_record_id
join entity_records r on r.id = me.right_entity_record_id
cross join lateral jsonb_array_elements(me.breakdown) as f
order by me.score desc;
-- You should see: one row per contributing signal for each auto-match - e.g. for the Anker SKU
-- match, signal `sku` with value 1, weight 3 and contribution 0.333, alongside lower-weighted
-- `product_name` and `brand` signals. An exact sku or gtin match (value 1) pushes the score into
-- the auto-merge band. The breakdown keys are PascalCase: Signal, Value, Weight, Contribution.
```

### Handy JSONB helpers

```sql
-- Pretty-print a whole merged record so you can see every field at once:
select jsonb_pretty(fields) from golden_records limit 1;
-- The durable blocking keys that make matching fast - a text[] column, not JSON:
select source_record_id, blocking_keys from entity_records limit 5;
```

> **Tip:** the `cluster_merge_events` table is empty after this tutorial because no automatic
> cluster **bridge-merge** happened here (that's when a new record links two previously separate
> clusters). To see that lineage populated, run the `within-batch-resolution` sample scenario
> against a container and query `cluster_merge_events`:
> `pwsh scripts/Run-DurableScenario.ps1 -Backend Postgres -ScenarioPath samples/durable/within-batch-resolution`.

When you're done exploring, leave `psql` with `\q`.

## Recap & next steps

You built a durable product catalog MDM project from nothing: created a product project with
merge rules, loaded three sources, watched Linkuity auto-merge confident product matches,
route an uncertain lookalike to review, and keep unrelated products separate. You also saw how
`product.profile.json` drives product matching and how `merge-policy.json` controls source
priority for the golden record.

Where to go next:

- Try changing one field in `supplier.csv` or `marketplace.csv`, then load into a fresh
  store (a new `--metadata` file, or a fresh Postgres database) to see how the golden record
  changes.
- Use `linkuity cluster list @store --project-id $projectId` to inspect which
  source records sit inside each cluster.
- Read [`docs/architecture.md`](../architecture.md) for how the pieces fit together.
