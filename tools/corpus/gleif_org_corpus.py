"""Pure logic for the GLEIF labelled organization corpus.

Everything that can be *wrong* lives here rather than in the driver, because the driver
reads a 4.9 GB file and this module can be tested in milliseconds. See
linkuity/docs/superpowers/specs/2026-08-05-gleif-labelled-org-corpus-design.md.

No I/O side effects at import time.
"""
import csv
import dataclasses
import functools
import hashlib
import json
import os
import shutil
import sys
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

# --- address observation extraction ----------------------------------------------
# Phase 0.2 read Entity.LegalAddress.* only and copied it onto every alias row, which
# made seven of eight corpus fields invariant within an entity by construction (see
# 2026-08-10-phase-0.4-two-observation-corpus.md). This extracts the addresses that
# defect ignored: legal, headquarters, and OtherAddress.N.
def _address_field_columns(prefix):
    """(corpus field name, GLEIF column) for the five address fields under `prefix`.

    Field order is address_line, city, region, postal_code, country -- the same order
    ADDRESS_COLUMNS already uses, so LEGAL_ADDRESS_COLUMNS below is exactly its first
    five entries. This is NOT the order the plan's prose lists them in (it writes
    country before postal_code); nothing depends on that prose order, only on all five
    fields being present and compared as one tuple.
    """
    return [
        ("address_line", f"{prefix}.FirstAddressLine"),
        ("city", f"{prefix}.City"),
        ("region", f"{prefix}.Region"),
        ("postal_code", f"{prefix}.PostalCode"),
        ("country", f"{prefix}.Country"),
    ]


LEGAL_ADDRESS_COLUMNS = ADDRESS_COLUMNS[:5]
HEADQUARTERS_ADDRESS_COLUMNS = _address_field_columns("Entity.HeadquartersAddress")

_OTHER_ADDRESS_PREFIX = "Entity.OtherAddresses.OtherAddress."
_OTHER_ADDRESS_SUFFIX = ".FirstAddressLine"


def other_address_slot_numbers(header):
    """Ascending N for every 'Entity.OtherAddresses.OtherAddress.N.*' group in `header`.

    Discovered from the header rather than hard-coded like OTHER_NAME_SLOTS: GLEIF's pad
    width is a snapshot fact, not a constant this module should assume. One column per
    slot (FirstAddressLine) is enough to detect the slot; GLEIF pads every field of a
    slot together, so the other four are assumed present alongside it.
    """
    numbers = []
    for name in header:
        if name.startswith(_OTHER_ADDRESS_PREFIX) and name.endswith(_OTHER_ADDRESS_SUFFIX):
            middle = name[len(_OTHER_ADDRESS_PREFIX):-len(_OTHER_ADDRESS_SUFFIX)]
            if middle.isdigit():
                numbers.append(int(middle))
    return sorted(numbers)


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
    """((name, alias_type, script_relation) per distinct name, LEGAL first), plus drops.

    Distinctness is case-folded; first occurrence in source order wins its type. Source
    order is: legal name, OtherEntityName 1..5, TransliteratedOtherEntityName 1..5.

    Returns (aliases, drops) with drops == {"blankName": n, "dedupedCaseFold": n}. Both
    are populated alias slots this function DECLINES to emit, so both get a named counter
    rather than a bare `continue` -- the build's Global Constraint, and the third instance
    of this defect class found in this corpus. The function stays PURE: it runs once per
    source row and run_parse accumulates, instead of this holding state across calls.
    """
    legal = row[ix["Entity.LegalName"]].strip()
    if not legal:
        raise ValueError("blank Entity.LegalName")

    out = [(legal, LEGAL, "same")]
    drops = {"blankName": 0, "dedupedCaseFold": 0}
    seen = {legal.casefold()}
    for value_col, type_col in OTHER_NAME_SLOTS + TRANSLITERATED_SLOTS:
        name = row[ix[value_col]].strip()
        alias_type = row[ix[type_col]].strip()
        if not name and not alias_type:
            # An UNUSED slot, not a declined row: GLEIF pads every entity out to 5 + 5
            # name/type column pairs, so the overwhelming majority of slots are empty
            # pairs carrying no alias at all. There is nothing here to count.
            continue
        # The type check runs BEFORE the blank-name check on purpose. A garbage type on a
        # blank-name slot is still a snapshot change, and skipping the slot on its blank
        # name first would route it past the abort-do-not-bucket protection.
        if alias_type not in KNOWN_ALIAS_TYPES:
            raise ValueError(f"unknown alias type {alias_type!r} in column {type_col}")
        if not name:
            drops["blankName"] += 1
            continue
        key = name.casefold()
        if key in seen:
            drops["dedupedCaseFold"] += 1
            continue
        seen.add(key)
        out.append((name, alias_type, script_relation(name, legal)))
    return out, drops


