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
OUT = r"C:\dev\datasets\gleif-org"

EXPECTED_GLEIF_SHA256 = "1BF318EE7EA08160AA45BD1965F61EB2AC669BA5ED5D901113151A72CFE33B07"
EXPECTED_SEC_SHA256 = "5B52D4B2591300A4BCCB254A3BCEC61E7EFE0859B2CD9C6B10CBADC695BC282E"

# Pinned from the first full run. None means "not pinned": every stage still runs, the
# observed values are printed as a paste-ready block, and nothing is published.
#
# Keys must match the DOTTED FLATTENING of the observed dict (check_expectations flattens
# both sides), so nested counters are pinned as "cik.seriesIds", not "cikSeriesIds".
EXPECTED = {
    "entities": 3_385_273,
    "gatedRecords": 3_946_009,
    "fullRecords": 4_020_605,
    "gatedTruePairs": 657_832,
    "fullTruePairs": 753_033,
    "sampleRecords": 200_411,
    "sampleTruePairs": 33_318,
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
    "aliasDrops": {
        "blankName": 0,
        "dedupedCaseFold": 5_012,
    },
    "aliasTypes": {
        "LEGAL": 3_385_273,
        "PREVIOUS_LEGAL_NAME": 270_924,
        "TRADING_OR_OPERATING_NAME": 150_076,
        "ALTERNATIVE_LANGUAGE_LEGAL_NAME": 54_329,
        "PREFERRED_ASCII_TRANSLITERATED_LEGAL_NAME": 94_747,
        "AUTO_ASCII_TRANSLITERATED_LEGAL_NAME": 65_256,
    },
    # Verify-derived: only run_verify computes the field fingerprint, so check_expectations
    # skips both of these on a run that does not include the verify stage.
    "fieldSliceSha256": "4674d9a11dd58a60e758d06cfb6a4c2bcf4e61e631e2476d6e33209e1d57956d",
    # The slice hash covers 500 of 3.9M records, so it catches a swapped column mapping but
    # only inside the slice. The population rates are the other half of spec check 6: they
    # are computed over EVERY record, so a coverage regression outside the slice -- a field
    # that stops being emitted for one country, say -- moves these and nothing else.
    # jurisdiction is NOT 1.0: ~4 gated records lack it.
    "fieldFingerprint": {
        "population": {
            "id": 1.0,
            "organization_name": 1.0,
            "address_line": 1.0,
            "city": 1.0,
            "region": 0.6789769105949834,
            "postal_code": 0.9876510671921934,
            "country": 1.0,
            "jurisdiction": 0.999998986317568,
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
