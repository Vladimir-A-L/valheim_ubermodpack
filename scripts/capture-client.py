#!/usr/bin/env python3
"""Получить кадр окна Valheim через SSH и интерактивную задачу Windows, без игрового ввода."""

import argparse
import base64
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time


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
    worker = base64.b64encode((windows / "capture-window.ps1").read_bytes()).decode("ascii")
    host = base64.b64encode((windows / "capture-host.cs").read_bytes()).decode("ascii")
    controller = (windows / "invoke-capture.ps1").read_text().replace("__WORKER_BASE64__", worker)
    controller = controller.replace("__HOST_BASE64__", host)
    encoded = base64.b64encode(controller.encode("utf-8")).decode("ascii")
    command = (
        "try { & ([ScriptBlock]::Create([Text.Encoding]::UTF8.GetString("
        f"[Convert]::FromBase64String('{encoded}')))); exit 0 "
        "} catch { [Console]::Error.WriteLine($_); exit 1 }\n"
    )
    request_utc = datetime.now(timezone.utc).isoformat()
    started = time.monotonic()
    try:
        completed = subprocess.run(
            ["ssh", "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", args.host,
             "powershell.exe -NoLogo -NoProfile -NonInteractive -Command -"],
            input=command.encode("ascii"), capture_output=True, timeout=55,
        )
    except subprocess.TimeoutExpired:
        raise SystemExit("Таймаут SSH: результат неизвестен. Проверьте задачи ValheimVision на клиенте.") from None
    if completed.stderr:
        (args.output / "transport.stderr").write_bytes(completed.stderr)
    if completed.returncode:
        raise SystemExit(f"Ошибка транспорта; диагностика: {args.output}")
    result = json.loads(completed.stdout.decode("utf-8-sig"))
    result["controller"] = {
        "request_started_at_utc": request_utc,
        "response_received_at_utc": datetime.now(timezone.utc).isoformat(),
        "elapsed_seconds": round(time.monotonic() - started, 6),
    }
    save_capture(result, args.output)
    if result["status"] != "captured":
        raise SystemExit(f"Кадр не получен: {result.get('error')}. Диагностика: {args.output}")
    target = result["target"]
    print(f"Получен PNG {target['width']}×{target['height']}, DPI {target['dpi']}: {args.output / 'frame.png'}")
    print("Содержание кадра ещё требует визуальной проверки; игровой ввод не выполнялся.")


if __name__ == "__main__":
    main()