def entity_records(row, ix):
    """(one record dict per distinct name, alias drop counts) for this entity.

    Each record carries the entity's whole payload. `_ordinal` is assigned over the FULL
    alias list, before gating, so a gated record has the same id in records.csv and
    records-full.csv. The drop counts are passed straight through from entity_aliases so
    run_parse can accumulate them without reaching past this function.
    """
    lei = row[ix["LEI"]].strip()
    payload = {corpus_col: row[ix[gleif_col]].strip()
               for corpus_col, gleif_col in ADDRESS_COLUMNS}

    aliases, drops = entity_aliases(row, ix)
    records = []
    for ordinal, (name, alias_type, relation) in enumerate(aliases):
        record = dict(payload)
        record["id"] = record_id(lei, ordinal)
        record["organization_name"] = name
        record["alias_type"] = alias_type
        record["script_relation"] = relation
        record["_ordinal"] = ordinal
        record["_gated"] = is_gated(alias_type, relation)
        records.append(record)
    return records, drops


def entity_addresses(row, ix, other_slot_numbers):
    """(distinct address tuples for this entity, drops), legal first, then headquarters,
    then OtherAddress slots in ascending N.

    An address is (address_line, city, region, postal_code, country), trimmed.
    Distinctness is by the WHOLE tuple, case-folded: first occurrence in source order
    wins its original casing, exactly as entity_aliases keeps the first-seen name's
    casing. Source order is fixed and is what Task 3's pairing depends on staying
    stable across builds.

    Returns (addresses, drops) with drops == {"blankAddress": n, "dedupedCaseFold": n}.
    A group whose five fields are ALL blank is an unused OtherAddress slot -- GLEIF pads
    a fixed number of slots per entity and the overwhelming majority go unused -- or, in
    principle, an absent headquarters address. A group that repeats an address already
    seen (legal == headquarters is the common case, ~68% of entities per the plan) is a
    duplicate. Neither is an address this function emits, and both get a NAMED counter
    rather than a bare `continue` -- the module's Global Constraint (see entity_aliases).
    The function is PURE: no I/O, callable once per source row.
    """
    groups = [LEGAL_ADDRESS_COLUMNS, HEADQUARTERS_ADDRESS_COLUMNS]
    groups += [_address_field_columns(f"Entity.OtherAddresses.OtherAddress.{n}")
               for n in other_slot_numbers]

    out = []
    seen = set()
    drops = {"blankAddress": 0, "dedupedCaseFold": 0}
    for columns in groups:
        address = tuple(row[ix[gleif_col]].strip() for _, gleif_col in columns)
        if not any(address):
            drops["blankAddress"] += 1
            continue
        key = tuple(field.casefold() for field in address)
        if key in seen:
            drops["dedupedCaseFold"] += 1
            continue
        seen.add(key)
        out.append(address)
    return out, drops


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


@dataclasses.dataclass
class BuildConfig:
    """Everything the build depends on, injectable so the tests exercise the real path.

    `expected` maps observed-count name -> pinned value. A missing key or a None value
    means "not pinned yet": the build runs every stage, prints a paste-ready block, and
    refuses to publish. Same convention as build-sec-recall-corpus.py.
    """
    gleif_path: str
    sec_path: str
    out_dir: str
    expected: dict
    sample_target: int = 200_000
    expected_gleif_sha256: str = ""
    expected_sec_sha256: str = ""


def tmp_dir(config):
    return os.path.join(config.out_dir, ".build-tmp")


CIK_AUTHORITY = "RA000665"


