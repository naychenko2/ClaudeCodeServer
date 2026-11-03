#!/usr/bin/env bash
#
# mutation-copy.sh — тёплая изолированная копия дерева для проверки мутаций.
#
# Зачем: мутацию («мутация краснит тест») доказывают в копии, чтобы не трогать общее
# дерево, где параллельно работают соседи. Копия руками в /tmp собирается с нуля (всё
# решение, мимо BuildConcurrencyGate) и остаётся мусором на диске. Эта копия собирается
# инкрементально: пересобираются только тронутые проекты.
#
# Как устроена копия (замеры и обоснование — docs/operations/testing.md, «Мутации в копии»):
#   * git worktree add --detach --no-checkout на коммит источника — .git в копии есть
#     (тесты ищут корень репозитория подъёмом до .git), копия видна в `git worktree list`;
#   * файлы источника — rsync -a по списку `git ls-files -co --exclude-standard`: отслеживаемые
#     с незакоммиченными правками плюс новые неигнорируемые; mtime сохраняются, иначе MSBuild
#     сочтёт все исходники новее obj и пересоберёт всё;
#   * тёплая сборка — rsync -a каталогов obj/ и bin/<конф>/<tfm>/ref/ проектов backend/**.
#     Остальной bin/ (≈14 ГБ из 15) не копируется: он не вход компиляции и доезжает сборкой
#     только для собранных проектов;
#   * абсолютные пути в obj: текстовые файлы с путём источника переписываются на путь копии
#     (fix_obj_paths), а *.CoreCompileInputs.cache пересчитывается (prime_compile_cache) —
#     иначе хеш входов компиляции, куда входят абсолютные пути ссылок на проекты, расходится
#     и CoreCompile идёт заново во ВСЕХ проектах с ProjectReference;
#   * frontend/node_modules — symlink на node_modules источника, как в worktree-setup.sh.
#
# Использование:
#   scripts/mutation-copy.sh create [--from <дерево>]   путь копии — последней строкой stdout
#   scripts/mutation-copy.sh remove <путь>
#   scripts/mutation-copy.sh gc [--older-than-hours N]  (дефолт 24)
#
# Корень копий: ${CCS_MUTATION_ROOT:-$HOME/.cache/ccs-mutation}. Сборку и тесты в копии
# запускает вызывающий как обычно; скрипт только создаёт и убирает.

set -euo pipefail

# Метаданные копии (источник, время создания) — вне самой копии, чтобы не светиться в
# git status: $CCS_MUTATION_ROOT/.meta/<имя копии>.
META_DIR=".meta"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

die() { echo "ошибка: $*" >&2; exit 1; }
log() { echo "$*" >&2; }

mutation_root() {
  local root="${CCS_MUTATION_ROOT:-$HOME/.cache/ccs-mutation}"
  mkdir -p "$root"
  (cd "$root" && pwd -P)
}

# Каталоги obj/ и bin/*/*/ref/ проектов backend (пути от корня дерева).
list_build_dirs() {
  local src="$1"
  [ -d "$src/backend" ] || return 0
  (cd "$src" && find backend -type d \( -name node_modules -o -name .git \) -prune -o \
     -type d -name obj -print -prune)
  (cd "$src" && find backend -type d \( -name node_modules -o -name .git \) -prune -o \
     -type d -path '*/bin/*/*/ref' -print -prune)
}

# Переписать абсолютный путь источника на путь копии в текстовых артефактах obj/:
# сгенерированный .editorconfig (вход компиляции — с mtime источника, иначе CoreCompile),
# dgspec/props/targets/assets restore, списки записанных файлов (IncrementalClean).
fix_obj_paths() {
  local src="$1" dst="$2" f ts esrc edst
  esrc="$(printf '%s' "$src" | sed 's/[#&\\]/\\&/g')"
  edst="$(printf '%s' "$dst" | sed 's/[#&\\]/\\&/g')"
  while IFS= read -r -d '' f; do
    grep -qF "$src" "$f" 2>/dev/null || continue
    ts="$(stat -c %y "$f")"
    sed -i "s#$esrc#$edst#g" "$f"
    touch -d "$ts" "$f"
  done < <(find "$dst/backend" -path '*/obj/*' -type f \( -name '*.txt' -o -name '*.json' \
             -o -name '*.props' -o -name '*.targets' -o -name '*.editorconfig' \) -print0 2>/dev/null)
}

