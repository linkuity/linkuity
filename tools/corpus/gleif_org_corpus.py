"""Pure logic for the GLEIF labelled organization corpus.

Everything that can be *wrong* lives here rather than in the driver, because the driver
reads a 4.9 GB file and this module can be tested in milliseconds. See
linkuity/docs/superpowers/specs/2026-08-05-gleif-labelled-org-corpus-design.md.

No I/O side effects at import time.
"""
import csv
import functools
import hashlib
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
