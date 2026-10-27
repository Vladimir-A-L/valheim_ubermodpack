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
                       {"kind": "key", "keys": [87], "hold_ms": 60000},
                       {"kind": "key", "keys": [91]}, {"kind": "key", "keys": [87, 87]},
                       {"kind": "key", "keys": [17, 27]}, {"kind": "key", "keys": [87.0]},
                       {"kind": "text", "text": "line\ncommand"}, {"kind": "text", "text": "a" * 129}]:
            with self.subTest(action=action), self.assertRaises(ValueError):
                scenario.inputs.validate_action(action, target)

    def test_valid_short_actions(self):
        target = {"width": 1280, "height": 720}
        for action in [{"kind": "click", "x": 1279, "y": 719},
                       {"kind": "key", "keys": [87], "hold_ms": 500},
                       {"kind": "text", "text": "Automation"}]:
            scenario.inputs.validate_action(action, target)


if __name__ == "__main__":
    unittest.main()
