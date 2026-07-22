# Configuration reference: matching profiles and merge policy

Every Linkuity run — batch or durable — is configured by up to two small JSON
files:

1. A **matching profile** (`*.profile.json`) — required. Declares the fields in
   your data, what kind of value each one is, how they're compared, and the
   thresholds that decide auto-match vs. review vs. no-match.
2. A **merge policy** (`*.merge.json`) — optional. Declares, per field, which
   source wins when a cluster's members disagree. Omit it and every field
   merges by consensus.

The same two files are consumed identically by the CLI, the HTTP API, and
durable (incremental) ingest — there is exactly one profile format and one
merge-policy format across the whole product. This page is the field-by-field
reference for both. For the *why* behind the pipeline — blocking, scoring,
decision bands, clustering, tuning — see
[`how-matching-works.md`](how-matching-works.md); this page won't repeat that
narrative, only the schema and how to author it.

## The matching profile

A profile is the entire configuration for one content type (taxonomy) — the
engine has no per-taxonomy code. Adding a new content type that reuses
existing semantic types, evaluators, and strategies is a pure data change: a
new `*.profile.json` file, nothing else.

Profiles are loaded and validated by
[`MatchingProfileConfigLoader`](../src/Linkuity.Matching/Profiles/Configuration/MatchingProfileConfigLoader.cs),
against the strategy registry built by
[`MatchingDefaults.CreateRegistry`](../src/Linkuity.Matching/MatchingDefaults.cs).
Every name in the file — every strategy, evaluator, semantic type, and role —
is checked against that registry at load time; an unknown or misspelled name
throws immediately, naming the bad value and (for strategies/evaluators)
listing what's actually registered. A profile never silently falls back to a
default.

Here is a real profile,
[`samples/durable/organization-360/organization.profile.json`](../samples/durable/organization-360/organization.profile.json),
annotated:

```jsonc
{
  // Selects this profile for projects/runs of the same content type.
  "contentType": "organization",

  "fields": [
    // A field with no roles ([]) is carried through but never used for
    // matching — typical for a raw "which system did this come from" column.
    { "name": "source", "semanticType": "SourceIdentifier", "roles": [] },

    // similarityEvaluator + weight decide how much this field's agreement
    // (or disagreement) counts toward the pair score.
    { "name": "organization_name", "semanticType": "OrganizationName",
      "roles": ["Searchable", "Matchable", "Blocking"],
      "similarityEvaluator": "fuzzy", "weight": 2.0 },

    // Identifier: an exact match on this field is strong enough evidence to
    // auto-match a pair on its own (see "roles" below).
    { "name": "domain_name", "semanticType": "DomainName",
      "roles": ["Searchable", "Matchable", "Blocking", "Identifier"],
      "similarityEvaluator": "exact", "weight": 2.5 },

    { "name": "email", "semanticType": "Email",
      "roles": ["Searchable", "Matchable", "Blocking", "Identifier"],
      "similarityEvaluator": "exact", "weight": 2.5 },

    { "name": "phone", "semanticType": "Phone",
      "roles": ["Matchable", "Blocking", "Identifier"],
      "similarityEvaluator": "exact", "weight": 2.0 },

    { "name": "address_line", "semanticType": "AddressLine",
      "roles": ["Searchable", "Matchable"],
      "similarityEvaluator": "jaccard", "weight": 1.0 },

    { "name": "postal_code", "semanticType": "PostalCode",
      "roles": ["Matchable"],
      "similarityEvaluator": "exact", "weight": 1.0 }
  ],

  // Which stage-strategy runs at each step of the pipeline (see below).
  "normalizationStrategy": "identity",
  "blockingStrategies": ["exact-value", "token-name"],
  "candidateRetrievalStrategy": "linear",
  "similarityStrategy": "field-weighted",
  "scoringStrategy": "identifier-weighted",
  "decisionStrategy": "threshold",
  "clusteringStrategy": "union-find",

  // Decision bands: score >= autoMatchThreshold auto-matches; score in
  // [reviewThreshold, autoMatchThreshold) becomes a review task; below
  // reviewThreshold is a no-match. autoMatchThreshold must be > reviewThreshold.
  "autoMatchThreshold": 0.90,
  "reviewThreshold": 0.75

  // reviewFloorGate omitted here -> defaults to 0.75 (see below).
}
```

