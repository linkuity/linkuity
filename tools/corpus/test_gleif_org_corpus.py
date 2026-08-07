r"""Tests for the GLEIF organization corpus builder.

Runs entirely on synthetic fixtures. Nothing here touches the 4.9 GB source, so the
whole suite must stay under a second -- a test suite nobody runs is not a test suite.

    python -m unittest discover -s tools\corpus -p "test_*.py" -v
"""
import csv as _csv
import json
import os
import tempfile
import unittest

import gleif_org_corpus as g


class TestColumnIndex(unittest.TestCase):
    def test_maps_required_columns(self):
        header = ["LEI", "Entity.LegalName", "Entity.LegalAddress.City"]
        ix = g.column_index(header, required=["LEI", "Entity.LegalAddress.City"])
        self.assertEqual(ix["LEI"], 0)
        self.assertEqual(ix["Entity.LegalAddress.City"], 2)

    def test_missing_column_raises_naming_it(self):
        with self.assertRaises(KeyError) as ctx:
            g.column_index(["LEI"], required=["LEI", "Entity.LegalName"])
        self.assertIn("Entity.LegalName", str(ctx.exception))


class TestDominantScript(unittest.TestCase):
    def test_latin(self):
        self.assertEqual(g.dominant_script("Prva stavebna sporitelna"), "LATIN")

    def test_diacritics_are_still_latin(self):
        self.assertEqual(g.dominant_script("Prva stavebna sporitelna".replace("a", "\u00e1")), "LATIN")

    def test_cyrillic(self):
        self.assertEqual(g.dominant_script("\u041e\u0431\u0449\u0435\u0441\u0442\u0432\u043e"), "CYRILLIC")

    def test_digits_and_punctuation_only_is_none(self):
        self.assertEqual(g.dominant_script("123 -- 456"), "NONE")

    def test_mixed_takes_the_majority(self):
        # 8 Cyrillic letters vs 3 Latin -> CYRILLIC
        self.assertEqual(g.dominant_script("\u041e\u0431\u0449\u0435\u0441\u0442\u0432\u043e LLC"), "CYRILLIC")


class TestScriptRelation(unittest.TestCase):
    def test_same_script(self):
        self.assertEqual(g.script_relation("ACME GMBH", "ACME GesmbH"), "same")

    def test_cross_script(self):
        self.assertEqual(g.script_relation("MATADOR Rus LLC", "\u041e\u0431\u0449\u0435\u0441\u0442\u0432\u043e"), "cross")

    def test_unscripted_alias_counts_as_same(self):
        # A name with no letters cannot be evidence of a script change, and calling it
        # "cross" would silently drop it from the gate corpus.
        self.assertEqual(g.script_relation("123", "ACME LTD"), "same")


GLEIF_HEADER = (
    ["LEI", "Entity.LegalName"]
    + [c for pair in g.OTHER_NAME_SLOTS for c in pair]
    + [c for pair in g.TRANSLITERATED_SLOTS for c in pair]
    + [c for _, c in g.ADDRESS_COLUMNS]
    + ["Entity.RegistrationAuthority.RegistrationAuthorityID",
       "Entity.RegistrationAuthority.RegistrationAuthorityEntityID"]
)
IX = g.column_index(GLEIF_HEADER)


def gleif_row(lei, legal, others=(), translits=(), address=None, ra=("", "")):
    """Build one GLEIF-shaped row.

    others/translits are sequences of (name, type); address is a dict keyed by corpus
    column name.
    """
    row = [""] * len(GLEIF_HEADER)
    row[IX["LEI"]] = lei
    row[IX["Entity.LegalName"]] = legal
    for slot, (name, typ) in zip(g.OTHER_NAME_SLOTS, others):
        row[IX[slot[0]]], row[IX[slot[1]]] = name, typ
    for slot, (name, typ) in zip(g.TRANSLITERATED_SLOTS, translits):
        row[IX[slot[0]]], row[IX[slot[1]]] = name, typ
    for corpus_col, gleif_col in g.ADDRESS_COLUMNS:
        row[IX[gleif_col]] = (address or {}).get(corpus_col, "")
    row[IX["Entity.RegistrationAuthority.RegistrationAuthorityID"]] = ra[0]
    row[IX["Entity.RegistrationAuthority.RegistrationAuthorityEntityID"]] = ra[1]
    return row


class TestIsGated(unittest.TestCase):
    def test_legal_is_gated(self):
        self.assertTrue(g.is_gated("LEGAL", "same"))

    def test_previous_legal_name_is_gated(self):
        self.assertTrue(g.is_gated("PREVIOUS_LEGAL_NAME", "same"))

    def test_trading_name_is_gated(self):
        self.assertTrue(g.is_gated("TRADING_OR_OPERATING_NAME", "same"))

    def test_same_script_transliteration_is_gated(self):
        self.assertTrue(g.is_gated("AUTO_ASCII_TRANSLITERATED_LEGAL_NAME", "same"))
        self.assertTrue(g.is_gated("PREFERRED_ASCII_TRANSLITERATED_LEGAL_NAME", "same"))

    def test_cross_script_transliteration_is_not_gated(self):
        self.assertFalse(g.is_gated("AUTO_ASCII_TRANSLITERATED_LEGAL_NAME", "cross"))

    def test_alternative_language_is_never_gated(self):
        self.assertFalse(g.is_gated("ALTERNATIVE_LANGUAGE_LEGAL_NAME", "same"))
        self.assertFalse(g.is_gated("ALTERNATIVE_LANGUAGE_LEGAL_NAME", "cross"))

    def test_unknown_type_raises(self):
        with self.assertRaises(ValueError):
            g.is_gated("SOMETHING_NEW", "same")


