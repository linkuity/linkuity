# Location — Non-Built-In Taxonomy Sample

A 7-row venue dataset that proves Linkuity's matching engine is not limited to the
built-in `person` / `organization` content types. `location` is a **config-only**
taxonomy — nothing new was shipped in code to support it; it is composed entirely
from existing semantic types (`OrganizationName`, `AddressLine`, `PostalCode`,
`Phone`, `DomainName`) declared in a profile. If you need to resolve entities that
don't fit the built-in shapes, this is the pattern: write a profile, not a PR.

## Files

- `sample.csv` — 7 input rows describing 3 physical coffee-shop venues, ingested from
  three source systems: **Google** (Maps/Business listings), **Yelp**, and **POS**
  (point-of-sale system).
- `location.profile.json` — matching profile. Declares `contentType: "location"` and
  the fields, semantic types, and weights used for blocking, scoring, and clustering.
- `location.merge.json` — merge policy. Per-field source-priority rules for building
  the golden record.
- `expectations.json` — asserted golden record count and cluster membership, used by
  `Run-Scenario.ps1` and `SampleScenarioTests`.

## The scenario

A coffee chain, **Blue Bottle**, operates two San Francisco locations (Ferry Building
and Mint St) alongside an unrelated competitor, **Sightglass Coffee**. Each venue is
listed independently in Google, Yelp, and (for one location) a POS system, with the
usual real-world noise: abbreviated names (`Blue Bottle` vs `Blue Bottle Coffee`),
abbreviated street types (`Ferry Bldg` vs `Ferry Building`), and differently
formatted phone numbers (`(415) 555-0100` vs `415-555-0100`).

The hard part: **both Blue Bottle locations share the same name and the same
website domain.** A naive matcher that blocks or scores heavily on `name` or
`domain_name` alone would merge the two distinct venues into one. This sample is
designed so that doesn't happen.

## Teaching points

- **`phone` is the identifier.** Each physical venue has its own phone line, so
  `phone` carries the `Identifier` role (alongside `Matchable` and `Blocking`) with
  `exact` similarity. A shared exact phone floors a pair to a match; the two Blue
  Bottle locations have *different* phone numbers, so they never get that floor
  against each other.
- **`domain_name` blocks but is deliberately *not* an identifier.** A chain's
  website domain is shared across every location — `bluebottle.com` appears on
  three of the seven rows, spanning two different venues. If `domain_name` carried
  the `Identifier` role, it would incorrectly floor Ferry Building and Mint St to a
  match. It keeps `Blocking` (so same-domain rows are compared to each other, keeping
  candidate generation cheap) and `Matchable` (so it still contributes to the
  weighted score), but never gets identifier-strength trust.
- **`address_line` carries heavy weight (3.0, tied with `phone`).** With `name` and
  `domain_name` both unreliable for telling the two Blue Bottle venues apart,
  address is what actually separates them: `1 Ferry Building` / `One Ferry Building`
  cluster together under `jaccard` similarity, while `66 Mint St` scores low against
  either Ferry Building variant. `postal_code` (`94111` vs `94103`) reinforces the
  same split as an exact-match signal.

Together, `phone` (identifier-strength exact match) and `address_line` (heavily
weighted token similarity) are what keep the two Blue Bottle locations apart despite
sharing a name and a domain — exactly the failure mode a multi-location chain would
otherwise trigger.

## Expected result

3 golden records:

| Cluster | Members | Venue |
|---|---|---|
| `cluster_ferry_building_blue_bottle` | `goog-001`, `yelp-002`, `pos-003` | Blue Bottle Coffee, Ferry Building |
| `cluster_mint_st_blue_bottle` | `goog-004`, `yelp-005` | Blue Bottle Coffee, Mint St |
| `cluster_sightglass` | `goog-006`, `yelp-007` | Sightglass Coffee |

The two Blue Bottle locations stay **separate** despite the shared name and domain,
because their phones and addresses differ; each venue's rows merge on the shared
phone identifier.

## Running it

```bash
dotnet run --project src/Linkuity.Cli -- run --input samples/location/sample.csv --profile samples/location/location.profile.json --merge-policy samples/location/location.merge.json --output ./data/output/location-check
```

Expected: **3 golden records**, matching `expectations.json`.

See [`docs/configuration.md`](../../docs/configuration.md) for the full profile and
merge-policy schema, including field roles, similarity evaluators, and strategy
options.
