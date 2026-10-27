#!/usr/bin/env bash
set -euo pipefail
umask 077

: "${VALHEIM_SERVER_DIR:?Укажите каталог установленного сервера}"
: "${VALHEIM_STATE_DIR:?Укажите отдельный каталог сохранений и журналов}"
: "${VALHEIM_PASSWORD_FILE:?Укажите приватный файл с паролем сервера}"

server_dir=$(realpath -e "$VALHEIM_SERVER_DIR")
password_file=$(realpath -e "$VALHEIM_PASSWORD_FILE")
server_name=${VALHEIM_SERVER_NAME:-Valheim development}
world_name=${VALHEIM_WORLD_NAME:-Development}
port=${VALHEIM_PORT:-2456}
mode=${VALHEIM_MODE:-vanilla}
password=$(cat "$password_file")

if [[ ${#password} -lt 5 || "$server_name" == *"$password"* || "$password" == *$'\n'* ]]; then
    echo 'Пароль должен содержать не менее 5 символов, без перевода строки и совпадения с именем сервера.' >&2
    exit 1
fi
if [[ ! "$port" =~ ^[0-9]+$ ]] || ((10#$port < 1024 || 10#$port > 65534)); then
    echo 'Порт должен быть в диапазоне 1024–65534.' >&2
    exit 1
fi
if [[ ! "$world_name" =~ ^[A-Za-z0-9_-]+$ ]]; then
    echo 'Имя мира: только латинские буквы, цифры, дефис и подчёркивание.' >&2
    exit 1
fi
mkdir -p "$VALHEIM_STATE_DIR"
state_dir=$(realpath -e "$VALHEIM_STATE_DIR")
cd "$server_dir"
export SteamAppId=892970
export TZ=UTC
export LD_LIBRARY_PATH="$server_dir/linux64${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"

case "$mode" in
    vanilla) ;;
    bepinex)
        test -f BepInEx/core/BepInEx.Preloader.dll
        test -f doorstop_libs/libdoorstop_x64.so
        export DOORSTOP_ENABLED=1
        export DOORSTOP_TARGET_ASSEMBLY="$server_dir/BepInEx/core/BepInEx.Preloader.dll"
        export LD_LIBRARY_PATH="$server_dir/doorstop_libs:$LD_LIBRARY_PATH"
        export LD_PRELOAD="$server_dir/doorstop_libs/libdoorstop_x64.so${LD_PRELOAD:+:$LD_PRELOAD}"
        ;;
    *) echo 'Режим должен быть vanilla или bepinex.' >&2; exit 1 ;;
esac

# exec позволяет systemd передать SIGINT игре и дождаться штатного сохранения.
exec ./valheim_server.x86_64 -nographics -batchmode \
    -name "$server_name" -world "$world_name" -port "$port" -password "$password" \
    -public 0 -savedir "$state_dir" -logFile "$state_dir/server.log" -modifier resources most \
    -saveinterval 300 -backups 4 -backupshort 600 -backuplong 3600
