#!/bin/sh
# Установщик агента устройства (ADR-016, agent-distribution Р10).
# Запускается строкой `curl -fsSL https://S/agent/install.sh | sh -s -- --server ... --code ...`.
#
# Шаги:
#   1. определить RID (только linux-x64);
#   2. получить /agent/manifest.json;
#   3. скачать архив во временный файл, сверить размер и SHA-256;
#   4. распаковать в versions/{v} каталога данных агента
#      (${XDG_DATA_HOME:-$HOME/.local/share}/ai-home-agent);
#   5. вызвать `ai-home-agent install` с теми же аргументами.
#
# Без прав администратора. Ошибка — понятный текст по-русски, ненулевой код, мусор убран.
# Повторный запуск поверх установленного агента его не ломает: новый архив ложится в
# новый versions/{v}, активный указатель не трогаем (это дело команды install).
#
# POSIX-совместимый sh: dash, ash, bash --posix. Без bash-измов ([[ ]], pipefail, <<<).
#
# Коды возврата:
#   0  успех
#   2  неверные аргументы
#   3  сервер не раздаёт агента (5xx, невалидный JSON, нет архива под RID)
#   4  битый архив (размер или SHA-256 не совпали)
#   5  нет нужного инструмента в PATH
#   10 ai-home-agent install вернул ненулевой код (каталог версии уже создан, дальше — сам агент)

set -eu

# ---------- утилиты вывода ----------
log()  { printf '%s\n' "$*" >&2; }
die()  { log "ошибка: $*"; exit "$1"; }
need() { command -v "$1" >/dev/null 2>&1 || die "$EX_TOOL" "нужен $1 в PATH"; }

EX_OK=0
EX_USAGE=2
EX_DOWNLOAD=3
EX_INTEGRITY=4
EX_TOOL=5
EX_INSTALL=10

# ---------- справка ----------
usage() {
    cat >&2 <<'EOF'
Установщик агента AI Home (Linux, POSIX sh).

Использование:
    install.sh --server URL --code КОД [--name ИМЯ] [--always-on]

Аргументы:
    --server URL     адрес сервера (например, https://home.example.com)
    --code КОД       код сопряжения из UI
    --name ИМЯ       имя устройства (по умолчанию — имя хоста)
    --always-on      режим без входа в систему (systemd --user + linger; только Linux)

Каталог данных агента:
    ${XDG_DATA_HOME:-$HOME/.local/share}/ai-home-agent/versions/{version}

Коды возврата: 0 — успех, 2 — аргументы, 3 — сервер, 4 — целостность архива,
                5 — нет инструмента, 10 — install вернул ошибку.
EOF
}

# ---------- разбор аргументов ----------
SERVER=""
CODE=""
NAME=""
ALWAYS_ON=0

while [ $# -gt 0 ]; do
    case "$1" in
        --server) SERVER="${2:-}"; shift 2 ;;
        --code)   CODE="${2:-}";   shift 2 ;;
        --name)   NAME="${2:-}";   shift 2 ;;
        --always-on) ALWAYS_ON=1;  shift ;;
        -h|--help) usage; exit "$EX_OK" ;;
        --) shift; break ;;
        -*) die "$EX_USAGE" "неизвестный аргумент: $1 (ожидались --server/--code/--name/--always-on)" ;;
        *)  die "$EX_USAGE" "неожиданный позиционный аргумент: $1" ;;
    esac
done

[ -n "$SERVER" ] || die "$EX_USAGE" "не указан --server"
[ -n "$CODE" ]   || die "$EX_USAGE" "не указан --code"

# URL без завершающего слэша: jq и подстановки соберут полный путь
case "$SERVER" in
    */) SERVER="${SERVER%/}" ;;
esac

# ---------- RID ----------
ARCH="$(uname -m 2>/dev/null || echo unknown)"
case "$ARCH" in
    x86_64|amd64) RID="linux-x64" ;;
    *) die "$EX_USAGE" "неподдерживаемая архитектура: '$ARCH' (этот установщик — только linux-x64)" ;;
esac

# ---------- инструменты ----------
need curl
need jq
need sha256sum
need tar

