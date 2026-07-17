-- Linkuity PostgreSQL schema – migration 0001
-- Applied once by DbUp; journalled in public.schema_versions.

create table projects (
    id                   uuid        primary key,
    name                 text        not null,
    content_type         text        not null,
    merge_configuration  jsonb       null,
    created_at           timestamptz not null
);

create table sources (
    id          uuid        primary key,
    project_id  uuid        not null,
    name        text        not null,
    created_at  timestamptz not null
);

create index ix_sources_project_id on sources (project_id);

create table ingest_batches (
    id            uuid        primary key,
    project_id    uuid        not null,
    source_id     uuid        not null,
    job_id        uuid        null,
    record_count  int         not null,
    created_at    timestamptz not null
);

create index ix_ingest_batches_project_id on ingest_batches (project_id);

create table entity_records (
    id                 uuid        primary key,
    project_id         uuid        not null,
    source_id          uuid        not null,
    ingest_batch_id    uuid        not null,
    source_record_id   text        not null,
    fields             jsonb       not null,
    blocking_keys      text[]      not null default '{}',
    cluster_id         uuid        null,
    created_at         timestamptz not null
);

create index ix_entity_records_project_id on entity_records (project_id);
create index ix_entity_records_cluster_id on entity_records (cluster_id);

create table match_edges (
    id                      uuid             primary key,
    project_id              uuid             not null,
    ingest_batch_id         uuid             not null,
    left_entity_record_id   uuid             not null,
    right_entity_record_id  uuid             not null,
    score                   double precision not null,
    method                  text             not null,
    decision                text             not null default '',
    breakdown               jsonb            not null default '[]',
    created_at              timestamptz      not null
);

create index ix_match_edges_project_id_pair
    on match_edges (project_id, left_entity_record_id, right_entity_record_id);

create table clusters (
    id                      uuid        primary key,
    project_id              uuid        not null,
    status                  text        not null default 'active',
    merged_into_cluster_id  uuid        null,
    created_at              timestamptz not null
);

create index ix_clusters_project_id on clusters (project_id);

create table golden_records (
    id                 uuid        primary key,
    project_id         uuid        not null,
    cluster_id         uuid        not null,
    current_version_id uuid        not null,
    fields             jsonb       not null,
    updated_at         timestamptz not null
);

create index ix_golden_records_project_id on golden_records (project_id);

create table golden_record_versions (
    id               uuid        primary key,
    golden_record_id uuid        not null,
    project_id       uuid        not null,
    cluster_id       uuid        not null,
    ingest_batch_id  uuid        not null,
    version_number   int         not null,
    fields           jsonb       not null,
    created_at       timestamptz not null
);

create index ix_golden_record_versions_project_id on golden_record_versions (project_id);
create index ix_golden_record_versions_gr_version
    on golden_record_versions (golden_record_id, version_number);

create table review_tasks (
    id                         uuid             primary key,
    project_id                 uuid             not null,
    ingest_batch_id            uuid             not null,
    new_entity_record_id       uuid             not null,
    candidate_entity_record_id uuid             not null,
    score                      double precision not null,
    reason                     text             not null,
    status                     text             not null,
    breakdown                  jsonb            not null default '[]',
    left_cluster_id            uuid             null,
    right_cluster_id           uuid             null,
    created_at                 timestamptz      not null
);

create index ix_review_tasks_project_id on review_tasks (project_id);

create table cluster_merge_events (
    id                                uuid             primary key,
    project_id                        uuid             not null,
    survivor_cluster_id               uuid             not null,
    absorbed_cluster_id               uuid             not null,
    absorbed_member_entity_record_ids uuid[]           not null,
    trigger_record_ids                uuid[]           not null,
    score                             double precision not null,
    breakdown                         jsonb            not null default '[]',
    ingest_batch_id                   uuid             not null,
    created_at                        timestamptz      not null
);

create index ix_cluster_merge_events_project_id on cluster_merge_events (project_id);
