#!/usr/bin/env bash
# tests-affected.sh — какие тест-проекты (*.Tests.csproj) затронуты изменениями.
#
# Берёт список изменённых файлов, находит проекты, которым они принадлежат, и идёт
# по графу ProjectReference в ОБРАТНУЮ сторону (кто зависит от изменённого, транзитивно).
# Ссылка с ReferenceOutputAssembly="false" (динамические модули: Main тянет их только ради
# порядка сборки и копии dll в modules/) — «мягкое» ребро: зависимый проект попадает в набор,
# но дальше от него замыкание НЕ идёт (типов модуля он не видит, потребители Main их не
# перекомпилируют). Тест-проекты, которым типы модуля нужны, ссылаются на него напрямую —
# это жёсткое ребро. Внешних зависимостей нет: bash 4+, git, awk, sed, grep.
#
# Использование:
#   scripts/tests-affected.sh                # изменения от merge-base с master до рабочего дерева
#   scripts/tests-affected.sh <base>         # от <base> (коммит/ветка) до рабочего дерева
#   git diff --name-only A B | scripts/tests-affected.sh -   # список файлов со stdin
#   scripts/tests-affected.sh -v [<base>|-]  # причины отбора — в stderr
#
# Вывод: пути *.Tests.csproj от корня репозитория, по одному в строке, отсортированы;
# пусто — тесты не затронуты. Пример прогона затронутых:
#   scripts/tests-affected.sh | xargs -r -n1 dotnet test -v q -clp:ErrorsOnly
#
# Правила (консервативные — лучше лишний тест-проект, чем пропущенный):
#   * файл внутри каталога проекта → этот проект (самый вложенный каталог с .csproj);
#   * файл в backend/ вне каталога проекта (Directory.Build.*, *.slnx, global.json…) → ВСЕ тесты;
#   * файл вне backend/ (docs/, frontend/) → ничего: бэкенд-тесты их не читают.
#     Единственное исключение — CLAUDE.md в корне: его читает ProjectMapHygieneGuardTests
#     (Guards.Tests), см. ROOT_FILE_OWNER ниже.
set -euo pipefail

VERBOSE=0
if [ "${1:-}" = "-v" ]; then VERBOSE=1; shift; fi
BASE_ARG="${1:-}"

ROOT="$(git rev-parse --show-toplevel)"
cd "$ROOT"

why() { [ "$VERBOSE" = 1 ] && echo "$*" >&2 || true; }

# --- список изменённых файлов ---
if [ "$BASE_ARG" = "-" ]; then
  CHANGED="$(cat)"
else
  BASE="${BASE_ARG:-$(git merge-base HEAD master 2>/dev/null || git rev-parse HEAD~1)}"
  # коммиты base..HEAD + незакоммиченные правки отслеживаемых + новые файлы
  CHANGED="$( { git diff --name-only "$BASE"; git ls-files --others --exclude-standard; } | sort -u)"
  why "base = $BASE"
fi

# --- проекты и обратный граф ссылок ---
declare -A PROJ_DIR   # проект (путь csproj) → каталог
declare -A RDEPS      # проект → пробел-список тех, кто на него ссылается
ALL_PROJECTS=()
while IFS= read -r csproj; do
  ALL_PROJECTS+=("$csproj")
  PROJ_DIR["$csproj"]="$(dirname "$csproj")"
done < <(find backend -name '*.csproj' -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/node_modules/*' | sort)

for csproj in "${ALL_PROJECTS[@]}"; do
  dir="${PROJ_DIR[$csproj]}"
  # ProjectReference бывает многострочным — склеиваем файл, потом вытаскиваем теги целиком
  while IFS= read -r tag; do
    inc="$(sed -n 's/.*Include="\([^"]*\)".*/\1/p' <<< "$tag")"
    [ -n "$inc" ] || continue
    soft=""
    grep -qi 'ReferenceOutputAssembly="false"' <<< "$tag" && soft="|soft"
    target="$(realpath -m --relative-to="$ROOT" "$dir/${inc//\\//}")"
    RDEPS["$target"]+="$csproj$soft "
  done < <(tr '\n' ' ' < "$csproj" | grep -o '<ProjectReference[^>]*>')
done

# --- файлы → затронутые проекты (прямо) ---
declare -A AFFECTED
ALL_TESTS_REASON=""
# Файлы вне backend/, которые читает конкретный тест-проект (из корня репозитория).
declare -A ROOT_FILE_OWNER=(
  ["CLAUDE.md"]="backend/ClaudeHomeServer.Guards.Tests/ClaudeHomeServer.Guards.Tests.csproj"
)

owner_of() {  # путь файла → csproj самого вложенного каталога-владельца
  local f="$1" best="" bestlen=0 p d
  for p in "${ALL_PROJECTS[@]}"; do
    d="${PROJ_DIR[$p]}"
    case "$f" in "$d"/*) [ ${#d} -gt "$bestlen" ] && { best="$p"; bestlen=${#d}; } ;; esac
  done
  echo "$best"
}

while IFS= read -r f; do
  [ -n "$f" ] || continue
  if [ -n "${ROOT_FILE_OWNER[$f]:-}" ]; then
    AFFECTED["${ROOT_FILE_OWNER[$f]}"]="$f"; why "файл $f → проект ${ROOT_FILE_OWNER[$f]}"; continue
  fi
  case "$f" in
    backend/*)
      o="$(owner_of "$f")"
      if [ -n "$o" ]; then AFFECTED["$o"]="$f"; why "файл $f → проект $o"
      else ALL_TESTS_REASON="$f"; fi ;;
    *) why "файл $f вне backend/ → тесты не затронуты" ;;
  esac
done <<< "$CHANGED"

# --- обратное транзитивное замыкание (мягкое ребро — в набор, но без продолжения) ---
declare -A SEEN EXPANDED
queue=("${!AFFECTED[@]}")
for q in "${queue[@]}"; do SEEN["$q"]=1; done
while [ "${#queue[@]}" -gt 0 ]; do
  cur="${queue[0]}"; queue=("${queue[@]:1}")
  [ -z "${EXPANDED[$cur]:-}" ] || continue
  EXPANDED["$cur"]=1
  for entry in ${RDEPS[$cur]:-}; do
    dep="${entry%|soft}"
    if [ "$entry" = "$dep" ]; then
      why "  $cur ← $dep"; SEEN["$dep"]=1; queue+=("$dep")
    else
      why "  $cur ← $dep (ReferenceOutputAssembly=false: без продолжения)"; SEEN["$dep"]=1
    fi
  done
done

# --- вывод: только *.Tests.csproj ---
{
  for p in "${ALL_PROJECTS[@]}"; do
    case "$p" in *.Tests.csproj)
      if [ -n "$ALL_TESTS_REASON" ] || [ -n "${SEEN[$p]:-}" ]; then echo "$p"; fi ;;
    esac
  done
} | sort
[ -n "$ALL_TESTS_REASON" ] && why "файл $ALL_TESTS_REASON затрагивает все тесты" || true
