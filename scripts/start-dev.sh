#!/usr/bin/env bash
set -euo pipefail

repo_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
: "${VALHEIM_SERVER_DIR:?Укажите каталог сервера}"
: "${VALHEIM_STATE_DIR:?Укажите каталог состояния}"
: "${VALHEIM_PASSWORD_FILE:?Укажите файл пароля}"
unit=${VALHEIM_UNIT:-valheim-dev}
systemd-run --user --unit="$unit" --collect \
    --property="MemoryMax=${VALHEIM_MEMORY_MAX:-4G}" --property="CPUQuota=${VALHEIM_CPU_QUOTA:-200%}" \
    --property=KillSignal=SIGINT --property=TimeoutStopSec=120 \
    --property="RuntimeMaxSec=${VALHEIM_RUNTIME_MAX:-600}" \
    --setenv="VALHEIM_SERVER_DIR=$(realpath -e "$VALHEIM_SERVER_DIR")" \
    --setenv="VALHEIM_STATE_DIR=$(realpath -m "$VALHEIM_STATE_DIR")" \
    --setenv="VALHEIM_PASSWORD_FILE=$(realpath -e "$VALHEIM_PASSWORD_FILE")" \
    --setenv="VALHEIM_SERVER_NAME=${VALHEIM_SERVER_NAME:-Valheim development}" \
    --setenv="VALHEIM_WORLD_NAME=${VALHEIM_WORLD_NAME:-Development}" \
    --setenv="VALHEIM_PORT=${VALHEIM_PORT:-2456}" --setenv="VALHEIM_MODE=${VALHEIM_MODE:-vanilla}" \
    /bin/bash "$repo_dir/scripts/run-server.sh"
