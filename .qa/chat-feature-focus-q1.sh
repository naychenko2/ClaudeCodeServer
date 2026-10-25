#!/usr/bin/env bash
# QA фичи «один чат — одна фича» (ветка feat/chat-feature-focus) — сценарии 1–6.
# Проверка идёт по живому стенду: REST под JWT владельца и MCP-over-HTTP (тот же путь, что у агента).
# Каждый вызов печатается в лог (метод, путь, код, тело), поэтому лог пригоден как доказательство.
set -uo pipefail

BASE="${BASE:-http://127.0.0.1:5555}"
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/raw/chat-feature-focus-q1"
rm -rf "$OUT"; mkdir -p "$OUT"
LOG="$OUT/log.txt"
: > "$LOG"

say() { echo "$*" | tee -a "$LOG"; }
hdr() { say ""; say "===== $* ====="; }
LAST_CODE=""

# Вызов REST: call <tag> <method> <path> <body|-> [callerSessionId|-]
# Код ответа — в LAST_CODE, тело — в $OUT/<tag>.json
call() {
  local tag="$1" method="$2" path="$3" body="$4" caller="${5:--}"
  local f="$OUT/$tag.json" args=() code
  args=(-s -o "$f" -w '%{http_code}' -X "$method" "$BASE$path" -H "Authorization: Bearer $TOKEN")
  [ "$body" != "-" ] && args+=(-H 'Content-Type: application/json' -d "$body")
  [ "$caller" != "-" ] && args+=(-H "X-Caller-Session-Id: $caller")
  code=$(curl "${args[@]}")
  LAST_CODE="$code"
  say "[$tag] $method $path caller=${caller%%-*} → HTTP $code"
  say "      тело: $(jq -c . < "$f" 2>/dev/null | head -c 500)"
}

# Вызов MCP: mcp <tag> <chatId> <tool> <argsJson>
mcp() {
  local tag="$1" chat="$2" tool="$3" args="$4"
  local f="$OUT/$tag.json" code
  code=$(curl -s -o "$f" -w '%{http_code}' -X POST "$BASE/mcp/tasks/$chat" \
    -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"$tool\",\"arguments\":$args}}")
  LAST_CODE="$code"
  say "[$tag] MCP tools/call $tool chat=$chat → HTTP $code isError=$(jq -r '.result.isError // "нет-поля"' < "$f")"
  say "      текст: $(jq -r '.result.content[0].text // (.error.message // "нет-текста")' < "$f" | head -c 500)"
}

mcp_text() { jq -r '.result.content[0].text // ""' < "$1"; }
rest_err() { jq -r '.error // ""' < "$1"; }
task_count() { curl -s -H "Authorization: Bearer $TOKEN" "$BASE/api/tasks" | jq 'length'; }
# Есть ли задача с точным названием (счёт «задача не создана»)
task_by_title() { curl -s -H "Authorization: Bearer $TOKEN" "$BASE/api/tasks" | jq --arg t "$1" '[.[] | select(.title == $t)] | length'; }
new_chat() { # new_chat <имя> → id сессии
  curl -s -X POST "$BASE/api/projects/$PROJECT/sessions" \
    -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
    -d "{\"name\":\"$1\",\"mode\":\"acceptEdits\"}" | jq -r '.id'
}
epic() { # epic <title> [sourceSessionId] → id задачи-эпика
  local body="{\"title\":\"$1\",\"role\":\"feature\""
  [ "${2:-}" != "" ] && body="$body,\"sourceSessionId\":\"$2\""
  body="$body}"
  curl -s -X POST "$BASE/api/tasks" -H "Authorization: Bearer $TOKEN" \
    -H 'Content-Type: application/json' -d "$body" | jq -r '.id'
}
chat_features() { # привязка чата к фичам (для доказательства и UI)
  curl -s -H "Authorization: Bearer $TOKEN" "$BASE/api/tasks/features?sessionId=$1" \
    | jq -c '[.[] | {featureId: (.featureId // .id), title: (.featureTitle // .title), kind: (.kind // .role)}]'
}

##############################################################################
say "Стенд: $BASE   $(date -u +%FT%TZ)"

hdr "Вход владельца"
TOKEN=$(curl -s -X POST "$BASE/api/auth/login" -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"12345"}' | jq -r '.token')
[ -n "$TOKEN" ] && [ "$TOKEN" != "null" ] || { say "НЕ УДАЛОСЬ ВОЙТИ"; exit 1; }
say "JWT получен, длина ${#TOKEN}"

