#!/usr/bin/env bash
# Сторож от молчаливого отката патча gss-ntlmssp: стоит ли версия с '+ccs' и висит ли apt-mark hold.
# Exit: 0 — всё на месте; 1 — пропатченной версии нет (громкое предупреждение); 2 — патч есть, hold нет.
# Звать из /opt/ccs/check-release.sh после выкатки и из cron/сторожа. Ничего не меняет.
set -uo pipefail
v=$(dpkg-query -W -f='${Version}' gss-ntlmssp 2>/dev/null || true)
if [[ -z "$v" ]]; then
  echo "WARNING: gss-ntlmssp не установлен: NTLM для WebDAV выключен" >&2; exit 1
fi
if [[ "$v" != *+ccs* ]]; then
  echo "WARNING: gss-ntlmssp $v БЕЗ патча '+ccs' (вероятно, apt upgrade откатил его): Windows SSPI получит InvalidToken. Установка — docs/operations/remote-access.md" >&2
  exit 1
fi
if ! apt-mark showhold | grep -qx gss-ntlmssp; then
  echo "WARNING: gss-ntlmssp $v пропатчен, но без apt-mark hold: следующий apt upgrade его перезапишет. Выполните: sudo apt-mark hold gss-ntlmssp" >&2
  exit 2
fi
echo "gss-ntlmssp $v: патч на месте, hold стоит"
