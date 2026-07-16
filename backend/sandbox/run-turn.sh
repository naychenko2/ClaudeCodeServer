#!/bin/sh
# Обвязка убиваемости процессов песочницы. Бэкенд (на хосте) запускает процессы
# container-пользователей через `docker exec -i … /app/run-turn.sh <turn-id> <cmd…>`.
# Убийство docker-клиента на хосте НЕ трогает процесс в контейнере, поэтому
# прерывание делается изнутри: `kill -KILL -<pgid>` по pid-файлу метки хода
# (DockerProcessRunner.Kill).
#
# Процесс запускается через `setsid --wait` на ПЕРЕДНЕМ плане (не в фоне!): фоновый
# `setsid … &` отвязывал бы stdin от новой сессии и ломал stream-json/pty-протокол.
# setsid делает потомка лидером новой группы процессов — сигнал группе (-pgid)
# снимает всё дерево (claude + node MCP-серверы). pgid пишется самим потомком
# (его $$ == pgid) перед exec целевой команды.
set -u
TURN_ID="$1"; shift
mkdir -p /tmp/turns
setsid --wait sh -c 'echo $$ > "/tmp/turns/$0.pid"; exec "$@"' "$TURN_ID" "$@"
STATUS=$?
rm -f "/tmp/turns/$TURN_ID.pid"
exit "$STATUS"
