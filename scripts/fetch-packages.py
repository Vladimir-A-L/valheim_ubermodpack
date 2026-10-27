#!/usr/bin/env python3
"""Закрепить явные версии пакетов либо сверить архивы и основные DLL по существующему lock."""

import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import stat
import subprocess
import zipfile


def package_parts(value):
    match = re.fullmatch(r"([A-Za-z0-9_]+)-([A-Za-z0-9_]+)-(\d+\.\d+\.\d+)", value)
    if not match:
        raise ValueError(f"Некорректный идентификатор пакета: {value}")
    return match.groups()


def file_hash(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def inspect_archive(path, record):
    owner, name, version = package_parts(record["package"])
    with zipfile.ZipFile(path) as archive:
        seen = set()
        for item in archive.infolist():
            normalized = PurePosixPath(item.filename.replace("\\", "/"))
            if (normalized.is_absolute() or ".." in normalized.parts
                    or ":" in item.filename or stat.S_ISLNK(item.external_attr >> 16)):
                raise ValueError(f"Небезопасный путь в архиве: {item.filename}")
            canonical = str(normalized).casefold()
            if canonical in seen:
                raise ValueError(f"Повторяющийся путь в архиве: {item.filename}")
            seen.add(canonical)
        metadata = json.loads(archive.read("manifest.json"))
        if metadata["name"] != name or metadata["version_number"] != version:
            raise ValueError(f"Идентификатор внутри архива не совпадает: {owner}-{name}-{version}")
        expected = record["representative_file"]
        candidates = [item for item in archive.infolist()
                      if PurePosixPath(item.filename.replace("\\", "/")).name == expected["name"]]
        if len(candidates) != 1:
            raise ValueError(f"Не найдена единственная DLL {expected['name']}")
        item = candidates[0]
        with archive.open(item) as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        if "sha256" in expected and (item.file_size != expected["bytes"] or digest != expected["sha256"]):
            raise ValueError(f"DLL не совпадает с закреплённым SHA256/размером: {record['package']}")
        return metadata.get("dependencies", []), item.filename


def check_dependencies(records):
    versions = {}
    for record in records:
        owner, name, version = package_parts(record["package"])
        identity = (owner, name)
        if identity in versions:
            raise ValueError(f"Несколько версий одного пакета: {owner}-{name}")
        versions[identity] = tuple(map(int, version.split(".")))
    missing = []
    for record in records:
        for dependency in record["dependencies"]:
            owner, name, version = package_parts(dependency)
            if versions.get((owner, name), ()) < tuple(map(int, version.split("."))):
                missing.append(f"{record['package']} требует {dependency}")
    if missing:
        raise ValueError("Незакреплённые зависимости:\n" + "\n".join(missing))


def fetch(record, cache, prior):
    package = record["package"]
    owner, name, version = package_parts(package)
    url = f"https://thunderstore.io/package/download/{owner}/{name}/{version}/"
    destination = cache / f"{package}.zip"
    if not destination.exists():
        partial = destination.with_suffix(".zip.part")
        subprocess.run(
            ["curl", "--fail", "--location", "--silent", "--show-error", "--retry", "2",
             "--connect-timeout", "15", "--max-time", "300", "--proto", "=https",
             "--proto-redir", "=https", "--output", str(partial), url], check=True,
        )
        inspect_archive(partial, record)
        partial.rename(destination)
    dependencies, entry = inspect_archive(destination, record)
    digest = file_hash(destination)
    if prior and digest != prior["archive_sha256"]:
        raise ValueError(f"Архив отличается от закреплённого SHA256: {package}")
    if prior and dependencies != prior["dependencies"]:
        raise ValueError(f"Зависимости отличаются от lock: {package}")
    with zipfile.ZipFile(destination) as archive:
        item = archive.getinfo(entry)
        with archive.open(item) as stream:
            dll_hash = hashlib.file_digest(stream, "sha256").hexdigest()
    return {
        "package": package, "download_url": url, "archive_sha256": digest,
        "archive_bytes": destination.stat().st_size, "dependencies": dependencies,
        "verification_source": (prior or record).get("verification_source", "user-inventory"),
        "verified_file": {"name": record["representative_file"]["name"], "bytes": item.file_size,
                          "sha256": dll_hash, "archive_path": entry},
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", type=Path, default=Path("manifests/baseline-reported.json"))
    parser.add_argument("--selection", type=Path, default=Path("manifests/core-candidate.json"))
    parser.add_argument("--cache", type=Path, default=Path(".local/packages"))
    parser.add_argument("--lock", type=Path, required=True, help="Новый lock либо существующий для проверки")
    parser.add_argument("--observe-releases", action="store_true",
                        help="Создать новый lock из явных версий; хеши наблюдаются, не сверяются с инвентаризацией")
    args = parser.parse_args()
    selected = json.loads(args.selection.read_text())
    selection = selected["packages"]
    if len(selection) != len(set(selection)):
        raise ValueError("Выборка содержит повторяющийся пакет")
    prior = {}
    if args.lock.exists():
        prior = {row["package"]: row for row in json.loads(args.lock.read_text())["packages"]}
        if set(prior) != set(selection):
            raise ValueError("Для другого состава создайте отдельный lock")
        catalog = {key: {"package": key, "representative_file": row["verified_file"]}
                   for key, row in prior.items()}
    elif args.observe_releases:
        catalog = {key: {"package": key, "verification_source": "release-archive-observation",
                         "representative_file": {"name": selected["representative_files"][key]}}
                   for key in selection}
    else:
        source = json.loads(args.baseline.read_text())
        catalog = {row["package"]: row for row in source["packages"]}
        if any(p not in catalog for p in selection):
            raise ValueError("Пакет без исходного SHA256: используйте явный режим --observe-releases")
    args.cache.mkdir(parents=True, exist_ok=True)
    with ThreadPoolExecutor(max_workers=3) as pool:
        records = list(pool.map(lambda p: fetch(catalog[p], args.cache, prior.get(p)), selection))
    check_dependencies(records)
    if not prior:
        args.lock.parent.mkdir(parents=True, exist_ok=True)
        with args.lock.open("x", encoding="utf-8") as stream:
            json.dump({
                "schema_version": 1, "captured_at_utc": datetime.now(timezone.utc).isoformat(),
                "status": "archives-verified-not-gameplay-tested", "packages": records,
            }, stream, ensure_ascii=False, indent=2)
            stream.write("\n")
    verb = "Закреплены" if args.observe_releases and not prior else "Сверены"
    print(f"{verb} архивы, основные DLL и зависимости: {len(records)} пакетов. Lock: {args.lock}")


if __name__ == "__main__":
    main()
