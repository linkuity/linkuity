"""Build the GLEIF labelled multi-field organization corpus.

Emits a gated corpus, a full corpus, a 200k iteration sample, a small cross-source CIK
probe, and corpus.manifest.json. The source snapshot is pinned by SHA-256, not by row
counts: a different snapshot that happens to preserve the counts must NOT be accepted
silently.

Design: linkuity/docs/superpowers/specs/2026-08-05-gleif-labelled-org-corpus-design.md

Stages run in order by default; --stage re-runs one of them against the existing temp
directory, so fixing a verification bug does not cost another pass over 4.9 GB.

    python build-gleif-org-corpus.py
    python build-gleif-org-corpus.py --stage verify
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
    "aliasTypes": {
        "LEGAL": 3_385_273,
        "PREVIOUS_LEGAL_NAME": 270_924,
        "TRADING_OR_OPERATING_NAME": 150_076,
        "ALTERNATIVE_LANGUAGE_LEGAL_NAME": 54_329,
        "PREFERRED_ASCII_TRANSLITERATED_LEGAL_NAME": 94_747,
        "AUTO_ASCII_TRANSLITERATED_LEGAL_NAME": 65_256,
    },
    "fieldSliceSha256": None,
}

def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stage", choices=g.STAGE_ORDER,   # one definition, in the module
                        help="run one stage against the existing temp directory")
    args = parser.parse_args(argv)

    config = g.BuildConfig(gleif_path=GLEIF, sec_path=SEC, out_dir=OUT,
                           expected=EXPECTED, sample_target=200_000,
                           expected_gleif_sha256=EXPECTED_GLEIF_SHA256,
                           expected_sec_sha256=EXPECTED_SEC_SHA256)
    stages = [args.stage] if args.stage else g.STAGE_ORDER
    return g.run_build(config, stages)


if __name__ == "__main__":
    sys.exit(main())
