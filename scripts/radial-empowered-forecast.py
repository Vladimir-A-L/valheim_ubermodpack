#!/usr/bin/env python3
"""Таблица согласованной новой ревизии; не изменяет конфиги или сохранения игры."""
import argparse
import csv
import json
from decimal import Decimal
from pathlib import Path

def unique_budget(ids, rules):
    result = sum(Decimal(rules["Weights"][name]) for name in ids)
    for name in sorted(ids):
        result *= 1 + Decimal(rules["Weights"][name]) / Decimal(rules["GrowthDivisor"])
    for synergy in sorted(rules["Synergies"], key=lambda row: row["Id"]):
        if set(synergy["Requires"]) <= set(ids):
            result *= Decimal(synergy["Factor"])
    return result


def reward(budget, rules):
    scale = Decimal(rules["Bands"][-1]["Upper"])
    complete = int(budget // scale)
    remainder = budget % scale
    groups = [(size, 6) for size in range(1, complete + 1)]
    coins = 0
    if remainder:
        band = next(row for row in rules["Bands"] if remainder <= row["Upper"])
        coins = band["Coins"]
        if not coins:
            groups.append((complete + 1, band["Quality"]))
    return groups, coins


def rows(config):
    kind = config["Types"][0]
    rules = config["Rewards"]
    for profile in ("Near", "Middle", "Far"):
        maximum = kind[profile]["MaximumDepth"]
        price = 7 if profile == "Far" else 10
        for role in ("guard", "boss", "helper"):
            pool = kind["OrdinaryModifiers"] if role == "guard" else kind["Modifiers"]
            for depth in range(maximum + 1):
                raw = 6 + depth // 2 if profile == "Far" else max(0, (depth - 16 + 3) // 4)
                ids = pool[:raw]
                stacks = max(0, raw - len(pool))
                combat = unique_budget(ids, rules)
                budget = combat * Decimal(price) / 10 + price * stacks
                groups, coins = reward(budget, rules)
                if role == "boss":
                    groups = [(size * 2, quality) for size, quality in groups]
                elif role == "helper":
                    groups = []  # У помощника нет отдельной гарантированной бюджетной выдачи.
                yield {
                    "profile": profile.lower(), "role": role, "depth": depth, "raw_count": raw,
                    "unique_count": len(ids), "modifiers": ",".join(ids), "empowered": stacks,
                    "stack_health_factor": str(1 + Decimal(".15") * stacks),
                    "stack_damage_factor": str(1 + Decimal(".10") * stacks),
                    "combat_budget_for_count": str(combat), "loot_budget": str(budget),
                    "groups_size_quality": ";".join(f"{size}:{quality}" for size, quality in groups),
                    "item_count": sum(size for size, _ in groups),
                    "chest_remainder_coins": coins if role == "guard" else "not-applicable",
                    "boss_coins_range": {"Near": "50-200", "Middle": "150-500", "Far": "300-900"}[profile]
                    if role == "boss" else "not-applicable",
                    "helper_reward_note": "ordinary-PoV-only" if role == "helper" else "",
                }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, default=Path("configs/radial-empowered/radial-dungeons.json"))
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    config = json.loads(args.config.read_text(), parse_float=Decimal)
    values = list(rows(config))
    with args.output.open("w", newline="") as output:
        writer = csv.DictWriter(output, fieldnames=list(values[0]))
        writer.writeheader()
        writer.writerows(values)
    print(f"Строк: {len(values)}; {args.output}")


if __name__ == "__main__":
    main()
