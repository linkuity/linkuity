"""Build the GLEIF labelled multi-field organization corpus.

Emits a gated corpus, a full corpus, a 200k iteration sample, a small cross-source CIK
probe, and corpus.manifest.json. The source snapshot is pinned by SHA-256, not by row
counts: a different snapshot that happens to preserve the counts must NOT be accepted
silently.

Design: linkuity/docs/superpowers/specs/2026-08-05-gleif-labelled-org-corpus-design.md

Stages run in order by default; --stage re-runs a subset of them against the existing
temp directory, so fixing a verification bug does not cost another pass over 4.9 GB.
--stage accepts multiple values because run_build refuses to publish unless "verify" is
in the SAME invocation -- a single-valued --stage could never express "verify then
publish, but skip parse", which is exactly the workflow this exists for. Selected
stages are reported in canonical STAGE_ORDER regardless of the order typed, purely for
readability -- run_build itself tests stage membership, not order, so this is not what
makes verify run before publish.

    python build-gleif-org-corpus.py
    python build-gleif-org-corpus.py --stage verify
    python build-gleif-org-corpus.py --stage verify publish
"""
import argparse
import sys

import gleif_org_corpus as g

GLEIF = r"C:\dev\datasets\gleif\20260727-1600-gleif-goldencopy-lei2-golden-copy.csv"
SEC = r"C:\dev\datasets\sec\cik-lookup-data.txt"
# Phase 0.4 (2026-08-10) rewrote entity_records to pair names with addresses by
# cycling instead of copying the legal address onto every alias row -- see
# linkuity/.superpowers/sdd/2026-08-10-phase-0.4-two-observation-corpus/. That
# changed what this script builds, so its output now goes to a NEW directory rather
# than overwriting the frozen alias-only corpus at C:\dev\datasets\gleif-org, which
# remains the baseline for in-flight comparisons and is no longer reproducible by
# this script's current code.
OUT = r"C:\dev\datasets\gleif-org-two-observation"

EXPECTED_GLEIF_SHA256 = "1BF318EE7EA08160AA45BD1965F61EB2AC669BA5ED5D901113151A72CFE33B07"
EXPECTED_SEC_SHA256 = "5B52D4B2591300A4BCCB254A3BCEC61E7EFE0859B2CD9C6B10CBADC695BC282E"