class TestEntityAliases(unittest.TestCase):
    def test_singleton_yields_just_the_legal_name(self):
        row = gleif_row("LEI0000000000000001", "ACME LTD")
        aliases, drops = g.entity_aliases(row, IX)
        self.assertEqual(aliases, [("ACME LTD", "LEGAL", "same")])
        # Nine unused slot pairs. An absent alias is not a declined row, so nothing counts.
        self.assertEqual(drops, {"blankName": 0, "dedupedCaseFold": 0})

    def test_legal_name_is_always_first(self):
        row = gleif_row("LEI0000000000000002", "ACME LTD",
                        others=[("OLD ACME LTD", "PREVIOUS_LEGAL_NAME")])
        self.assertEqual(g.entity_aliases(row, IX)[0][0][1], "LEGAL")

    def test_alias_equal_to_legal_name_is_dropped_case_insensitively(self):
        row = gleif_row("LEI0000000000000003", "ACME LTD",
                        others=[("acme ltd", "PREVIOUS_LEGAL_NAME")])
        aliases, drops = g.entity_aliases(row, IX)
        self.assertEqual(len(aliases), 1)
        # The drop is COUNTED, not silent: this is a populated source slot the build
        # declines to emit, and ~5k of them were unaccounted for before this counter.
        self.assertEqual(drops["dedupedCaseFold"], 1)

    def test_duplicate_aliases_keep_first_occurrence_type(self):
        row = gleif_row("LEI0000000000000004", "ACME LTD",
                        others=[("ACME TRADING", "TRADING_OR_OPERATING_NAME"),
                                ("acme trading", "PREVIOUS_LEGAL_NAME")])
        aliases, drops = g.entity_aliases(row, IX)
        self.assertEqual(len(aliases), 2)
        self.assertEqual(aliases[1], ("ACME TRADING", "TRADING_OR_OPERATING_NAME", "same"))
        self.assertEqual(drops["dedupedCaseFold"], 1)

    def test_blank_named_slot_that_carries_a_type_is_counted(self):
        row = gleif_row("LEI0000000000000012", "ACME LTD",
                        others=[("   ", "PREVIOUS_LEGAL_NAME")])
        aliases, drops = g.entity_aliases(row, IX)
        self.assertEqual(len(aliases), 1)
        self.assertEqual(drops, {"blankName": 1, "dedupedCaseFold": 0})

    def test_blank_named_slot_with_a_garbage_type_still_raises(self):
        # Check ORDER, not just presence: the blank-name skip used to run first, so a
        # snapshot that introduced a new alias type on a blank-name slot would have been
        # waved through instead of aborting the build.
        row = gleif_row("LEI0000000000000013", "ACME LTD", others=[("", "SOMETHING_NEW")])
        with self.assertRaises(ValueError) as ctx:
            g.entity_aliases(row, IX)
        self.assertIn("SOMETHING_NEW", str(ctx.exception))

    def test_transliterated_type_is_read_from_its_own_column(self):
        row = gleif_row("LEI0000000000000005", "Prv\u00e1 stavebn\u00e1",
                        translits=[("Prva stavebna", "AUTO_ASCII_TRANSLITERATED_LEGAL_NAME")])
        self.assertEqual(g.entity_aliases(row, IX)[0][1][1],
                         "AUTO_ASCII_TRANSLITERATED_LEGAL_NAME")

    def test_cross_script_alias_is_labelled_cross(self):
        row = gleif_row("LEI0000000000000006",
                        "\u041e\u0431\u0449\u0435\u0441\u0442\u0432\u043e",
                        others=[("MATADOR Rus LLC", "ALTERNATIVE_LANGUAGE_LEGAL_NAME")])
        self.assertEqual(g.entity_aliases(row, IX)[0][1][2], "cross")

    def test_untyped_alias_raises_rather_than_being_bucketed(self):
        row = gleif_row("LEI0000000000000007", "ACME LTD", others=[("ACME PLC", "")])
        with self.assertRaises(ValueError):
            g.entity_aliases(row, IX)

    def test_blank_legal_name_raises(self):
        row = gleif_row("LEI0000000000000008", "")
        with self.assertRaises(ValueError):
            g.entity_aliases(row, IX)


class TestEntityRecords(unittest.TestCase):
    def test_every_record_carries_the_entity_payload(self):
        row = gleif_row("LEI0000000000000009", "ACME LTD",
                        others=[("OLD ACME LTD", "PREVIOUS_LEGAL_NAME")],
                        address={"address_line": "1 Main St", "city": "Dublin",
                                 "region": "", "postal_code": "D01",
                                 "country": "IE", "jurisdiction": "IE",
                                 "legal_form": "H8VW"})
        records, drops = g.entity_records(row, IX)
        self.assertEqual(len(records), 2)
        self.assertEqual(drops, {"blankName": 0, "dedupedCaseFold": 0})
        for r in records:
            self.assertEqual(r["city"], "Dublin")
            self.assertEqual(r["legal_form"], "H8VW")
            self.assertEqual(r["region"], "")   # missing stays missing, never invented

    def test_ordinals_are_assigned_over_the_full_list_not_the_gated_subset(self):
        # The excluded alias sits BETWEEN two gated ones. If ordinals were assigned after
        # filtering, the gated record IDs would differ between records.csv and
        # records-full.csv and spec check 1 could never pass.
        row = gleif_row("LEI0000000000000010", "ACME LTD",
                        others=[("ACME SA", "ALTERNATIVE_LANGUAGE_LEGAL_NAME"),
                                ("OLD ACME LTD", "PREVIOUS_LEGAL_NAME")])
        records, _ = g.entity_records(row, IX)
        self.assertEqual([r["_ordinal"] for r in records], [0, 1, 2])
        self.assertEqual([r["_gated"] for r in records], [True, False, True])
        gated = [r["_ordinal"] for r in records if r["_gated"]]
        self.assertEqual(gated, [0, 2])

    def test_record_id_round_trips(self):
        rid = g.record_id("LEI0000000000000011", 3)
        self.assertEqual(rid, "gleif-LEI0000000000000011-3")
        self.assertEqual(g.lei_from_record_id(rid), "LEI0000000000000011")


class TestTruePairs(unittest.TestCase):
    def test_singletons_contribute_nothing(self):
        self.assertEqual(g.true_pairs_from_sizes([1, 1, 1]), 0)

    def test_pair_and_triple(self):
        self.assertEqual(g.true_pairs_from_sizes([2, 3]), 1 + 3)

    def test_matches_the_pinned_sec_figure_shape(self):
        # C(n,2) summed -- same formula build-sec-recall-corpus.py uses.
        self.assertEqual(g.true_pairs_from_sizes([5]), 10)


