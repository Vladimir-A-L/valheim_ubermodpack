#!/usr/bin/env python3
"""Выполнить одно ограниченное действие по проверенному кадру; затем требуется новое наблюдение."""

import argparse
import base64
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import uuid

from vision_transport import execute_worker


def validate_action(action, target):
    kind = action.get("kind")
    if kind in {"click", "move"}:
        for field, limit in (("x", "width"), ("y", "height")):
            if type(action.get(field)) is not int or not 0 <= action[field] < target[limit]:
                raise ValueError("Координата за пределами клиентской области")
    elif kind == "look":
        if any(type(action.get(axis)) is not int or abs(action[axis]) > 200 for axis in ("dx", "dy")):
            raise ValueError("Поворот камеры ограничен 200 отсчётами по каждой оси")
    elif kind == "key":
        keys = action.get("keys", [])
        allowed = {8, 9, 13, 16, 17, 27, 32, 37, 38, 39, 40, 112, 113, 114, 115, 116, 117}
        allowed.update(range(48, 91))
        if (not 1 <= len(keys) <= 2 or len(set(keys)) != len(keys)
                or any(type(k) is not int or k not in allowed for k in keys)
                or {17, 27}.issubset(keys)):
            raise ValueError("Недопустимые клавиши")
        if not 20 <= action.get("hold_ms", 80) <= 1500:
            raise ValueError("Удержание должно быть 20–1500 мс")
        action.setdefault("hold_ms", 80)
    elif kind == "text":
        value = action.get("text", "")
        if not isinstance(value, str) or not 1 <= len(value) <= 128 or any(ord(c) < 32 or ord(c) == 127 for c in value):
            raise ValueError("Допустима одна строка 1–128 символов")
    else:
        raise ValueError("Неизвестное действие")


def prepare_request(observation, action, state, expected, max_age):
    source = json.loads((observation / "observation.json").read_text())
    if not re.fullmatch(r"[0-9a-f]{32}", source.get("run_id", "")):
        raise ValueError("Некорректный идентификатор наблюдения")
    digest = hashlib.sha256((observation / "frame.png").read_bytes()).hexdigest()
    if (source.get("schema_version") != 1 or source.get("status") != "captured"
            or source.get("image_sha256") != digest):
        raise ValueError("Исходный кадр отсутствует или изменён")
    if not state.strip() or not expected.strip() or not 0 < max_age <= 120:
        raise ValueError("Требуются состояние, ожидаемый результат и возраст 1–120 с")
    validate_action(action, source["target"])
    return {
        "schema_version": 1, "action_id": uuid.uuid4().hex,
        "observation_run_id": source["run_id"], "image_sha256": digest,
        "planned_at_utc": datetime.now(timezone.utc).isoformat(),
        "review": {"method": "visual_model", "state": state}, "expected": expected,
        "max_age_seconds": max_age, "action": action,
    }


def perform(host, observation, action, state, expected, output, max_age=60, method="visual_model"):
    request = prepare_request(observation, action, state, expected, max_age)
    request["review"]["method"] = method
    output.mkdir(parents=True, exist_ok=False)
    (output / "request.json").write_text(json.dumps(request, ensure_ascii=False, indent=2) + "\n")
    windows = Path(__file__).with_name("windows")
    worker = (windows / "input-window.ps1").read_text()
    worker = worker.replace("__WINDOW_COMMON__", (windows / "window-common.ps1").read_text())
    for marker, data in (("__NATIVE_BASE64__", (windows / "input-native.cs").read_bytes()),
                         ("__REQUEST_BASE64__", json.dumps(request).encode())):
        worker = worker.replace(marker, base64.b64encode(data).decode())
    result = execute_worker(host, worker, output)
    (output / "result.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True)
    parser.add_argument("--observation", type=Path, required=True)
    parser.add_argument("--action", type=Path, required=True, help="Приватный JSON действия")
    parser.add_argument("--state", required=True, help="Состояние на просмотренном кадре")
    parser.add_argument("--expect", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--max-age", type=int, default=60)
    args = parser.parse_args()
    os.umask(0o077)
    result = perform(args.host, args.observation, json.loads(args.action.read_text()),
                     args.state, args.expect, args.output, args.max_age)
    print(f"Статус ввода: {result['status']}; игровой результат требует нового кадра.")
    if result["status"] != "sent":
        raise SystemExit(result.get("error", "Результат неопределён"))


if __name__ == "__main__":
    main()
