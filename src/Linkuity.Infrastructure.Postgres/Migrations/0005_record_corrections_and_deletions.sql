-- Record corrections & deletion — PostgreSQL parity with the file metadata store (F6 milestones 1/2).
--
-- superseded_at / deleted_at mirror EntityRecord.SupersededAt / EntityRecord.DeletedAt: null means
-- this row is the current, live record for its (project_id, source_record_id); non-null means a later
-- correction replaced it (superseded_at) or the source system withdrew it (deleted_at). The row is
-- kept, never deleted, so history (match_edges, golden_record_versions) referencing this id stays
-- valid — same reasoning as every other tombstone in this schema.
--
-- record_corrected_events / record_deleted_events are the audit trail for each, mirroring
-- cluster_dissolution_events' (0004) shape and indexing: one row per event, queryable by project.

alter table entity_records add column superseded_at timestamptz null;
alter table entity_records add column deleted_at timestamptz null;

-- Backs FindCurrentRecordBySourceRecordId's project-scoped, case-insensitive lookup. Without this,
-- that lookup (and the removed duplicate-check it replaces) scans every record in the project.
create index ix_entity_records_project_source_lookup
    on entity_records (project_id, lower(source_record_id));

create table record_corrected_events (
    id                           uuid        primary key,
    project_id                   uuid        not null,
    superseded_entity_record_id  uuid        not null,
    corrected_entity_record_id   uuid        not null,
    previous_fields              jsonb       not null,
    new_fields                   jsonb       not null,
    previous_cluster_id          uuid        null,
    ingest_batch_id              uuid        not null,
    created_at                   timestamptz not null
);

create index ix_record_corrected_events_project_id on record_corrected_events (project_id);

create table record_deleted_events (
    id                        uuid        primary key,
    project_id                uuid        not null,
    deleted_entity_record_id  uuid        not null,
    previous_fields           jsonb       not null,
    previous_cluster_id       uuid        null,
    ingest_batch_id           uuid        not null,
    created_at                timestamptz not null
);

create index ix_record_deleted_events_project_id on record_deleted_events (project_id);