def _open_pair(directory, records_name, truth_name):
    rf = open(os.path.join(directory, records_name), "w", newline="", encoding="utf-8")
    tf = open(os.path.join(directory, truth_name), "w", newline="", encoding="utf-8")
    rw, tw = csv.writer(rf), csv.writer(tf)
    rw.writerow(RECORD_COLUMNS)
    tw.writerow(["record_id", "canonical_key"])
    return rf, tf, rw, tw


def run_parse(config):
    """One streaming pass over GLEIF; writes both variants and the CIK candidate set.

    Peak memory is the current row plus the CIK candidate map (a few thousand entries),
    so it is independent of corpus size.
    """
    csv.field_size_limit(10_000_000)
    out = tmp_dir(config)
    os.makedirs(out, exist_ok=True)

    alias_types, script_relations = {}, {}
    # Alias slots that were populated in the source but declined by entity_aliases. Named
    # counters, never a bare skip: reconciling the source vocabulary against the emitted
    # aliasTypes is how these ~5k rows were found missing in the first place.
    alias_drops = {"blankName": 0, "dedupedCaseFold": 0}
    entities = gated_records = full_records = 0
    gated_pairs = full_pairs = 0
    cik = {"rows": 0, "numeric": 0, "seriesIds": 0, "empty": 0,
           "wrongAuthorityCikShaped": 0, "duplicateCikKeys": 0}
    cik_candidates = {}

    gf, gt, gw, gtw = _open_pair(out, "records.csv", "ground-truth.csv")
    ff, ft, fw, ftw = _open_pair(out, "records-full.csv", "ground-truth-full.csv")
    try:
        with open(config.gleif_path, encoding="utf-8", newline="") as fh:
            reader = csv.reader(fh)
            ix = column_index(next(reader))
            authority_col = ix["Entity.RegistrationAuthority.RegistrationAuthorityID"]
            entity_id_col = ix["Entity.RegistrationAuthority.RegistrationAuthorityEntityID"]

            for lineno, row in enumerate(reader, start=2):
                try:
                    records, drops = entity_records(row, ix)
                except ValueError as exc:
                    raise ValueError(f"line {lineno}: {exc}") from exc
                for reason, count in drops.items():
                    alias_drops[reason] += count

                entities += 1
                lei = row[ix["LEI"]].strip()
                gated_in_entity = 0
                for record in records:
                    alias_types[record["alias_type"]] = alias_types.get(record["alias_type"], 0) + 1
                    script_relations[record["script_relation"]] = \
                        script_relations.get(record["script_relation"], 0) + 1
                    values = [record[c] for c in RECORD_COLUMNS]
                    fw.writerow(values)
                    ftw.writerow([record["id"], lei])
                    full_records += 1
                    if record["_gated"]:
                        gw.writerow(values)
                        gtw.writerow([record["id"], lei])
                        gated_records += 1
                        gated_in_entity += 1
                full_pairs += true_pairs_from_sizes([len(records)])
                gated_pairs += true_pairs_from_sizes([gated_in_entity])

                authority = row[authority_col].strip()
                entity_id = row[entity_id_col].strip()
                if authority == CIK_AUTHORITY:
                    cik["rows"] += 1
                    if not entity_id:
                        cik["empty"] += 1
                    elif entity_id.isdigit():
                        cik["numeric"] += 1
                        key = entity_id.zfill(10)
                        if key in cik_candidates:
                            # Two LEIs claiming one CIK. Measured: 14 of these exist, and
                            # they are NOT all duplicate registrations -- several are a fund
                            # trust and one of its series (RBC FUNDS TRUST vs RBC BlueBay
                            # Emerging Market Unconstrained Fixed Income Fund), i.e. distinct
                            # legal entities sharing one SEC filing. Keying both to the CIK
                            # would assert they are the same entity, so the collision is
                            # DROPPED -- but counted, never silently, because a bare
                            # overwrite here is what made 5,045 and 5,031 both look correct.
                            cik["duplicateCikKeys"] += 1
                            continue
                        cik_candidates[key] = {
                            "lei": lei,
                            "record": {c: records[0][c] for c in RECORD_COLUMNS},
                        }
                    else:
                        cik["seriesIds"] += 1
                elif entity_id.isdigit() and len(entity_id.lstrip("0")) <= 10:
                    # British Virgin Islands and Cayman company numbers collide with
                    # zero-padded CIKs. Counted, never joined -- spec section 2.
                    cik["wrongAuthorityCikShaped"] += 1
    finally:
        for handle in (gf, gt, ff, ft):
            handle.close()

    observed = {
        "entities": entities,
        "gatedRecords": gated_records,
        "fullRecords": full_records,
        "gatedTruePairs": gated_pairs,
        "fullTruePairs": full_pairs,
        "aliasTypes": dict(sorted(alias_types.items())),
        "aliasDrops": dict(sorted(alias_drops.items())),
        "scriptRelations": dict(sorted(script_relations.items())),
        "cik": cik,
    }
    with open(os.path.join(out, "cik-candidates.json"), "w", encoding="utf-8") as fh:
        json.dump(cik_candidates, fh, sort_keys=True)
    return observed   # run_build caches this to parse-counts.json


