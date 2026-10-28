"""Импорт не завершать по частичному checkpoint; исходную копию сохранить один раз."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location('character_import',
    Path(__file__).resolve().parents[1] / 'scripts/finalize-character-import.py')
IMPORT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(IMPORT)


class CharacterImportTests(unittest.TestCase):
    def test_wait_for_profile_and_completed_checkpoint_then_preserve_snapshot(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            config, profile, receipt = (root / name for name in ('settings.yml', 'test.fch', 'receipt.json'))
            config.write_text('serverSettings:\n  loadServerCharacterOnJoin: false # import\n')
            complete = 'WorldCharacterCheckpointCompleted operation=qa, persisted=1, pending=0\n'
            self.assertFalse(IMPORT.finalize(config, profile, complete, receipt))
            profile.write_bytes(b'original' * 20)
            self.assertFalse(IMPORT.finalize(config, profile,
                'WorldCharacterCheckpointPartial operation=qa, persisted=0, pending=1', receipt))
            self.assertIn(': false', config.read_text())
            self.assertTrue(IMPORT.finalize(config, profile, complete, receipt))
            self.assertIn(': true # import', config.read_text())
            self.assertEqual(receipt.with_suffix('.fch').read_bytes(), b'original' * 20)
            profile.write_bytes(b'later' * 30)
            self.assertTrue(IMPORT.finalize(config, profile, complete, receipt))
            self.assertEqual(receipt.with_suffix('.fch').read_bytes(), b'original' * 20)

    def test_duplicate_setting_stops_without_mutation(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            config = root / 'settings.yml'
            original = '  loadServerCharacterOnJoin: false\n' * 2
            config.write_text(original)
            with self.assertRaises(ValueError):
                IMPORT.finalize(config, root / 'none.fch', '', root / 'receipt.json')
            self.assertEqual(config.read_text(), original)