# Пересчитать *.CoreCompileInputs.cache в копии. Это SHA-256 от @(Compile);@(ReferencePath)…
# (цель _GenerateCompileDependencyCache SDK), а в ReferencePath — абсолютные пути ссылок на
# проекты: в копии хеш другой, файл перезаписывается и CoreCompile идёт во всех проектах с
# ProjectReference. Поэтому заранее прогоняем в копии цепочку CoreBuild до CoreCompile
# (без сборки ссылок; PrepareResources обязателен — EmbeddedResource тоже входит в хеш) и
# возвращаем пересчитанным файлам mtime источника.
# Обход — по каждому .csproj с obj/, а не по решению: часть проектов в .slnx не входит и
# собирается только по ссылкам (Execution, Watchdog, Deploy…), решение их не пересчитало бы.
PRIME_TARGETS='<Project>
  <Target Name="CcsPrimeCompileCache"
          DependsOnTargets="BuildOnlySettings;PrepareForBuild;ResolveReferences;PrepareResources;ResolveKeySource;SetWin32ManifestProperties;FindReferenceAssembliesForReferences;_GenerateCompileInputs;BeforeCompile;_TimeStampBeforeCompile;_GenerateCompileDependencyCache" />
</Project>'

prime_compile_cache() {
  local src="$1" dst="$2" sln gitdir targets trav f rel
  command -v dotnet >/dev/null || { log "dotnet не найден — хеши компиляции не пересчитаны"; return 0; }
  gitdir="$(git -C "$dst" rev-parse --path-format=absolute --git-dir)"
  targets="$gitdir/ccs-prime.targets"
  trav="$gitdir/ccs-prime.proj"
  printf '%s\n' "$PRIME_TARGETS" > "$targets"
  {
    echo '<Project><ItemGroup>'
    find "$dst/backend" \( -name node_modules -o -name bin -o -name obj \) -prune -o -name '*.csproj' -print \
      | while IFS= read -r f; do [ -d "$(dirname "$f")/obj" ] && echo "  <P Include=\"$f\" />"; done
    echo '</ItemGroup><Target Name="Prime">'
    echo "  <MSBuild Projects=\"@(P)\" Targets=\"CcsPrimeCompileCache\" BuildInParallel=\"true\" Properties=\"BuildingProject=true;BuildProjectReferences=false;CustomAfterMicrosoftCommonTargets=$targets\" />"
    echo '</Target></Project>'
  } > "$trav"
  # Restore по решению: снимает рассинхрон dgspec после смены путей, чтобы первая сборка
  # вызывающего не делала restore сама. Проекты вне решения он тоже достаёт — по ссылкам.
  sln="$(cd "$dst" && ls backend/*.slnx backend/*.sln 2>/dev/null | head -1 || true)"
  if [ -n "$sln" ]; then
    (cd "$dst" && dotnet restore "$sln" -nologo -v:q) >&2 \
      || log "предупреждение: restore в копии не удался"
  fi
  # Второй заход — на случай сбоя самого MSBuild под нагрузкой (ловили «Internal CLR error»).
  (cd "$dst" && dotnet msbuild "$trav" -t:Prime -m:6 -nologo -v:q) >&2 \
    || (cd "$dst" && dotnet msbuild "$trav" -t:Prime -m:6 -nologo -v:q) >&2 \
    || log "предупреждение: пересчёт хешей компиляции не удался — первая сборка будет дольше"
  rm -f "$targets" "$trav"
  while IFS= read -r -d '' f; do
    rel="${f#"$dst"/}"
    [ -f "$src/$rel" ] && touch -r "$src/$rel" "$f"
  done < <(find "$dst/backend" -path '*/obj/*' -type f -name '*.CoreCompileInputs.cache' -print0)
}

