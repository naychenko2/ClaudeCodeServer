#!/usr/bin/env bash
# Ставит root-помощника ccs-ipmi: скрипт опроса датчиков BMC (ipmitool sdr) +
# systemd-службу. Безопасно запускать повторно.
#
# Использование:
#   sudo ./deploy/ipmi-helper/install.sh            # установка / обновление
#   sudo ./deploy/ipmi-helper/install.sh --dry-run  # показать, что будет сделано
#   sudo ./deploy/ipmi-helper/install.sh --remove   # полное удаление
#
# Скрипт службы кладётся в /usr/local/lib/ccs-ipmi/ с владельцем root и правами
# 0755: он исполняется от root, поэтому быть записываемым не-root он не должен
# (иначе это путь повышения прав).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
LIB_DIR=/usr/local/lib/ccs-ipmi
UNIT=ccs-ipmi.service
UNIT_DST=/etc/systemd/system/$UNIT
DRY_RUN=0
REMOVE=0
for arg in "$@"; do
  case "$arg" in
    --dry-run) DRY_RUN=1 ;;
    --remove) REMOVE=1 ;;
    -h|--help) sed -n '2,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "Неизвестный аргумент: $arg" >&2; exit 64 ;;
  esac
done

run() { if [[ $DRY_RUN -eq 1 ]]; then printf '+ %s\n' "$*"; else "$@"; fi; }

if [[ $DRY_RUN -eq 0 && $EUID -ne 0 ]]; then
  echo "Нужен root: sudo $0" >&2
  exit 1
fi

if [[ $REMOVE -eq 1 ]]; then
  run systemctl disable --now "$UNIT" || true
  run rm -f "$UNIT_DST"
  run rm -rf "$LIB_DIR"
  run systemctl daemon-reload
  echo "ccs-ipmi удалён."
  exit 0
fi

# Предпосылки: без ipmitool и BMC-устройства служба бесполезна — отказываем сразу.
if [[ ! -x /usr/bin/ipmitool ]]; then
  echo "Нет /usr/bin/ipmitool: apt install ipmitool" >&2
  [[ $DRY_RUN -eq 1 ]] || exit 1
fi
if [[ ! -c /dev/ipmi0 ]]; then
  echo "Нет /dev/ipmi0: загрузите модули ipmi_si и ipmi_devintf (modprobe) и проверьте, что на машине есть BMC" >&2
  [[ $DRY_RUN -eq 1 ]] || exit 1
fi

# 1. Скрипт: владелец root, запись только root.
run install -d -o root -g root -m 0755 "$LIB_DIR"
run install -o root -g root -m 0755 \
  "$REPO_ROOT/deploy/ipmi-helper/ccs-ipmi-helper.py" "$LIB_DIR/ccs-ipmi-helper.py"

# 2. Unit.
run install -o root -g root -m 0644 "$REPO_ROOT/deploy/systemd/$UNIT" "$UNIT_DST"
run systemctl daemon-reload
run systemctl enable "$UNIT"
# restart, а не start: при обновлении скрипта служба должна подхватить новую версию.
run systemctl restart "$UNIT"

echo "--- итог ---"
run systemctl --no-pager --lines=0 status "$UNIT" || true
run ls -l "$LIB_DIR"
