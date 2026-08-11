r"""Field-variation check for a labelled entity-resolution corpus.

Reports, per column, the share of MULTI-RECORD entities that have more than one
distinct value for that column. This is the check that would have caught the org
corpus's structural defect before it was adopted: because every alias record of one
entity carries a byte-for-byte copy of the entity's address, city, region, postal
code, country, jurisdiction and legal form, those seven fields NEVER vary within an
entity -- which forces m (the probability two same-entity records agree) to exactly
1.0 for those fields, and that in turn makes the EM disagreement weight undefined.
See linkuity/.superpowers/sdd/2026-08-10-phase-0.4-two-observation-corpus/.

Taxonomy-neutral by construction: every column except the first (the record id) is
read from the records CSV header and treated identically. Nothing here knows the
name of a field, what taxonomy produced it, or what corpus it came from.

Usable as a module:

    import corpus_variation as v
    report = v.measure_corpus_variation(records_csv, ground_truth_csv)
    print(v.format_report(report))

Usable as a CLI:

    python corpus_variation.py <records.csv> <ground-truth.csv>

No I/O side effects at import time.
"""
import argparse
import csv
import dataclasses
import sys

# Sentinel meaning "this (entity, column) pair is already known to have 2+ distinct
# values." Stored in place of the value once the second distinct value is seen, so
# tracking a column that has resolved to VARIES costs one shared-object reference
# rather than a growing string.
VARIES = object()


def normalize(value):
    """Trimmed, ordinal-case-insensitive comparison key.

    `.lower()`, not `.casefold()`: the engine compares with .NET's
    StringComparison.OrdinalIgnoreCase (ReachabilityDiagnosticService.cs:340,369),
    which maps each character to its invariant lowercase form one-to-one and never
    expands one character into several. Python's `str.casefold()` is a broader,
    caseless-matching fold that DOES expand -- "straße".casefold() == "strasse" --
    which would treat two strings as equal that the engine treats as different.
    `str.lower()` does not perform that expansion and tracks Ordinal semantics.
    """
    return value.strip().lower()


def read_truth_pairs(truth_path):
    """Yield (record_id, canonical_key) from a ground-truth CSV, streaming."""
    with open(truth_path, encoding="utf-8", newline="") as fh:
        reader = csv.reader(fh)
        header = next(reader)
        rid_col = header.index("record_id")
        key_col = header.index("canonical_key")
        for row in reader:
            yield row[rid_col], row[key_col]


def entity_sizes(pairs):
    """(record_id, canonical_key) pairs -> {canonical_key: record count}."""
    sizes = {}
    for _, key in pairs:
        sizes[key] = sizes.get(key, 0) + 1
    return sizes


def multi_record_index(pairs, multi_sizes):
    """{record_id: canonical_key}, restricted to entities present in `multi_sizes`.

    A single-record entity cannot vary and would dilute the rate, so its records are
    dropped here rather than carried into the accumulation pass -- the pass that
    follows never even sees a record id that isn't a key of the returned dict.
    """
    return {rid: key for rid, key in pairs if key in multi_sizes}


def load_multi_record_index(truth_path):
    """Two streaming passes over the ground truth: sizes, then the pruned id index.

    Reading the file twice (rather than materializing every pair once and reusing
    it) keeps peak memory to the DERIVED dicts -- entity sizes, then only the id
    mapping for records that belong to a multi-record entity -- never the full
    (record_id, canonical_key) pair list, which is proportional to every record.
    """
    sizes = entity_sizes(read_truth_pairs(truth_path))
    multi_sizes = {key: count for key, count in sizes.items() if count >= 2}
    index = multi_record_index(read_truth_pairs(truth_path), multi_sizes)
    return index, multi_sizes


def record_columns(records_path):
    """Every column of the records CSV except the first (the record id)."""
    with open(records_path, encoding="utf-8", newline="") as fh:
        return next(csv.reader(fh))[1:]


def read_record_rows(records_path):
    """Yield (record_id, [values for every column after id]), streaming."""
    with open(records_path, encoding="utf-8", newline="") as fh:
        reader = csv.reader(fh)
        next(reader)   # header; record_columns reads it separately
        for row in reader:
            yield row[0], row[1:]


