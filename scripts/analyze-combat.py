#!/usr/bin/env python3
"""Приватный разбор ручного журнала, наблюдений CombatRecorder и снимков данжа."""

import argparse
from collections import Counter, defaultdict
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import tempfile


ROOT = Path(__file__).resolve().parents[1]


def utc(value):
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError("Время без часового пояса")
    return parsed.astimezone(timezone.utc)


def json_lines(paths):
    rows, issues, hashes = [], [], {}
    for path in paths:
        data = path.read_bytes()
        hashes[str(path)] = hashlib.sha256(data).hexdigest()
        lines = data.splitlines()
        for index, line in enumerate(lines):
            if not line.strip():
                continue
            try:
                row = json.loads(line)
                if not isinstance(row, dict):
                    raise ValueError("Событие должно быть объектом")
                rows.append(row)
            except (json.JSONDecodeError, UnicodeDecodeError):
                if index == len(lines) - 1 and not data.endswith(b"\n"):
                    issues.append({"kind": "incomplete_tail", "file": str(path), "line": index + 1})
                else:
                    raise ValueError(f"Повреждён журнал {path}, строка {index + 1}") from None
    return rows, issues, hashes


def unique(rows, key):
    seen, result, duplicates = {}, [], 0
    for row in rows:
        identity = key(row)
        canonical = json.dumps(row, sort_keys=True, ensure_ascii=False, allow_nan=False)
        if identity in seen:
            if seen[identity] != canonical:
                raise ValueError("Конфликт записей одного события")
            duplicates += 1
        else:
            seen[identity] = canonical
            result.append(row)
    return result, duplicates


def gaps(numbers):
    numbers = sorted(numbers)
    expected = 0
    result = []
    for number in numbers:
        if not isinstance(number, int) or number < expected:
            raise ValueError("Неверная последовательность событий")
        if number != expected:
            result.append([expected, number - 1])
        expected = number + 1
    return result


def legacy(rows, player):
    rows = [r for r in rows if r.get("Sample", {}).get("Character") == player
            and not r["Sample"].get("Probe", False)]
    rows, duplicates = unique(rows, lambda r: (r.get("Actor"), r["Sample"]["Attempt"],
                                             r["Sample"]["Sequence"]))
    groups = defaultdict(list)
    for row in rows:
        groups[(row.get("Actor"), row["Sample"]["Attempt"])].append(row)
    attempts, last = [], None
    for (_, attempt), group in groups.items():
        samples = sorted((r["Sample"] for r in group), key=lambda s: s["Sequence"])
        damage = [s for s in samples if s["Kind"] == "damage"]
        deaths = [s for s in damage if s["Before"] > 1.001 and s["After"] <= 1.001]
        inside = [s for s in damage if s["Position"][1] > 4000]
        attempts.append({"attempt": attempt, "events": len(samples),
                         "first_utc": samples[0]["ClientUtc"], "last_utc": samples[-1]["ClientUtc"],
                         "sequence_gaps": gaps([s["Sequence"] for s in samples]),
                         "damage_events": len(damage),
                         "inside_sources": dict(Counter(s["Attacker"] for s in inside)),
                         "threshold_crossings": len(deaths),
                         "first_threshold_utc": deaths[0]["ClientUtc"] if deaths else None,
                         "last_position": samples[-1]["Position"]})
        candidate = max(samples, key=lambda s: utc(s["ClientUtc"]))
        if last is None or utc(candidate["ClientUtc"]) > utc(last["ClientUtc"]):
            last = candidate
    return {"attempts": sorted(attempts, key=lambda a: utc(a["first_utc"])), "duplicates": duplicates,
            "last_position": last["Position"] if last else None,
            "last_position_utc": last["ClientUtc"] if last else None,
            "limitations": ["events_are_not_unique_hits_or_enemies", "no_outgoing_damage_or_attacker_id",
                            "hp_loss_at_god_floor_is_not_damage"]}


