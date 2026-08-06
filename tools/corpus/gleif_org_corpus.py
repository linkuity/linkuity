"""Pure logic for the GLEIF labelled organization corpus.

Everything that can be *wrong* lives here rather than in the driver, because the driver
reads a 4.9 GB file and this module can be tested in milliseconds. See
linkuity/docs/superpowers/specs/2026-08-05-gleif-labelled-org-corpus-design.md.

No I/O side effects at import time.
"""
import csv
import functools
import hashlib
import os
import unicodedata

# --- alias type vocabulary -------------------------------------------------------
# Verified closed against the whole 20260727-1600 snapshot on 2026-08-06: these five
# values plus LEGAL account for every alias, and there are no untyped ones. An
# unrecognised value is therefore a snapshot change, not an edge case -- abort, do not
# bucket it into "other".
LEGAL = "LEGAL"
OTHER_NAME_TYPES = frozenset({
    "PREVIOUS_LEGAL_NAME",
    "TRADING_OR_OPERATING_NAME",
    "ALTERNATIVE_LANGUAGE_LEGAL_NAME",
})
TRANSLITERATED_TYPES = frozenset({
    "PREFERRED_ASCII_TRANSLITERATED_LEGAL_NAME",   # registrant-supplied
    "AUTO_ASCII_TRANSLITERATED_LEGAL_NAME",        # machine-generated ASCII folding
})
KNOWN_ALIAS_TYPES = frozenset({LEGAL}) | OTHER_NAME_TYPES | TRANSLITERATED_TYPES

# Gated unconditionally. Transliterated types are gated only when same-script; every
# ALTERNATIVE_LANGUAGE_LEGAL_NAME is excluded from the gate corpus (65.2% share not one
# token with the legal name -- spec section 3).
ALWAYS_GATED = frozenset({LEGAL, "PREVIOUS_LEGAL_NAME", "TRADING_OR_OPERATING_NAME"})

# --- source column layout --------------------------------------------------------
OTHER_NAME_SLOTS = [
    (f"Entity.OtherEntityNames.OtherEntityName.{k}",
     f"Entity.OtherEntityNames.OtherEntityName.{k}.type")
    for k in range(1, 6)
]
TRANSLITERATED_SLOTS = [
    (f"Entity.TransliteratedOtherEntityNames.TransliteratedOtherEntityName.{k}",
     f"Entity.TransliteratedOtherEntityNames.TransliteratedOtherEntityName.{k}.type")
    for k in range(1, 6)
]

# corpus column -> GLEIF column. Order here is the order in records.csv.
ADDRESS_COLUMNS = [
    ("address_line", "Entity.LegalAddress.FirstAddressLine"),
    ("city", "Entity.LegalAddress.City"),
    ("region", "Entity.LegalAddress.Region"),
    ("postal_code", "Entity.LegalAddress.PostalCode"),
    ("country", "Entity.LegalAddress.Country"),
    ("jurisdiction", "Entity.LegalJurisdiction"),
    ("legal_form", "Entity.LegalForm.EntityLegalFormCode"),
]

RECORD_COLUMNS = (["id", "organization_name"]
                  + [c for c, _ in ADDRESS_COLUMNS]
                  + ["alias_type", "script_relation"])

REQUIRED_GLEIF_COLUMNS = (
    ["LEI", "Entity.LegalName",
     "Entity.RegistrationAuthority.RegistrationAuthorityID",
     "Entity.RegistrationAuthority.RegistrationAuthorityEntityID"]
    + [c for _, c in ADDRESS_COLUMNS]
    + [c for pair in OTHER_NAME_SLOTS for c in pair]
    + [c for pair in TRANSLITERATED_SLOTS for c in pair]
)


def column_index(header, required=REQUIRED_GLEIF_COLUMNS):
    """Map column name -> position, failing loudly on any missing required column.

    GLEIF has 338 columns and republishes daily. A silently-absent column would read as
    an empty field on every record, which looks like a coverage change rather than a bug.
    """
    ix = {name: i for i, name in enumerate(header)}
    missing = [c for c in required if c not in ix]
    if missing:
        raise KeyError(f"GLEIF header is missing required column(s): {', '.join(missing)}")
    return ix


@functools.lru_cache(maxsize=None)
def _script_of_char(ch):
    """Unicode script family of one character, or None if it is not a letter.

    Cached because the parse pass classifies roughly 4M names and the character
    repertoire is tiny by comparison.
    """
    if not ch.isalpha():
        return None
    try:
        return unicodedata.name(ch).split()[0]
    except ValueError:
        return None


def dominant_script(text):
    """The most frequent script family among the letters of `text`, or 'NONE'."""
    counts = {}
    for ch in text:
        script = _script_of_char(ch)
        if script is not None:
            counts[script] = counts.get(script, 0) + 1
    if not counts:
        return "NONE"
    # max on (count, name) so ties break deterministically rather than by dict order.
    return max(counts.items(), key=lambda kv: (kv[1], kv[0]))[0]