def run_sample(config):
    """Draw a bounded iteration sample from the GATE corpus, by entity.

    Selection is `sample_fraction(lei) < p` with p = target / gated_records, so it is
    deterministic, uncorrelated with LEI prefix, and needs no second pass to size. The
    resulting record count lands NEAR the target rather than on it; the observed value is
    pinned rather than forced, because forcing it would mean truncating an entity.
    """
    out = tmp_dir(config)
    gated_path = os.path.join(out, "records.csv")
    with open(gated_path, encoding="utf-8", newline="") as fh:
        gated_records = sum(1 for _ in fh) - 1
    probability = min(1.0, config.sample_target / gated_records) if gated_records else 0.0

    sizes, entities = {}, 0
    with open(gated_path, encoding="utf-8", newline="") as rf, \
         open(os.path.join(out, "records-200k.csv"), "w", newline="", encoding="utf-8") as sf, \
         open(os.path.join(out, "ground-truth-200k.csv"), "w", newline="", encoding="utf-8") as tf:
        reader = csv.reader(rf)
        header = next(reader)
        id_col = header.index("id")
        sw, tw = csv.writer(sf), csv.writer(tf)
        sw.writerow(RECORD_COLUMNS)
        tw.writerow(["record_id", "canonical_key"])

        current_lei, keep = None, False
        for row in reader:
            lei = lei_from_record_id(row[id_col])
            if lei != current_lei:
                current_lei = lei
                keep = sample_fraction(lei) < probability
                if keep:
                    entities += 1
            if keep:
                sw.writerow(row)
                tw.writerow([row[id_col], lei])
                sizes[lei] = sizes.get(lei, 0) + 1

    return {
        "sampleRecords": sum(sizes.values()),
        "sampleEntities": entities,
        "sampleTruePairs": true_pairs_from_sizes(sizes.values()),
        "sampleProbability": probability,
    }


def read_sec_names(sec_path, wanted_ciks):
    """CIK -> ordered names, for the wanted CIKs only. Also returns the blank-name count.

    The lookup file is latin-1, one 'NAME:CIK:' per line, and names may contain colons --
    split on the LAST one, exactly as build-sec-recall-corpus.py does.
    """
    names = {}
    malformed = []
    blank_name_rows = 0
    with open(sec_path, encoding="latin-1") as fh:
        for lineno, raw in enumerate(fh, start=1):
            raw = raw.strip()
            if not raw:
                continue
            body = raw[:-1] if raw.endswith(":") else raw
            i = body.rfind(":")
            if i < 0:
                malformed.append((lineno, raw[:80]))
                continue
            if i == 0:
                # BLANK-NAME row: a valid CIK with a zero-length name. Four exist in this
                # snapshot and each of those CIKs has a real named entry elsewhere, so the
                # exclusion loses no registrant. COUNTED, not silently skipped -- the SEC
                # builder carries a long comment about these same four rows, because a bare
                # `continue` here once put two different record counts into circulation.
                blank_name_rows += 1
                continue
            cik = body[i + 1:]
            if cik in wanted_ciks:
                names.setdefault(cik, []).append(body[:i])
    if malformed:
        raise ValueError(f"{len(malformed)} unparsable SEC row(s), first at line {malformed[0][0]}")
    return names, blank_name_rows