def detailed(rows, player):
    rows = [r for r in rows if "stream" in r and "kind" in r]
    if any(r.get("schema") != 1 for r in rows):
        raise ValueError("Неподдерживаемая схема CombatRecorder")
    rows, duplicates = unique(rows, lambda r: (r["stream"], r["sequence"]))
    streams, entities = defaultdict(list), {}
    totals = {d: {"rpc_events": 0, "hp_loss": 0.0, "ward_loss": 0.0, "zero_net_loss_events": 0}
              for d in ("incoming", "outgoing")}
    health_calls = 0
    peak = {"count": 0}
    last_player = None
    for row in rows:
        utc(row["observed_at_utc"])
        streams[row["stream"]].append(row)
        kind, value = row["kind"], row["value"]
        if kind == "nearby":
            people = [e for e in value.get("entities", []) if e.get("player") and e.get("name") == player]
            if people:
                person = people[0]
                if last_player is None or utc(row["observed_at_utc"]) > utc(last_player[0]):
                    last_player = (row["observed_at_utc"], person["position"])
                close = [e for e in value["entities"] if not e.get("player") and not e.get("dead")
                         and math.dist(person["position"], e["position"]) <= 5]
                if len(close) > peak["count"]:
                    peak = {"count": len(close), "observed_at_utc": row["observed_at_utc"],
                            "modifier_counts": dict(Counter(e["modifier_count"] for e in close)),
                            "origin_rooms": dict(Counter(str(e["room"]) for e in close)),
                            "snapshot_omitted_entities": value.get("omitted", 0)}
        if kind == "health_damage":
            health_calls += 1  # Вложенный слой не суммируется повторно с RPC.
        if kind not in ("damage_rpc", "healing"):
            continue
        target = value.get("before") if kind == "damage_rpc" else value.get("target")
        if not target:
            continue
        identity = target["id"] if target.get("persistent_id") else row["stream"] + ":" + target["id"]
        entity = entities.setdefault(identity, {"id": identity, "prefab": target["prefab"],
            "room": target["room"], "depth": target["depth"], "modifiers": target.get("modifiers", []),
            "persistent_id": target.get("persistent_id", False), "hp_loss": 0.0, "ward_loss": 0.0,
            "healing": 0.0, "rpc_events": 0, "observed_dead": False})
        if kind == "healing":
            entity["healing"] += value["actual"]
            continue
        entity["rpc_events"] += 1
        entity["hp_loss"] += value["hp_loss"]
        entity["ward_loss"] += value["ward_loss"]
        entity["observed_dead"] |= value["after"].get("dead", False)
        attacker = value.get("attacker") or {}
        direction = ("incoming" if target.get("player") and target.get("name") == player else
                     "outgoing" if attacker.get("player") and attacker.get("name") == player else None)
        if direction:
            count = totals[direction]
            count["rpc_events"] += 1
            count["hp_loss"] += value["hp_loss"]
            count["ward_loss"] += value["ward_loss"]
            count["zero_net_loss_events"] += value["hp_loss"] <= 0 and value["ward_loss"] <= 0
    stream_reports = []
    for stream, group in streams.items():
        ordered = sorted(group, key=lambda r: r["sequence"])
        stream_reports.append({"stream": stream, "events": len(group),
            "sequence_gaps": gaps([r["sequence"] for r in group]),
            "has_start": ordered[0]["kind"] == "start", "has_end": ordered[-1]["kind"] == "end",
            "truncated": any(r["kind"] == "truncated" for r in group),
            "errored_events": sum(bool(r["value"].get("error")) for r in group)})
    return {"streams": stream_reports, "duplicates": duplicates, "totals": totals,
            "health_layer_events_not_added_twice": health_calls, "entities": list(entities.values()),
            "peak_nearby_5m": peak, "last_player_position": last_player[1] if last_player else None,
            "last_player_utc": last_player[0] if last_player else None,
            "limitations": ["only_target_owner_records_damage", "missing_streams_are_not_zero_damage",
                            "current_weapon_may_differ_from_projectile_weapon",
                            "net_hp_and_ward_loss_are_not_raw_damage",
                            "healing_context_is_not_proof_of_modifier_source"]}


def snapshot(path):
    document = json.loads(path.read_text())
    if not document["result"]["ok"]:
        raise ValueError("Диагностический снимок содержит ошибку")
    return document["result"]["value"]


