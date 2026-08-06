"""Tests for the GLEIF organization corpus builder.

Runs entirely on synthetic fixtures. Nothing here touches the 4.9 GB source, so the
whole suite must stay under a second -- a test suite nobody runs is not a test suite.

    python -m unittest discover -s tools\\corpus -p "test_*.py" -v
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
        self.assertEqual(g.dominant_script("Prva stavebna sporitelna".replace("a", "á")), "LATIN")

    def test_cyrillic(self):
        self.assertEqual(g.dominant_script("Общество"), "CYRILLIC")

    def test_digits_and_punctuation_only_is_none(self):
        self.assertEqual(g.dominant_script("123 -- 456"), "NONE")

    def test_mixed_takes_the_majority(self):
        # 8 Cyrillic letters vs 3 Latin -> CYRILLIC
        self.assertEqual(g.dominant_script("Общество LLC"), "CYRILLIC")


class TestScriptRelation(unittest.TestCase):
    def test_same_script(self):
        self.assertEqual(g.script_relation("ACME GMBH", "ACME GesmbH"), "same")

    def test_cross_script(self):
        self.assertEqual(g.script_relation("MATADOR Rus LLC", "Общество"), "cross")

    def test_unscripted_alias_counts_as_same(self):
        # A name with no letters cannot be evidence of a script change, and calling it
        # "cross" would silently drop it from the gate corpus.
        self.assertEqual(g.script_relation("123", "ACME LTD"), "same")


if __name__ == "__main__":
    unittest.main()
