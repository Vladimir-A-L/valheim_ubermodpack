#!/usr/bin/env python3
"""Сверка конфигурации однократной подготовки с фактическим именем персонажа.

Не запускает игру, не правит сохранения и не подтверждает прибытие/выдачу в клиенте.
"""

import argparse
import hashlib
import json
import math
import re
from pathlib import Path


def check(payload, server_config, character, world):
    read = lambda path: json.loads(path.read_text(encoding="utf-8-sig"))
    manifest = read(payload / "manifest.json")
    client = read(payload / "forest-manual-test.json")
    server = read(server_config / "forest-manual-server.json")
    manual = read(server_config / "forest-manual-test.json")
    errors = []

    def require(condition, message):
        if not condition:
            errors.append(message)

    require(world == "ForestCryptQA", "Модуль подготовки требует мир ForestCryptQA")
    require(bool(character.strip()), "Не задано фактическое имя персонажа")
    require(client["PlayerName"] == character, "Имя клиента отличается от персонажа (с учётом регистра)")
    require(manifest["character"] == client["PlayerName"], "Имя в manifest отличается от настройки клиента")
    require(server["Enabled"] is True, "Серверный журнал выключен")
    require(character in server["Players"], "Персонаж отсутствует в серверном допуске (с учётом регистра)")
    require(server["AllowProbe"] is False, "Контрольные пробы должны быть выключены")
    require(manual == client, "Настройки ручного теста клиента и сервера различаются")
    for key in ("Enabled", "Record", "AutoPrepareOnce"):
        require(client[key] is True, f"Для однократной подготовки требуется {key}=true")
    require(client["ResetPov"] is False, "Подготовка не должна сбрасывать PoV")
    preparation = client["PreparationId"]
    require(bool(re.fullmatch(r"[a-zA-Z0-9_-]{1,40}", preparation)), "Недопустимый PreparationId")
    require(not client["ResetInventory"] or preparation != "v1", "Сброс комплекта требует отдельного ID")
    position = client["StartPosition"]
    require(len(position) == 3 and all(math.isfinite(v) for v in position), "Некорректная точка прибытия")
    require(math.isfinite(client["StartYaw"]), "Некорректное направление прибытия")
    for entry in manifest["files"]:
        actual = hashlib.sha256((payload / entry["source"]).read_bytes()).hexdigest()
        require(actual == entry["sha256"], f"SHA256 не совпал: {entry['source']}")
    require((payload / "forest-loadout.json").read_bytes() ==
            (server_config / "forest-loadout.json").read_bytes(), "Комплекты клиента и сервера различаются")
    return {"passed": not errors, "errors": errors, "payload_files": len(manifest["files"])}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--payload", type=Path, required=True)
    parser.add_argument("--server-config", type=Path, required=True)
    parser.add_argument("--character", required=True, help="Фактическое имя из server-peers, с учётом регистра")
    parser.add_argument("--world", required=True)
    args = parser.parse_args()
    result = check(args.payload, args.server_config, args.character, args.world)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    raise SystemExit(0 if result["passed"] else 1)
