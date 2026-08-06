# Corpus builders

Scripts that turn a pinned public snapshot into a labelled corpus for `match corpus audit`.

**Code lives here. Data does not.** The corpora themselves are in `C:\dev\datasets\`, outside the
repo, so a multi-GB CSV can never be staged by accident. These scripts are tracked because they
carry pinned SHA-256 hashes and expected counts — the things that make a measurement reproducible —
and those deserve review and history.

| Script | Source | Output |
|---|---|---|
| `build-sec-recall-corpus.py` | `datasets\sec\cik-lookup-data.txt` | `datasets\sec-recall\` — 1,052,432 names / 97,142 true pairs, one field |
| `build-gleif-org-corpus.py` | `datasets\gleif\20260727-1600-...-lei2-golden-copy.csv` | `datasets\gleif-org\` — ~4.0M alias records / ~753k true pairs, eight columns |

Both pin their source by SHA-256 and refuse to write when an expected count drifts. A failing
integrity check means the corpus is wrong; it is never something to relax.

Run the tests with:

    python -m unittest discover -s tools\corpus -p "test_*.py" -v