def script_relation(alias, legal):
    """'same' or 'cross', relative to the entity's legal name.

    A name with no letters at all is 'same': it is not evidence of a script change, and
    classifying it 'cross' would silently drop it from the gate corpus.
    """
    a, l = dominant_script(alias), dominant_script(legal)
    if a == "NONE" or l == "NONE":
        return "same"
    return "same" if a == l else "cross"


def is_gated(alias_type, relation):
    """Whether a record belongs in the GATE corpus (records.csv).

    Non-gated records are DROPPED, never rekeyed -- rekeying would score an engine that
    correctly matched a cross-script transliteration as a false merge. Spec section 5.
    """
    if alias_type not in KNOWN_ALIAS_TYPES:
        raise ValueError(f"unknown alias type {alias_type!r}")
    if alias_type in ALWAYS_GATED:
        return True
    if alias_type in TRANSLITERATED_TYPES:
        return relation == "same"
    return False   # ALTERNATIVE_LANGUAGE_LEGAL_NAME


def entity_aliases(row, ix):
    """(name, alias_type, script_relation) per distinct name, LEGAL first.

    Distinctness is case-folded; first occurrence in source order wins its type. Source
    order is: legal name, OtherEntityName 1..5, TransliteratedOtherEntityName 1..5.
    """
    legal = row[ix["Entity.LegalName"]].strip()
    if not legal:
        raise ValueError("blank Entity.LegalName")

    out = [(legal, LEGAL, "same")]
    seen = {legal.casefold()}
    for value_col, type_col in OTHER_NAME_SLOTS + TRANSLITERATED_SLOTS:
        name = row[ix[value_col]].strip()
        if not name:
            continue
        alias_type = row[ix[type_col]].strip()
        if alias_type not in KNOWN_ALIAS_TYPES:
            raise ValueError(f"unknown alias type {alias_type!r} in column {type_col}")
        key = name.casefold()
        if key in seen:
            continue
        seen.add(key)
        out.append((name, alias_type, script_relation(name, legal)))
    return out


def entity_records(row, ix):
    """One record dict per distinct name, each carrying the entity's whole payload.

    `_ordinal` is assigned over the FULL alias list, before gating, so a gated record has
    the same id in records.csv and records-full.csv.
    """
    lei = row[ix["LEI"]].strip()
    payload = {corpus_col: row[ix[gleif_col]].strip()
               for corpus_col, gleif_col in ADDRESS_COLUMNS}

    records = []
    for ordinal, (name, alias_type, relation) in enumerate(entity_aliases(row, ix)):
        record = dict(payload)
        record["id"] = record_id(lei, ordinal)
        record["organization_name"] = name
        record["alias_type"] = alias_type
        record["script_relation"] = relation
        record["_ordinal"] = ordinal
        record["_gated"] = is_gated(alias_type, relation)
        records.append(record)
    return records


def record_id(lei, ordinal):
    return f"gleif-{lei}-{ordinal}"


def lei_from_record_id(rid):
    """'gleif-<LEI>-<ordinal>' -> '<LEI>'. Used by the INDEPENDENT true-pair recount."""
    if not rid.startswith("gleif-"):
        raise ValueError(f"not a gleif record id: {rid!r}")
    return rid[len("gleif-"):rid.rindex("-")]


