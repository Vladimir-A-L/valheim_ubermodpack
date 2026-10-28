#!/usr/bin/env python3
"""Закрепить серверный профиль после однократного импорта на закрытом QA-сервере."""
import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import tempfile
import time
from datetime import datetime, timezone

FIELD = re.compile(r'^(  loadServerCharacterOnJoin:) (false|true)([^\n]*)$', re.MULTILINE)
CHECKPOINT = re.compile(r'WorldCharacterCheckpointCompleted .*persisted=([1-9][0-9]*), pending=0(?:\s|$)')


def atomic_write(path, data):
    fd, temporary = tempfile.mkstemp(prefix=path.name + '.', dir=path.parent)
    try:
        with os.fdopen(fd, 'wb') as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
        directory = os.open(path.parent, os.O_DIRECTORY)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


def finalize(config, profile, log, receipt):
    """Не переключать по факту одного сохранения мира без завершённого checkpoint персонажа."""
    if receipt.exists():
        return True
    text = config.read_text(encoding='utf-8')
    matches = list(FIELD.finditer(text))
    if len(matches) != 1:
        raise ValueError('Ожидается один известный параметр loadServerCharacterOnJoin')
    if not profile.is_file() or profile.stat().st_size < 64 or not CHECKPOINT.search(log):
        return False
    before = profile.stat()
    data = profile.read_bytes()
    after = profile.stat()
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        return False
    # Храним исходную импортированную копию рядом с приватной квитанцией.
    snapshot = receipt.with_suffix('.fch')
    if not snapshot.exists():
        atomic_write(snapshot, data)
    updated = FIELD.sub(lambda m: m[1] + ' true' + m[3], text)
    if config.read_text(encoding='utf-8') != text:
        raise RuntimeError('Конфигурация изменилась во время подготовки; нужен повтор')
    atomic_write(config, updated.encode())
    atomic_write(receipt, (json.dumps({
        'completed_at_utc': datetime.now(timezone.utc).isoformat(),
        'profile_sha256': hashlib.sha256(data).hexdigest(),
        'server_authority_configured': True,
        'settings_reload_requires_runtime_confirmation': True,
    }, indent=2) + '\n').encode())
    return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', type=Path, required=True)
    parser.add_argument('--profile', type=Path, required=True)
    parser.add_argument('--server-log', type=Path, required=True)
    parser.add_argument('--receipt', type=Path, required=True)
    args = parser.parse_args()
    args.receipt.parent.mkdir(parents=True, exist_ok=True)
    with args.receipt.with_suffix('.lock').open('w') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        while True:
            log = args.server_log.read_text(encoding='utf-8', errors='replace') if args.server_log.exists() else ''
            if finalize(args.config, args.profile, log, args.receipt):
                print('Серверный профиль закреплён; проверить reload ServerManager.', flush=True)
                return
            time.sleep(5)


if __name__ == '__main__':
    main()
