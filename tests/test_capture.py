import base64
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


spec = importlib.util.spec_from_file_location("capture", Path(__file__).parents[1] / "scripts/capture-client.py")
capture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(capture)

# PNG 1×1: проверяем целостность передачи, а не распознавание игрового экрана.
PNG = base64.b64decode(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a2ioAAAAASUVORK5CYII="
)


class CaptureEvidenceTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.output = Path(directory.name)
        self.result = {
            "schema_version": 1, "status": "captured", "visual_verdict": "unreviewed",
            "image_base64": base64.b64encode(PNG).decode(),
            "image_sha256": hashlib.sha256(PNG).hexdigest(),
        }

    def test_verified_image_remains_visually_unreviewed(self):
        capture.save_capture(self.result, self.output)
        self.assertEqual(PNG, (self.output / "frame.png").read_bytes())
        metadata = json.loads((self.output / "observation.json").read_text())
        self.assertNotIn("image_base64", metadata)
        self.assertEqual("unreviewed", metadata["visual_verdict"])

    def test_tampering_missing_image_and_wrong_format_are_rejected(self):
        for changes in ({"image_sha256": "0" * 64}, {"image_base64": None},
                        {"image_base64": base64.b64encode(b"not an image").decode()}):
            with self.subTest(changes=changes):
                with self.assertRaises(ValueError):
                    capture.save_capture(self.result | changes, self.output)
                self.assertEqual([], list(self.output.iterdir()))

    def test_refused_capture_keeps_diagnostic_without_frame(self):
        result = {"schema_version": 1, "status": "failed", "error": "window_not_foreground"}
        capture.save_capture(result, self.output)
        self.assertFalse((self.output / "frame.png").exists())
        self.assertEqual(result, json.loads((self.output / "observation.json").read_text()))

    def test_unknown_protocol_is_not_recorded_as_evidence(self):
        for changes in ({"schema_version": 2}, {"status": "ok"}):
            with self.subTest(changes=changes):
                with self.assertRaises(ValueError):
                    capture.save_capture(self.result | changes, self.output)
                self.assertEqual([], list(self.output.iterdir()))

    def test_previous_capture_is_never_overwritten(self):
        (self.output / "frame.png").write_bytes(b"previous evidence")
        with self.assertRaises(FileExistsError):
            capture.save_capture(self.result, self.output)
        self.assertEqual(b"previous evidence", (self.output / "frame.png").read_bytes())
