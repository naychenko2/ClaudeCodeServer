#!/usr/bin/env bash
# Раскладывает СИСТЕМНЫЕ (root) настройки машины под ccs: порог oomd на
# user@.service и лимиты inotify. Пара к install-user-units.sh, который кладёт
# user-юниты без root. Безопасно запускать повторно.
#
# Использование:
#   sudo ./deploy/systemd/install-system-tuning.sh            # раскладка + применение
#   sudo ./deploy/systemd/install-system-tuning.sh --dry-run  # показать, что будет сделано
#
# Применение без перелогина: drop-in user@.service подхватывается daemon-reload,
# а для уже работающего user@<uid>.service значение дополнительно ставится
# set-property --runtime (живёт до перезагрузки, дальше действует drop-in).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DRY_RUN=0
for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    -h|--help) sed -n '2,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Неизвестный аргумент: $arg" >&2; exit 64 ;;
  esac
done

run() { if [[ $DRY_RUN -eq 1 ]]; then printf '+ %s\n' "$*"; else "$@"; fi; }

if [[ $DRY_RUN -eq 0 && $EUID -ne 0 ]]; then
  echo "Нужен root: sudo $0" >&2
  exit 1
fi

# 1. oomd: сессионный порог user@.service.
run install -d -m 0755 /etc/systemd/system/user@.service.d
run install -m 0644 "$REPO_ROOT/deploy/systemd/system/user@.service.d/50-ccs-oomd.conf" \
  /etc/systemd/system/user@.service.d/50-ccs-oomd.conf
run systemctl daemon-reload

# Уже запущенным user-инстансам drop-in не перечитывается до рестарта (а рестарт =
# выход из сессии), поэтому ставим то же значение runtime-свойством.
for unit in $(systemctl list-units --type=service --state=running --plain --no-legend 'user@*.service' | awk '{print $1}'); do
  run systemctl set-property --runtime "$unit" ManagedOOMMemoryPressureLimit=80%
done

# 2. inotify.
run install -m 0644 "$REPO_ROOT/deploy/sysctl.d/60-inotify.conf" /etc/sysctl.d/60-inotify.conf
run sysctl -q -p /etc/sysctl.d/60-inotify.conf

echo "--- итог ---"
run systemctl show 'user@1000.service' -p ManagedOOMMemoryPressure -p ManagedOOMMemoryPressureLimit
run sysctl fs.inotify.max_user_watches fs.inotify.max_user_instances
