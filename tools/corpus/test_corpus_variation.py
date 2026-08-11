r"""Tests for corpus_variation.py.

Runs entirely on synthetic fixtures -- nothing here touches the multi-GB corpora, so
the whole suite must stay under a second.

    python -m unittest discover -s tools\corpus -p "test_*.py" -v
"""
import csv as _csv
import os
import tempfile
import unittest

import corpus_variation as v


def write_records(path, header, rows):
    """rows: list of lists, each aligned to `header` (header[0] is the id column)."""
    with open(path, "w", newline="", encoding="utf-8") as fh:
        w = _csv.writer(fh)
        w.writerow(header)
        w.writerows(rows)


def write_truth(path, pairs):
    with open(path, "w", newline="", encoding="utf-8") as fh:
        w = _csv.writer(fh)
        w.writerow(["record_id", "canonical_key"])
        w.writerows(pairs)


class TestNormalize(unittest.TestCase):
    def test_trims_and_casefolds(self):
        self.assertEqual(v.normalize("  Acme LTD  "), "acme ltd")

    def test_blank_stays_blank(self):
        self.assertEqual(v.normalize("   "), "")


class TestEntitySizes(unittest.TestCase):
    def test_counts_records_per_key(self):
        pairs = [("r1", "A"), ("r2", "A"), ("r3", "B")]
        self.assertEqual(v.entity_sizes(pairs), {"A": 2, "B": 1})


class TestMultiRecordIndex(unittest.TestCase):
    def test_singleton_entities_are_excluded(self):
        pairs = [("r1", "A"), ("r2", "A"), ("r3", "B")]
        sizes = v.entity_sizes(pairs)
        multi = {k: c for k, c in sizes.items() if c >= 2}
        self.assertEqual(v.multi_record_index(pairs, multi), {"r1": "A", "r2": "A"})