class TestIndependentRecount(unittest.TestCase):
    def test_recount_agrees_with_the_emitting_formula(self):
        rows = [("gleif-AAA-0",), ("gleif-AAA-1",), ("gleif-AAA-3",),
                ("gleif-BBB-0",), ("gleif-BBB-2",),
                ("gleif-CCC-0",)]
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "records.csv")
            with open(path, "w", newline="", encoding="utf-8") as fh:
                w = _csv.writer(fh)
                w.writerow(g.RECORD_COLUMNS)
                for (rid,) in rows:
                    w.writerow([rid] + [""] * (len(g.RECORD_COLUMNS) - 1))
            self.assertEqual(g.recount_true_pairs(path),
                             g.true_pairs_from_sizes([3, 2, 1]))

    def test_recount_rejects_a_non_gleif_id(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "records.csv")
            with open(path, "w", newline="", encoding="utf-8") as fh:
                w = _csv.writer(fh)
                w.writerow(g.RECORD_COLUMNS)
                w.writerow(["sec-0000000003-0"] + [""] * (len(g.RECORD_COLUMNS) - 1))
            with self.assertRaises(ValueError):
                g.recount_true_pairs(path)


class TestSampleFraction(unittest.TestCase):
    def test_in_unit_interval(self):
        for lei in ["A" * 20, "B" * 20, "5493001KJTIIGC8Y1R12"]:
            self.assertTrue(0.0 <= g.sample_fraction(lei) < 1.0)

    def test_deterministic(self):
        self.assertEqual(g.sample_fraction("5493001KJTIIGC8Y1R12"),
                         g.sample_fraction("5493001KJTIIGC8Y1R12"))

    def test_spreads_across_the_interval(self):
        # A hash sample must not correlate with LEI prefix. Sequential LEIs sharing a
        # 16-char prefix must not land in the same decile -- that correlation is exactly
        # the head-of-file bias that invalidated the first round of measurements.
        vals = [g.sample_fraction(f"5493001KJTIIGC8Y1R{i:02d}") for i in range(40)]
        self.assertGreater(len({int(v * 10) for v in vals}), 5)


class TestSha256(unittest.TestCase):
    def test_matches_hashlib_and_is_uppercase(self):
        import hashlib
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "f.bin")
            payload = b"gleif-org corpus builder\n" * 100
            with open(path, "wb") as fh:
                fh.write(payload)
            digest = g.sha256(path)
            self.assertEqual(digest, hashlib.sha256(payload).hexdigest().upper())
            self.assertEqual(digest, digest.upper())
            self.assertEqual(len(digest), 64)


def write_records(path, ids, extra=None):
    """Write a records.csv with the real header and only ids populated."""
    extra = extra or {}
    with open(path, "w", newline="", encoding="utf-8") as fh:
        w = _csv.writer(fh)
        w.writerow(g.RECORD_COLUMNS)
        for rid in ids:
            values = extra.get(rid, {})
            w.writerow([values.get(c, "") if c != "id" else rid for c in g.RECORD_COLUMNS])


def write_truth(path, pairs):
    with open(path, "w", newline="", encoding="utf-8") as fh:
        w = _csv.writer(fh)
        w.writerow(["record_id", "canonical_key"])
        w.writerows(pairs)