cmd_create() {
  local from=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --from) [ $# -ge 2 ] || die "--from без значения"; from="$2"; shift 2 ;;
      *) die "create: неизвестный аргумент $1" ;;
    esac
  done
  [ -n "$from" ] || from="$(cd "$SCRIPT_DIR/.." && pwd)"
  local src
  src="$(git -C "$from" rev-parse --show-toplevel 2>/dev/null)" || die "$from — не git-дерево"
  src="$(cd "$src" && pwd -P)"

  local root name dst head
  root="$(mutation_root)"
  case "$src/" in "$root"/*) die "источник $src сам лежит под $root" ;; esac
  name="$(basename "$src")"
  dst="$(mktemp -d "$root/$name-XXXXXX")"
  head="$(git -C "$src" rev-parse HEAD)"

  # Пустой каталог git worktree add принимает. --no-checkout: файлы привезёт rsync с mtime.
  git -C "$src" worktree add --quiet --detach --no-checkout "$dst" "$head" >&2 \
    || { rmdir "$dst" 2>/dev/null; die "git worktree add не удался"; }
  mkdir -p "$root/$META_DIR"
  printf 'source=%s\ncreated=%s\nhead=%s\n' "$src" "$(date +%s)" "$head" > "$root/$META_DIR/$(basename "$dst")"

  # Исходники: отслеживаемые (с рабочими правками) и новые неигнорируемые. Удалённые в
  # рабочем дереве файлы rsync пропустит (--ignore-missing-args) — в копии их тоже не будет.
  (cd "$src" && git ls-files -z -co --exclude-standard) \
    | rsync -a --from0 --files-from=- --ignore-missing-args "$src/" "$dst/"

  # Тёплые obj/ и bin/*/*/ref/.
  local dirs
  dirs="$(list_build_dirs "$src")"
  if [ -n "$dirs" ]; then
    printf '%s\n' "$dirs" | sed 's#$#/***#' \
      | rsync -a --include='*/' --include-from=- --exclude='*' --prune-empty-dirs "$src/" "$dst/"
    fix_obj_paths "$src" "$dst"
  fi

  # Индекс копии = HEAD; рабочие файлы не трогаются, правки источника видны как diff.
  git -C "$dst" reset --quiet >&2 || true

  if [ -n "$dirs" ] && [ "${CCS_MUTATION_NO_PRIME:-}" != 1 ]; then
    prime_compile_cache "$src" "$dst"
  fi

  local nm
  if [ -e "$src/frontend/node_modules" ] && [ -d "$dst/frontend" ]; then
    nm="$(readlink -f "$src/frontend/node_modules")"
    ln -s "$nm" "$dst/frontend/node_modules"
  fi

  echo "$dst"
}

# Путь обязан быть непосредственным подкаталогом корня копий.
resolve_copy() {
  local p="$1" root rp
  root="$(mutation_root)"
  [ -d "$p" ] || die "$p — не каталог"
  rp="$(cd "$p" && pwd -P)"
  [ "$(dirname "$rp")" = "$root" ] || die "$p не лежит в корне копий $root — отказ"
  case "$(basename "$rp")" in .*) die "$p — служебный каталог корня копий, отказ" ;; esac
  echo "$rp"
}

remove_copy() {
  local rp="$1" common=""
  if [ -f "$rp/.git" ]; then
    common="$(git -C "$rp" rev-parse --path-format=absolute --git-common-dir 2>/dev/null || true)"
  fi
  if [ -n "$common" ]; then
    git --git-dir="$common" worktree remove --force --force "$rp" >/dev/null 2>&1 || true
  fi
  rm -rf "$rp"
  rm -f "$(dirname "$rp")/$META_DIR/$(basename "$rp")"
  if [ -n "$common" ]; then
    git --git-dir="$common" worktree prune >/dev/null 2>&1 || true
  fi
}

cmd_remove() {
  [ $# -eq 1 ] || die "использование: remove <путь>"
  local rp
  rp="$(resolve_copy "$1")"
  remove_copy "$rp"
  echo "удалено: $rp"
}

# Есть ли живой процесс с cwd или открытым файлом под путём. Один find по /proc вместо
# readlink на каждый дескриптор: процессов на машине тысячи. Чужие /proc/*/fd без прав
# не читаются — свои процессы видны, этого достаточно.
has_live_process() {
  local dir="$1" hit
  hit="$(find /proc/[0-9]*/cwd /proc/[0-9]*/fd/ -maxdepth 1 \( -lname "$dir" -o -lname "$dir/*" \) \
           -print -quit 2>/dev/null || true)"
  [ -n "$hit" ]
}

cmd_gc() {
  local hours=24
  while [ $# -gt 0 ]; do
    case "$1" in
      --older-than-hours) [ $# -ge 2 ] || die "--older-than-hours без значения"; hours="$2"; shift 2 ;;
      *) die "gc: неизвестный аргумент $1" ;;
    esac
  done
  case "$hours" in ''|*[!0-9]*) die "--older-than-hours ждёт целое число" ;; esac
  local root now d created
  root="$(mutation_root)"
  now="$(date +%s)"
  for d in "$root"/*/; do
    [ -d "$d" ] || continue
    d="${d%/}"
    created="$(sed -n 's/^created=//p' "$root/$META_DIR/$(basename "$d")" 2>/dev/null || true)"
    [ -n "$created" ] || created="$(stat -c %Y "$d")"
    [ $((now - created)) -ge $((hours * 3600)) ] || continue
    if has_live_process "$d"; then
      log "пропущено (живой процесс): $d"
      continue
    fi
    remove_copy "$d"
    echo "удалено: $d"
  done
}

case "${1:-}" in
  create) shift; cmd_create "$@" ;;
  remove) shift; cmd_remove "$@" ;;
  gc) shift; cmd_gc "$@" ;;
  *) die "использование: $0 create [--from <дерево>] | remove <путь> | gc [--older-than-hours N]" ;;
esac