say "Фич-флаг features-overview (нужен для UI-регрессии):"
call flag_features_overview PUT /api/feature-flags/features-overview '{"enabled":true}' -

hdr "Проект и чаты"
PROJ_ROOT="$HERE/qa-project"; mkdir -p "$PROJ_ROOT"
PROJECT=$(curl -s -X POST "$BASE/api/projects" -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d "{\"name\":\"QA chat-feature-focus $(date -u +%H%M%S)\",\"rootPath\":\"$PROJ_ROOT\",\"createDirectory\":true}" | jq -r '.id')
say "projectId=$PROJECT"

CHAT_A=$(new_chat "qa-chat-A");           say "chatA (привяжется к фиче A)=$CHAT_A"
CHAT_NEW=$(new_chat "qa-chat-fresh");      say "chatFresh (свободный)=$CHAT_NEW"
CHAT_LEGACY=$(new_chat "qa-chat-legacy");  say "chatLegacy (две фичи)=$CHAT_LEGACY"

hdr "Фичи-эпики"
EPIC_A=$(epic "Фича A" "$CHAT_A");       say "epicA=$EPIC_A (SourceSessionId=chatA)"
EPIC_B=$(epic "Фича B");                say "epicB=$EPIC_B (без чата — свободна)"
EPIC_C=$(epic "Фича C");                say "epicC=$EPIC_C (свободна)"
EPIC_D=$(epic "Фича D" "$CHAT_LEGACY"); say "epicD=$EPIC_D (легаси-чат)"
EPIC_E=$(epic "Фича E" "$CHAT_LEGACY"); say "epicE=$EPIC_E (легаси-чат)"

say "Привязка чатов (GET /api/tasks/features?sessionId=…):"
for c in "$CHAT_A" "$CHAT_NEW" "$CHAT_LEGACY"; do
  say "  chat=$c → $(chat_features "$c")"
done

##############################################################################
hdr "Сценарий 1: чат фичи A создаёт задачу фичи B (REST) → 409 chat_feature_bound"
BEFORE=$(task_count); say "счёт задач до: $BEFORE"
call s1_rest_create POST /api/tasks \
  "{\"title\":\"шаг B из чата A\",\"featureId\":\"$EPIC_B\",\"role\":\"step\"}" "$CHAT_A"
AFTER=$(task_count); say "счёт задач после: $AFTER"
say "задач с названием «шаг B из чата A»: $(task_by_title 'шаг B из чата A')"
[ "$BEFORE" = "$AFTER" ] && say "OK счёт не изменился" || say "СБОЙ счёт изменился"
say "code=$(jq -r '.code' < "$OUT/s1_rest_create.json")"
say "текст отказа: $(rest_err "$OUT/s1_rest_create.json")"

hdr "Сценарий 1b: тот же отказ через MCP (tasks_create)"
BEFORE=$(task_count); say "счёт задач до: $BEFORE"
mcp s1_mcp_create "$CHAT_A" tasks_create \
  "{\"title\":\"шаг B из чата A (MCP)\",\"featureId\":\"$EPIC_B\",\"role\":\"step\"}"
AFTER=$(task_count); say "счёт задач после: $AFTER"
[ "$BEFORE" = "$AFTER" ] && say "OK счёт не изменился" || say "СБОЙ счёт изменился"

hdr "Сценарий 2: тот же чат создаёт новый эпик (REST) → 409"
BEFORE=$(task_count); say "счёт задач до: $BEFORE"
call s2_rest_epic POST /api/tasks '{"title":"Фича B2 из чата A","role":"feature"}' "$CHAT_A"
call s2_rest_epic_tail POST /api/tasks '{"title":"Фича B3 из чата A","role":"feature "}' "$CHAT_A"
call s2_rest_epic_head POST /api/tasks '{"title":"Фича B4 из чата A","role":" feature"}' "$CHAT_A"
AFTER=$(task_count); say "счёт задач после: $AFTER"
[ "$BEFORE" = "$AFTER" ] && say "OK счёт не изменился" || say "СБОЙ счёт изменился"