# ---------- временный каталог ----------
# mktemp -d даёт уникальный каталог с правами 0700 — безопаснее ручного конкатена $$.
if command -v mktemp >/dev/null 2>&1; then
    TMPDIR_RUN=$(mktemp -d -t ccs-agent-install.XXXXXX 2>/dev/null) \
        || TMPDIR_RUN=$(mktemp -d 2>/dev/null) \
        || die "$EX_TOOL" "mktemp -d не сработал"
else
    TMP_BASE="${TMPDIR:-/tmp}"
    TMPDIR_RUN="$TMP_BASE/ccs-agent-install.$$"
    mkdir -p "$TMPDIR_RUN" || die "$EX_TOOL" "не удалось создать $TMPDIR_RUN"
fi
# cleanup вызывается через trap ниже — shellcheck его не видит
# shellcheck disable=SC2329
cleanup() { rm -rf "$TMPDIR_RUN" 2>/dev/null || true; }
trap cleanup EXIT INT TERM

# ---------- помощники ----------
# fetch URL OUT: тихо, с ретраями; код возврата = код утилиты, без вывода на терминал
fetch() {
    _url="$1"; _out="$2"
    if curl -fsSL --retry 3 --connect-timeout 10 --max-time 300 \
            -o "$_out" "$_url" 2>/dev/null; then
        return 0
    fi
    return 1
}

# ---------- каталог данных ----------
XDG_DATA_HOME="${XDG_DATA_HOME:-}"
if [ -z "$XDG_DATA_HOME" ]; then
    if [ -n "${HOME:-}" ] && [ -d "$HOME" ]; then
        XDG_DATA_HOME="$HOME/.local/share"
    else
        die "$EX_USAGE" "не заданы ни XDG_DATA_HOME, ни HOME — некуда положить агента"
    fi
fi
VERSIONS_DIR="$XDG_DATA_HOME/ai-home-agent/versions"

# ---------- манифест ----------
log "==> манифест: $SERVER/agent/manifest.json"
MANIFEST_FILE="$TMPDIR_RUN/manifest.json"
if ! fetch "$SERVER/agent/manifest.json" "$MANIFEST_FILE"; then
    # Сервер вернул не 2xx или вообще не ответил
    if [ -s "$MANIFEST_FILE" ]; then
        # В теле может быть JSON {"error":"..."} от контроллера раздачи (503)
        reason=$(jq -r '.error // empty' "$MANIFEST_FILE" 2>/dev/null || true)
        if [ -n "$reason" ]; then
            die "$EX_DOWNLOAD" "сервер не раздаёт агента: $reason"
        fi
        die "$EX_DOWNLOAD" "манифест недоступен: $(head -c 200 "$MANIFEST_FILE")"
    fi
    die "$EX_DOWNLOAD" "манифест недоступен: $SERVER/agent/manifest.json"
fi

# Валидный ли это JSON вообще
if ! jq -e . "$MANIFEST_FILE" >/dev/null 2>&1; then
    die "$EX_DOWNLOAD" "манифест — не валидный JSON: $(head -c 200 "$MANIFEST_FILE")"
fi

# ---------- поля манифеста ----------
VERSION=$(jq -r '.version' "$MANIFEST_FILE")
RID_ENTRY=$(jq -c --arg r "$RID" '.archives[$r]' "$MANIFEST_FILE")
ARCHIVE_FILE=$(printf '%s' "$RID_ENTRY" | jq -r '.file')
ARCHIVE_SIZE=$(printf '%s' "$RID_ENTRY" | jq -r '.size')
ARCHIVE_SHA=$(printf '%s' "$RID_ENTRY" | jq -r '.sha256' | tr '[:upper:]' '[:lower:]')

[ -n "$VERSION" ]      || die "$EX_DOWNLOAD" "манифест не содержит version"
[ "$RID_ENTRY" != "null" ] || die "$EX_DOWNLOAD" "манифест не содержит архив для $RID"
[ -n "$ARCHIVE_FILE" ] || die "$EX_DOWNLOAD" "манифест не содержит file"
[ -n "$ARCHIVE_SHA" ]  || die "$EX_DOWNLOAD" "манифест не содержит sha256"
echo "$ARCHIVE_SHA" | grep -Eq '^[0-9a-f]{64}$' \
    || die "$EX_DOWNLOAD" "манифест: sha256 не 64 hex ('$ARCHIVE_SHA')"