def run_cik(config):
    """Cross-source probe: one GLEIF record and every SEC name, keyed by CIK.

    Small by construction (12,636 true pairs) and never gated. It exists because it is
    the only labelled data here where one entity appears with sharply different field
    coverage on different records -- the Principle 7 case. Spec section 4.1.
    """
    out = tmp_dir(config)
    with open(os.path.join(out, "cik-candidates.json"), encoding="utf-8") as fh:
        candidates = json.load(fh)

    sec_names, sec_blank_name_rows = read_sec_names(config.sec_path, set(candidates))
    joined = sorted(c for c in candidates if c in sec_names)

    cik_dir = os.path.join(out, "cik")
    os.makedirs(cik_dir, exist_ok=True)
    sizes = []
    seen_leis = {}
    duplicate_leis = 0
    with open(os.path.join(cik_dir, "records.csv"), "w", newline="", encoding="utf-8") as rf, \
         open(os.path.join(cik_dir, "ground-truth.csv"), "w", newline="", encoding="utf-8") as tf:
        rw, tw = csv.writer(rf), csv.writer(tf)
        rw.writerow(RECORD_COLUMNS)
        tw.writerow(["record_id", "canonical_key"])
        for cik in joined:
            candidate = candidates[cik]
            lei = candidate["lei"]
            if lei in seen_leis:
                duplicate_leis += 1
            seen_leis[lei] = cik

            gleif_id = f"cik-{cik}-gleif-0"
            record = dict(candidate["record"])
            record["id"] = gleif_id
            rw.writerow([record[c] for c in RECORD_COLUMNS])
            tw.writerow([gleif_id, cik])

            for ordinal, name in enumerate(sec_names[cik]):
                sec_id = f"cik-{cik}-sec-{ordinal}"
                row = {c: "" for c in RECORD_COLUMNS}
                row["id"] = sec_id
                row["organization_name"] = name
                row["alias_type"] = LEGAL
                row["script_relation"] = "same"
                rw.writerow([row[c] for c in RECORD_COLUMNS])
                tw.writerow([sec_id, cik])
            sizes.append(1 + len(sec_names[cik]))

    unresolved = sum(1 for c in candidates if c not in sec_names)
    return {
        "cikEntities": len(joined),
        "cikRecords": sum(sizes),
        "cikTruePairs": true_pairs_from_sizes(sizes),
        "cikUnresolvedNumeric": unresolved,
        "cikDuplicateLeis": duplicate_leis,
        "cikSecBlankNameRows": sec_blank_name_rows,
    }


STAGE_ORDER = ["parse", "sample", "cik", "verify", "publish"]

PUBLISHED_FILES = [
    "records.csv", "ground-truth.csv",
    "records-full.csv", "ground-truth-full.csv",
    "records-200k.csv", "ground-truth-200k.csv",
    os.path.join("cik", "records.csv"), os.path.join("cik", "ground-truth.csv"),
]

SNAPSHOT = "20260727-1600"

# The manifest keys this build GENERATES. Everything else found in an existing manifest is
# foreign and is carried forward by run_publish -- see the comment there.
BUILD_OWNED_MANIFEST_KEYS = frozenset({
    "snapshot", "gleifSource", "secSource", "gleifSourceSha256", "secSourceSha256",
    "sha256", "counts", "fieldFingerprint", "gatedAliasTypes",
})


def _flatten(observed, prefix=""):
    """Flatten nested observed counts to dotted keys so expectations can pin any of them."""
    flat = {}
    for key, value in observed.items():
        name = f"{prefix}{key}"
        if isinstance(value, dict):
            flat.update(_flatten(value, prefix=f"{name}."))
        else:
            flat[name] = value
    return flat


# Expectation keys that only run_verify can produce. Nothing else in the build computes
# the field fingerprint, so on a partial run these are absent BY DESIGN, not wrong.
VERIFY_DERIVED_EXPECTATIONS = ("fieldSliceSha256", "fieldFingerprint")