hdr "Сценарий 2b: тот же чат создаёт новый эпик (MCP) → Deny"
BEFORE=$(task_count); say "счёт задач до: $BEFORE"
mcp s2_mcp_epic "$CHAT_A" tasks_create '{"title":"Фича B5 из чата A","role":"feature"}'
mcp s2_mcp_epic_tail "$CHAT_A" tasks_create '{"title":"Фича B6 из чата A","role":"feature "}'
mcp s2_mcp_epic_head "$CHAT_A" tasks_create '{"title":"Фича B7 из чата A","role":" feature"}'
AFTER=$(task_count); say "счёт задач после: $AFTER"
[ "$BEFORE" = "$AFTER" ] && say "OK счёт не изменился" || say "СБОЙ счёт изменился"

hdr "Сценарий 3: свежий чат — эпик, задача своей фичи, задача чужой фичи"
call s3_epic POST /api/tasks \
  "{\"title\":\"Фича C из свежего чата\",\"role\":\"feature\",\"sourceSessionId\":\"$CHAT_NEW\"}" "$CHAT_NEW"
EPIC_C_NEW=$(jq -r '.id' < "$OUT/s3_epic.json")
say "эпик, заведённый свежим чатом: $EPIC_C_NEW (это и есть «своя» фича чата)"
call s3_same_feature POST /api/tasks \
  "{\"title\":\"шаг C из свежего чата\",\"featureId\":\"$EPIC_C_NEW\",\"role\":\"step\",\"sourceSessionId\":\"$CHAT_NEW\"}" "$CHAT_NEW"
call s3_other_feature POST /api/tasks \
  "{\"title\":\"шаг D из свежего чата\",\"featureId\":\"$EPIC_D\",\"role\":\"step\"}" "$CHAT_NEW"
mcp s3_same_feature_mcp "$CHAT_NEW" tasks_create \
  "{\"title\":\"шаг C из свежего чата (MCP)\",\"featureId\":\"$EPIC_C_NEW\",\"role\":\"step\"}"
mcp s3_other_feature_mcp "$CHAT_NEW" tasks_create \
  "{\"title\":\"шаг D из свежего чата (MCP)\",\"featureId\":\"$EPIC_D\",\"role\":\"step\"}"
say "привязка свежего чата после: $(chat_features "$CHAT_NEW")"

hdr "Сценарий 4: легаси-чат с двумя фичами (D и E)"
call s4_feature_d POST /api/tasks \
  "{\"title\":\"шаг D из легаси\",\"featureId\":\"$EPIC_D\",\"role\":\"step\"}" "$CHAT_LEGACY"
call s4_feature_e POST /api/tasks \
  "{\"title\":\"шаг E из легаси\",\"featureId\":\"$EPIC_E\",\"role\":\"step\"}" "$CHAT_LEGACY"
call s4_new_epic POST /api/tasks '{"title":"Фича F из легаси","role":"feature"}' "$CHAT_LEGACY"
mcp s4_feature_d_mcp "$CHAT_LEGACY" tasks_create \
  "{\"title\":\"шаг D из легаси (MCP)\",\"featureId\":\"$EPIC_D\",\"role\":\"step\"}"
mcp s4_feature_e_mcp "$CHAT_LEGACY" tasks_create \
  "{\"title\":\"шаг E из легаси (MCP)\",\"featureId\":\"$EPIC_E\",\"role\":\"step\"}"
mcp s4_new_epic_mcp "$CHAT_LEGACY" tasks_create '{"title":"Фича F из легаси (MCP)","role":"feature"}'
say "привязка легаси-чата: $(chat_features "$CHAT_LEGACY")"

hdr "Сценарий 4b: кнопка в UI (без заголовка вызывающего) — гард пропускает"
call s4b_ui_button POST /api/tasks \
  "{\"title\":\"шаг B по кнопке UI\",\"featureId\":\"$EPIC_B\",\"role\":\"step\"}" -

hdr "Сценарий 5: запуск исполнителя задачи чужой фичи из чата фичи A"
STEP_B=$(curl -s -X POST "$BASE/api/tasks" -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d "{\"title\":\"шаг B для запуска\",\"featureId\":\"$EPIC_B\",\"role\":\"step\"}" | jq -r '.id')
say "stepB=$STEP_B (фича B)"
call s5_rest_execute POST "/api/tasks/$STEP_B/execute" '{}' "$CHAT_A"
say "задача после отказа: $(curl -s -H "Authorization: Bearer $TOKEN" "$BASE/api/tasks/$STEP_B" | jq -c '{executorLaunchedBySessionId, executorLaunchedByPersonaId, status, linkedSessionId}')"
mcp s5_mcp_execute "$CHAT_A" tasks_run_executor "{\"taskId\":\"$STEP_B\"}"
say "задача после MCP-отказа: $(curl -s -H "Authorization: Bearer $TOKEN" "$BASE/api/tasks/$STEP_B" | jq -c '{executorLaunchedBySessionId, status, linkedSessionId}')"

