import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch, Mock


sys.path.insert(0, str(Path(__file__).parents[1] / "scripts"))
spec = importlib.util.spec_from_file_location("scenario", Path(__file__).parents[1] / "scripts/scenario-client.py")
scenario = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scenario)


class ScenarioGuardsTests(unittest.TestCase):
    def run_sequence(self, steps, recognized, delays=(), results=None):
        clock = [0.0]
        captures = []

        def observe(host, output):
            clock[0] += delays[len(captures)] if len(captures) < len(delays) else 0.1
            captures.append(output.name)
            return {}

        def sleep(seconds):
            clock[0] += seconds

        case = {"context": {"ocr_version": "tesseract test"}, "steps": steps}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with patch.object(scenario.subprocess, "run", return_value=Mock(stdout="tesseract test\n")), \
                    patch.object(scenario, "observe", side_effect=observe), \
                    patch.object(scenario, "recognize", side_effect=recognized), \
                    patch.object(scenario.time, "monotonic", side_effect=lambda: clock[0]), \
                    patch.object(scenario.time, "sleep", side_effect=sleep), \
                    patch.object(scenario.inputs, "perform", side_effect=results,
                                 return_value={"status": "sent"}) as perform:
                report = scenario.run("test-alias", case, root / "evidence", root)
                return report, captures, perform.call_args_list

    @staticmethod
    def step(**overrides):
        return {"name": "guard", "before": [{"text": "Ready"}], "after": [{"text": "Ready"}],
                "action": {"kind": "key", "keys": [13]}, **overrides}

    def test_fresh_matching_postcondition_replaces_duplicate_capture(self):
        report, captures, calls = self.run_sequence([self.step(), self.step()], [True] * 3)
        self.assertEqual("passed", report["status"])
        self.assertEqual(["00-before", "00-after-00", "01-after-00"], captures)
        self.assertEqual("00-after-00", calls[1].args[1].name)
        self.assertTrue(report["steps"][1]["reused_observation"])

    def test_old_or_different_postcondition_requires_new_capture(self):
        for second, delays in [(self.step(), (0, 11)), (self.step(before=[{"text": "Other"}]), ())]:
            with self.subTest(second=second, delays=delays):
                report, captures, calls = self.run_sequence([self.step(), second], [True] * 4, delays)
                self.assertEqual("passed", report["status"])
                self.assertIn("01-before", captures)
                self.assertFalse(report["steps"][1]["reused_observation"])

    def test_reused_frame_does_not_bypass_windows_refusal_or_retry(self):
        report, captures, calls = self.run_sequence(
            [self.step(), self.step()], [True] * 2,
            results=[{"status": "sent"}, {"status": "refused", "error": "window_not_foreground"}])
        self.assertEqual("inconclusive", report["status"])
        self.assertEqual("window_not_foreground", report["error"])
        self.assertEqual(2, len(calls))
        self.assertEqual(2, len(captures))

    def test_world_confirmation_requires_consecutive_observations_without_repeated_input(self):
        report, captures, calls = self.run_sequence(
            [self.step(stable_observations=2)], [True, True, False, True, True])
        self.assertEqual("passed", report["status"])
        self.assertEqual(5, len(captures))
        self.assertEqual(1, len(calls))

    def test_single_world_match_is_inconclusive(self):
        report, captures, calls = self.run_sequence(
            [self.step(stable_observations=2, timeout_seconds=1)], [True, True])
        self.assertEqual("inconclusive", report["status"])
        self.assertEqual(1, len(calls))

    def test_ocr_variants_cannot_change_text_or_lower_threshold(self):
        for variant in ({"minimum_confidence": 20}, {"text": "Other"}, {"region": [0, 0, 1, 1]}):
            check = {"minimum_confidence": 70, "variants": [variant]}
            with self.subTest(variant=variant), self.assertRaisesRegex(ValueError, "invalid_ocr_variant"):
                scenario.recognize_text(Path("unused"), 0, None, check, "eng")

    def test_ocr_variants_keep_confidence_and_full_phrase_checks(self):
        from PIL import Image
        check = {"minimum_confidence": 70, "text": "Meadows", "region": [0, 0, 20, 20],
                 "variants": [{"preprocessing": "none"}, {"preprocessing": "light_text_v1", "psm": 7}]}
        with tempfile.TemporaryDirectory() as directory, Image.new("RGB", (20, 20)) as frame:
            for confidences, expected in [((60, 69), False), ((60, 90), True), ((90, 60), True)]:
                outputs = [Mock(stdout=f"conf\ttext\n{confidence}\tMeadows\n") for confidence in confidences]
                with self.subTest(confidences=confidences), \
                        patch.object(scenario.subprocess, "run", side_effect=outputs):
                    result = scenario.recognize_text(Path(directory), 0, frame, check, "eng")
                    self.assertEqual(expected, result["matched"])
                    self.assertEqual(2, len(result["variants"]))

    def test_untrusted_observation_id_cannot_be_used_as_remote_path(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for run_id in ("../outside", "a" * 33, "", "C:\\outside"):
                (root / "observation.json").write_text(json.dumps({"run_id": run_id}))
                with self.subTest(run_id=run_id), self.assertRaises(ValueError):
                    scenario.inputs.prepare_request(root, {"kind": "key", "keys": [9]}, "state", "after", 30)

    def run_stub(self, recognized, result):
        case = {"context": {"ocr_version": "tesseract test"}, "steps": [{
            "name": "guard", "before": [], "action": {"kind": "key", "keys": [9]},
            "after": [], "timeout_seconds": 0,
        }]}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with patch.object(scenario.subprocess, "run", return_value=Mock(stdout="tesseract test\n")), \
                    patch.object(scenario, "observe", return_value={}), \
                    patch.object(scenario, "recognize", return_value=recognized), \
                    patch.object(scenario.inputs, "perform", return_value=result) as perform:
                report = scenario.run("test-alias", case, root / "evidence", root)
                return report, perform.call_count

    def test_unknown_screen_never_sends_input(self):
        report, calls = self.run_stub(False, {"status": "sent"})
        self.assertEqual("inconclusive", report["status"])
        self.assertEqual(0, calls)

    def test_refused_input_is_not_retried(self):
        report, calls = self.run_stub(True, {"status": "refused", "error": "window_not_foreground"})
        self.assertEqual("window_not_foreground", report["error"])
        self.assertEqual(1, calls)

    def test_missing_postcondition_is_not_success_or_retry(self):
        report, calls = self.run_stub(True, {"status": "sent"})
        self.assertEqual("inconclusive", report["status"])
        self.assertEqual(1, calls)

    def test_ocr_requires_complete_phrase_and_confident_words(self):
        header = "conf\ttext\n"
        self.assertTrue(scenario.match_tsv(header + "95\tStart\n90\tGame\n", "Start game", 70))
        self.assertFalse(scenario.match_tsv(header + "95\tStart\n20\tGame\n", "Start game", 70))
        self.assertFalse(scenario.match_tsv(header + "95\tStart\n20\tnot\n90\tGame\n", "Start game", 70))
        self.assertFalse(scenario.match_tsv(header + "95\tRestart\n90\tGame\n", "Start game", 70))
        self.assertFalse(scenario.match_tsv(header + "95\tStart\n90\tGame\n", "", 70))

    def test_bounds_and_durations_cannot_escape_test_window(self):
        target = {"width": 1280, "height": 720}
        for action in [{"kind": "click", "x": -1, "y": 100}, {"kind": "click", "x": 1280, "y": 100},
                       {"kind": "move", "x": 0, "y": 720}, {"kind": "move", "x": 1.5, "y": 10},
                       {"kind": "look", "dx": 201, "dy": 0}, {"kind": "look", "dx": 0, "dy": 1.5},
                       {"kind": "key", "keys": [87], "hold_ms": 60000},
                       {"kind": "key", "keys": [91]}, {"kind": "key", "keys": [87, 87]},
                       {"kind": "key", "keys": [17, 27]}, {"kind": "key", "keys": [87.0]},
                       {"kind": "text", "text": "line\ncommand"}, {"kind": "text", "text": "a" * 129}]:
            with self.subTest(action=action), self.assertRaises(ValueError):
                scenario.inputs.validate_action(action, target)

    def test_valid_short_actions(self):
        target = {"width": 1280, "height": 720}
        for action in [{"kind": "click", "x": 1279, "y": 719},
                       {"kind": "move", "x": 0, "y": 0},
                       {"kind": "look", "dx": 0, "dy": 100},
                       {"kind": "key", "keys": [87], "hold_ms": 500},
                       {"kind": "text", "text": "Automation"}]:
            scenario.inputs.validate_action(action, target)


if __name__ == "__main__":
    unittest.main()
