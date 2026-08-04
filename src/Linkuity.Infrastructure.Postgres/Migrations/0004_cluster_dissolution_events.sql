-- Cluster dissolution events.
--
-- The audit trail for a component the merge policy refused: the member list, the numbers that
-- refused it, and (when an already-published cluster was re-evaluated and failed) which cluster
-- id it used to be. Dissolution must never be silent -- a customer whose established entity
-- splits without a record of why has been handed a worse problem than the over-merge the split
-- prevented. Mirrors cluster_merge_events (0001), which is the same kind of audit row for the
-- opposite outcome.

create table cluster_dissolution_events (
    id                        uuid             primary key,
    project_id                uuid             not null,
    member_entity_record_ids  uuid[]           not null,
    previous_cluster_id       uuid             null,
    reason                    text             not null,
    comparisons_inside        bigint           not null,
    agreements_inside         bigint           not null,
    ingest_batch_id           uuid             not null,
    created_at                timestamptz      not null
);

create index ix_cluster_dissolution_events_project_id on cluster_dissolution_events (project_id);

-- "Why did cluster X disappear" is the query this table exists to answer (see the module comment
-- above); without this index it is a sequential scan at any real scale.
create index ix_cluster_dissolution_events_previous_cluster_id on cluster_dissolution_events (previous_cluster_id);
