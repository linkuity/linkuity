# Benchmark plan

**Status:** Design only. No benchmark results are published, and none should be quoted
until they are honestly measured and reproducible.

Linkuity already has a **performance/scale** track — a runtime-and-memory harness
(`src/Linkuity.Mdm.Benchmarks`, `generate` + `measure`) and a written plan in
[performance-testing-plan.md](performance-testing-plan.md). What that track does **not**
cover, and what this document scopes, is **matching quality** (precision / recall / F1
against labeled ground truth) and an honest **cross-tool comparison**. The two together
form the full benchmark story.

## Goals

1. **Quality.** Measure precision, recall, and F1 of Linkuity's match decisions against
   datasets with known duplicate ground truth.
2. **Cost.** Report runtime and peak memory for the same runs (reusing the existing
   performance harness where possible).
3. **Comparability.** Where a fair, apples-to-apples configuration is possible, compare
   against other open-source approaches — **without publishing numbers until they are
   measured and reproducible**.

## Metrics

| Metric | Definition |
|--------|------------|
| Precision | Of the pairs Linkuity links, the fraction that are true matches. |
| Recall | Of the true matches, the fraction Linkuity links. |
| F1 | Harmonic mean of precision and recall. |
| Runtime | Wall-clock to resolve the dataset (report methodology; note fsync/checkpoint variance for durable runs). |
| Peak memory | Peak working set during resolution. |

Report cluster-level as well as pair-level metrics where the dataset's ground truth
supports it (pairwise metrics can flatter or punish transitive clustering differently).

## Datasets

Use public, appropriately licensed entity-resolution datasets with ground-truth labels.
Candidates to evaluate for license and fit:

- Febrl / synthetic person generators (labeled duplicates)
- The North Carolina Voter (NCVR) style deduplication sets
- DBLP–ACM / DBLP–Scholar bibliographic matching sets
- Linkuity's own `generate` harness, which knows its injected duplicate ground truth

Record, for every dataset: size, duplicate rate, field schema, and license.

## Comparison targets (design only)

Potential open-source comparisons include **Splink**, **Zingg**, and
**RapidFuzz**-based pipelines. Comparisons are only meaningful if each tool is
configured competently and the task is genuinely the same (same fields, same blocking
intent, same match definition). Until that is done and reproducible, **do not publish a
comparison table** — an unfair benchmark is worse than none.

## Reproducibility requirements

Every published result must ship with:

- The exact dataset (or a script that fetches/generates it deterministically).
- The exact Linkuity match configuration / profile used.
- Tool versions for every system compared.
- Hardware and OS (CPU, RAM, disk; native vs. containerized; PostgreSQL version and
  relevant settings for durable runs).
- The command lines and a script that regenerates the numbers end to end.
- Raw output, not just summary metrics.

## Deliverables (when executed)

- A quality harness that scores Linkuity output against a dataset's ground-truth labels
  (precision / recall / F1), reusing `Linkuity.Mdm.Benchmarks` for dataset generation
  and cost measurement.
- A dated results artifact under `docs/roadmap/measurements/` (matching the convention
  the performance track already uses).
- Only after the above: a comparison summary, with the reproducibility bundle attached.

## Non-goals

- Marketing numbers. No headline metric is quoted anywhere in the repo until it is
  reproducible from this plan.
- Cherry-picked datasets. Report the datasets where Linkuity does poorly alongside the
  ones where it does well.