def accumulate_variation(rows, columns, index):
    """The core measurement, pure and testable against small in-memory fixtures.

    rows: iterable of (record_id, values) with values aligned to `columns`.
    index: record_id -> canonical_key, already restricted to multi-record entities
        (see multi_record_index) -- a record id absent from `index` is skipped, so
        this function never needs to know an entity's size, only its membership.

    Peak memory is one dict entry per (entity, column) still awaiting its second
    distinct POPULATED value; a column resolves to the VARIES sentinel the moment a
    second distinct value appears and is never grown again after that.

    A blank value is counted in `blank_counts` but otherwise SKIPPED -- it is never
    stored as a pending value and never compared against one. This mirrors the
    engine: WeightedFieldSimilarityStrategy.Evaluate
    (src/Linkuity.Matching/Strategies/Defaults/WeightedFieldSimilarityStrategy.cs:40-48)
    emits MissingOneSide/MissingBoth and never Compared when either side is blank, so
    a blank contributes neither agreement nor disagreement there. Treating blank as a
    value here would let a corpus with heavy missingness but no genuine within-entity
    disagreement read as "varies" -- the same defect class this tool exists to catch,
    one level up.

    Returns (varies_counts, blank_counts, records_considered):
        varies_counts[col]  -- entities where `col` has 2+ distinct POPULATED values
        blank_counts[col]   -- records (not entities) where `col` is blank
        records_considered  -- total records seen that belong to a multi-record entity
    """
    pending = {}
    varies_counts = {c: 0 for c in columns}
    blank_counts = {c: 0 for c in columns}
    records_considered = 0

    for rid, values in rows:
        key = index.get(rid)
        if key is None:
            continue
        records_considered += 1
        state = pending.setdefault(key, {})
        for col, raw in zip(columns, values):
            val = normalize(raw)
            if not val:
                blank_counts[col] += 1
                continue   # neither agreement nor disagreement -- not a comparison
            if col not in state:
                state[col] = val
            elif state[col] is not VARIES and state[col] != val:
                varies_counts[col] += 1
                state[col] = VARIES

    return varies_counts, blank_counts, records_considered


@dataclasses.dataclass
class CorpusVariationReport:
    records_path: str
    truth_path: str
    columns: list
    multi_record_entities: int
    records_considered: int
    varies_counts: dict
    blank_counts: dict

    def varies_share(self, col):
        if self.multi_record_entities == 0:
            return 0.0
        return self.varies_counts[col] / self.multi_record_entities

    def blank_share(self, col):
        if self.records_considered == 0:
            return 0.0
        return self.blank_counts[col] / self.records_considered


def measure_corpus_variation(records_path, truth_path):
    """The whole check: two streaming passes over the truth, one over the records."""
    index, multi_sizes = load_multi_record_index(truth_path)
    columns = record_columns(records_path)
    varies_counts, blank_counts, records_considered = accumulate_variation(
        read_record_rows(records_path), columns, index)
    return CorpusVariationReport(
        records_path=records_path,
        truth_path=truth_path,
        columns=columns,
        multi_record_entities=len(multi_sizes),
        records_considered=records_considered,
        varies_counts=varies_counts,
        blank_counts=blank_counts,
    )


def format_report(report):
    """Fixed-width, fixed-precision text -- deterministic run to run, so it is
    stable enough to commit as an artifact and diff across corpus revisions."""
    lines = [
        "Corpus variation report",
        f"  records:                {report.records_path}",
        f"  ground truth:           {report.truth_path}",
        f"  multi-record entities:  {report.multi_record_entities:,}",
        f"  records among them:     {report.records_considered:,}",
        "",
    ]
    if not report.columns:
        lines.append("(no columns)")
        return "\n".join(lines)

    name_width = max(len("field"), max(len(c) for c in report.columns))
    header = f"{'field':<{name_width}}  {'varies':>28}  {'blank':>28}"
    lines.append(header)
    lines.append("-" * len(header))
    for col in report.columns:
        varies_n = report.varies_counts[col]
        blank_n = report.blank_counts[col]
        varies_cell = (f"{varies_n:,}/{report.multi_record_entities:,} "
                       f"({report.varies_share(col) * 100:.4f}%)")
        blank_cell = (f"{blank_n:,}/{report.records_considered:,} "
                      f"({report.blank_share(col) * 100:.4f}%)")
        lines.append(f"{col:<{name_width}}  {varies_cell:>28}  {blank_cell:>28}")
    return "\n".join(lines)


def build_arg_parser():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("records_csv", help="records CSV; first column is the record id")
    parser.add_argument("ground_truth_csv", help="ground-truth CSV: record_id,canonical_key")
    return parser


def main(argv=None):
    args = build_arg_parser().parse_args(argv)
    report = measure_corpus_variation(args.records_csv, args.ground_truth_csv)
    print(format_report(report))
    return 0


if __name__ == "__main__":
    sys.exit(main())
