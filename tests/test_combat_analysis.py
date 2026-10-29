"""Проверки полноты доказательств и отсутствия двойного подсчёта боя."""

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("combat_analysis", ROOT / "scripts/analyze-combat.py")
ANALYSIS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ANALYSIS)


class CombatAnalysisTests(unittest.TestCase):
    def legacy_row(self, seq, before=20, after=20):
        return {"Actor": "fixture", "Sample": {"Character": "QA", "Attempt": "a", "Sequence": seq,
                "ClientUtc": f"2026-10-29T09:00:{seq:02}Z", "Kind": "start" if seq == 0 else "damage",
                "Before": before, "After": after, "Position": [0, 5000, 0], "Attacker": "Skeleton"}}

    def test_floor_crossings_are_not_separate_attempts(self):
        rows = [self.legacy_row(0), self.legacy_row(1, 20, 1), self.legacy_row(2, 1, 1),
                self.legacy_row(3, 8, 1)]
        result = ANALYSIS.legacy(rows + [rows[-1]], "QA")
        self.assertEqual(result["duplicates"], 1)
        self.assertEqual(len(result["attempts"]), 1)
        self.assertEqual(result["attempts"][0]["threshold_crossings"], 2)
        self.assertEqual(result["attempts"][0]["damage_events"], 3)

    def test_conflicting_event_is_rejected(self):
        with self.assertRaises(ValueError):
            ANALYSIS.legacy([self.legacy_row(1, 20, 1), self.legacy_row(1, 20, 5)], "QA")

    def test_missing_events_are_visible(self):
        result = ANALYSIS.legacy([self.legacy_row(0), self.legacy_row(3)], "QA")
        self.assertEqual(result["attempts"][0]["sequence_gaps"], [[1, 2]])

    def test_partial_tail_and_corrupt_middle(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "events.jsonl"
            path.write_text('{"ok":1}\n{"unfinished"')
            rows, issues, _ = ANALYSIS.json_lines([path])
            self.assertEqual(len(rows), 1)
            self.assertEqual(issues[0]["kind"], "incomplete_tail")
            path.write_text('{"bad"\n{"ok":1}\n')
            with self.assertRaises(ValueError):
                ANALYSIS.json_lines([path])

    def test_nested_damage_is_not_added_twice(self):
        target = {"id": "guard", "persistent_id": True, "prefab": "Skeleton", "room": 3,
                  "depth": 20, "player": False, "modifiers": ["warded"]}
        value = {"before": target, "after": {"dead": False}, "hp_loss": 4, "ward_loss": 8,
                 "attacker": {"player": True, "name": "QA"}}
        rows = [{"schema": 1, "stream": "s", "sequence": i, "observed_at_utc": "2026-10-29T09:00:00Z",
                 "kind": kind, "value": value} for i, kind in enumerate(["damage_rpc", "health_damage"])]
        result = ANALYSIS.detailed(rows, "QA")
        self.assertEqual(result["totals"]["outgoing"]["hp_loss"], 4)
        self.assertEqual(result["totals"]["outgoing"]["ward_loss"], 8)
        self.assertEqual(result["health_layer_events_not_added_twice"], 1)
        self.assertFalse(result["streams"][0]["has_end"])

    def test_arena_origin_is_separate_from_nearness(self):
        rooms = [{"id": 1, "arena": False}, {"id": 2, "arena": True}]
        guard = {"kind": "guard", "token": "test", "room": 1, "depth": 40, "mods": 1023,
                 "position": [0, 5000, 1]}
        current = {"dungeons": [{"token": "test", "layout": {"rooms": rooms}}],
                   "objects": [guard, dict(guard, room=2, position=[0, 5000, 30])]}
        result = ANALYSIS.spatial(current, [0, 5000, 0], 5)
        self.assertEqual(result["nearby_guards"], 1)
        self.assertEqual(result["dungeons"][0]["arena_nearby"], 0)
        self.assertEqual(result["dungeons"][0]["arena_remaining"], 1)

    def test_public_output_and_naive_time_are_rejected(self):
        with self.assertRaises(ValueError):
            ANALYSIS.private_output(ROOT / "docs/raw-combat.json")
        with self.assertRaises(ValueError):
            ANALYSIS.utc("2026-10-29T09:00:00")

    def test_nearby_peak_keeps_three_dimensional_distance(self):
        person = {"player": True, "name": "QA", "position": [0, 5000, 0]}
        guard = {"player": False, "position": [1, 5000, 0], "modifier_count": 10, "room": 40}
        upper = dict(guard, position=[0, 5010, 0])
        rows = [{"schema": 1, "stream": "s", "sequence": 0, "observed_at_utc": "2026-10-29T09:00:00Z",
                 "kind": "nearby", "value": {"entities": [person, guard, upper], "omitted": 0}}]
        result = ANALYSIS.detailed(rows, "QA")
        self.assertEqual(result["peak_nearby_5m"]["count"], 1)
        self.assertEqual(result["last_player_position"], [0, 5000, 0])


if __name__ == "__main__":
    unittest.main()
