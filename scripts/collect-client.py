#!/usr/bin/env python3
"""Снять приватную инвентаризацию Windows-клиента по существующему SSH-доступу."""

import argparse
import base64
import json
import os
from pathlib import Path
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, help="SSH-алиас либо user@host")
    parser.add_argument("--output", type=Path, required=True, help="Приватный JSON вне Git")
    args = parser.parse_args()
    os.umask(0o077)
    script = base64.b64encode(Path(__file__).with_name("inventory-client.ps1").read_bytes()).decode("ascii")
    command = (
        "try { & ([ScriptBlock]::Create([Text.Encoding]::UTF8.GetString("
        f"[Convert]::FromBase64String('{script}')))); exit 0 "
        "} catch { [Console]::Error.WriteLine($_); exit 1 }\n"
    )
    # -Command - не ограничен длиной Windows command line; скрипт не сохраняется на клиенте.
    result = subprocess.run(
        ["ssh", "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", args.host,
         "powershell.exe -NoLogo -NoProfile -NonInteractive -Command -"],
        input=command.encode("ascii"), capture_output=True, timeout=300,
    )
    if result.returncode:
        print(result.stderr.decode("utf-8", errors="replace"), file=sys.stderr)
        raise SystemExit(result.returncode)
    payload = json.loads(result.stdout.decode("utf-8-sig"))
    if payload.get("schema_version") != 1 or "valheim.exe" not in payload.get("root_entries", []):
        raise SystemExit("Инвентаризация имеет неизвестный формат или не содержит Valheim")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    # Не перезаписывать исходное свидетельство при повторном запуске.
    with args.output.open("x", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
    print(f"Сохранено {len(payload['files'])} файлов: {args.output}")
    print(f"Игра запущена: {payload['game_running']}")
    for line in payload["log_versions"]:
        print(line)


if __name__ == "__main__":
    main()
