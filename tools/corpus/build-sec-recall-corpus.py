"""Build the SEC recall corpus for `match corpus audit`.

Emits records.csv (id, organization_name), ground-truth.csv (record_id, canonical_key) and
corpus.manifest.json. The source snapshot is pinned by SHA-256, not by row counts: a different
snapshot that happens to preserve the counts must NOT be accepted silently.
"""
import csv, collections, hashlib, json, os, sys

SRC = r"C:\dev\datasets\sec\cik-lookup-data.txt"
OUT = r"C:\dev\datasets\sec-recall"

# Filled in by Step 3. Empty means "not yet pinned" and the script refuses to write.
EXPECTED_SOURCE_SHA256 = "5B52D4B2591300A4BCCB254A3BCEC61E7EFE0859B2CD9C6B10CBADC695BC282E"

EXPECTED_LINES = 1_052_436          # raw non-empty lines in the source file
EXPECTED_BLANK_NAME_ROWS = 4        # valid CIK, zero-length name — excluded, not emitted
EXPECTED_RECORDS = 1_052_432        # EXPECTED_LINES - EXPECTED_BLANK_NAME_ROWS
EXPECTED_MULTI_CIKS = 57_780
EXPECTED_TRUE_PAIRS = 97_142


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def main():
    source_hash = sha256(SRC)
    if not EXPECTED_SOURCE_SHA256:
        print(f"Source SHA-256 is not pinned yet.\n  {source_hash}\n"
              "Paste it into EXPECTED_SOURCE_SHA256 and re-run.", file=sys.stderr)
        return 2
    if source_hash != EXPECTED_SOURCE_SHA256:
        print(f"SOURCE SNAPSHOT CHANGED — refusing to write.\n"
              f"  expected {EXPECTED_SOURCE_SHA256}\n  actual   {source_hash}", file=sys.stderr)
        return 1

    by_cik = collections.defaultdict(list)
    lines = 0
    malformed = []
    blank_name_rows = []   # (lineno, cik) — see comment below
    with open(SRC, encoding="latin-1") as f:
        for lineno, raw in enumerate(f, start=1):
            raw = raw.strip()
            if not raw:
                continue
            lines += 1
            body = raw[:-1] if raw.endswith(":") else raw
            i = body.rfind(":")          # names can contain colons; take the LAST one
            if i < 0:
                malformed.append((lineno, raw[:80]))
                continue
            if i == 0:
                # BLANK-NAME row: a valid CIK with a zero-length name, e.g. ":0001003197:".
                # Exactly four of these exist in this snapshot — CIKs 0001003197,
                # 0001036125, 0001161343, 0001593547 — and each of those CIKs also has
                # >=1 real named entry elsewhere in the file, so excluding the blank row
                # loses no registrant. The corpus exists to measure NAME matching; a
                # record with no name has nothing to match on, so it is excluded rather
                # than emitted. It is excluded EXPLICITLY and COUNTED here (not silently
                # skipped): an earlier throwaway script dropped these same four rows
                # with a plain `continue` while still counting them as parsed lines,
                # which is exactly how two different "SEC record count" figures
                # (1,052,436 vs 1,052,432) ended up circulating in this project's notes
                # without anyone noticing. Do NOT "simplify" this branch back into a
                # silent skip — EXPECTED_BLANK_NAME_ROWS below is what makes the
                # exclusion pinned instead of drift-prone.
                blank_name_rows.append((lineno, body[i + 1:]))
                continue
            by_cik[body[i + 1:]].append(body[:i])

    if malformed:
        print(f"{len(malformed)} unparsable row(s) — refusing to write. Silent skipping would "
              "make the record count a lie:", file=sys.stderr)
        for lineno, text in malformed[:10]:
            print(f"  line {lineno}: {text}", file=sys.stderr)
        return 1

    emitted = sum(len(n) for n in by_cik.values())
    multi = {c: n for c, n in by_cik.items() if len(n) > 1}
    pairs = sum(len(n) * (len(n) - 1) // 2 for n in multi.values())

    problems = []
    if lines != EXPECTED_LINES:
        problems.append(f"line count {lines:,} != expected {EXPECTED_LINES:,}")
    if len(blank_name_rows) != EXPECTED_BLANK_NAME_ROWS:
        problems.append(f"blank-name rows {len(blank_name_rows):,} != expected "
                         f"{EXPECTED_BLANK_NAME_ROWS:,}")
    if emitted != EXPECTED_RECORDS:
        problems.append(f"emitted {emitted:,} records != expected {EXPECTED_RECORDS:,}")
    if len(multi) != EXPECTED_MULTI_CIKS:
        problems.append(f"multi-name CIKs {len(multi):,} != expected {EXPECTED_MULTI_CIKS:,}")
    if pairs != EXPECTED_TRUE_PAIRS:
        problems.append(f"true pairs {pairs:,} != expected {EXPECTED_TRUE_PAIRS:,}")
    if problems:
        print("CORPUS INTEGRITY FAILURE — refusing to write:", file=sys.stderr)
        for p in problems:
            print(f"  - {p}", file=sys.stderr)
        return 1

    os.makedirs(OUT, exist_ok=True)
    records_path = os.path.join(OUT, "records.csv")
    truth_path = os.path.join(OUT, "ground-truth.csv")

    with open(records_path, "w", newline="", encoding="utf-8") as rf, \
         open(truth_path, "w", newline="", encoding="utf-8") as tf:
        rw, tw = csv.writer(rf), csv.writer(tf)
        rw.writerow(["id", "organization_name"])       # AuditCliCommon.ReadCsv requires "id"
        tw.writerow(["record_id", "canonical_key"])    # ReadGroundTruth requires these
        for cik in sorted(by_cik):                     # deterministic output order
            for ordinal, name in enumerate(by_cik[cik]):
                rid = f"sec-{cik}-{ordinal}"
                rw.writerow([rid, name])
                tw.writerow([rid, cik])

    blank_name_ciks = sorted({cik for _, cik in blank_name_rows})
    manifest = {
        "snapshot": "2026-07-27",
        "source": SRC,
        "corpusSourceSha256": source_hash,
        "recordsSha256": sha256(records_path),
        "groundTruthSha256": sha256(truth_path),
        "counts": {"parsedLines": lines, "emittedRecords": emitted,
                   "multiNameCiks": len(multi), "truePairs": pairs},
        "blankNameRowsExcluded": len(blank_name_rows),
        "blankNameCiks": blank_name_ciks,
    }
    with open(os.path.join(OUT, "corpus.manifest.json"), "w", encoding="utf-8") as mf:
        json.dump(manifest, mf, indent=2)

    print(f"excluded {len(blank_name_rows)} blank-name row(s): CIK "
          + ", ".join(blank_name_ciks))
    print(f"wrote {emitted:,} records, {pairs:,} true pairs to {OUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
