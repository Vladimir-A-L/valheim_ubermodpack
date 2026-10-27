"""Транспорт ограниченного интерактивного задания Windows; артефакты остаются приватными."""

import base64
from datetime import datetime, timezone
import json
from pathlib import Path
import subprocess
import time


def execute_worker(host_alias, worker, output):
    windows = Path(__file__).with_name("windows")
    worker = base64.b64encode(worker.encode("utf-8")).decode("ascii")
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
            ["ssh", "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", host_alias,
             "powershell.exe -NoLogo -NoProfile -NonInteractive -Command -"],
            input=command.encode("ascii"), capture_output=True, timeout=55,
        )
    except subprocess.TimeoutExpired:
        raise SystemExit("Таймаут SSH: результат неизвестен. Проверьте задачи ValheimVision на клиенте.") from None
    if completed.stderr:
        (output / "transport.stderr").write_bytes(completed.stderr)
    if completed.returncode:
        raise SystemExit(f"Ошибка транспорта; диагностика: {output}")
    result = json.loads(completed.stdout.decode("utf-8-sig"))
    result["controller"] = {
        "request_started_at_utc": request_utc,
        "response_received_at_utc": datetime.now(timezone.utc).isoformat(),
        "elapsed_seconds": round(time.monotonic() - started, 6),
    }
    return result