class TestLoadMultiRecordIndex(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = self._tmp.name
        self.addCleanup(self._tmp.cleanup)

    def test_pruned_to_multi_record_entities_only(self):
        path = os.path.join(self.tmp, "truth.csv")
        write_truth(path, [("r1", "A"), ("r2", "A"), ("r3", "B"), ("r4", "C"), ("r5", "C")])
        index, sizes = v.load_multi_record_index(path)
        self.assertEqual(index, {"r1": "A", "r2": "A", "r4": "C", "r5": "C"})
        self.assertEqual(sizes, {"A": 2, "C": 2})


class TestAccumulateVariation(unittest.TestCase):
    """The core measurement, exercised directly against small row lists."""

    def test_field_that_varies_across_the_entitys_records(self):
        columns = ["name"]
        index = {"r1": "A", "r2": "A"}
        rows = [("r1", ["Acme Ltd"]), ("r2", ["Acme Holdings"])]
        varies, blanks, considered = v.accumulate_variation(rows, columns, index)
        self.assertEqual(varies, {"name": 1})
        self.assertEqual(blanks, {"name": 0})
        self.assertEqual(considered, 2)

    def test_field_that_does_not_vary(self):
        columns = ["city"]
        index = {"r1": "A", "r2": "A"}
        rows = [("r1", ["Dublin"]), ("r2", ["Dublin"])]
        varies, blanks, considered = v.accumulate_variation(rows, columns, index)
        self.assertEqual(varies, {"city": 0})

    def test_field_entirely_blank_reads_as_no_variation_with_full_blank_count(self):
        columns = ["region"]
        index = {"r1": "A", "r2": "A"}
        rows = [("r1", [""]), ("r2", ["  "])]
        varies, blanks, considered = v.accumulate_variation(rows, columns, index)
        self.assertEqual(varies, {"region": 0})
        self.assertEqual(blanks, {"region": 2})   # both blank -- "no variation" for a
        self.assertEqual(considered, 2)           # different reason than agreement

    def test_comparison_is_trimmed_and_case_insensitive(self):
        columns = ["name"]
        index = {"r1": "A", "r2": "A"}
        rows = [("r1", ["Acme Ltd"]), ("r2", ["  acme ltd  "])]
        varies, _, _ = v.accumulate_variation(rows, columns, index)
        self.assertEqual(varies, {"name": 0})

    def test_third_distinct_value_does_not_double_count(self):
        # Once a column resolves to VARIES for an entity it must be counted exactly
        # once, no matter how many further distinct values that entity's records add.
        columns = ["name"]
        index = {"r1": "A", "r2": "A", "r3": "A"}
        rows = [("r1", ["Acme"]), ("r2", ["Acme Holdings"]), ("r3", ["Acme Group"])]
        varies, _, _ = v.accumulate_variation(rows, columns, index)
        self.assertEqual(varies, {"name": 1})

    def test_single_record_entities_are_excluded_via_the_index(self):
        # r3 belongs to a singleton entity and is simply absent from `index` -- the
        # caller (multi_record_index) is what enforces the 2+ record rule.
        columns = ["name"]
        index = {"r1": "A", "r2": "A"}
        rows = [("r1", ["Acme"]), ("r2", ["Acme"]), ("r3", ["Solo Corp"])]
        varies, blanks, considered = v.accumulate_variation(rows, columns, index)
        self.assertEqual(considered, 2)
        self.assertEqual(varies, {"name": 0})

    def test_multiple_columns_are_tracked_independently(self):
        columns = ["name", "city"]
        index = {"r1": "A", "r2": "A"}
        rows = [("r1", ["Acme", "Dublin"]), ("r2", ["Acme Holdings", "Dublin"])]
        varies, _, _ = v.accumulate_variation(rows, columns, index)
        self.assertEqual(varies, {"name": 1, "city": 0})

    def test_distinct_entities_are_tracked_independently(self):
        columns = ["name"]
        index = {"r1": "A", "r2": "A", "r3": "B", "r4": "B"}
        rows = [("r1", ["Acme"]), ("r2", ["Acme Holdings"]),
                ("r3", ["Beta"]), ("r4", ["Beta"])]
        varies, _, considered = v.accumulate_variation(rows, columns, index)
        self.assertEqual(varies, {"name": 1})   # only entity A varies
        self.assertEqual(considered, 4)


class TestRecordColumns(unittest.TestCase):
    def test_excludes_the_id_column(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "r.csv")
            write_records(path, ["id", "name", "city"], [])
            self.assertEqual(v.record_columns(path), ["name", "city"])


class TestReadRecordRows(unittest.TestCase):
    def test_yields_id_and_remaining_values(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = os.path.join(tmp, "r.csv")
            write_records(path, ["id", "name", "city"],
                          [["r1", "Acme", "Dublin"], ["r2", "Beta", "Cork"]])
            self.assertEqual(list(v.read_record_rows(path)),
                             [("r1", ["Acme", "Dublin"]), ("r2", ["Beta", "Cork"])])


class TestMeasureCorpusVariationEndToEnd(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = self._tmp.name
        self.addCleanup(self._tmp.cleanup)
        self.records = os.path.join(self.tmp, "records.csv")
        self.truth = os.path.join(self.tmp, "ground-truth.csv")

    def test_reproduces_the_alias_corpus_shape_on_a_small_fixture(self):
        # Two entities, 2 records each: name always varies (alias vs legal name),
        # city is copied verbatim onto every alias -- the exact defect this tool
        # exists to catch, at a scale a human can check by hand.
        write_records(self.records, ["id", "name", "city"], [
            ["e1-0", "Acme Ltd", "Dublin"],
            ["e1-1", "Acme Trading", "Dublin"],
            ["e2-0", "Beta Corp", "Cork"],
            ["e2-1", "Beta Holdings", "Cork"],
            ["solo-0", "Solo Corp", "Galway"],   # singleton entity -- must not count
        ])
        write_truth(self.truth, [
            ("e1-0", "E1"), ("e1-1", "E1"),
            ("e2-0", "E2"), ("e2-1", "E2"),
            ("solo-0", "SOLO"),
        ])
        report = v.measure_corpus_variation(self.records, self.truth)
        self.assertEqual(report.multi_record_entities, 2)
        self.assertEqual(report.records_considered, 4)
        self.assertEqual(report.varies_counts, {"name": 2, "city": 0})
        self.assertAlmostEqual(report.varies_share("name"), 1.0)
        self.assertAlmostEqual(report.varies_share("city"), 0.0)

    def test_no_multi_record_entities_does_not_divide_by_zero(self):
        write_records(self.records, ["id", "name"], [["r1", "Acme"], ["r2", "Beta"]])
        write_truth(self.truth, [("r1", "A"), ("r2", "B")])
        report = v.measure_corpus_variation(self.records, self.truth)
        self.assertEqual(report.multi_record_entities, 0)
        self.assertEqual(report.varies_share("name"), 0.0)
        self.assertEqual(report.blank_share("name"), 0.0)


class TestFormatReport(unittest.TestCase):
    def test_report_is_readable_and_includes_expected_figures(self):
        report = v.CorpusVariationReport(
            records_path="r.csv", truth_path="t.csv",
            columns=["name", "city"],
            multi_record_entities=2,
            records_considered=4,
            varies_counts={"name": 2, "city": 0},
            blank_counts={"name": 0, "city": 0},
        )
        text = v.format_report(report)
        self.assertIn("multi-record entities:  2", text)
        self.assertIn("name", text)
        self.assertIn("100.0000%", text)
        self.assertIn("0.0000%", text)

    def test_no_columns_does_not_crash(self):
        report = v.CorpusVariationReport(
            records_path="r.csv", truth_path="t.csv", columns=[],
            multi_record_entities=0, records_considered=0,
            varies_counts={}, blank_counts={})
        self.assertIn("no columns", v.format_report(report))


class TestCliMain(unittest.TestCase):
    def test_main_prints_a_report_and_returns_zero(self):
        import io
        import contextlib

        with tempfile.TemporaryDirectory() as tmp:
            records = os.path.join(tmp, "records.csv")
            truth = os.path.join(tmp, "ground-truth.csv")
            write_records(records, ["id", "name"], [["r1", "Acme"], ["r2", "Acme Holdings"]])
            write_truth(truth, [("r1", "A"), ("r2", "A")])
            buf = io.StringIO()
            with contextlib.redirect_stdout(buf):
                code = v.main([records, truth])
            self.assertEqual(code, 0)
            self.assertIn("Corpus variation report", buf.getvalue())


if __name__ == "__main__":
    unittest.main()
