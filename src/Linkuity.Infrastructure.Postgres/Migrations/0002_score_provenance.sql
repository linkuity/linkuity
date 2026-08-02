-- Score provenance on match edges.
--
-- Given a stored edge it was not possible to say what produced it. `method` records which
-- ingest path wrote the row, not which scorer computed its number, and no profile identity was
-- kept at all. Two edges written a month apart could carry the same profile name and have come
-- from materially different rules — different thresholds, weights, or blocking — with nothing
-- in the store to distinguish them.
--
-- That becomes acute when a second scoring model exists: without provenance, scores on two
-- different scales sit in one column and look alike.
--
-- Empty-string defaults rather than NULL, matching `decision` and `breakdown` above. Existing
-- rows keep their meaning and read as what they are: edges of unknown provenance. Backfilling a
-- guess would be worse than recording the absence.

alter table match_edges
    add column if not exists scorer               text not null default '',
    add column if not exists profile_content_type text not null default '',
    add column if not exists profile_fingerprint  text not null default '';

-- Answers "which edges were scored by rules that no longer apply?" — the question a profile
-- change raises and, until now, nothing could answer.
create index if not exists ix_match_edges_project_id_profile_fingerprint
    on match_edges (project_id, profile_fingerprint);
