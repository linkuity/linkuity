-- Cluster cohesion counters.
--
-- Cohesion is agreements over comparisons made INSIDE a cluster, and until now the store kept
-- neither number: a cluster whose members disagree was indistinguishable from one the engine
-- never looked inside. These two counters are the denominator and numerator, kept as running
-- totals across every ingest that touches the cluster, not as per-pair rows — the whole point of
-- this design is two integers per cluster rather than one row per comparison.
--
-- Zero-default, not NULL, matching every other counter column in this schema: existing clusters
-- written before this migration read as 0/0, which ClusterEvidenceCounts.AgreementRate already
-- treats as "has not contradicted itself" (nothing compared, nothing to disagree about). Re-ingest
-- is the migration policy here, so no backfill is attempted or needed.

alter table clusters
    add column if not exists comparisons_inside bigint not null default 0,
    add column if not exists agreements_inside   bigint not null default 0;