def _is_verify_derived(key):
    """True for a flattened expectation key that only exists once verify has run."""
    return key.split(".")[0] in VERIFY_DERIVED_EXPECTATIONS


def check_expectations(observed, expected, verify_ran=True):
    """-> (problems, unpinned). None or absent means 'not pinned yet'.

    `verify_ran` says whether run_verify ran in THIS invocation. When it did not, the
    verify-derived keys are skipped entirely: comparing them against a value nothing
    computed made `--stage parse`, `--stage sample` and `--stage cik` each abort with
    "CORPUS INTEGRITY FAILURE ... observed None" on a completely correct run. This
    narrows WHICH keys are checked on a partial run; it never weakens the check when
    verify did run, which is the only run that can publish.
    """
    flat_observed, flat_expected = _flatten(observed), _flatten(expected)
    problems, unpinned = [], []
    for key, want in flat_expected.items():
        if not verify_ran and _is_verify_derived(key):
            continue
        if want is None:
            unpinned.append(key)
            continue
        got = flat_observed.get(key)
        if got != want:
            problems.append(f"{key}: observed {got:,} != expected {want:,}"
                            if isinstance(got, int) and isinstance(want, int)
                            else f"{key}: observed {got!r} != expected {want!r}")
    return problems, unpinned


def run_verify(config, observed):
    """Spec section 8, checks 1-3 and 6-8. Returns a list of problems; empty means pass.

    Also computes the field fingerprint and writes `fieldSliceSha256` into `observed`, so
    a swapped column mapping is a pinnable expectation rather than a manual eyeball.
    """
    out = tmp_dir(config)
    p = lambda *parts: os.path.join(out, *parts)
    problems = []

    observed["fieldFingerprint"] = field_fingerprint(p("records.csv"))
    observed["fieldSliceSha256"] = observed["fieldFingerprint"]["sliceSha256"]

    problems += check_gated_is_subset(p("records.csv"), p("records-full.csv"))
    for name in ["records.csv", "records-full.csv", "records-200k.csv"]:
        problems += [f"{name}: {x}" for x in check_ids_unique_and_sorted(p(name))]
    for records, truth in [("records.csv", "ground-truth.csv"),
                           ("records-full.csv", "ground-truth-full.csv"),
                           ("records-200k.csv", "ground-truth-200k.csv")]:
        problems += [f"{records}: {x}" for x in check_truth_covers_records(p(records), p(truth))]
    problems += check_sample_entity_complete(p("records-200k.csv"), p("records.csv"))

    # Check 7: recount with code that shares no state with the emitting counter.
    for name, key in [("records.csv", "gatedTruePairs"),
                      ("records-full.csv", "fullTruePairs"),
                      ("records-200k.csv", "sampleTruePairs")]:
        recount = recount_true_pairs(p(name))
        if recount != observed.get(key):
            problems.append(f"{name}: independent recount {recount:,} != emitted "
                            f"{observed.get(key)!r}")

    # Check 8: the CIK probe's join cardinality.
    if observed.get("cikDuplicateLeis"):
        problems.append(f"{observed['cikDuplicateLeis']} LEI(s) map to more than one CIK")
    with open(p("cik", "ground-truth.csv"), encoding="utf-8", newline="") as fh:
        reader = csv.reader(fh)
        next(reader)
        bad = [row[1] for row in reader if len(row[1]) != 10 or not row[1].isdigit()]
    if bad:
        problems.append(f"{len(bad)} CIK key(s) are not 10-digit numerics, e.g. {bad[0]!r}")

    # Accounting identities over counters that are incremented in DIFFERENT places. They
    # held when checked by hand during review; in code they are the mechanism that catches
    # the NEXT uncounted `continue`, because any new silent drop makes one side short.
    cik = observed["cik"]
    for left_name, left, right_name, right in [
        ("cik.numeric", cik["numeric"],
         "cikEntities + cikUnresolvedNumeric + cik.duplicateCikKeys",
         observed["cikEntities"] + observed["cikUnresolvedNumeric"] + cik["duplicateCikKeys"]),
        ("cik.rows", cik["rows"],
         "cik.numeric + cik.seriesIds + cik.empty",
         cik["numeric"] + cik["seriesIds"] + cik["empty"]),
        ("fullRecords", observed["fullRecords"],
         "sum(aliasTypes)", sum(observed["aliasTypes"].values())),
    ]:
        if left != right:
            problems.append(f"reconciliation failed: {left_name} = {left:,} "
                            f"!= {right_name} = {right:,}")

    return problems


