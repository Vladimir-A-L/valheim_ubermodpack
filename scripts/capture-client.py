#!/usr/bin/env python3
"""Получить кадр окна Valheim через SSH и интерактивную задачу Windows, без игрового ввода."""

import argparse
import base64
import hashlib
import json
import os
from pathlib import Path


def save_capture(result, output):
    result = dict(result)
    if result.get("schema_version") != 1:
        raise ValueError("Неизвестная версия ответа захвата")
    if result.get("status") not in ("captured", "failed"):
        raise ValueError("Неизвестный статус захвата")
    image_data = result.pop("image_base64", None)
    if result["status"] == "captured":
        if not image_data:
            raise ValueError("Ответ не содержит PNG")
        pixels = base64.b64decode(image_data, validate=True)
        if not pixels.startswith(b"\x89PNG\r\n\x1a\n"):
            raise ValueError("Ответ не является PNG")
        if hashlib.sha256(pixels).hexdigest() != result["image_sha256"]:
            raise ValueError("SHA256 полученного PNG не совпадает с метаданными")
        with (output / "frame.png").open("xb") as stream:
            stream.write(pixels)
    with (output / "observation.json").open("x", encoding="utf-8") as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2)
        stream.write("\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, help="SSH-алиас тестового клиента")
    parser.add_argument("--output", type=Path, required=True, help="Новый приватный каталог наблюдения")
    args = parser.parse_args()
    os.umask(0o077)
    args.output.mkdir(parents=True, exist_ok=False)
    windows = Path(__file__).with_name("windows")
    worker = (windows / "capture-window.ps1").read_text().replace(
        "__WINDOW_COMMON__", (windows / "window-common.ps1").read_text())
    from vision_transport import execute_worker
    result = execute_worker(args.host, worker, args.output)
    save_capture(result, args.output)
    if result["status"] != "captured":
        raise SystemExit(f"Кадр не получен: {result.get('error')}. Диагностика: {args.output}")
    target = result["target"]
    print(f"Получен PNG {target['width']}×{target['height']}, DPI {target['dpi']}: {args.output / 'frame.png'}")
    print("Содержание кадра ещё требует визуальной проверки; игровой ввод не выполнялся.")


if __name__ == "__main__":
    main()
