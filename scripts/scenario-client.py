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


def recognize_text(directory, index, frame, check, language):
    from PIL import Image, ImageOps
    minimum = check["minimum_confidence"]
    if not 70 <= minimum <= 100:
        raise ValueError("invalid_ocr_threshold")
    variants = check.get("variants", [{}])
    if not isinstance(variants, list) or not 1 <= len(variants) <= 2:
        raise ValueError("invalid_ocr_variants")
    results = []
    for number, variant in enumerate(variants):
        # Варианты могут менять только обработку пикселей; ожидаемый текст и порог общие.
        if not isinstance(variant, dict) or set(variant) - {"preprocessing", "psm"}:
            raise ValueError("invalid_ocr_variant")
        options = {**check, **variant}
        name = f"ocr-{index}" + (f"-{number}" if "variants" in check else "")
        crop = directory / f"{name}.png"
        pixels = frame.crop(check["region"])
        preprocessing = options.get("preprocessing", "none")
        if preprocessing == "gold_text_v1":
            # Отделить буквы интерфейса от фона. Цветовые параметры фиксированы.
            mask = Image.new("L", pixels.size)
            colors = pixels.convert("RGB").getdata()
            mask.putdata([0 if r > 190 and g > 120 and r > 1.5 * b else 255 for r, g, b in colors])
            pixels = ImageOps.expand(mask, border=5, fill=255)
        elif preprocessing == "light_text_v1":
            pixels = ImageOps.expand(
                ImageOps.invert(ImageOps.autocontrast(ImageOps.grayscale(pixels))), border=10, fill=255)
        elif preprocessing != "none":
            raise ValueError("unknown_preprocessing")
        psm = options.get("psm", 6)
        if type(psm) is not int or psm not in (6, 7):
            raise ValueError("invalid_ocr_psm")
        pixels.resize((pixels.width * 3, pixels.height * 3)).save(crop)
        tsv = subprocess.run(
            ["tesseract", str(crop), "stdout", "-l", language, "--psm", str(psm), "tsv"],
            capture_output=True, text=True, timeout=10, check=True,
        ).stdout
        (directory / f"{name}.tsv").write_text(tsv)
        results.append({"preprocessing": preprocessing, "psm": psm,
                        "matched": match_tsv(tsv, check["text"], minimum)})
    return {"check": check, "matched": any(result["matched"] for result in results), "variants": results}


def recognize(directory, checks, context, observation):
    from PIL import Image
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
            results.append(recognize_text(directory, index, frame, check, context["language"]))
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
    cached = None
    try:
        for index, step in enumerate(scenario["steps"]):
            if time.monotonic() - started > 600:
                raise ValueError("scenario_deadline")
            before = output / f"{index:02d}-before"
            before_checks = resolve_checks(step["before"], scenario_root)
            after_checks = resolve_checks(step["after"], scenario_root)
            stable_observations = step.get("stable_observations", 1)
            if type(stable_observations) is not int or not 1 <= stable_observations <= 3:
                raise ValueError("invalid_stable_observations")
            # Постусловие предыдущего шага уже проверено. Повторный кадр нужен только при иной
            # проверке или задержке; Windows всё равно проверяет QPC, окно и фокус перед вводом.
            reused = cached is not None and cached[2] == before_checks and time.monotonic() - cached[3] <= 10
            if reused:
                before, observation = cached[:2]
            else:
                observation = observe(host, before)
                if not recognize(before, before_checks, scenario["context"], observation):
                    raise ValueError(f"precondition_failed:{index}")
            entry = {"name": step["name"], "matched": False, "reused_observation": reused,
                     "before": before.name}
            report["steps"].append(entry)
            cached = None
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
            consecutive = 0
            while time.monotonic() < deadline:
                after = output / f"{index:02d}-after-{attempt:02d}"
                observed_since = time.monotonic()
                observation = observe(host, after)
                if recognize(after, after_checks, scenario["context"], observation):
                    consecutive += 1
                    if consecutive >= stable_observations:
                        matched = True
                        cached = (after, observation, after_checks, observed_since)
                        break
                else:
                    consecutive = 0
                attempt += 1
                time.sleep(1)
            entry["matched"] = matched
            if matched:
                entry["after"] = after.name
            if not matched:
                raise ValueError(f"postcondition_unconfirmed:{index}")
        report["status"] = "passed"
    except (ValueError, OSError, subprocess.SubprocessError, SystemExit, KeyboardInterrupt) as error:
        report["error"] = str(error)
    finally:
        report["finished_at_utc"] = datetime.now(timezone.utc).isoformat()
        report["duration_seconds"] = round(time.monotonic() - started, 3)
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
