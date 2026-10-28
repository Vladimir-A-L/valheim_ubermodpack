#!/usr/bin/env python3
"""Сценарный прогноз групп наград; не игровой плагин и не измерение боевого баланса."""

import argparse
from datetime import datetime, timezone
from decimal import Decimal, ROUND_CEILING
import hashlib
import json
from pathlib import Path


def decimal(value):
    result = Decimal(str(value))
    if not result.is_finite():
        raise ValueError("Нужны конечные численные параметры")
    return result


def number(value):
    return float(round(value, 8))


def validate(model):
    scale = decimal(model["ancient_scale"])
    knots = model["quality_knots"]
    scores = [decimal(row["score"]) for row in knots]
    if len(scores) < 2 or not 0 < scores[0] < scale or scores[-1] != scale:
        raise ValueError("Порог первого предмета должен быть положительным и ниже Ancient")
    if any(a >= b for a, b in zip(scores, scores[1:])):
        raise ValueError("Пороги качества должны строго возрастать")
    if knots[0]["rarity"] != "Common" or knots[-1]["rarity"] != "Ancient":
        raise ValueError("Шкала должна начинаться обычным предметом и заканчиваться Ancient")
    gold = model["gold_for_small_positive_remainder"]
    if not isinstance(gold, int) or isinstance(gold, bool) or gold < 0:
        raise ValueError("Компенсация должна быть целым неотрицательным количеством монет")
    for profile in model["profiles"]:
        if decimal(profile["location_bonus"]) < 0:
            raise ValueError("Надбавка не может быть отрицательной")
    if decimal(model["example_modifier_weight"]) <= 0:
        raise ValueError("Пример веса модификатора должен быть положительным")
    maximum = model["max_assigned_modifiers"]
    if not isinstance(maximum, int) or isinstance(maximum, bool) or maximum <= 0:
        raise ValueError("Нужен положительный целочисленный предел модификаторов")
    for count in model["modifier_count_examples"]:
        if not isinstance(count, int) or isinstance(count, bool) or not 0 <= count <= maximum:
            raise ValueError("Некорректное число модификаторов")
    if any(decimal(factor) < 1 for factor in model["synergy_factor_examples"]):
        raise ValueError("Пример усиления сочетания должен быть не меньше единицы")
    if decimal(model["protected_progression_zone_budget"]) != 0:
        raise ValueError("Бюджет гарантированной начальной части должен быть нулевым")


def quality(remainder, knots):
    """Вероятности соседних качеств; один общий выбор на всю незавершённую группу."""
    for left, right in zip(knots, knots[1:]):
        lower, upper = decimal(left["score"]), decimal(right["score"])
        if lower <= remainder <= upper:
            high = (remainder - lower) / (upper - lower)
            return {rarity: number(probability) for rarity, probability in
                    ((left["rarity"], 1 - high), (right["rarity"], high)) if probability > 0}
    raise ValueError("Остаток за пределами шкалы появления предмета")


def project(budget, model):
    budget = decimal(budget)
    if budget < 0:
        raise ValueError("Бюджет не может быть отрицательным")
    scale = decimal(model["ancient_scale"])
    floor = decimal(model["quality_knots"][0]["score"])
    completed = int(budget // scale)
    remainder = budget - completed * scale
    ancient = completed * (completed + 1) // 2
    active = completed + 1 if remainder >= floor else 0
    distribution = quality(remainder, model["quality_knots"]) if active else {}
    return {
        "quality_budget": number(budget), "completed_groups": completed,
        "remainder": number(remainder), "guaranteed_ancient_items": ancient,
        "active_group_size": active, "active_group_rarity_probabilities": distribution,
        "total_special_items": ancient + active,
        "gold_compensation": model["gold_for_small_positive_remainder"] if 0 < remainder < floor else 0,
        "standard_dungeon_loot_additional": True,
    }


def guard_budget(modifier_points, location_bonus, synergy_factor, protected=False):
    points, bonus, factor = map(decimal, (modifier_points, location_bonus, synergy_factor))
    if points < 0 or bonus < 0 or factor < 1:
        raise ValueError("Некорректные очки, надбавка или коэффициент сочетания")
    if protected:
        if points != 0:
            raise ValueError("В гарантированной части не должно быть усиленной охраны")
        return Decimal(0)
    return bonus + points * factor


def forecast(model):
    validate(model)
    scale = decimal(model["ancient_scale"])
    floor = decimal(model["quality_knots"][0]["score"])
    weight = decimal(model["example_modifier_weight"])
    max_points = model["max_assigned_modifiers"] * weight
    rows, thresholds, protected_rows, unmodified_rows = [], [], [], []
    for profile in model["profiles"]:
        bonus = decimal(profile["location_bonus"])
        protected_rows.append({"profile": profile["id"], **project(guard_budget(0, bonus, 1, True), model)})
        unmodified_rows.append({"profile": profile["id"], **project(guard_budget(0, bonus, 1), model)})
        for count in model["modifier_count_examples"]:
            for factor in model["synergy_factor_examples"]:
                rows.append({"profile": profile["id"], "modifier_count": count,
                             "modifier_points": number(count * weight), "location_bonus": number(bonus),
                             "synergy_factor": number(decimal(factor)),
                             **project(guard_budget(count * weight, bonus, factor), model)})
        lower, upper = (3 * scale + floor - bonus) / max_points, (4 * scale - bonus) / max_points
        first = max(0, int(((scale - bonus) / weight).to_integral_value(rounding=ROUND_CEILING)))
        thresholds.append({"profile": profile["id"], "first_ancient_modifier_count_without_synergy": first,
                           "max_budget_without_synergy": number(bonus + max_points),
                           "six_plus_four_synergy_min_at_max_modifiers": number(lower),
                           "six_plus_four_synergy_max_exclusive_at_max_modifiers": number(upper),
                           "six_plus_four_reachable_without_synergy": lower <= 1 < upper})
    return {
        "schema_version": 2, "captured_at_utc": datetime.now(timezone.utc).isoformat(),
        "status": "unified-affix-cost-example-not-gameplay-observation",
        "reward_policy": {key: model[key] for key in
                          ("reward_scope", "native_epicloot_container_loot", "item_drop_limits",
                           "large_reward_example_is_target")},
        "formula": "B = locationBonus + sum(globalModifierCosts) * globalSynergyFactor; protectedZone -> 0",
        "quality_curve": "piecewise-linear-probabilities-between-adjacent-knots",
        "rows": rows, "six_ancient_plus_four_thresholds": thresholds,
        "protected_zone_examples": protected_rows, "unprotected_zero_modifier_examples": unmodified_rows,
        "stage_examples": [project(b, model) for b in (0, 5, 10, 60, 100, 105, 160, 200, 260, 300, 305, 360)],
        "limitations": ["ten-points-per-modifier-is-an-example", "rarity-knots-and-location-bonuses-are-proposals",
                        "global-synergy-weights-not-calibrated", "native-EpicLoot-loot-not-simulated",
                        "item-base-selection-and-boss-gates-not-simulated",
                        "forty-transition-layout-not-implemented", "guarded-chest-frequency-not-selected",
                        "no-gameplay-or-runtime-reward-change"],
    }


def main():
    root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inputs", type=Path, default=root / "manifests/dungeon-reward-forecast-inputs.json")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    payload = args.inputs.read_bytes()
    result = forecast(json.loads(payload))
    result["inputs_sha256"] = hashlib.sha256(payload).hexdigest()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Сценарный прогноз: {len(result['rows'])} случаев, {len(result['stage_examples'])} примеров границ")


if __name__ == "__main__":
    main()