hdr "Сценарий 5b: смена featureId у задачи из чата фичи A → 409, фича не меняется"
FREE=$(curl -s -X POST "$BASE/api/tasks" -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"title":"свободная задача"}' | jq -r '.id')
say "free=$FREE"
call s5b_rest_update PUT "/api/tasks/$FREE" "{\"featureId\":\"$EPIC_B\"}" "$CHAT_A"
say "featureId задачи после отказа: $(curl -s -H "Authorization: Bearer $TOKEN" "$BASE/api/tasks/$FREE" | jq -c '{featureId, role}')"
mcp s5b_mcp_update "$CHAT_A" tasks_update "{\"id\":\"$FREE\",\"featureId\":\"$EPIC_B\"}"
say "featureId задачи после MCP-отказа: $(curl -s -H "Authorization: Bearer $TOKEN" "$BASE/api/tasks/$FREE" | jq -c '{featureId, role}')"

hdr "Сценарий 7 (доп.): ось «исполнитель» — чат-исполнитель задачи фичи A"
STEP_A=$(curl -s -X POST "$BASE/api/tasks" -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d "{\"title\":\"шаг A для исполнителя\",\"featureId\":\"$EPIC_A\",\"role\":\"step\"}" | jq -r '.id')
say "stepA=$STEP_A (фича A)"
call s7_rest_execute_no_caller POST "/api/tasks/$STEP_A/execute" '{}' -
EX_CHAT=$(curl -s -H "Authorization: Bearer $TOKEN" "$BASE/api/tasks/$STEP_A" | jq -r '.linkedSessionId')
say "чат-исполнитель: $EX_CHAT"
say "привязка чата-исполнителя: $(chat_features "$EX_CHAT")"
call s7_rest_create_from_executor POST /api/tasks \
  "{\"title\":\"шаг B из чата исполнителя\",\"featureId\":\"$EPIC_B\",\"role\":\"step\"}" "$EX_CHAT"

hdr "Сценарий 6: текст отказа REST и MCP — побайтно"
call s6_rest POST /api/tasks \
  "{\"title\":\"шаг B снова\",\"featureId\":\"$EPIC_B\",\"role\":\"step\"}" "$CHAT_A"
mcp s6_mcp "$CHAT_A" tasks_create \
  "{\"title\":\"шаг B снова\",\"featureId\":\"$EPIC_B\",\"role\":\"step\"}"
REST_TXT=$(rest_err "$OUT/s6_rest.json")
MCP_TXT=$(mcp_text "$OUT/s6_mcp.json")
say "REST: [$REST_TXT]"
say "MCP : [$MCP_TXT]"
if [ "$REST_TXT" = "$MCP_TXT" ] && [ -n "$REST_TXT" ]; then say "OK тексты совпадают побайтно"; else say "СБОЙ тексты различаются"; fi
say "длины: REST=${#REST_TXT} MCP=${#MCP_TXT}"
printf '%s' "$REST_TXT" > "$OUT/deny-text-rest.txt"
printf '%s' "$MCP_TXT" > "$OUT/deny-text-mcp.txt"
say "sha256:"; sha256sum "$OUT/deny-text-rest.txt" "$OUT/deny-text-mcp.txt" | tee -a "$LOG"

hdr "Итог по кодам"
for f in "$OUT"/*.json; do
  b=$(basename "$f" .json)
  say "$b → $(jq -c '{code, error, isError: .result.isError, text: (.result.content[0].text // null)}' "$f" 2>/dev/null | head -c 320)"
done

say ""
say "Готово. Лог: $LOG"
say "Идентификаторы для UI-части: chatA=$CHAT_A chatFresh=$CHAT_NEW chatLegacy=$CHAT_LEGACY projectId=$PROJECT"
say "Фичи: A=$EPIC_A B=$EPIC_B C=$EPIC_C D=$EPIC_D E=$EPIC_E"
