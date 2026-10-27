#!/usr/bin/env python3
"""Ограниченный сценарий: кадр → OCR-предусловие → одно действие → OCR-постусловие."""

import argparse
import csv
from datetime import datetime, timezone
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import re
import subprocess
import time


def load_module(name, filename):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(filename))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


capture = load_module("capture_client", "capture-client.py")
inputs = load_module("input_client", "input-client.py")


def words(text):
    return re.findall(r"[a-z0-9]+", text.casefold())


def resolve_checks(checks, root):
    resolved = []
    for original in checks:
        check = dict(original)
        if "text_file" in check:
            check["text"] = (root / check.pop("text_file")).read_text().strip()
        resolved.append(check)
    return resolved


def match_tsv(tsv, expected, minimum):
    accepted = []
    for row in csv.DictReader(io.StringIO(tsv), delimiter="\t"):
        if row.get("text"):
            accepted.extend(words(row["text"]) if float(row["conf"]) >= minimum else [None])
    wanted = words(expected)
    return bool(wanted) and any(accepted[i:i + len(wanted)] == wanted for i in range(len(accepted)))


def observe(host, output):
    from vision_transport import execute_worker
    output.mkdir(parents=True, exist_ok=False)
    windows = Path(__file__).with_name("windows")
    worker = (windows / "capture-window.ps1").read_text().replace(
        "__WINDOW_COMMON__", (windows / "window-common.ps1").read_text())
    result = execute_worker(host, worker, output)
    capture.save_capture(result, output)
    if result["status"] != "captured":
        raise ValueError(result.get("error", "capture_failed"))
    return result


def recognize(directory, checks, context, observation):
    from PIL import Image, ImageOps
    target = observation["target"]
    if [target["width"], target["height"], target["dpi"]] != context["geometry"]:
        raise ValueError("geometry_mismatch")
    if target["executable"].casefold() != context["executable"].casefold():
        raise ValueError("test_runtime_mismatch")
    results = []
    with Image.open(directory / "frame.png") as frame:
        for index, check in enumerate(checks):
            region = check["region"]
            x, y, right, bottom = region
            if not 0 <= x < right <= frame.width or not 0 <= y < bottom <= frame.height:
                raise ValueError("invalid_ocr_region")
            crop = directory / f"ocr-{index}.png"
            pixels = frame.crop(region)
            preprocessing = check.get("preprocessing", "none")
            if preprocessing == "gold_text_v1":
                # Отделить буквы интерфейса от фона. Цветовые параметры фиксированы.
                mask = Image.new("L", pixels.size)
                colors = pixels.convert("RGB").getdata()
                mask.putdata([0 if r > 190 and g > 120 and r > 1.5 * b else 255 for r, g, b in colors])
                pixels = ImageOps.expand(mask, border=5, fill=255)
            elif preprocessing != "none":
                raise ValueError("unknown_preprocessing")
            pixels.resize((pixels.width * 3, pixels.height * 3)).save(crop)
            tsv = subprocess.run(
                ["tesseract", str(crop), "stdout", "-l", context["language"], "--psm", "6", "tsv"],
                capture_output=True, text=True, timeout=10, check=True,
            ).stdout
            (directory / f"ocr-{index}.tsv").write_text(tsv)
            minimum = check["minimum_confidence"]
            if not 70 <= minimum <= 100:
                raise ValueError("invalid_ocr_threshold")
            results.append({"check": check, "matched": match_tsv(tsv, check["text"], minimum)})
    verdict = {"method": "tesseract", "image_sha256": observation["image_sha256"], "checks": results}
    (directory / "review.json").write_text(json.dumps(verdict, indent=2) + "\n")
    return bool(results) and all(row["matched"] for row in results)


def run(host, scenario, output, scenario_root):
    output.mkdir(parents=True, exist_ok=False)
    version = subprocess.run(["tesseract", "--version"], capture_output=True, text=True, check=True).stdout
    if version.splitlines()[0] != scenario["context"]["ocr_version"]:
        raise ValueError("ocr_version_mismatch")
    if not 1 <= len(scenario["steps"]) <= 30:
        raise ValueError("invalid_step_count")
    (output / "scenario.json").write_text(json.dumps(scenario, indent=2) + "\n")
    report = {"started_at_utc": datetime.now(timezone.utc).isoformat(), "status": "inconclusive", "steps": []}
    started = time.monotonic()
    try:
        for index, step in enumerate(scenario["steps"]):
            if time.monotonic() - started > 600:
                raise ValueError("scenario_deadline")
            before = output / f"{index:02d}-before"
            before_checks = resolve_checks(step["before"], scenario_root)
            after_checks = resolve_checks(step["after"], scenario_root)
            observation = observe(host, before)
            if not recognize(before, before_checks, scenario["context"], observation):
                raise ValueError(f"precondition_failed:{index}")
            action = dict(step["action"])
            if "text_file" in action:
                action["text"] = (scenario_root / action.pop("text_file")).read_text().strip()
            result = inputs.perform(host, before, action, step["name"], str(after_checks),
                                    output / f"{index:02d}-action", 30, "tesseract")
            if result["status"] != "sent":
                raise ValueError(result.get("error", "input_inconclusive"))
            matched = False
            deadline = time.monotonic() + min(60, step.get("timeout_seconds", 20))
            attempt = 0
            while time.monotonic() < deadline:
                after = output / f"{index:02d}-after-{attempt:02d}"
                observation = observe(host, after)
                if recognize(after, after_checks, scenario["context"], observation):
                    matched = True
                    break
                attempt += 1
                time.sleep(1)
            report["steps"].append({"name": step["name"], "matched": matched})
            if not matched:
                raise ValueError(f"postcondition_unconfirmed:{index}")
        report["status"] = "passed"
    except (ValueError, OSError, subprocess.SubprocessError, SystemExit, KeyboardInterrupt) as error:
        report["error"] = str(error)
    finally:
        report["finished_at_utc"] = datetime.now(timezone.utc).isoformat()
        (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True)
    parser.add_argument("--scenario", type=Path, required=True)
    parser.add_argument("--profile", type=Path, help="Приватная идентичность подготовленного тестового клиента")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    os.umask(0o077)
    scenario = json.loads(args.scenario.read_text())
    if args.profile:
        profile = json.loads(args.profile.read_text())
        for field in ("geometry", "valheim", "network_protocol", "lock_sha256"):
            if scenario["context"][field] != profile[field]:
                raise SystemExit(f"Профиль не соответствует сценарию: {field}")
        scenario["context"]["executable"] = profile["executable"]
        scenario["context"]["profile_sha256"] = hashlib.sha256(args.profile.read_bytes()).hexdigest()
    if "executable" not in scenario["context"]:
        raise SystemExit("Укажите --profile с путём отдельного тестового клиента")
    result = run(args.host, scenario, args.output, args.scenario.parent)
    print(result["status"])
    if result["status"] != "passed":
        raise SystemExit(result.get("error", "inconclusive"))


if __name__ == "__main__":
    main()