def run_publish(config, observed):
    """Move the temp build into place and write the manifest LAST.

    os.replace, not shutil.move: on Windows shutil.move raises when the target already
    exists, which would make the second run of the byte-reproducibility check fail for a
    reason that has nothing to do with reproducibility.
    """
    out, tmp = config.out_dir, tmp_dir(config)
    os.makedirs(os.path.join(out, "cik"), exist_ok=True)
    manifest_path = os.path.join(out, "corpus.manifest.json")
    previous_manifest_path = manifest_path + ".prev"

    # The manifest is a DURABLE RECORD, not just a build artifact: it accumulates
    # measurement this build cannot produce (today a ~12 KB `budget` block -- 78 minutes
    # and 9.3 GB of full-corpus audit, added out of band). Regeneration must therefore be
    # ADDITIVE over foreign keys: the build refreshes exactly BUILD_OWNED_MANIFEST_KEYS and
    # carries everything else forward untouched. Rebuilding from a fixed key set instead
    # would delete that measurement silently.
    # Where to read the foreign keys from. The canonical manifest wins when both exist --
    # it is the newer of the two. `.prev` is the fallback, and that is not a corner case:
    # it is EXACTLY the state a crashed publish leaves behind, and the obvious response to
    # a crash is to re-run the build. Reading only the canonical path would mean a routine
    # retry rebuilt the manifest with no foreign keys and then deleted the one file that
    # still held them -- destroying the `budget` block silently, one retry cycle later.
    # So .prev is BOTH written and read: it is the recovery source across a failed publish,
    # not a staging artifact.
    read_from = None
    if os.path.exists(manifest_path):
        read_from = manifest_path
    elif os.path.exists(previous_manifest_path):
        read_from = previous_manifest_path

    carried = {}
    if read_from is not None:
        # Refusing to publish over a manifest that cannot be read is the correct outcome:
        # its foreign keys cannot be carried, and continuing would drop them. Named, not a
        # bare traceback, and raised BEFORE anything is renamed, moved or removed.
        try:
            with open(read_from, encoding="utf-8") as fh:
                existing = json.load(fh)
        except json.JSONDecodeError as exc:
            raise ValueError(f"{read_from} is not valid JSON ({exc}); refusing to publish, "
                             f"because overwriting it would lose the keys it carries") from exc
        if not isinstance(existing, dict):
            raise ValueError(f"{read_from} is valid JSON but not an object "
                             f"({type(existing).__name__}); refusing to publish, because "
                             f"overwriting it would lose the keys it carries")
        carried = {key: value for key, value in existing.items()
                   if key not in BUILD_OWNED_MANIFEST_KEYS}

    if os.path.exists(manifest_path):
        # RENAMED aside before the move loop, never deleted. Two invariants that used to be
        # in tension, now both held for the whole window:
        #   * the CANONICAL path is absent throughout the loop, so a crash partway through
        #     cannot leave a mixed-generation corpus beside a manifest asserting
        #     completeness -- "no manifest" keeps meaning "the build did not finish";
        #   * the carried foreign keys SURVIVE that crash. They exist in this file and
        #     nowhere else, and re-earning them means re-running a 78-minute, 9.3 GB audit,
        #     so deleting here would trade a millisecond-wide invariant for a durability
        #     hole on the most expensive artifact on the branch.
        # The .prev copy is removed LAST, only once the new manifest is safely written --
        # and, per the block above, only once its keys have been read back into it.
        os.replace(manifest_path, previous_manifest_path)

    hashes = {}
    for name in PUBLISHED_FILES:
        source, target = os.path.join(tmp, name), os.path.join(out, name)
        os.replace(source, target)
        hashes[name.replace(os.sep, "/")] = sha256(target)

    # Computed in run_verify against the same bytes -- do not re-walk 4M records here.
    fingerprint = observed["fieldFingerprint"]
    manifest = dict(carried)
    manifest.update({
        "snapshot": SNAPSHOT,
        "gleifSource": config.gleif_path,
        "secSource": config.sec_path,
        "gleifSourceSha256": config.expected_gleif_sha256 or sha256(config.gleif_path),
        "secSourceSha256": config.expected_sec_sha256 or sha256(config.sec_path),
        "sha256": hashes,
        "counts": observed,
        "fieldFingerprint": fingerprint,
        "gatedAliasTypes": sorted(ALWAYS_GATED)
                           + [f"{t} (same-script only)" for t in sorted(TRANSLITERATED_TYPES)],
    })
    with open(manifest_path, "w", encoding="utf-8") as fh:
        json.dump(manifest, fh, indent=2, sort_keys=True)
    # LAST, and only ever after .prev has been READ: `carried` came from whichever of the
    # two files existed, so by this line its keys are in the manifest just written. That is
    # the first moment .prev stops being the backup and becomes redundant. Deleting it any
    # earlier -- or on a path that never read it -- is the silent-loss bug this guards.
    if os.path.exists(previous_manifest_path):
        os.remove(previous_manifest_path)
    shutil.rmtree(tmp, ignore_errors=True)
    return manifest_path