def true_pairs_from_sizes(sizes):
    """Sum of C(n,2) over entity sizes -- the same formula build-sec-recall-corpus.py uses."""
    return sum(n * (n - 1) // 2 for n in sizes)


def recount_true_pairs(records_csv_path):
    """Recount true pairs from records.csv ALONE, deriving membership from record ids.

    Deliberately shares no state with the counter that emitted ground truth: spec check 7.
    Streams, and relies only on ids being grouped, not on the file being sorted.
    """
    sizes = {}
    with open(records_csv_path, encoding="utf-8", newline="") as fh:
        reader = csv.reader(fh)
        header = next(reader)
        id_col = header.index("id")
        for row in reader:
            lei = lei_from_record_id(row[id_col])
            sizes[lei] = sizes.get(lei, 0) + 1
    return true_pairs_from_sizes(sizes.values())


def sample_fraction(lei):
    """Deterministic value in [0,1) from blake2b(LEI), for entity-level sampling.

    Hash-based, never prefix-based: GLEIF is written in ascending LEI order and the LEI
    prefix encodes the issuing LOU, which correlates with jurisdiction and therefore with
    script. A file-prefix sample reads Region at 76.0% against 65.9% corpus-wide.
    """
    digest = hashlib.blake2b(lei.encode("utf-8"), digest_size=8).digest()
    return int.from_bytes(digest, "big") / 2 ** 64


def sha256(path):
    """Uppercase hex SHA-256, streamed -- same convention as the SEC builder."""
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def _read_ids(path):
    """Yield record ids from a records.csv, streaming."""
    with open(path, encoding="utf-8", newline="") as fh:
        reader = csv.reader(fh)
        id_col = next(reader).index("id")
        for row in reader:
            yield row[id_col]


def _sort_key(rid):
    """(LEI, ordinal) -- the output ordering. Ordinal sorts numerically, not as text."""
    lei = lei_from_record_id(rid)
    return lei, int(rid[rid.rindex("-") + 1:])


def check_ids_unique_and_sorted(records_csv_path):
    """Spec check 2. Sortedness makes every other check an O(1)-memory merge."""
    problems, previous = [], None
    for rid in _read_ids(records_csv_path):
        key = _sort_key(rid)
        if previous is not None:
            if key == previous[1]:
                problems.append(f"duplicate record id {rid}")
            elif key < previous[1]:
                problems.append(f"record id {rid} sorts before {previous[0]}")
        previous = (rid, key)
        if len(problems) >= 10:
            break
    return problems


def check_gated_is_subset(gated_csv, full_csv):
    """Spec check 1. Lockstep merge; both files share the (LEI, ordinal) ordering."""
    problems = []
    full = _read_ids(full_csv)
    current = next(full, None)
    for rid in _read_ids(gated_csv):
        target = _sort_key(rid)
        while current is not None and _sort_key(current) < target:
            current = next(full, None)
        if current is None or _sort_key(current) != target:
            problems.append(f"gated record {rid} is not present in {os.path.basename(full_csv)}")
            if len(problems) >= 10:
                break
        else:
            current = next(full, None)
    return problems


def check_truth_covers_records(records_csv, truth_csv):
    """Every record labelled exactly once, key == the LEI in its own id.

    Report mode of `corpus audit` does NOT enforce record/ground-truth id-set equality --
    only --compare-baseline does -- so this check is what proves label completeness.
    """
    problems = []
    with open(truth_csv, encoding="utf-8", newline="") as fh:
        reader = csv.reader(fh)
        header = next(reader)
        rid_col, key_col = header.index("record_id"), header.index("canonical_key")
        truth = ((row[rid_col], row[key_col]) for row in reader)

        for rid in _read_ids(records_csv):
            entry = next(truth, None)
            if entry is None:
                problems.append(f"record {rid} has no ground-truth row")
                break
            if entry[0] != rid:
                problems.append(f"ground truth is out of step: expected {rid}, saw {entry[0]}")
                break
            expected_key = lei_from_record_id(rid)
            if entry[1] != expected_key:
                problems.append(f"record {rid} keyed {entry[1]}, expected {expected_key}")
                if len(problems) >= 10:
                    break
        else:
            if next(truth, None) is not None:
                problems.append("ground truth has rows with no corresponding record")
    return problems


def check_sample_entity_complete(sample_csv, gated_csv):
    """Spec check 3. Every entity in the sample brings ALL of its gated records.

    Counts per LEI in both files; the sample's LEI set is small enough to hold.
    """
    wanted = {}
    for rid in _read_ids(sample_csv):
        lei = lei_from_record_id(rid)
        wanted[lei] = wanted.get(lei, 0) + 1

    actual = {}
    for rid in _read_ids(gated_csv):
        lei = lei_from_record_id(rid)
        if lei in wanted:
            actual[lei] = actual.get(lei, 0) + 1

    problems = []
    for lei in sorted(wanted):
        if wanted[lei] != actual.get(lei, 0):
            problems.append(f"entity {lei} is split: {wanted[lei]} record(s) in the sample, "
                            f"{actual.get(lei, 0)} in the gate corpus")
            if len(problems) >= 10:
                break
    return problems


def field_fingerprint(records_csv, slice_size=500):
    """Spec check 6: per-column population rates plus a hash over a deterministic slice.

    Partition and id checks cannot catch a CONSISTENTLY wrong build. A swapped
    city/region mapping changes neither the record count nor the partition -- it changes
    this hash. The slice is taken at a fixed stride so it spans the whole file rather
    than its (LOU-correlated) head.
    """
    with open(records_csv, encoding="utf-8", newline="") as fh:
        total = sum(1 for _ in fh) - 1
    stride = max(1, total // slice_size)

    populated = {c: 0 for c in RECORD_COLUMNS}
    digest = hashlib.sha256()
    taken = 0
    with open(records_csv, encoding="utf-8", newline="") as fh:
        reader = csv.reader(fh)
        header = next(reader)
        for index, row in enumerate(reader):
            for name, value in zip(header, row):
                if value:
                    populated[name] += 1
            if index % stride == 0 and taken < slice_size:
                digest.update(("\x1f".join(row) + "\x1e").encode("utf-8"))
                taken += 1

    return {
        "records": total,
        "population": {c: (populated[c] / total if total else 0.0) for c in RECORD_COLUMNS},
        "sliceSize": taken,
        "sliceStride": stride,
        "sliceSha256": digest.hexdigest(),
    }