(Standard JSON does not support `//` comments; the loader is configured with
`ReadCommentHandling = Skip` and `AllowTrailingCommas = true`, so real profile
files *can* use `//` comments and trailing commas if you want them — the
annotated block above is not fiction, it will load as-is.)

### Schema, field by field

| Key | Required | Meaning |
|---|---|---|
| `contentType` | yes | The content type this profile matches (`person`, `organization`, or your own, e.g. `location`). Selects the profile for a project/run. |
| `fields[]` | yes, ≥ 1 entry | One entry per column in your data. Field names must be unique within a profile (duplicates throw at load time). |
| `fields[].name` | yes | The column name in your input data. |
| `fields[].semanticType` | yes | What kind of value this is — see [the semantic-type vocabulary](#the-semantic-type-vocabulary) below. Drives normalization and which blocking strategies apply. Unknown value throws. |
| `fields[].roles` | no (omitted ⇒ no roles, same as `[]`) | Any combination of `Searchable`, `Matchable`, `Blocking`, `Identifier` — see [roles](#field-roles) below. Unknown role name throws. |
| `fields[].similarityEvaluator` | no | Which evaluator compares this field: `exact`, `fuzzy`, `jaccard`, `ngram`, `numeric`, or `date`. Must be registered if present; unknown value throws. |
| `fields[].weight` | no | Relative importance in weighted scoring. Defaults to `1.0`. |
| `fields[].evaluatorOptions` | no | Per-evaluator tuning knobs, as a string-to-string map. Recognized keys: `numeric.tolerance`, `numeric.maxPercentDiff` (for the `numeric` evaluator), `date.maxDays` (for `date`), `ngram.size` (for `ngram`). E.g. `{ "ngram.size": "3" }`. |
| `normalizationStrategy` | yes | `identity` (no-op — the usual choice when data is already clean at ingest) or `semantic-field` (applies per-semantic-type cleaning at match time). Must be registered. |
| `blockingStrategies[]` | yes, ≥ 1 entry | Which blocking strategies run; the engine unions the keys they all produce. Built-ins: `exact-value`, `token-name`, `prefix`, `ngram`, `phonetic`, `dob-lastname-phonetic`. Each name must be registered. |
| `candidateRetrievalStrategy` | yes | `linear` or `blocking-linear` in the in-process engine registry (durable ingest overrides this with an index-backed retrieval — Lucene — regardless of what's declared here; see [Batch vs. durable usage](#batch-vs-durable-usage-of-the-same-files)). Must be registered. |
| `similarityStrategy` | yes | `default` or `field-weighted` (the built-in profiles use `field-weighted`, which honors per-field `similarityEvaluator`/`weight`). Must be registered. |
| `scoringStrategy` | yes | `default`, `weighted`, or `identifier-weighted` (the built-in profiles use `identifier-weighted`, which floors a pair to `0.98` on an exact `Identifier` match). Must be registered. |
| `decisionStrategy` | yes | `threshold` (the only built-in decision strategy). Must be registered. |
| `clusteringStrategy` | yes | `union-find` (the only built-in clustering strategy). Must be registered. |
| `autoMatchThreshold` | yes | Score ≥ this auto-matches. Must be in `[0, 1]`. |
| `reviewThreshold` | yes | Score in `[reviewThreshold, autoMatchThreshold)` becomes a review task. Must be in `[0, 1]`, and **`autoMatchThreshold` must be strictly greater than `reviewThreshold`** — the loader rejects the equal boundary. |
| `reviewFloorGate` | no | Minimum weighted per-field similarity a non-identifier candidate must reach before the scorer's `0.80` review floor applies. Defaults to `0.75` when omitted. Must be in `[0, 1]`; there is no required relationship to `reviewThreshold` — it's a free tuning knob (see [how-matching-works.md](how-matching-works.md#the-profile-model)). |

Source: [`MatchingProfileConfigLoader.Build`/`BuildField`](../src/Linkuity.Matching/Profiles/Configuration/MatchingProfileConfigLoader.cs) and
[`MatchingProfileDocument`](../src/Linkuity.Matching/Profiles/Configuration/MatchingProfileDocument.cs).

### Field roles

Roles are a set of flags (JSON: an array of strings) describing what a field
is *for*:

| Role | Effect |
|---|---|
| `Searchable` | The field can be used to look up records (informational; doesn't itself change matching). |
| `Matchable` | The field's similarity is compared and contributes to the weighted pair score. |
| `Blocking` | The field is eligible to produce blocking keys — only records sharing a blocking key are ever compared. |
| `Identifier` | The field is a *strong identifier*: an exact match on it is decisive evidence (the `identifier-weighted` scorer floors such a pair near auto-match), and it also produces an exact blocking key. Normally paired with `Matchable` and `Blocking`. |

An empty `roles: []` means the field is carried through the pipeline (e.g.
into the golden record) but plays no part in matching — the usual choice for
a `source`/`SourceIdentifier` column.

Source: [`FieldRole`](../src/Linkuity.Matching/Profiles/FieldRole.cs).

### The semantic-type vocabulary

`semanticType` is the shared palette every profile draws from — the exact
members of the `SemanticFieldType` enum, no more, no fewer:

```
FirstName, LastName, FullName, Email, Phone, DateOfBirth, AddressLine,
PostalCode, OrganizationName, DomainName, SourceIdentifier, Sku, Gtin,
ProductName
```

Source: [`SemanticFieldType`](../src/Linkuity.Core/Models/SemanticFieldType.cs).

A field's semantic type drives which normalization rules apply to it and
which blocking strategies can key off it (e.g. `exact-value` blocking keys off
`Email`, `Phone`, `DomainName`, `DateOfBirth`, or any field carrying the
`Identifier` role; `token-name` keys off `LastName`, `FullName`,
`OrganizationName`, `ProductName`). `Sku`/`Gtin`/`ProductName` exist so a
product-catalog taxonomy can be built with **no engine change** — they behave
like any other identifier-capable or name-like type. Full detail on how
semantic type feeds normalization and blocking is in
[how-matching-works.md](how-matching-works.md#normalization) and
[the "Normalization" table in architecture.md](architecture.md).

## The merge-policy file

A merge policy decides, field by field, which value wins when a cluster's
member records disagree. It pairs with a profile but is entirely optional —
**omit it and every field merges by consensus**: the most common value in the
cluster wins, ties broken by the longest string.

Shape:

```json
{
  "mergeFields": [
    { "fieldName": "email", "sourcePriority": ["CRM", "Marketing", "Support", "Billing"] },
    { "fieldName": "phone", "sourcePriority": ["Support", "CRM", "Billing", "Marketing"] }
  ]
}
```

| Key | Meaning |
|---|---|
| `mergeFields[]` | One entry per field you want source-priority merging for. Any field *not* listed here falls back to consensus merge. |
| `mergeFields[].fieldName` | The field name (matches a profile field's `name`). Required. |
| `mergeFields[].sourcePriority` | An ordered list of source names. The merger walks the list and takes the first non-empty value from a record whose `source` field matches; if no listed source contributes a non-empty value, it falls back to consensus for that field too. Required. |

Source-priority resolution depends on a `source` column being present on the
input records (the value compared against each `sourcePriority` entry); the
`source`/`id` columns themselves are never merge targets. See
[`GoldenRecordMerge`](../src/Linkuity.Mdm/Resolution/GoldenRecordMerge.cs) (durable)
and [`GoldenRecordService`](../src/Linkuity.Pipeline/GoldenRecordService.cs) (batch)
for the exact consensus/priority algorithm, and
[how-matching-works.md](how-matching-works.md#golden-records-and-merge-policy)
for the concept. Model: [`MergeConfiguration`](../src/Linkuity.Core/Models/MergeConfiguration.cs) /
[`MergeField`](../src/Linkuity.Core/Models/MergeField.cs).

## Two annotated examples

### Built-in profile + a merge file

`person` and `organization` ship **built-in** — zero configuration needed,
resolved by name. You can still pair a built-in profile with your own merge
policy, since merging is a separate, optional file:

```powershell
dotnet run --project src/Linkuity.Cli -- run `
  --input samples/people-multi-source/sample.csv `
  --profile person `
  --merge-policy samples/people-multi-source/people-multi-source.merge.json `
  --output ./data/output/people-multi-source
```

`--profile person` resolves the built-in profile (no file needed — see
[`ProfileResolver`](../src/Linkuity.Matching/Profiles/ProfileResolver.cs)),
while `--merge-policy` points at
[`samples/people-multi-source/people-multi-source.merge.json`](../samples/people-multi-source/people-multi-source.merge.json),
which gives `email`, `phone`, `date_of_birth`, `address_line`, and
`postal_code` their own source-priority orders; `first_name`, `last_name`,
and `company` are absent from the merge file, so they merge by consensus.
(You can equally point `--profile` at your *own* file whose `contentType` is
`person` — a loaded profile silently overrides the built-in of the same
content type, letting you customize `person` without touching engine code.)

### Custom taxonomy (config-only)

`location` is **not** a built-in content type — it's a worked example showing
that a brand-new taxonomy is a pure configuration change, no code:
[`samples/location/location.profile.json`](../samples/location/location.profile.json) +
[`samples/location/location.merge.json`](../samples/location/location.merge.json).
It reuses only existing semantic types (`OrganizationName`, `AddressLine`,
`PostalCode`, `Phone`, `DomainName`) to resolve venue records, and deliberately
keeps `domain_name` `Blocking`+`Matchable` but *not* `Identifier` (a chain's
shared website domain would otherwise wrongly floor two different locations to
a match), while `phone` — unique per venue — does carry `Identifier`. See
[`samples/location/README.md`](../samples/location/README.md) for the full
walkthrough. To add your own taxonomy, follow the same pattern: write a
`*.profile.json` with a new `contentType`, reusing existing semantic types and
strategies wherever they fit; only reach for new code when an existing
semantic type, normalization rule, or evaluator genuinely can't express what
you need.

## Batch vs. durable usage of the same files

The profile and merge-policy formats are identical everywhere; only how you
point Linkuity at them differs:

| Path | Profile | Merge policy |
|---|---|---|
| **CLI (batch)** — `linkuity run` | `--profile <name\|profile.json>` (built-in name, e.g. `person`, or a path to a `*.profile.json` file) | `--merge-policy <merge.json>` (optional) |
| **HTTP API** — `POST /run` | `profile` multipart form field (built-in name or raw profile JSON text) | `merge-policy` multipart form field (optional; raw merge-policy JSON text) |
| **CLI (durable)** — `project create` / `ingest-incremental` | `--content-type <type>` selects which registered profile a project uses; `--profiles <file\|dir>` registers one or more custom `*.profile.json` files (a directory loads every `*.profile.json` in it) alongside the built-ins | `--merge-policy <merge.json>` on `project create` (or later via `project merge-policy set`) |

A few consequences worth calling out:

- **Built-ins need no `--profiles`/file at all.** A durable project whose
  `--content-type` is `person` or `organization` resolves immediately.
- **A loaded profile overrides a built-in of the same `contentType`.** This is
  how you customize `person`/`organization` without an engine change; two
  *loaded* profiles declaring the same `contentType` is an authoring error and
  throws at startup.
- **An unregistered content type throws**, listing every profile that *is*
  registered — there's no silent fallback.
- Batch runs are always stateless (`profile`/`merge-policy` given per-request);
  durable projects store the merge policy once, on the project, and apply it
  consistently to every batch import and every later incremental ingest.

See [`docs/cli.md`](cli.md) for full CLI flag references, [`docs/http-api.md`](http-api.md)
for the `/run` endpoint contract, and the
[durable MDM quick start](tutorials/cli-durable-mdm-quickstart.md) for a
guided walkthrough of `project create --content-type ... --merge-policy ...`
and `ingest-incremental --profiles ...`. For the full authoring reference
(including the taxonomy-scaling model — config-only vs. shared-vocabulary
extension vs. new strategy), see
["Authoring a matching profile" in architecture.md](architecture.md#authoring-a-matching-profile-no-code-changes).