def spatial(current, position, radius, baseline=None):
    guards = [o for o in current["objects"] if o["kind"] == "guard"]
    near = [o for o in guards if math.dist(o["position"], position) <= radius]
    tokens = Counter(o["token"] for o in near)
    dungeons = []
    for dungeon in current["dungeons"]:
        token, rooms = dungeon["token"], dungeon["layout"]["rooms"]
        if token not in tokens:
            continue
        selected = [o for o in guards if o["token"] == token]
        arenas = {r["id"] for r in rooms if r.get("arena")}
        arena_guards = [o for o in selected if o["room"] in arenas]
        groups = Counter((o["room"], o["depth"], o["mods"].bit_count()) for o in near if o["token"] == token)
        before = next((d for d in baseline["dungeons"] if d["token"] == token), None) if baseline else None
        dungeons.append({"token": token, "remaining_guards": len(selected),
            "nearby": sum(groups.values()), "nearby_groups": [
                {"room": room, "depth": depth, "modifiers": mods, "count": count}
                for (room, depth, mods), count in sorted(groups.items())],
            "arena_remaining": len(arena_guards),
            "arena_nearby": sum(math.dist(o["position"], position) <= radius for o in arena_guards),
            "layout_matches_baseline": before["layout"] == dungeon["layout"] if before else None,
            "baseline_guards": sum(o["token"] == token and o["kind"] == "guard"
                                   for o in baseline["objects"]) if before else None})
    return {"radius_m": radius, "position": position, "nearby_guards": len(near), "dungeons": dungeons,
            "limitation": "post_battle_proximity_does_not_prove_individual_hit_attribution"}


def private_output(path):
    resolved = path.resolve()
    if resolved.is_relative_to(ROOT) and not resolved.is_relative_to(ROOT / ".local"):
        raise ValueError("Отчёт содержит игровые данные: используйте .local/ или каталог вне checkout")
    return resolved


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--events", type=Path, nargs="*", default=[])
    parser.add_argument("--combat", type=Path, nargs="*", default=[])
    parser.add_argument("--snapshot", type=Path)
    parser.add_argument("--baseline", type=Path)
    parser.add_argument("--player", default="Drug")
    parser.add_argument("--radius", type=float, default=5)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if not args.events and not args.combat:
        parser.error("Нужен --events или --combat")
    if not math.isfinite(args.radius) or args.radius <= 0:
        parser.error("Радиус должен быть конечным положительным числом")
    destination = private_output(args.output)
    inputs = args.events + args.combat + [p for p in [args.snapshot, args.baseline] if p]
    if destination in [p.resolve() for p in inputs]:
        parser.error("Выходной отчёт не должен заменять исходное доказательство")
    rows, issues, hashes = json_lines(args.events + args.combat)
    old = legacy(rows, args.player)
    result = {"schema": 1, "analyzed_at_utc": datetime.now(timezone.utc).isoformat(),
              "player": args.player, "input_sha256": hashes, "input_issues": issues,
              "legacy": old, "combat": detailed(rows, args.player)}
    candidates = [(old["last_position_utc"], old["last_position"]),
                  (result["combat"]["last_player_utc"], result["combat"]["last_player_position"])]
    candidates = [(stamp, point) for stamp, point in candidates if stamp and point]
    position = max(candidates, key=lambda pair: utc(pair[0]))[1] if candidates else None
    if args.snapshot and position:
        result["spatial"] = spatial(snapshot(args.snapshot), position, args.radius,
                                    snapshot(args.baseline) if args.baseline else None)
        for path in [args.snapshot, args.baseline]:
            if path:
                result["input_sha256"][str(path)] = hashlib.sha256(path.read_bytes()).hexdigest()
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile(mode="w", dir=destination.parent, prefix=".combat-", delete=False) as handle:
        temporary = Path(handle.name)
        try:
            handle.write(json.dumps(result, ensure_ascii=False, indent=2, allow_nan=False) + "\n")
            handle.flush()
            temporary.replace(destination)
        finally:
            temporary.unlink(missing_ok=True)
    print(f"Приватный отчёт: {destination}")


if __name__ == "__main__":
    main()