def run_build(config, stages):
    """Run the requested stages in order. 0 = published, 1 = failed, 2 = not pinned."""
    if "publish" in stages and "verify" not in stages:
        # --stage publish on its own would move an unverified build into place, which is
        # precisely the partial-corpus-that-looks-complete failure the temp dir prevents.
        print("publish requires verify in the same run.", file=sys.stderr)
        return 1

    if "parse" in stages:
        for label, path, expected in [("GLEIF", config.gleif_path, config.expected_gleif_sha256),
                                      ("SEC", config.sec_path, config.expected_sec_sha256)]:
            actual = sha256(path)
            if not expected:
                print(f"{label} SHA-256 is not pinned yet:\n  {actual}", file=sys.stderr)
                return 2
            if actual != expected:
                print(f"{label} SNAPSHOT CHANGED -- refusing to write.\n"
                      f"  expected {expected}\n  actual   {actual}", file=sys.stderr)
                return 1

    # Each stage's counts are cached on disk so `--stage verify` after a code fix costs
    # seconds instead of another pass over 4.9 GB.
    observed = {}
    for stage, runner, cache in [("parse", run_parse, "parse-counts.json"),
                                 ("sample", run_sample, "sample-counts.json"),
                                 ("cik", run_cik, "cik-counts.json")]:
        cache_path = os.path.join(tmp_dir(config), cache)
        if stage in stages:
            counts = runner(config)
            with open(cache_path, "w", encoding="utf-8") as fh:
                json.dump(counts, fh, indent=2, sort_keys=True)
        elif os.path.exists(cache_path):
            with open(cache_path, encoding="utf-8") as fh:
                counts = json.load(fh)
        else:
            print(f"stage '{stage}' was skipped and {cache} does not exist -- run it first.",
                  file=sys.stderr)
            return 1
        observed.update(counts)

    if "verify" in stages:
        problems = run_verify(config, observed)
        if problems:
            print("CORPUS VERIFICATION FAILURE -- refusing to write:", file=sys.stderr)
            for problem in problems:
                print(f"  - {problem}", file=sys.stderr)
            return 1

    mismatches, unpinned = check_expectations(observed, config.expected,
                                              verify_ran="verify" in stages)
    if mismatches:
        print("CORPUS INTEGRITY FAILURE -- refusing to write:", file=sys.stderr)
        for mismatch in mismatches:
            print(f"  - {mismatch}", file=sys.stderr)
        return 1
    if unpinned:
        print("Not pinned yet: " + ", ".join(sorted(unpinned)), file=sys.stderr)
        print(json.dumps(observed, indent=2, sort_keys=True))
        print("Paste these into EXPECTED and re-run.", file=sys.stderr)
        return 2

    if "publish" in stages:
        path = run_publish(config, observed)
        print(f"wrote {observed['gatedRecords']:,} gated records, "
              f"{observed['gatedTruePairs']:,} true pairs -> {path}")
    return 0
