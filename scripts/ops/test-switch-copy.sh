#!/usr/bin/env bash
# Проверка копирования switch-release.sh на временном каталоге (прод не трогаем).
# Критерий: боевые appsettings.Local.json*, .mcp.json, backups-secrets, logs/, data/ в app
# переживают копирование, даже если в staging лежат их устаревшие копии; новая сборка доезжает.
# Аргумент: путь к проверяемому switch-release.sh (по умолчанию — соседний).
set -u
S=${1:-$(dirname "$(readlink -f "$0")")/switch-release.sh}
T=$(mktemp -d /tmp/switchtest-XXXXXX)
mkdir -p $T/app/{logs,data,wwwroot,mcp-dify} $T/staging/{wwwroot,modules/notes,modules/spend,logs,data} $T/releases $T/srv/api $T/shim
echo OLD > $T/app/ClaudeHomeServer.dll; echo old-build > $T/app/build-id.txt
echo "CONFIG-A (боевой)" > $T/app/appsettings.Local.json; echo BAK-A > $T/app/appsettings.Local.json.bak-x
echo MCP-A > $T/app/.mcp.json; echo SECRETS-A > $T/app/backups-secrets
echo LOG-A > $T/app/logs/a.log; echo DATA-A > $T/app/data/d.json
echo OLDWWW > $T/app/wwwroot/old.js; echo keep > $T/app/mcp-dify/keep.txt
echo NEW > $T/staging/ClaudeHomeServer.dll; echo new-build > $T/staging/build-id.txt; echo NEWFILE > $T/staging/ClaudeHomeServer.New.dll
echo "CONFIG-B (устаревший)" > $T/staging/appsettings.Local.json; echo BAK-B > $T/staging/appsettings.Local.json.bak-y
echo MCP-B > $T/staging/.mcp.json; echo SECRETS-B > $T/staging/backups-secrets
echo LOG-B > $T/staging/logs/b.log; echo DATA-B > $T/staging/data/e.json; echo NEWWWW > $T/staging/wwwroot/new.js
printf '#!/bin/sh\nexit 0\n' > $T/shim/systemctl; chmod +x $T/shim/systemctl; echo ok > $T/srv/api/health
PORT=$(python3 -c 'import socket;s=socket.socket();s.bind(("127.0.0.1",0));print(s.getsockname()[1])')
python3 -m http.server $PORT --bind 127.0.0.1 --directory $T/srv >/dev/null 2>&1 & SPID=$!
sleep 1
CCS_SWITCH_DETACHED=1 APP=$T/app STAGING=$T/staging RELEASES=$T/releases LOG=$T/deploy.log STAMP=20261014-999999 \
 CCS_SYSTEMCTL="$T/shim/systemctl" HEALTH_URL=http://127.0.0.1:$PORT/api/health HEALTH_TRIES=3 HEALTH_SLEEP=1 bash "$S" >/dev/null; echo "switch rc=$?"
kill $SPID
bad=0
chk(){ if [ -e "$1" ] && [ "$(cat "$1")" = "$2" ]; then echo "OK   $1 = $2"; else echo "FAIL $1 = $(cat "$1" 2>/dev/null) (ждали $2)"; bad=1; fi; }
none(){ if [ ! -e "$1" ]; then echo "OK   нет $1"; else echo "FAIL есть $1"; bad=1; fi; }
cd $T/app
chk appsettings.Local.json "CONFIG-A (боевой)"; chk appsettings.Local.json.bak-x BAK-A; none appsettings.Local.json.bak-y
chk .mcp.json MCP-A; chk backups-secrets SECRETS-A; chk logs/a.log LOG-A; chk data/d.json DATA-A; none logs/b.log; none data/e.json
chk ClaudeHomeServer.dll NEW; chk build-id.txt new-build; chk ClaudeHomeServer.New.dll NEWFILE; chk wwwroot/new.js NEWWWW; none wwwroot/old.js; chk mcp-dify/keep.txt keep
chk $T/releases/20261014-999999/appsettings.Local.json "CONFIG-A (боевой)"

# --- Кейс 2: rsync недоступен — отказ ДО остановки сервиса, app не тронут ---
mkdir -p $T/app2 $T/nopath $T/releases2; echo OLD > $T/app2/ClaudeHomeServer.dll
for b in bash sh date mkdir cp du cut curl seq sleep head tr cat dirname readlink sed grep flock python3 rm env mktemp; do
  p=$(command -v $b) && ln -sf "$p" $T/nopath/$b
done
printf '#!/bin/sh\ntouch %s/stopped\n' "$T" > $T/shim/systemctl-spy; chmod +x $T/shim/systemctl-spy
CCS_SWITCH_DETACHED=1 PATH=$T/nopath APP=$T/app2 STAGING=$T/staging RELEASES=$T/releases2 LOG=$T/deploy2.log STAMP=20261014-999998 \
 CCS_SYSTEMCTL="$T/shim/systemctl-spy" HEALTH_URL=http://127.0.0.1:1/h HEALTH_TRIES=1 HEALTH_SLEEP=1 $T/nopath/bash "$S" >/dev/null; rc=$?
if [ $rc = 1 ]; then echo "OK   rsync нет: rc=1"; else echo "FAIL rsync нет: rc=$rc (ждали 1)"; bad=1; fi
chk $T/app2/ClaudeHomeServer.dll OLD; none $T/stopped
if grep -q "rsync не установлен" $T/deploy2.log; then echo "OK   отказ записан в лог"; else echo "FAIL нет записи об отказе"; bad=1; fi

# --- Кейс 3: rsync упал после остановки — откат на прежний билд, ccs.service не стартует с недокопированным app ---
mkdir -p $T/app3 $T/shimfail $T/releases3; echo OLD3 > $T/app3/ClaudeHomeServer.dll
printf '#!/bin/sh\nexit 23\n' > $T/shimfail/rsync; chmod +x $T/shimfail/rsync
printf '#!/bin/sh\necho "$@" >> %s/calls3\n' "$T" > $T/shim/systemctl-rec; chmod +x $T/shim/systemctl-rec
CCS_SWITCH_DETACHED=1 PATH=$T/shimfail:$PATH APP=$T/app3 STAGING=$T/staging RELEASES=$T/releases3 LOG=$T/deploy3.log STAMP=20261014-999997 \
 CCS_SYSTEMCTL="$T/shim/systemctl-rec" HEALTH_URL=http://127.0.0.1:1/h HEALTH_TRIES=1 HEALTH_SLEEP=1 bash "$S" >/dev/null; rc=$?
if [ $rc = 1 ]; then echo "OK   rsync упал: rc=1"; else echo "FAIL rsync упал: rc=$rc (ждали 1)"; bad=1; fi
chk $T/app3/ClaudeHomeServer.dll OLD3
if grep -q "ОТКАЧЕНО" $T/deploy3.log; then echo "OK   откат записан в лог"; else echo "FAIL нет записи об откате"; bad=1; fi
# stop (выкатка), stop (откат), start (после отката): стартов до отката быть не должно
if [ "$(grep -c '^start' $T/calls3)" = 1 ]; then echo "OK   start только после отката"; else echo "FAIL start вызван $(grep -c '^start' $T/calls3) раз(а)"; bad=1; fi

[ $bad = 0 ] && echo "ИТОГ: ЗЕЛЁНО" || echo "ИТОГ: КРАСНО"
rm -rf "$T"
exit $bad
