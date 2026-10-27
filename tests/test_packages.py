import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile


spec = importlib.util.spec_from_file_location("packages", Path(__file__).parents[1] / "scripts/fetch-packages.py")
packages = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packages)


class PackageVerificationTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.archive = Path(self.directory.name) / "package.zip"
        self.record = {
            "package": "Author-Mod-1.2.3",
            "representative_file": {"name": "Mod.dll", "bytes": 3, "sha256": hashlib.sha256(b"dll").hexdigest()},
        }

    def write_archive(self, entry="plugins/Mod.dll", payload=b"dll", extra=None):
        with zipfile.ZipFile(self.archive, "w") as archive:
            archive.writestr("manifest.json", json.dumps({"name": "Mod", "version_number": "1.2.3"}))
            archive.writestr(entry, payload)
            if extra:
                archive.writestr(extra, b"other")

    def test_authentic_dll_with_windows_zip_path(self):
        self.write_archive(entry="plugins\\Mod.dll")
        dependencies, _ = packages.inspect_archive(self.archive, self.record)
        self.assertEqual([], dependencies)

    def test_modified_dll_is_rejected(self):
        self.write_archive(payload=b"bad")
        with self.assertRaisesRegex(ValueError, "SHA256"):
            packages.inspect_archive(self.archive, self.record)

    def test_traversal_is_rejected_for_both_platforms(self):
        for extra in ("../outside", "plugins/../../outside", "plugins\\..\\..\\outside", "C:/outside", "/outside"):
            with self.subTest(extra=extra):
                self.write_archive(extra=extra)
                with self.assertRaisesRegex(ValueError, "путь"):
                    packages.inspect_archive(self.archive, self.record)

    def test_case_insensitive_collision_is_rejected(self):
        self.write_archive(extra="Plugins/mod.dll")
        with self.assertRaisesRegex(ValueError, "Повторяющийся"):
            packages.inspect_archive(self.archive, self.record)

    def test_duplicate_dll_is_rejected(self):
        self.write_archive(extra="other/Mod.dll")
        with self.assertRaisesRegex(ValueError, "единственная"):
            packages.inspect_archive(self.archive, self.record)

    def test_missing_or_too_old_dependency_is_rejected(self):
        dependent = {"package": "Author-Mod-1.0.0", "dependencies": ["Author-Lib-2.0.0"]}
        for records in ([dependent], [dependent, {"package": "Author-Lib-1.9.9", "dependencies": []}]):
            with self.assertRaisesRegex(ValueError, "зависимости"):
                packages.check_dependencies(records)

    def test_newer_explicit_dependency_is_accepted(self):
        packages.check_dependencies([
            {"package": "Author-Mod-1.0.0", "dependencies": ["Author-Lib-2.0.0"]},
            {"package": "Author-Lib-2.1.0", "dependencies": []},
        ])

    def test_two_versions_cannot_be_loaded_together(self):
        with self.assertRaisesRegex(ValueError, "Несколько версий"):
            packages.check_dependencies([
                {"package": "Author-Mod-1.0.0", "dependencies": []},
                {"package": "Author-Mod-2.0.0", "dependencies": []},
            ])

    def test_updated_release_can_be_verified_and_staged_without_original_inventory(self):
        self.write_archive()
        root = Path(self.directory.name)
        cache = root / "cache"
        cache.mkdir()
        archive = cache / "Author-Mod-1.2.3.zip"
        self.archive.rename(archive)
        selection = root / "selection.json"
        selection.write_text(json.dumps({"packages": [self.record["package"]]}))
        lock = root / "lock.json"
        lock.write_text(json.dumps({"packages": [{
            "package": self.record["package"], "dependencies": [],
            "archive_sha256": packages.file_hash(archive),
            "verified_file": self.record["representative_file"],
        }]}))
        scripts = Path(__file__).parents[1] / "scripts"
        result = subprocess.run([
            sys.executable, str(scripts / "fetch-packages.py"), "--lock", str(lock),
            "--selection", str(selection), "--cache", str(cache), "--baseline", str(root / "absent.json"),
        ], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        output = root / "overlay"
        result = subprocess.run([
            sys.executable, str(scripts / "stage-foundation.py"), "--lock", str(lock),
            "--selection", str(selection), "--cache", str(cache), "--profile", "core",
            "--output", str(output), "--config-root", str(root / "no-config"),
        ], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(b"dll", (output / "BepInEx/plugins/Author-Mod/Mod.dll").read_bytes())

    def test_new_release_observation_records_actual_hash(self):
        self.write_archive()
        root = Path(self.directory.name)
        self.archive.rename(root / "Author-Mod-1.2.3.zip")
        record = {"package": self.record["package"], "representative_file": {"name": "Mod.dll"},
                  "verification_source": "release-archive-observation"}
        actual = packages.fetch(record, root, None)
        self.assertEqual("release-archive-observation", actual["verification_source"])
        self.assertEqual(self.record["representative_file"]["sha256"], actual["verified_file"]["sha256"])

    def test_changed_locked_dependencies_are_rejected(self):
        self.write_archive()
        root = Path(self.directory.name)
        archive = root / "Author-Mod-1.2.3.zip"
        self.archive.rename(archive)
        with self.assertRaisesRegex(ValueError, "Зависимости"):
            packages.fetch(self.record, root, {
                "archive_sha256": packages.file_hash(archive), "dependencies": ["Unknown-Library-1.0.0"],
            })


if __name__ == "__main__":
    unittest.main()
