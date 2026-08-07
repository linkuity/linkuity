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
                    records = entity_records(row, ix)
                except ValueError as exc:
                    raise ValueError(f"line {lineno}: {exc}") from exc

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