case "$ARCHIVE_SIZE" in
    ''|*[!0-9]*) die "$EX_DOWNLOAD" "манифест: size не число ('$ARCHIVE_SIZE')" ;;
esac
[ "$ARCHIVE_SIZE" -gt 0 ] || die "$EX_DOWNLOAD" "манифест: size не положительное"

# Имя архива — короткое, без путей: не дать скачать мимо манифеста
case "$ARCHIVE_FILE" in
    */*|*\\*) die "$EX_DOWNLOAD" "манифест: имя архива содержит разделитель пути ('$ARCHIVE_FILE')" ;;
esac

# ---------- скачиваем архив ----------
ARCHIVE_LOCAL="$TMPDIR_RUN/$ARCHIVE_FILE"
log "==> скачиваю: $VERSION/$RID/$ARCHIVE_FILE ($ARCHIVE_SIZE байт)"
if ! fetch "$SERVER/agent/$VERSION/$RID/$ARCHIVE_FILE" "$ARCHIVE_LOCAL"; then
    die "$EX_DOWNLOAD" "не удалось скачать архив: $SERVER/agent/$VERSION/$RID/$ARCHIVE_FILE"
fi

# ---------- размер ----------
GOT_SIZE=$(wc -c < "$ARCHIVE_LOCAL" | tr -d '[:space:]')
[ "$GOT_SIZE" = "$ARCHIVE_SIZE" ] \
    || die "$EX_INTEGRITY" "размер скачанного ($GOT_SIZE) не совпадает с манифестом ($ARCHIVE_SIZE)"

# ---------- SHA-256 ----------
GOT_SHA=$(sha256sum "$ARCHIVE_LOCAL" | awk '{print $1}')
[ "$GOT_SHA" = "$ARCHIVE_SHA" ] \
    || die "$EX_INTEGRITY" "SHA-256 скачанного ($GOT_SHA) не совпадает с манифестом ($ARCHIVE_SHA)"

# ---------- распаковка ----------
VERSION_DIR="$VERSIONS_DIR/$VERSION"
log "==> распаковка: $VERSION_DIR"
# Каталог может уже существовать от прошлого запуска — `tar -xzf` и `unzip` спокойно
# перезатирают, активный указатель не трогаем (это дело команды install).
mkdir -p "$VERSION_DIR" || die "$EX_TOOL" "не удалось создать $VERSION_DIR (нет прав?)"

case "$ARCHIVE_FILE" in
    *.tar.gz|*.tgz)
        tar -xzf "$ARCHIVE_LOCAL" -C "$VERSION_DIR" \
            || die "$EX_INTEGRITY" "не удалось распаковать tar.gz"
        ;;
    *.zip)
        need unzip
        unzip -qo "$ARCHIVE_LOCAL" -d "$VERSION_DIR" \
            || die "$EX_INTEGRITY" "не удалось распаковать zip"
        ;;
    *) die "$EX_INTEGRITY" "неизвестный формат архива: $ARCHIVE_FILE" ;;
esac

# ---------- запуск install ----------
AGENT_BIN="$VERSION_DIR/ai-home-agent"
if [ ! -x "$AGENT_BIN" ]; then
    chmod +x "$AGENT_BIN" 2>/dev/null || true
fi
[ -x "$AGENT_BIN" ] || die "$EX_TOOL" "бинарь $AGENT_BIN не найден или неисполняемый после распаковки"

# Передаём install ровно те же аргументы, что пришли скрипту. Сборка через set --:
# так значения с пробелами (--server URL с путём) пройдут как отдельные слова.
set -- install --server "$SERVER" --code "$CODE"
if [ -n "$NAME" ];      then set -- "$@" --name "$NAME"; fi
if [ "$ALWAYS_ON" = "1" ]; then set -- "$@" --always-on; fi

log "==> $AGENT_BIN $*"
if ! "$AGENT_BIN" "$@"; then
    rc=$?
    die "$EX_INSTALL" "ai-home-agent install вернул код $rc"
fi

log "==> готово: $VERSION_DIR"
exit "$EX_OK"