# Pinned from the first full run against the NEW (Phase 0.4) record definition -- name x
# address cycling, `2026-08-10-phase-0.4-two-observation-corpus.md`. `entities`,
# `aliasDrops`, `aliasTypes`, and every `cik*`/`cik.*` key are UNCHANGED from the old
# alias-only corpus's pinned values: entity_aliases and the CIK stage read only
# `records[0]`, whose name and address are still the entity's legal name paired with its
# legal address (ordinal 0 of both lists) exactly as before Tasks 2/3. Confirmed, not
# assumed: this discovery run's CORPUS INTEGRITY FAILURE output showed zero mismatches on
# any of those keys against the OLD pinned values, which is how those old values ended up
# copied forward unchanged below rather than re-measured.
#
# Keys must match the DOTTED FLATTENING of the observed dict (check_expectations flattens
# both sides), so nested counters are pinned as "cik.seriesIds", not "cikSeriesIds".
EXPECTED = {
    "entities": 3_385_273,
    "gatedRecords": 4_492_972,
    "fullRecords": 4_570_093,
    "gatedTruePairs": 1_207_777,
    "fullTruePairs": 1_314_675,
    "sampleRecords": 200_385,
    "sampleTruePairs": 53_890,
    "cikEntities": 5_021,
    "cikRecords": 12_883,
    "cikTruePairs": 12_636,
    "cikUnresolvedNumeric": 10,
    "cikDuplicateLeis": 0,
    "cikSecBlankNameRows": 4,
    "cik": {
        "rows": 27_500,
        "numeric": 5_045,
        "seriesIds": 22_454,
        "empty": 1,
        "wrongAuthorityCikShaped": 1_455_066,
        "duplicateCikKeys": 14,
    },
    # Populated alias slots the build DECLINES to emit. Reconciling the source vocabulary
    # against aliasTypes is what showed these rows leaving with no counter at all.
    # blankName is 0 for THIS snapshot: every blank alias slot also has a blank type, so it
    # is an unused column pair rather than a declined row. Pinned at 0 rather than dropped,
    # because a snapshot that starts publishing a typed slot with no name must be visible.
    # Unchanged from the alias-only corpus -- entity_aliases itself did not change.
    "aliasDrops": {
        "blankName": 0,
        "dedupedCaseFold": 5_012,
    },
    # aliasTypes counts PER EMITTED RECORD, not per distinct name -- unlike aliasDrops,
    # this DID move even though entity_aliases did not, because cycling now reuses a name
    # (and therefore its alias type) across every record its address list is longer than
    # its name list. The clearest example: 540,734 address-only-multi-record entities
    # (one LEGAL name, 2+ addresses) each contribute their LEGAL type more than once.
    "aliasTypes": {
        "LEGAL": 3_932_065,
        "PREVIOUS_LEGAL_NAME": 270_943,
        "TRADING_OR_OPERATING_NAME": 150_086,
        "ALTERNATIVE_LANGUAGE_LEGAL_NAME": 55_990,
        "PREFERRED_ASCII_TRANSLITERATED_LEGAL_NAME": 95_737,
        "AUTO_ASCII_TRANSLITERATED_LEGAL_NAME": 65_272,
    },
    # New under Phase 0.4 -- entity_addresses' own drop accounting (Task 2).
    "addressDrops": {
        "blankAddress": 16_836_682,
        "dedupedCaseFold": 2_785_065,
    },
    # New under Phase 0.4 -- entities that become multi-record for the FIRST time under
    # this plan: one distinct name, 2+ distinct addresses (Task 3's headline consequence).
    "addressOnlyMultiRecordEntities": 540_734,
    # New under Phase 0.4, job 3 of Task 4: the (distinct name count, distinct address
    # count) joint distribution over every entity. This makes fullRecords'
    # CARDINALITY arithmetically reproducible without re-parsing the 4.9 GB source -- a
    # reader can redo the sum by hand from the numbers below -- and run_verify asserts
    # fullRecords == full_records_from_joint(nameAddressJoint) on every build as a
    # regression tripwire on that formula. CARDINALITY ONLY, per 2026-08-10 review: both
    # sides are aggregations of the same max(n,a) computed in the same pass, so this is
    # true by construction and cannot catch a pairing-CONTENT bug (e.g. cycling always
    # emitting the first name against the first address regardless of ordinal) -- only a
    # future desync between the write loop's record count and this formula. See
    # gleif_org_corpus.py's module comment above name_address_joint_key for the full
    # scope statement and where pairing-content correctness IS actually covered
    # (fieldSliceSha256, TestAddressPairingEndToEnd).
    "nameAddressJoint": {
        "01x01": 2_302_430, "01x02": 539_510, "01x03": 817, "01x04": 407,
        "02x01": 350_368, "02x02": 113_106, "02x03": 1_434, "02x04": 2_696,
        "03x01": 44_114, "03x02": 15_662, "03x03": 315, "03x04": 297,
        "04x01": 6_572, "04x02": 3_475, "04x03": 417, "04x04": 183,
        "05x01": 1_470, "05x02": 768, "05x03": 88, "05x04": 39,
        "06x01": 744, "06x02": 326, "06x03": 6, "06x04": 5,
        "07x01": 14, "07x02": 8,
        "08x01": 1, "08x02": 1,
    },
    # Verify-derived: only run_verify computes the field fingerprint, so check_expectations
    # skips both of these on a run that does not include the verify stage.
    "fieldSliceSha256": "e484c95e9b6c7983fc7ef3304914ecde34f45190ddb2e1eeac7d369e297ba06c",
    # The slice hash covers 500 of the gated records, so it catches a swapped column
    # mapping but only inside the slice. The population rates are the other half of spec
    # check 6: computed over EVERY gated record, so a coverage regression outside the
    # slice -- a field that stops being emitted for one country, say -- moves these and
    # nothing else. `jurisdiction` is NOT 1.0: a handful of gated records lack it.
    "fieldFingerprint": {
        "population": {
            "id": 1.0,
            "organization_name": 1.0,
            "address_line": 1.0,
            "city": 1.0,
            "region": 0.6934490132589297,
            "postal_code": 0.9874468391968613,
            "country": 1.0,
            "jurisdiction": 0.9999984420112121,
            "legal_form": 1.0,
            "alias_type": 1.0,
            "script_relation": 1.0,
        },
    },
}

def build_arg_parser():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stage", choices=g.STAGE_ORDER, nargs="+",
                        help="run these stages against the existing temp directory, "
                             "in STAGE_ORDER; publish requires verify in the same call")
    return parser


def resolve_stages(selected):
    """Return the requested stages in canonical STAGE_ORDER.

    Note this is normalisation, NOT a correctness fix. `run_build` tests stage
    MEMBERSHIP (`"publish" in stages`), never order, so it already runs verify's code
    before publish's code whichever order the list arrives in. Sorting exists so the
    stage list reads canonically wherever it is logged or reported, and so the contract
    stays honest if run_build ever does become order-sensitive.

    The actual defect this file fixed was `--stage` accepting a single value while
    run_build requires verify and publish in the SAME invocation -- which made
    `--stage publish` impossible to satisfy. `nargs="+"` is what fixed that.
    """
    return [s for s in g.STAGE_ORDER if s in selected] if selected else g.STAGE_ORDER


def main(argv=None):
    args = build_arg_parser().parse_args(argv)

    config = g.BuildConfig(gleif_path=GLEIF, sec_path=SEC, out_dir=OUT,
                           expected=EXPECTED, sample_target=200_000,
                           expected_gleif_sha256=EXPECTED_GLEIF_SHA256,
                           expected_sec_sha256=EXPECTED_SEC_SHA256)
    stages = resolve_stages(args.stage)
    return g.run_build(config, stages)


if __name__ == "__main__":
    sys.exit(main())
