r"""Tests for build-gleif-org-corpus.py's --stage argument handling.

The driver's filename is not a valid Python identifier (hyphens), so it is loaded via
importlib rather than a normal import. This file tests ONLY the stage-selection logic --
argument parsing and the (order-cosmetic) canonicalization done by resolve_stages --
never the build itself, which touches a 4.9 GB source and takes minutes. run_build tests
stage MEMBERSHIP, never order, so resolve_stages's sorting is a readability aid, not a
correctness mechanism; see gleif_org_corpus.run_build for the actual stage semantics.

    python -m unittest discover -s tools\corpus -p "test_*.py" -v
"""
import importlib.util
import os
import unittest

_spec = importlib.util.spec_from_file_location(
    "build_gleif_org_corpus_driver",
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "build-gleif-org-corpus.py"))
driver = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(driver)


class TestResolveStages(unittest.TestCase):
    def test_no_selection_runs_every_stage_in_order(self):
        self.assertEqual(driver.resolve_stages(None), driver.g.STAGE_ORDER)

    def test_single_stage_selection(self):
        self.assertEqual(driver.resolve_stages(["verify"]), ["verify"])

    def test_out_of_order_selection_is_canonicalized(self):
        # Normalisation only: run_build tests stage MEMBERSHIP, never order, so it
        # already runs verify's code before publish's code regardless of list order.
        # This pins that resolve_stages reports the selection in canonical STAGE_ORDER
        # for readability, not that ordering affects run_build's behavior.
        self.assertEqual(driver.resolve_stages(["publish", "verify"]), ["verify", "publish"])


class TestArgParsing(unittest.TestCase):
    def test_no_stage_argument_resolves_to_every_stage(self):
        args = driver.build_arg_parser().parse_args([])
        self.assertEqual(driver.resolve_stages(args.stage), driver.g.STAGE_ORDER)

    def test_stage_publish_verify_resolves_to_verify_then_publish(self):
        # Pins that argparse's nargs="+" accepts multiple --stage values (the actual
        # fix for the defect: --stage used to accept only one value, so `--stage
        # publish` alone could never satisfy run_build's "publish requires verify in
        # the same invocation" guard) and that resolve_stages reports them in
        # canonical STAGE_ORDER for readability -- not because run_build needs them in
        # that order; it tests membership, not order.
        args = driver.build_arg_parser().parse_args(["--stage", "publish", "verify"])
        self.assertEqual(driver.resolve_stages(args.stage), ["verify", "publish"])

    def test_unknown_stage_is_rejected_by_argparse(self):
        with self.assertRaises(SystemExit):
            driver.build_arg_parser().parse_args(["--stage", "nonsense"])


if __name__ == "__main__":
    unittest.main()
