#!/usr/bin/env python3
"""Подготовить новый overlay библиотек либо кандидата; конфиги из старой сборки не восстанавливаются."""

import argparse
import importlib.util
import json
from pathlib import Path, PurePosixPath
import shutil
import zipfile


spec = importlib.util.spec_from_file_location("packages", Path(__file__).with_name("fetch-packages.py"))
packages = importlib.util.module_from_spec(spec)
spec.loader.exec_module(packages)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lock", type=Path, default=Path("manifests/core-candidate.lock.json"))
    parser.add_argument("--cache", type=Path, default=Path(".local/packages"))
    parser.add_argument("--output", type=Path, required=True, help="Новый каталог overlay")
    parser.add_argument("--profile", choices=("foundation", "core"), default="foundation")
    parser.add_argument("--target", choices=("server", "client"), default="server")
    args = parser.parse_args()
    baseline = json.loads(Path("manifests/baseline-reported.json").read_text())
    locked = {row["package"]: row for row in json.loads(args.lock.read_text())["packages"]}
    wanted = {row["package"]: row for row in baseline["packages"]
              if row["stage"] == "foundation" or (args.profile == "core" and row["package"] in locked)}
    selection = json.loads(Path("manifests/core-candidate.json").read_text())
    if args.target == "server":
        for identity in selection.get("client_only", []):
            wanted.pop(identity, None)
    if not set(wanted).issubset(locked):
        raise ValueError("Lock не содержит все библиотеки")
    packages.check_dependencies([locked[key] for key in wanted])
    planned = []
    targets = set()
    for identity, record in wanted.items():
        archive_path = args.cache / f"{identity}.zip"
        if packages.file_hash(archive_path) != locked[identity]["archive_sha256"]:
            raise ValueError(f"Изменён архив: {identity}")
        packages.inspect_archive(archive_path, record)
        owner, name, _ = packages.package_parts(identity)
        with zipfile.ZipFile(archive_path) as archive:
            for item in archive.infolist():
                if item.is_dir() or item.filename.endswith("\\"):
                    continue
                path = PurePosixPath(item.filename.replace("\\", "/"))
                if name == "BepInExPack_Valheim":
                    if path.parts[0] != "BepInExPack_Valheim":
                        continue
                    target = PurePosixPath(*path.parts[1:])
                elif path.parts[0].lower() == "plugins":
                    target = PurePosixPath("BepInEx/plugins", f"{owner}-{name}", *path.parts[1:])
                elif path.parts[0].lower() == "config":
                    target = PurePosixPath("BepInEx/config", *path.parts[1:])
                elif path.suffix.lower() in (".dll", ".xml", ".pdb", ".mdb"):
                    target = PurePosixPath("BepInEx/plugins", f"{owner}-{name}", path)
                else:
                    # README, icon и license остаются в исходном архиве; неизвестный runtime требует разбора.
                    if path.name.lower() not in {"manifest.json", "readme.md", "changelog.md", "icon.png",
                                                 "license", "license.md", "license.txt", "sha256sums.txt"}:
                        raise ValueError(f"Неизвестное размещение ресурса библиотеки: {identity}/{path}")
                    continue
                if str(target).casefold() in targets:
                    raise ValueError(f"Конфликт файлов overlay: {target}")
                targets.add(str(target).casefold())
                planned.append((archive_path, item.filename, target))
    # Создание только после проверки всех архивов и маршрутов. Существующий профиль никогда не перезаписывается.
    args.output.mkdir(parents=True, exist_ok=False)
    for archive_path, entry, target in planned:
        destination = args.output / str(target)
        destination.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(archive_path) as archive, archive.open(entry) as source, destination.open("xb") as output:
            shutil.copyfileobj(source, output)
    if args.profile == "core":
        config_root = Path("configs/core")
        for source in config_root.rglob("*"):
            if source.is_file():
                destination = args.output / source.relative_to(config_root)
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(source, destination)
    print(f"Подготовлены {len(wanted)} пакетов, {len(planned)} файлов: {args.output}")


if __name__ == "__main__":
    main()
