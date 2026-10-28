"""Контроль прогноза: исторический пример и явные веса не подменяют друг друга."""

import copy
import importlib.util
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("reward_forecast", ROOT / "scripts/forecast-dungeon-rewards.py")
FORECAST = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(FORECAST)


class DungeonRewardForecastTests(unittest.TestCase):
    def setUp(self):
        self.model = json.loads((ROOT / "manifests/dungeon-reward-forecast-inputs.json").read_text())
        self.rules = json.loads((ROOT / "configs/history/dungeon-rewards-20261028.json").read_text())

    def test_interval_boundaries_and_overflow(self):
        rules = json.loads((ROOT / "configs/dungeon-rewards.json").read_text())
        for budget, coins, items, quality in [(0, 0, 0, {}), (10, 50, 0, {}), (20, 100, 0, {}),
                                             (20.01, 0, 1, {"Magic": 1}), (135, 0, 1, {"Mythic": 1}),
                                             (135.01, 0, 1, {"Ancient": 1}), (175, 0, 1, {}),
                                             (185, 50, 1, {}), (195, 100, 1, {}),
                                             (195.01, 0, 3, {"Magic": 1}), (350, 0, 3, {})]:
            p = FORECAST.project_intervals(budget, rules)
            self.assertEqual((p["gold_compensation"], p["total_special_items"],
                              p["active_group_rarity_probabilities"]), (coins, items, quality))
        forecast = FORECAST.forecast_runtime(self.model, rules)
        maximum = forecast["rows"][-1]["maximum"]
        self.assertGreater(maximum["quality_budget"], 175)
        self.assertLess(maximum["quality_budget"], 195)
        self.assertEqual(sum(row["combinations"] for row in forecast["rows"]), 1024)

    def test_historical_example_keeps_six_plus_four_and_small_remainder(self):
        self.assertEqual(FORECAST.project(300, self.model)["total_special_items"], 6)
        self.assertEqual(FORECAST.project(305, self.model)["gold_compensation"], 50)
        self.assertEqual(FORECAST.project(310, self.model)["total_special_items"], 10)
        self.assertEqual(FORECAST.project(300, self.model)["gold_compensation"], 0)

    def test_runtime_forecast_uses_actual_global_weights(self):
        result = FORECAST.forecast_runtime(self.model, self.rules)
        self.assertEqual(sum(row["combinations"] for row in result["rows"]), 1024)
        maximum = result["rows"][-1]["maximum"]
        self.assertAlmostEqual(maximum["quality_budget"], 114.72552)
        self.assertEqual(maximum["guaranteed_ancient_items"], 0)
        self.assertEqual(maximum["active_group_size"], 1)
        self.assertAlmostEqual(maximum["active_group_rarity_probabilities"]["Ancient"], 0.56046)

    def test_runtime_forecast_refuses_unpriced_or_duplicate_synergy(self):
        bad = copy.deepcopy(self.rules)
        bad["Synergies"][0]["Requires"].append("not-priced")
        with self.assertRaises(ValueError):
            FORECAST.forecast_runtime(self.model, bad)
        bad = copy.deepcopy(self.rules)
        bad["Synergies"].append(bad["Synergies"][0])
        with self.assertRaises(ValueError):
            FORECAST.forecast_runtime(self.model, bad)


if __name__ == "__main__":
    unittest.main()
