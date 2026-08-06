r"""Tests for the GLEIF organization corpus builder.

Runs entirely on synthetic fixtures. Nothing here touches the 4.9 GB source, so the
whole suite must stay under a second -- a test suite nobody runs is not a test suite.

    python -m unittest discover -s tools\corpus -p "test_*.py" -v
"""
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
        self.assertEqual(g.entity_aliases(row, IX), [("ACME LTD", "LEGAL", "same")])

    def test_legal_name_is_always_first(self):
        row = gleif_row("LEI0000000000000002", "ACME LTD",
                        others=[("OLD ACME LTD", "PREVIOUS_LEGAL_NAME")])
        self.assertEqual(g.entity_aliases(row, IX)[0][1], "LEGAL")

    def test_alias_equal_to_legal_name_is_dropped_case_insensitively(self):
        row = gleif_row("LEI0000000000000003", "ACME LTD",
                        others=[("acme ltd", "PREVIOUS_LEGAL_NAME")])
        self.assertEqual(len(g.entity_aliases(row, IX)), 1)

    def test_duplicate_aliases_keep_first_occurrence_type(self):
        row = gleif_row("LEI0000000000000004", "ACME LTD",
                        others=[("ACME TRADING", "TRADING_OR_OPERATING_NAME"),
                                ("acme trading", "PREVIOUS_LEGAL_NAME")])
        aliases = g.entity_aliases(row, IX)
        self.assertEqual(len(aliases), 2)
        self.assertEqual(aliases[1], ("ACME TRADING", "TRADING_OR_OPERATING_NAME", "same"))

    def test_transliterated_type_is_read_from_its_own_column(self):
        row = gleif_row("LEI0000000000000005", "Prv\u00e1 stavebn\u00e1",
                        translits=[("Prva stavebna", "AUTO_ASCII_TRANSLITERATED_LEGAL_NAME")])
        self.assertEqual(g.entity_aliases(row, IX)[1][1],
                         "AUTO_ASCII_TRANSLITERATED_LEGAL_NAME")

    def test_cross_script_alias_is_labelled_cross(self):
        row = gleif_row("LEI0000000000000006",
                        "\u041e\u0431\u0449\u0435\u0441\u0442\u0432\u043e",
                        others=[("MATADOR Rus LLC", "ALTERNATIVE_LANGUAGE_LEGAL_NAME")])
        self.assertEqual(g.entity_aliases(row, IX)[1][2], "cross")

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
        records = g.entity_records(row, IX)
        self.assertEqual(len(records), 2)
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
        records = g.entity_records(row, IX)
        self.assertEqual([r["_ordinal"] for r in records], [0, 1, 2])
        self.assertEqual([r["_gated"] for r in records], [True, False, True])
        gated = [r["_ordinal"] for r in records if r["_gated"]]
        self.assertEqual(gated, [0, 2])

    def test_record_id_round_trips(self):
        rid = g.record_id("LEI0000000000000011", 3)
        self.assertEqual(rid, "gleif-LEI0000000000000011-3")
        self.assertEqual(g.lei_from_record_id(rid), "LEI0000000000000011")


if __name__ == "__main__":
    unittest.main()