class TestVerificationPredicates(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = self._tmp.name
        self.addCleanup(self._tmp.cleanup)

    def p(self, name):
        return os.path.join(self.tmp, name)

    def test_ids_unique_and_sorted_passes(self):
        write_records(self.p("r.csv"), ["gleif-AAA-0", "gleif-AAA-1", "gleif-BBB-0"])
        self.assertEqual(g.check_ids_unique_and_sorted(self.p("r.csv")), [])

    def test_duplicate_id_is_reported(self):
        write_records(self.p("r.csv"), ["gleif-AAA-0", "gleif-AAA-0"])
        problems = g.check_ids_unique_and_sorted(self.p("r.csv"))
        self.assertEqual(len(problems), 1)
        self.assertIn("gleif-AAA-0", problems[0])

    def test_out_of_order_id_is_reported(self):
        write_records(self.p("r.csv"), ["gleif-BBB-0", "gleif-AAA-0"])
        self.assertEqual(len(g.check_ids_unique_and_sorted(self.p("r.csv"))), 1)

    def test_gated_subset_passes(self):
        write_records(self.p("full.csv"), ["gleif-AAA-0", "gleif-AAA-1", "gleif-AAA-2"])
        write_records(self.p("gated.csv"), ["gleif-AAA-0", "gleif-AAA-2"])
        self.assertEqual(g.check_gated_is_subset(self.p("gated.csv"), self.p("full.csv")), [])

    def test_gated_record_absent_from_full_is_reported(self):
        write_records(self.p("full.csv"), ["gleif-AAA-0"])
        write_records(self.p("gated.csv"), ["gleif-AAA-0", "gleif-AAA-9"])
        problems = g.check_gated_is_subset(self.p("gated.csv"), self.p("full.csv"))
        self.assertEqual(len(problems), 1)
        self.assertIn("gleif-AAA-9", problems[0])

    def test_truth_covers_records_passes(self):
        write_records(self.p("r.csv"), ["gleif-AAA-0", "gleif-AAA-1"])
        write_truth(self.p("t.csv"), [("gleif-AAA-0", "AAA"), ("gleif-AAA-1", "AAA")])
        self.assertEqual(g.check_truth_covers_records(self.p("r.csv"), self.p("t.csv")), [])

    def test_unlabelled_record_is_reported(self):
        write_records(self.p("r.csv"), ["gleif-AAA-0", "gleif-AAA-1"])
        write_truth(self.p("t.csv"), [("gleif-AAA-0", "AAA")])
        self.assertEqual(len(g.check_truth_covers_records(self.p("r.csv"), self.p("t.csv"))), 1)

    def test_wrong_canonical_key_is_reported(self):
        write_records(self.p("r.csv"), ["gleif-AAA-0"])
        write_truth(self.p("t.csv"), [("gleif-AAA-0", "BBB")])
        self.assertEqual(len(g.check_truth_covers_records(self.p("r.csv"), self.p("t.csv"))), 1)

    def test_sample_entity_complete_passes(self):
        write_records(self.p("gated.csv"), ["gleif-AAA-0", "gleif-AAA-1", "gleif-BBB-0"])
        write_records(self.p("s.csv"), ["gleif-AAA-0", "gleif-AAA-1"])
        self.assertEqual(g.check_sample_entity_complete(self.p("s.csv"), self.p("gated.csv")), [])

    def test_split_entity_in_sample_is_reported(self):
        write_records(self.p("gated.csv"), ["gleif-AAA-0", "gleif-AAA-1"])
        write_records(self.p("s.csv"), ["gleif-AAA-0"])
        problems = g.check_sample_entity_complete(self.p("s.csv"), self.p("gated.csv"))
        self.assertEqual(len(problems), 1)
        self.assertIn("AAA", problems[0])

    def test_fingerprint_reports_population_and_a_slice_hash(self):
        write_records(self.p("r.csv"), ["gleif-AAA-0", "gleif-AAA-1", "gleif-BBB-0", "gleif-CCC-0"],
                      extra={"gleif-AAA-0": {"region": "CA"}, "gleif-BBB-0": {"region": "NY"}})
        fp = g.field_fingerprint(self.p("r.csv"), slice_size=2)
        self.assertEqual(fp["records"], 4)
        self.assertAlmostEqual(fp["population"]["region"], 0.5)
        self.assertEqual(len(fp["sliceSha256"]), 64)

    def test_fingerprint_slice_hash_changes_when_two_columns_are_swapped(self):
        # A swapped city/region mapping changes neither the record count nor the
        # partition. This is the check that catches it.
        write_records(self.p("a.csv"), ["gleif-AAA-0"], extra={"gleif-AAA-0": {"city": "Dublin", "region": "L"}})
        write_records(self.p("b.csv"), ["gleif-AAA-0"], extra={"gleif-AAA-0": {"city": "L", "region": "Dublin"}})
        self.assertNotEqual(g.field_fingerprint(self.p("a.csv"), slice_size=1)["sliceSha256"],
                            g.field_fingerprint(self.p("b.csv"), slice_size=1)["sliceSha256"])


def write_gleif_fixture(path):
    """A 14-row GLEIF-shaped file exercising every branch the parse stage has.

    Deliberately NOT sorted by LEI in a couple of places is out of scope: GLEIF is
    published in ascending LEI order and the builder relies on that, so the fixture
    mirrors it.
    """
    rows = [
        # 3 gated records (LEGAL + PREVIOUS + TRADING), full payload
        gleif_row("LEI0000000000000001", "ACME LTD",
                  others=[("OLD ACME LTD", "PREVIOUS_LEGAL_NAME"),
                          ("ACME TRADING", "TRADING_OR_OPERATING_NAME")],
                  address={"address_line": "1 Main St", "city": "Dublin", "region": "Leinster",
                           "postal_code": "D01", "country": "IE", "jurisdiction": "IE",
                           "legal_form": "H8VW"}),
        # singleton distractor, region missing
        gleif_row("LEI0000000000000002", "BOREAL HOLDINGS SA",
                  address={"address_line": "2 Rue Neuve", "city": "Paris", "region": "",
                           "postal_code": "75001", "country": "FR", "jurisdiction": "FR",
                           "legal_form": "6QQB"}),
        # same-script transliteration: gated
        gleif_row("LEI0000000000000003", "Prv\u00e1 stavebn\u00e1 sporite\u013ea",
                  translits=[("Prva stavebna sporitelna", "AUTO_ASCII_TRANSLITERATED_LEGAL_NAME")],
                  address={"address_line": "3 Hlavna", "city": "Bratislava", "region": "",
                           "postal_code": "81109", "country": "SK", "jurisdiction": "SK",
                           "legal_form": "8Z6G"}),
        # cross-script transliteration: NOT gated, present in full only
        gleif_row("LEI0000000000000004",
                  "\u041e\u0431\u0449\u0435\u0441\u0442\u0432\u043e \u0410\u043b\u044c\u0444\u0430",
                  translits=[("Obshchestvo Alfa", "PREFERRED_ASCII_TRANSLITERATED_LEGAL_NAME")],
                  address={"address_line": "4 Tverskaya", "city": "Moscow", "region": "",
                           "postal_code": "125009", "country": "RU", "jurisdiction": "RU",
                           "legal_form": "AXSB"}),
        # alternative language: NEVER gated, and it sits between two gated aliases so the
        # ordinal-assignment rule is exercised end to end
        gleif_row("LEI0000000000000005", "MATADOR AUTOMOTIVE",
                  others=[("\u041e\u0431\u0449\u0435\u0441\u0442\u0432\u043e \u041c\u0430\u0442\u0430\u0434\u043e\u0440",
                           "ALTERNATIVE_LANGUAGE_LEGAL_NAME"),
                          ("MATADOR AUTO", "TRADING_OR_OPERATING_NAME")],
                  address={"address_line": "5 Long Rd", "city": "Bratislava", "region": "",
                           "postal_code": "81109", "country": "SK", "jurisdiction": "SK",
                           "legal_form": "8Z6G"}),
        # duplicate alias, case-folded away
        gleif_row("LEI0000000000000006", "CYGNUS PLC",
                  others=[("cygnus plc", "PREVIOUS_LEGAL_NAME")],
                  address={"address_line": "6 Cross St", "city": "London", "region": "England",
                           "postal_code": "EC1", "country": "GB", "jurisdiction": "GB",
                           "legal_form": "B6ES"}),
        # CIK join, accepted
        gleif_row("LEI0000000000000007", "DELTA CORP",
                  address={"address_line": "7 Wall St", "city": "New York", "region": "NY",
                           "postal_code": "10005", "country": "US", "jurisdiction": "US-DE",
                           "legal_form": "XTIQ"},
                  ra=("RA000665", "1234")),
        # CIK-shaped id under the WRONG authority: must be rejected and counted
        gleif_row("LEI0000000000000008", "TORTOLA OFFSHORE LTD",
                  address={"address_line": "8 Road Town", "city": "Tortola", "region": "",
                           "postal_code": "VG1110", "country": "VG", "jurisdiction": "VG",
                           "legal_form": "XTIQ"},
                  ra=("RA000063", "1234")),
        # RA000665 with a SEC SERIES id: excluded, counted, not a parse failure
        gleif_row("LEI0000000000000009", "EPSILON FUND",
                  address={"address_line": "9 State St", "city": "Boston", "region": "MA",
                           "postal_code": "02109", "country": "US", "jurisdiction": "US-MA",
                           "legal_form": "XTIQ"},
                  ra=("RA000665", "S000005113")),
        # RA000665 numeric id with NO matching CIK: counted separately
        gleif_row("LEI0000000000000010", "ZETA UNLISTED INC",
                  address={"address_line": "10 Elm St", "city": "Austin", "region": "TX",
                           "postal_code": "73301", "country": "US", "jurisdiction": "US-TX",
                           "legal_form": "XTIQ"},
                  ra=("RA000665", "9999999")),
    ]
    with open(path, "w", newline="", encoding="utf-8") as fh:
        w = _csv.writer(fh)
        w.writerow(GLEIF_HEADER)
        w.writerows(rows)


def write_sec_fixture(path):
    """cik-lookup-data.txt shape: NAME:CIK: with names that may contain colons.

    Includes one blank-name row (":0000009999:") -- that CIK is not in the parse
    fixture's candidate set, so it must never affect cikEntities/cikRecords/cikTruePairs.
    """
    lines = [
        "DELTA CORP:0000001234:",
        "DELTA CORPORATION:0000001234:",
        "DELTA CORP: THE:0000001234:",
        "UNRELATED HOLDINGS:0000005555:",
        ":0000009999:",
    ]
    with open(path, "w", encoding="latin-1", newline="\n") as fh:
        fh.write("\n".join(lines) + "\n")


class ParseFixture:
    """Shared setUp for the stage tests. NOT a TestCase, so unittest does not collect it
    and the parse assertions do not re-run once per downstream stage."""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = self._tmp.name
        self.addCleanup(self._tmp.cleanup)
        self.gleif = os.path.join(self.tmp, "gleif.csv")
        self.sec = os.path.join(self.tmp, "sec.txt")
        write_gleif_fixture(self.gleif)
        write_sec_fixture(self.sec)
        self.out = os.path.join(self.tmp, "out")
        self.config = g.BuildConfig(gleif_path=self.gleif, sec_path=self.sec,
                                    out_dir=self.out, expected={}, sample_target=4)
        self.observed = g.run_parse(self.config)

    def path(self, name):
        return os.path.join(g.tmp_dir(self.config), name)


class TestParseStageEndToEnd(ParseFixture, unittest.TestCase):
    def test_entity_and_record_counts(self):
        self.assertEqual(self.observed["entities"], 10)
        # full: 3 + 1 + 2 + 2 + 3 + 1 + 1 + 1 + 1 + 1
        self.assertEqual(self.observed["fullRecords"], 16)
        # gated drops the cross-script translit (1) and the alternative language (1)
        self.assertEqual(self.observed["gatedRecords"], 14)

    def test_alias_type_counts_are_reported(self):
        # Only entity 001's "OLD ACME LTD" is a genuine PREVIOUS_LEGAL_NAME record.
        # Entity 006's "cygnus plc" case-folds to its own legal name "CYGNUS PLC" and is
        # dropped by entity_aliases before it is ever typed -- see
        # TestEntityAliases.test_alias_equal_to_legal_name_is_dropped_case_insensitively.
        self.assertEqual(self.observed["aliasTypes"]["PREVIOUS_LEGAL_NAME"], 1)
        self.assertEqual(self.observed["aliasTypes"]["ALTERNATIVE_LANGUAGE_LEGAL_NAME"], 1)
        self.assertEqual(self.observed["aliasTypes"]["AUTO_ASCII_TRANSLITERATED_LEGAL_NAME"], 1)

    def test_alias_drops_are_counted_by_reason(self):
        # Entity 006's "cygnus plc" is the only declined alias slot in the fixture, and it
        # is the reason aliasTypes["PREVIOUS_LEGAL_NAME"] reads 1 and not 2. Before these
        # counters existed that difference had no name anywhere in the observed dict.
        self.assertEqual(self.observed["aliasDrops"],
                         {"blankName": 0, "dedupedCaseFold": 1})

    def test_true_pairs_match_the_independent_recount(self):
        self.assertEqual(self.observed["gatedTruePairs"],
                         g.recount_true_pairs(self.path("records.csv")))
        self.assertEqual(self.observed["fullTruePairs"],
                         g.recount_true_pairs(self.path("records-full.csv")))

    def test_gated_is_a_strict_subset_with_identical_keys(self):
        self.assertEqual(g.check_gated_is_subset(self.path("records.csv"),
                                                 self.path("records-full.csv")), [])
        self.assertEqual(g.check_truth_covers_records(self.path("records.csv"),
                                                      self.path("ground-truth.csv")), [])
        self.assertEqual(g.check_truth_covers_records(self.path("records-full.csv"),
                                                      self.path("ground-truth-full.csv")), [])

    def test_ordinals_survive_the_gate_filter(self):
        # LEI...05 emits ordinals 0,1,2 with ordinal 1 excluded from the gate corpus.
        ids = [r for r in _read_all_ids(self.path("records.csv"))
               if r.startswith("gleif-LEI0000000000000005-")]
        self.assertEqual(ids, ["gleif-LEI0000000000000005-0", "gleif-LEI0000000000000005-2"])

    def test_ids_are_unique_and_sorted(self):
        self.assertEqual(g.check_ids_unique_and_sorted(self.path("records.csv")), [])
        self.assertEqual(g.check_ids_unique_and_sorted(self.path("records-full.csv")), [])

    def test_missing_region_stays_empty(self):
        with open(self.path("records.csv"), encoding="utf-8", newline="") as fh:
            rows = list(_csv.DictReader(fh))
        boreal = [r for r in rows if r["organization_name"] == "BOREAL HOLDINGS SA"][0]
        self.assertEqual(boreal["region"], "")
        self.assertEqual(boreal["city"], "Paris")

    def test_cik_candidates_split_by_disposition(self):
        c = self.observed["cik"]
        self.assertEqual(c["numeric"], 2)          # 1234 and 9999999
        self.assertEqual(c["seriesIds"], 1)        # S000005113
        self.assertEqual(c["wrongAuthorityCikShaped"], 1)   # RA000063 with 1234
        self.assertEqual(c["duplicateCikKeys"], 0)  # standard fixture has no collision


class TestCikCollisionCounting(unittest.TestCase):
    """Self-contained: two LEIs whose numeric ids zero-pad to the same CIK.

    Deliberately NOT built on the shared ParseFixture fixture -- reusing it would
    perturb every already-pinned count in TestParseStageEndToEnd.
    """

    def test_duplicate_cik_key_is_counted_and_first_wins(self):
        import json

        rows = [
            gleif_row("LEI0000000000000101", "FIRST TRUST CORP",
                      address={"address_line": "1 First St", "city": "Boston", "region": "MA",
                               "postal_code": "02109", "country": "US", "jurisdiction": "US-MA",
                               "legal_form": "XTIQ"},
                      ra=("RA000665", "42")),
            gleif_row("LEI0000000000000102", "SECOND SERIES LLC",
                      address={"address_line": "2 Second St", "city": "Chicago", "region": "IL",
                               "postal_code": "60601", "country": "US", "jurisdiction": "US-IL",
                               "legal_form": "XTIQ"},
                      ra=("RA000665", "0000000042")),
        ]
        with tempfile.TemporaryDirectory() as tmp:
            gleif_path = os.path.join(tmp, "gleif.csv")
            with open(gleif_path, "w", newline="", encoding="utf-8") as fh:
                w = _csv.writer(fh)
                w.writerow(GLEIF_HEADER)
                w.writerows(rows)
            config = g.BuildConfig(gleif_path=gleif_path, sec_path="",
                                   out_dir=os.path.join(tmp, "out"), expected={})
            observed = g.run_parse(config)

            self.assertEqual(observed["cik"]["duplicateCikKeys"], 1)
            with open(os.path.join(g.tmp_dir(config), "cik-candidates.json"),
                      encoding="utf-8") as fh:
                candidates = json.load(fh)
            self.assertEqual(len(candidates), 1)
            self.assertEqual(candidates["0000000042"]["lei"], "LEI0000000000000101")


def _read_all_ids(path):
    return list(g._read_ids(path))


class TestSampleStage(ParseFixture, unittest.TestCase):
    """Reuses the parse-stage fixture; sample_target=4 on a 14-record gate corpus."""

    def test_sample_is_entity_complete(self):
        observed = g.run_sample(self.config)
        self.assertEqual(g.check_sample_entity_complete(self.path("records-200k.csv"),
                                                        self.path("records.csv")), [])
        self.assertGreater(observed["sampleRecords"], 0)
        self.assertLessEqual(observed["sampleRecords"], self.observed["gatedRecords"])

    def test_sample_true_pairs_match_the_independent_recount(self):
        observed = g.run_sample(self.config)
        self.assertEqual(observed["sampleTruePairs"],
                         g.recount_true_pairs(self.path("records-200k.csv")))

    def test_sample_truth_is_complete_and_correctly_keyed(self):
        g.run_sample(self.config)
        self.assertEqual(g.check_truth_covers_records(self.path("records-200k.csv"),
                                                      self.path("ground-truth-200k.csv")), [])

    def test_sample_is_deterministic(self):
        g.run_sample(self.config)
        first = _read_all_ids(self.path("records-200k.csv"))
        g.run_sample(self.config)
        self.assertEqual(first, _read_all_ids(self.path("records-200k.csv")))


class TestCikStage(ParseFixture, unittest.TestCase):
    def test_only_the_right_authority_joins(self):
        observed = g.run_cik(self.config)
        # DELTA CORP (RA000665, 1234) joins. TORTOLA (RA000063, same digits) does not.
        self.assertEqual(observed["cikEntities"], 1)
        with open(os.path.join(g.tmp_dir(self.config), "cik", "records.csv"),
                  encoding="utf-8", newline="") as fh:
            rows = list(_csv.DictReader(fh))
        names = {r["organization_name"] for r in rows}
        self.assertIn("DELTA CORP", names)
        self.assertNotIn("TORTOLA OFFSHORE LTD", names)

    def test_records_and_pairs(self):
        observed = g.run_cik(self.config)
        # 1 GLEIF + 3 SEC names for CIK 0000001234
        self.assertEqual(observed["cikRecords"], 4)
        self.assertEqual(observed["cikTruePairs"], 6)

    def test_ids_name_their_source(self):
        g.run_cik(self.config)
        with open(os.path.join(g.tmp_dir(self.config), "cik", "records.csv"),
                  encoding="utf-8", newline="") as fh:
            ids = [r["id"] for r in _csv.DictReader(fh)]
        self.assertIn("cik-0000001234-gleif-0", ids)
        self.assertIn("cik-0000001234-sec-0", ids)

    def test_sec_name_containing_a_colon_is_parsed_on_the_last_colon(self):
        g.run_cik(self.config)
        with open(os.path.join(g.tmp_dir(self.config), "cik", "records.csv"),
                  encoding="utf-8", newline="") as fh:
            names = {r["organization_name"] for r in _csv.DictReader(fh)}
        self.assertIn("DELTA CORP: THE", names)

    def test_numeric_id_with_no_matching_cik_is_counted_not_dropped(self):
        observed = g.run_cik(self.config)
        self.assertEqual(observed["cikUnresolvedNumeric"], 1)   # ZETA UNLISTED INC, 9999999

    def test_canonical_key_is_the_cik(self):
        g.run_cik(self.config)
        with open(os.path.join(g.tmp_dir(self.config), "cik", "ground-truth.csv"),
                  encoding="utf-8", newline="") as fh:
            keys = {r["canonical_key"] for r in _csv.DictReader(fh)}
        self.assertEqual(keys, {"0000001234"})

    def test_blank_name_row_is_counted_not_joined(self):
        # The fixture's ":0000009999:" row is a valid CIK with no name. Its CIK is not in
        # the candidate set, so it must be counted but never affect the join.
        observed = g.run_cik(self.config)
        self.assertEqual(observed["cikSecBlankNameRows"], 1)
        self.assertEqual(observed["cikEntities"], 1)
        self.assertEqual(observed["cikRecords"], 4)
        self.assertEqual(observed["cikTruePairs"], 6)


class TestVerifyAndPublish(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = self._tmp.name
        self.addCleanup(self._tmp.cleanup)
        self.gleif = os.path.join(self.tmp, "gleif.csv")
        self.sec = os.path.join(self.tmp, "sec.txt")
        write_gleif_fixture(self.gleif)
        write_sec_fixture(self.sec)
        self.out = os.path.join(self.tmp, "out")

    def config(self, expected):
        return g.BuildConfig(gleif_path=self.gleif, sec_path=self.sec, out_dir=self.out,
                             expected=expected, sample_target=4,
                             expected_gleif_sha256=g.sha256(self.gleif),
                             expected_sec_sha256=g.sha256(self.sec))

    def test_unpinned_expectations_refuse_to_publish(self):
        code = g.run_build(self.config({"entities": None}), g.STAGE_ORDER)
        self.assertEqual(code, 2)
        self.assertFalse(os.path.exists(os.path.join(self.out, "corpus.manifest.json")))
        self.assertFalse(os.path.exists(os.path.join(self.out, "records.csv")))

    def test_mismatched_expectation_refuses_to_publish(self):
        code = g.run_build(self.config({"entities": 999}), g.STAGE_ORDER)
        self.assertEqual(code, 1)
        self.assertFalse(os.path.exists(os.path.join(self.out, "corpus.manifest.json")))

    def test_changed_source_hash_refuses_before_parsing(self):
        config = self.config({"entities": 10})
        config.expected_gleif_sha256 = "0" * 64
        self.assertEqual(g.run_build(config, g.STAGE_ORDER), 1)

    def test_successful_build_publishes_every_artifact_with_manifest_last(self):
        code = g.run_build(self.config({"entities": 10}), g.STAGE_ORDER)
        self.assertEqual(code, 0)
        for name in ["records.csv", "ground-truth.csv", "records-full.csv",
                     "ground-truth-full.csv", "records-200k.csv", "ground-truth-200k.csv",
                     os.path.join("cik", "records.csv"), os.path.join("cik", "ground-truth.csv"),
                     "corpus.manifest.json"]:
            self.assertTrue(os.path.exists(os.path.join(self.out, name)), name)

    def test_manifest_records_hashes_counts_and_the_field_fingerprint(self):
        g.run_build(self.config({"entities": 10}), g.STAGE_ORDER)
        with open(os.path.join(self.out, "corpus.manifest.json"), encoding="utf-8") as fh:
            manifest = json.load(fh)
        self.assertEqual(manifest["snapshot"], "20260727-1600")
        self.assertEqual(len(manifest["gleifSourceSha256"]), 64)
        self.assertIn("records.csv", manifest["sha256"])
        self.assertEqual(manifest["counts"]["entities"], 10)
        self.assertIn("sliceSha256", manifest["fieldFingerprint"])
        self.assertIn("aliasTypes", manifest["counts"])

    def test_build_is_byte_reproducible(self):
        self.assertEqual(g.run_build(self.config({"entities": 10}), g.STAGE_ORDER), 0)
        with open(os.path.join(self.out, "corpus.manifest.json"), encoding="utf-8") as fh:
            first = json.load(fh)["sha256"]
        self.assertEqual(g.run_build(self.config({"entities": 10}), g.STAGE_ORDER), 0)
        with open(os.path.join(self.out, "corpus.manifest.json"), encoding="utf-8") as fh:
            second = json.load(fh)["sha256"]
        self.assertEqual(first, second)

    def test_publish_preserves_manifest_keys_the_build_does_not_own(self):
        # The published manifest carries a `budget` block written out of band -- 78 minutes
        # of full-corpus audit the build itself cannot reproduce. A rebuild must refresh
        # only its own keys and carry foreign ones through untouched.
        config = self.config({"entities": 10})
        self.assertEqual(g.run_build(config, g.STAGE_ORDER), 0)
        manifest_path = os.path.join(self.out, "corpus.manifest.json")
        with open(manifest_path, encoding="utf-8") as fh:
            manifest = json.load(fh)
        foreign = {"recall": 0.2914, "note": "measured out of band"}
        manifest["budget"] = foreign
        manifest["counts"] = "stale, must be refreshed"
        with open(manifest_path, "w", encoding="utf-8") as fh:
            json.dump(manifest, fh, indent=2, sort_keys=True)

        self.assertEqual(g.run_build(config, g.STAGE_ORDER), 0)
        with open(manifest_path, encoding="utf-8") as fh:
            rebuilt = json.load(fh)
        self.assertEqual(rebuilt["budget"], foreign)
        self.assertEqual(rebuilt["counts"]["entities"], 10)
        self.assertEqual(rebuilt["snapshot"], "20260727-1600")
        # The .prev backup is a crash-window artifact only: a SUCCESSFUL publish must not
        # leave a second manifest lying beside the real one for a reader to mistake.
        self.assertFalse(os.path.exists(manifest_path + ".prev"))

    def test_a_crash_mid_publish_keeps_both_invariants(self):
        # The artifacts are moved one at a time, and BOTH of these must hold across that
        # window -- they used to be in tension:
        #   (a) no corpus.manifest.json, so the half-moved corpus reads as unfinished
        #       rather than as a complete one described by the previous run's manifest;
        #   (b) the carried foreign keys survive, because they exist in that file and
        #       nowhere else. Deleting instead of renaming satisfied (a) and broke (b).
        config = self.config({"entities": 10})
        self.assertEqual(g.run_build(config, g.STAGE_ORDER), 0)
        manifest_path = os.path.join(self.out, "corpus.manifest.json")
        previous_path = manifest_path + ".prev"
        foreign = {"recall": 0.2914, "note": "78 minutes and 9.3 GB, not reproducible here"}
        with open(manifest_path, encoding="utf-8") as fh:
            manifest = json.load(fh)
        manifest["budget"] = foreign
        with open(manifest_path, "w", encoding="utf-8") as fh:
            json.dump(manifest, fh, indent=2, sort_keys=True)

        observed = g.run_parse(config)
        observed.update(g.run_sample(config))
        observed.update(g.run_cik(config))
        self.assertEqual(g.run_verify(config, observed), [])
        # Break the move loop partway: records-200k.csv is the fifth of eight.
        os.remove(os.path.join(g.tmp_dir(config), "records-200k.csv"))
        with self.assertRaises(OSError):
            g.run_publish(config, observed)

        self.assertFalse(os.path.exists(manifest_path))          # (a)
        self.assertTrue(os.path.exists(previous_path))           # (b)
        with open(previous_path, encoding="utf-8") as fh:
            self.assertEqual(json.load(fh)["budget"], foreign)

    def test_a_retry_after_a_crashed_publish_recovers_the_carried_keys(self):
        # The crash test above proves .prev is WRITTEN. This proves it is READ, which is
        # the half that matters in practice: the obvious response to a crashed publish is
        # to re-run the build, and on that run the canonical manifest does not exist. If
        # run_publish only ever read the canonical path, the retry would rebuild with no
        # foreign keys and then delete the one file still holding them -- destroying the
        # `budget` block silently, one retry cycle after the crash it survived.
        config = self.config({"entities": 10})
        self.assertEqual(g.run_build(config, g.STAGE_ORDER), 0)
        manifest_path = os.path.join(self.out, "corpus.manifest.json")
        previous_path = manifest_path + ".prev"
        foreign = {"recall": 0.2914, "note": "78 minutes and 9.3 GB, not reproducible here"}
        with open(manifest_path, encoding="utf-8") as fh:
            manifest = json.load(fh)
        manifest["budget"] = foreign
        with open(manifest_path, "w", encoding="utf-8") as fh:
            json.dump(manifest, fh, indent=2, sort_keys=True)

        observed = self._consistent_observed(config)
        os.remove(os.path.join(g.tmp_dir(config), "records-200k.csv"))
        with self.assertRaises(OSError):
            g.run_publish(config, observed)
        self.assertFalse(os.path.exists(manifest_path))
        self.assertTrue(os.path.exists(previous_path))

        # THE RETRY -- a plain re-run of the whole build, nothing hand-repaired.
        self.assertEqual(g.run_build(config, g.STAGE_ORDER), 0)
        with open(manifest_path, encoding="utf-8") as fh:
            recovered = json.load(fh)
        self.assertEqual(recovered["budget"], foreign)
        self.assertEqual(recovered["counts"]["entities"], 10)
        self.assertFalse(os.path.exists(previous_path))

    def test_publish_refuses_over_a_manifest_it_cannot_read(self):
        # Low severity -- it fails before the rename and the move loop, so nothing is lost.
        # But the failure must NAME the file rather than surfacing a bare JSONDecodeError,
        # and it must refuse rather than silently discarding the keys it could not read.
        config = self.config({"entities": 10})
        self.assertEqual(g.run_build(config, g.STAGE_ORDER), 0)
        manifest_path = os.path.join(self.out, "corpus.manifest.json")
        with open(manifest_path, "w", encoding="utf-8") as fh:
            fh.write("{ this is not json")

        observed = self._consistent_observed(config)
        with self.assertRaises(ValueError) as ctx:
            g.run_publish(config, observed)
        self.assertIn("corpus.manifest.json", str(ctx.exception))
        self.assertIn("refusing to publish", str(ctx.exception))
        # Refused early: the unreadable file is untouched and no .prev was created.
        self.assertTrue(os.path.exists(manifest_path))
        self.assertFalse(os.path.exists(manifest_path + ".prev"))

    def test_publish_refuses_over_a_manifest_that_is_not_an_object(self):
        config = self.config({"entities": 10})
        self.assertEqual(g.run_build(config, g.STAGE_ORDER), 0)
        manifest_path = os.path.join(self.out, "corpus.manifest.json")
        with open(manifest_path, "w", encoding="utf-8") as fh:
            fh.write("[1, 2, 3]")

        observed = self._consistent_observed(config)
        with self.assertRaises(ValueError) as ctx:
            g.run_publish(config, observed)
        self.assertIn("not an object", str(ctx.exception))

    def test_partial_stage_run_skips_verify_derived_expectations(self):
        # fieldSliceSha256 is produced only by run_verify, so pinning it must not make a
        # correct `--stage parse` abort with "observed None != expected ...".
        expected = {"entities": 10, "fieldSliceSha256": "0" * 64,
                    "fieldFingerprint": {"population": {"region": 1.0}}}
        config = self.config(expected)
        self.assertEqual(g.run_build(config, ["parse", "sample", "cik"]), 0)
        # And a single stage re-run against the now-cached downstream counts, which is the
        # workflow the driver's docstring advertises.
        self.assertEqual(g.run_build(config, ["parse"]), 0)
        self.assertEqual(g.run_build(config, ["sample"]), 0)

    def test_full_run_still_catches_a_wrong_field_slice_hash(self):
        # The partial-run relaxation must not weaken the run that actually publishes.
        expected = {"entities": 10, "fieldSliceSha256": "0" * 64}
        self.assertEqual(g.run_build(self.config(expected), g.STAGE_ORDER), 1)
        self.assertFalse(os.path.exists(os.path.join(self.out, "corpus.manifest.json")))

    def test_full_run_still_catches_a_wrong_population_rate(self):
        # The fixture's gate corpus really is 0.5 populated on region, so 0.25 is wrong.
        expected = {"entities": 10, "fieldFingerprint": {"population": {"region": 0.25}}}
        self.assertEqual(g.run_build(self.config(expected), g.STAGE_ORDER), 1)

    def _consistent_observed(self, config):
        observed = g.run_parse(config)
        observed.update(g.run_sample(config))
        observed.update(g.run_cik(config))
        self.assertEqual(g.run_verify(config, observed), [])
        return observed

    def test_verify_asserts_the_cik_numeric_reconciliation(self):
        config = self.config({})
        observed = self._consistent_observed(config)
        observed["cikUnresolvedNumeric"] += 1
        problems = g.run_verify(config, observed)
        self.assertTrue(any("cik.numeric" in p for p in problems), problems)

    def test_verify_asserts_the_cik_rows_reconciliation(self):
        config = self.config({})
        observed = self._consistent_observed(config)
        observed["cik"]["seriesIds"] += 1
        problems = g.run_verify(config, observed)
        self.assertTrue(any("cik.rows" in p for p in problems), problems)

    def test_verify_asserts_alias_types_sum_to_full_records(self):
        config = self.config({})
        observed = self._consistent_observed(config)
        observed["aliasTypes"]["LEGAL"] += 1
        problems = g.run_verify(config, observed)
        self.assertTrue(any("sum(aliasTypes)" in p for p in problems), problems)

    def test_verify_catches_a_corrupted_gate_corpus(self):
        # Stop before publish: run_publish removes the temp directory, so corrupting it
        # afterwards would test nothing.
        config = self.config({"entities": 10})
        self.assertEqual(g.run_build(config, ["parse", "sample", "cik"]), 0)
        # Append a gated record that exists in no full corpus.
        with open(os.path.join(g.tmp_dir(config), "records.csv"),
                  "a", newline="", encoding="utf-8") as fh:
            _csv.writer(fh).writerow(["gleif-ZZZ0000000000000-0"]
                                     + [""] * (len(g.RECORD_COLUMNS) - 1))
        self.assertEqual(g.run_build(config, ["verify", "publish"]), 1)
        self.assertFalse(os.path.exists(os.path.join(self.out, "corpus.manifest.json")))


if __name__ == "__main__":
    unittest.main()
