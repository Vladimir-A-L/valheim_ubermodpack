#!/usr/bin/env bash
set -euo pipefail
umask 077

if [[ $# -ne 2 ]]; then
    echo 'Использование: install-server.sh /path/to/steamcmd.sh /path/to/NEW/server' >&2
    exit 2
fi
steamcmd=$(realpath -e "$1")
destination=$(realpath -m "$2")
if [[ -e "$destination" ]]; then
    echo 'Укажите новый каталог: установленный или запущенный сервер не обновляется этим скриптом.' >&2
    exit 1
fi
mkdir -p "$destination"
"$steamcmd" +@sSteamCmdForcePlatformType linux +force_install_dir "$destination" \
    +login anonymous +app_info_update 1 +app_update 896660 validate +quit
test -x "$destination/valheim_server.x86_64"
test -s "$destination/steamapps/appmanifest_896660.acf"
echo "Загружен кандидат сервера: $destination. Зафиксируйте build/depot и проверьте запуск."
