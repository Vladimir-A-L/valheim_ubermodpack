#!/usr/bin/env python3
"""Пакеты обычных шагов по проверяемому маршруту; кадры и диагностика остаются приватными."""

import argparse
import base64
import json
from pathlib import Path
import subprocess
import time

from vision_transport import execute_worker


def run(host, profile, route, output):
    output.mkdir(parents=True, exist_ok=False)
    windows = Path(__file__).with_name("windows")
    template = (windows / "walk-forest.ps1").read_text()
    template = template.replace("__WINDOW_COMMON__", (windows / "window-common.ps1").read_text())
    template = template.replace("__NATIVE_BASE64__", base64.b64encode(
        (windows / "input-native.cs").read_bytes()).decode())
    index = 0
    began = time.monotonic()
    result = {"status": "inconclusive"}
    batches = []
    for number in range(100):
        batch = output / f"batch-{number:03d}"
        batch.mkdir()
        plan = {"executable": profile["executable"], "points": route, "start": index}
        worker = template.replace("__PLAN_BASE64__", base64.b64encode(json.dumps(plan).encode()).decode())
        result = execute_worker(host, worker, batch)
        (batch / "report.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n")
        batches.append({"directory": str(batch), "status": result["status"],
                        "next_index": result.get("next_index", index)})
        remote = result["remote_artifacts"].replace("\\", "/") + "/frame.png"
        subprocess.run(["scp", "-q", host + ":" + remote, str(batch / "frame.png")],
                       check=True, capture_output=True, timeout=30)
        index = result.get("next_index", index)
        print(f"Пакет {number}: {result['status']}, точки {index}/{len(route)}", flush=True)
        if result["status"] != "partial" or time.monotonic() - began > 2400:
            break
    summary = {"status": "passed" if result["status"] == "passed" else "inconclusive",
               "error": result.get("error"), "visited_waypoints": index, "total_waypoints": len(route),
               "duration_seconds": time.monotonic() - began, "batches": batches}
    (output / "report.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2) + "\n")
    return summary


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True)
    parser.add_argument("--profile", type=Path, required=True)
    parser.add_argument("--route", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    report = run(args.host, json.loads(args.profile.read_text()), json.loads(args.route.read_text()), args.output)
    print(json.dumps(report, ensure_ascii=False))
    raise SystemExit(0 if report["status"] == "passed" else 1)
