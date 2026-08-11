# Corpus builders

Scripts that turn a pinned public snapshot into a labelled corpus for `match corpus audit`.

**Code lives here. Data does not.** The corpora themselves are in `C:\dev\datasets\`, outside the
repo, so a multi-GB CSV can never be staged by accident. These scripts are tracked because they
carry pinned SHA-256 hashes and expected counts — the things that make a measurement reproducible —
and those deserve review and history.

| Script | Source | Output |
|---|---|---|
| `build-sec-recall-corpus.py` | `datasets\sec\cik-lookup-data.txt` | `datasets\sec-recall\` — 1,052,432 names / 97,142 true pairs, one field |
| `build-gleif-org-corpus.py` | `datasets\gleif\20260727-1600-...-lei2-golden-copy.csv` | `datasets\gleif-org-two-observation\` — ~4.5M name/address records / ~1.2M true pairs, eight columns, six of them genuinely varying within an entity |

Both pin their source by SHA-256 and refuse to write when an expected count drifts. A failing
integrity check means the corpus is wrong; it is never something to relax.

`build-gleif-org-corpus.py`'s output directory changed 2026-08-10 (Phase 0.4): `entity_records` now
pairs an entity's distinct names with its distinct addresses by cycling, instead of copying the
entity's single legal address onto every alias row. That is a different corpus, so it publishes to a
different directory rather than overwriting the frozen `datasets\gleif-org\` — which remains on disk,
untouched, relabelled in its own README as an **alias/name-matching benchmark** (a good one — 657,832
labelled alias pairs — just not a multi-field one, which it never was). See
`C:\dev\linkuity\docs\superpowers\plans\2026-08-10-phase-0.4-two-observation-corpus.md` for why.

Run the tests with:

    python -m unittest discover -s tools\corpus -p "test_*.py" -v
