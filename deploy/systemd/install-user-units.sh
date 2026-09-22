#!/usr/bin/env bash
# Раскладывает unit-файлы ccs в user-инстанс systemd, даёт daemon-reload,
# убирает перебивающие drop-in'ы из user.control/. Безопасно запускать повторно.
#
# Зачем отдельный скрипт:
# - `systemctl --user set-property` пишет в user.control/ — вне git и не
#   переживает daemon-reload без ручного вмешательства. Скрипт переносит эти
#   значения в user/ (под git), откуда systemd их подхватывает штатно.
# - Если не убрать старые drop-in'ы из user.control/, они молча перебивают
#   версионированные: приоритет у runtime-drop-in выше, чем у файлового.
#
# Использование:
#   ./deploy/systemd/install-user-units.sh                # раскладка + reload
#   ./deploy/systemd/install-user-units.sh --restart      # то же + restart ccs.service
#   ./deploy/systemd/install-user-units.sh --dry-run      # показать, что будет сделано
#
# Ошибка: не запускать под root и не от sudo. Нужен user systemd-инстанс
# текущего пользователя (XDG_RUNTIME_DIR указывает на user-шину).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
USER_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/systemd/user"
DRY_RUN=0
RESTART=0

for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    --restart) RESTART=1 ;;
    -h|--help)
      sed -n '2,16p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *)
      echo "Неизвестный аргумент: $arg" >&2
      exit 64
      ;;
  esac
done

run() {
  if [[ $DRY_RUN -eq 1 ]]; then
    printf '+ %s\n' "$*"
  else
    "$@"
  fi
}

# 1. Проба user-шины — если её нет, скрипт не должен тихо испортить файлы.
if [[ $DRY_RUN -eq 0 ]]; then
  if ! systemctl --user show --property=Id --value >/dev/null 2>&1; then
    echo "user-шина systemd недоступна. Зайдите в графическую сессию и запустите скрипт оттуда." >&2
    exit 1
  fi
fi

# 2. Раскладка unit-файлов и drop-in'ов.
run install -d -m 0755 "$USER_DIR/ccs.service.d"
run install -d -m 0755 "$USER_DIR/ccs-agents.slice.d"

run install -m 0644 "$REPO_ROOT/deploy/systemd/ccs.service" "$USER_DIR/ccs.service"
run install -m 0644 "$REPO_ROOT/deploy/systemd/ccs.service.d/50-oom-preference.conf" \
  "$USER_DIR/ccs.service.d/50-oom-preference.conf"
run install -m 0644 "$REPO_ROOT/deploy/systemd/ccs-agents.slice.d/50-memory-limits.conf" \
  "$USER_DIR/ccs-agents.slice.d/50-memory-limits.conf"
run install -m 0644 "$REPO_ROOT/deploy/systemd/ccs-agents.slice.d/50-oom-preference.conf" \
  "$USER_DIR/ccs-agents.slice.d/50-oom-preference.conf"

# 3. daemon-reload — иначе user/ не подхватится.
run systemctl --user daemon-reload

# 4. Снять перебивающие drop-in'ы из user.control/, оставшиеся от set-property.
#    Без этого шага файлы в user/ останутся в тени: приоритет у user.control/.
for unit in ccs.service ccs-agents.slice; do
  ctrl_dir="$HOME/.config/systemd/user.control/${unit}.d"
  if [[ -d $ctrl_dir ]]; then
    # Удаляем только те файлы, которые наш drop-in явно заменяет — иначе можно
    # случайно снести чужое runtime-изменение, не относящееся к этой задаче.
    case "$unit" in
      ccs.service)
        files=(50-ManagedOOMPreference.conf)
        ;;
      ccs-agents.slice)
        files=(50-MemoryHigh.conf 50-MemoryMax.conf 50-ManagedOOMMemoryPressure.conf)
        ;;
    esac
    for f in "${files[@]}"; do
      if [[ -f "$ctrl_dir/$f" ]]; then
        run rm -f "$ctrl_dir/$f"
      fi
    done
    # Если каталог опустел и состоит только из наших файлов, удаляем каталог.
    if [[ -d $ctrl_dir ]] && [[ -z "$(ls -A "$ctrl_dir" 2>/dev/null)" ]]; then
      run rmdir "$ctrl_dir"
    fi
  fi
done

# 5. Повторный reload — иначе systemd всё ещё видит вычищенные drop-in'ы.
run systemctl --user daemon-reload

# 6. Рестарт — только по явному флагу: скрипт применяется и во время обычной
#    выкатки, и как отдельная задача, и прерывать прод ради настройки лимитов
#    памяти на slice агентов нет причин.
if [[ $RESTART -eq 1 ]]; then
  run systemctl --user restart ccs.service
fi

# 7. Самопроверка: выводим итоговые значения.
echo "--- итог ---"
run systemctl --user show ccs.service -p ManagedOOMPreference
run systemctl --user show ccs-agents.slice -p MemoryHigh -p MemoryMax -p MemorySwapMax -p ManagedOOMMemoryPressure
