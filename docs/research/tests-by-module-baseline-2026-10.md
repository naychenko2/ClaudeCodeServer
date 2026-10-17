# Базовая линия: wall-time / testhost / пик памяти (шаг 0.1)

> Фича `27dd9c17` «Модульные тесты: отвязать от монолита и разнести по вертикалям»,
> разрез [tests-by-module-cut-2026-10.md](tests-by-module-cut-2026-10.md),
> план [tests-by-module-plan-2026-10.md](tests-by-module-plan-2026-10.md), пачка A, шаг **0.1**.
>
> Это **базовая линия «до»**: шаг 3.15 перемерит тем же способом и допишет колонку «после шага 3».
> Код продукта, `.csproj` и `.slnx` на этом шаге **не менялись** — только замеры.

## Окружение

| Параметр | Значение |
|---|---|
| .NET SDK | `10.0.112` (`dotnet --version`) |
| .NET runtime | `10.0.12` (Microsoft.NETCore.App / AspNetCore.App) |
| Машина | 24 ядра, 121 GiB RAM (WSL2, Linux) |
| Потолок узлов MSBuild | `-maxcpucount:6` из `backend/Directory.Build.rsp` |
| Worktree / ветка | `/home/an/Sources/ClaudeCodeServer-tests-by-module`, `feat/tests-by-module` |
| Дата/время замера | 2026-10-17, ~17:40–18:02 MSK |
| Состояние сервера сборки | **cold** — после `dotnet build-server shutdown`; **increment** — тёплый (после cold той же цели) |

«Состояние сервера сборки»: режим **cold** гасит MSBuild node-reuse server командой
`dotnet build-server shutdown` (следующая сборка поднимает свежий MSBuild-процесс и
пересобирает граф с нуля). Режим **increment** не трогает сервер: он тёплый после cold,
и в вертикали цели добавлена одна строка-маркер в `.cs`, чтобы MSBuild пересобрал только
зависимое. bin/obj при этом **не чистим** (это не `rm -rf bin obj`): csc остаётся
инкрементальным и пропускает up-to-date-сборки — см. «Нюанс cold vs increment» ниже.

## Wall-time `dotnet test` (сборка + прогон)

| Цель | cold (после `build-server shutdown`) | increment (после правки 1 `.cs`) |
|---|---:|---:|
| `ClaudeHomeServer.Notes.Tests` | **10.39 s** | **13.12 s** |
| `ClaudeHomeServer.Images.Tests` | **21.90 s** | **17.01 s** |
| `ClaudeHomeServer.slnx` (полный, 24 тест-проекта) | **343.23 s** | **277.48 s** |

Команды (одинаковые для cold и increment, различается только префикс — см. «Методика»):
- `dotnet test backend/ClaudeHomeServer.Notes.Tests --nologo -v q`
- `dotnet test backend/ClaudeHomeServer.Images.Tests --nologo -v q`
- `dotnet test backend/ClaudeHomeServer.slnx --nologo -v q`

## Полный прогон (slnx): testhost и пик памяти

| Прогон | Пик суммарного RSS testhost | Max concurrent testhost | Число сэмплов |
|---|---:|---:|---:|
| cold | **3 089 208 КБ (~2.95 GB)** | **4** | 644 |
| increment | **3 250 964 КБ (~3.10 GB)** | **8** | 521 |

**Как читать:** пик памяти даёт **один** самый большой testhost — монолит
`ClaudeHomeServer.Tests.dll` (9852 теста), который в момент пика держит ~3 GB RSS.
Сумма по остальным testhost'ам мала. Число concurrent testhost (4–8) — отдельная ось:
это одновременно живые testhost-процессы; потолок `-maxcpucount:6` — это потолок
**узлов MSBuild**, а testhost-процессы могут его кратковременно превышать (linger
после завершения VSTest проекта), поэтому observed max (8) > 6.

## Методика (повторить в шаге 3.15 тем же способом)

Один самодостаточный скрипт `measure.sh <target> <mode>`; `target ∈ {notes, images, slnx}`,
`mode ∈ {cold, increment}`. Запуск каждого замера — одна команда:

```
measure.sh notes cold        measure.sh notes increment
measure.sh images cold      measure.sh images increment
measure.sh slnx cold         measure.sh slnx increment
```

Скрипт (полный текст — ниже; время = `date +%s.%N` до/после `dotnet test`,
разность в секундах):

```bash
#!/usr/bin/env bash
# measure.sh <target> <mode>   target: notes|images|slnx   mode: cold|increment
set -uo pipefail
ROOT="/home/an/Sources/ClaudeCodeServer-tests-by-module"; cd "$ROOT" || exit 90
OUT="/tmp/baseline-measure"; mkdir -p "$OUT"
TARGET="$1"; MODE="$2"; MARKER="// baseline-marker-0.1"
case "$TARGET" in
  notes)  TESTARG="backend/ClaudeHomeServer.Notes.Tests";  MARKER_FILE="backend/ClaudeHomeServer.Notes/Controllers/NotesController.cs";;
  images) TESTARG="backend/ClaudeHomeServer.Images.Tests"; MARKER_FILE="backend/ClaudeHomeServer.Images/Services/ImageGenerationSettingsStore.cs";;
  slnx)   TESTARG="backend/ClaudeHomeServer.slnx";         MARKER_FILE="backend/ClaudeHomeServer.Notes/Controllers/NotesController.cs";;
esac
LOG="$OUT/${TARGET}-${MODE}"; : > "$LOG.test"; : > "$LOG.buildserver"; : > "$LOG.sampler"
SAMPLER_PID=""
# Сэмплер (только slnx): раз в 0.5 c — ps по testhost.dll, сумма RSS + число
if [ "$TARGET" = "slnx" ]; then
  ( while :; do
      ps -eo pid,rss,args --no-headers 2>/dev/null \
        | awk '/testhost\.dll/ {sum+=$2; n++} END {printf "%d %d\n", sum, n}' \
        | { read -r s n; echo "$(date +%s.%N) $s $n"; } >> "$LOG.sampler"
      sleep 0.5
    done ) &
  SAMPLER_PID=$!
fi
[ "$MODE" = "cold" ]      && dotnet build-server shutdown >> "$LOG.buildserver" 2>&1
[ "$MODE" = "increment" ] && printf '\n%s\n' "$MARKER" >> "$MARKER_FILE"
T0=$(date +%s.%N)
dotnet test "$TESTARG" --nologo -v q >> "$LOG.test" 2>&1; TESTRC=$?
T1=$(date +%s.%N)
[ -n "$SAMPLER_PID" ] && { kill "$SAMPLER_PID" 2>/dev/null; pkill -P "$SAMPLER_PID" 2>/dev/null; wait "$SAMPLER_PID" 2>/dev/null; }
[ "$MODE" = "increment" ] && git checkout -- "$MARKER_FILE"   # точно восстанавливает HEAD (вкл. trailing newline)
WALL=$(awk -v a="$T0" -v b="$T1" 'BEGIN{printf "%.2f", b-a}')
echo "target=$TARGET mode=$MODE wall=${WALL}s testrc=$TESTRC"
[ "$TARGET" = "slnx" ] && awk 'NR==1{m2=0;m3=0} {if($2>m2)m2=$2; if($3>m3)m3=$3}
  END{printf "peak_total_testhost_rss_kb=%d  max_concurrent_testhost=%d  samples=%d\n", m2, m3, NR}' "$LOG.sampler"
```

**Как именно снимали testhost и пик памяти.** В .NET VSTest тесты исполняет процесс
`testhost` — здесь это `dotnet exec … testhost.dll --role client` (не нативный `testhost`,
а `dotnet` с `testhost.dll` в `args`; оркестратор `vstest.console.dll` — отдельный процесс
и в пик **не** входит). Сэмплер в фоне раз в 0.5 c читает `ps -eo pid,rss,args`,
фильтрует строки по подстроке `testhost.dll`, суммирует их RSS (КБ) и считает их число;
**пик** = максимум по всем сэмплам (сумма RSS и число — максимумы независимы, берутся
в разные моменты). Прогон идёт на переднем плане (не `run_in_background`), сэмплер —
фоновый процесс внутри того же Bash-вызова; `kill`+`pkill -P` останавливает его после
`dotnet test`.

**Маркер (increment).** В конец `.cs` вертикали цели дописывается одна строка-комментарий
`// baseline-marker-0.1` (меняет mtime+контент → MSBuild перекомпилирует именно эту
продуктовую сборку и перессылает зависимых), после прогона `git checkout -- <файл>`
точно восстанавливает HEAD (включая отсутствие trailing newline, который append ломал).
Файлы маркеров: Notes → `NotesController.cs`, Images → `ImageGenerationSettingsStore.cs`,
slnx → `NotesController.cs` (та же вертикаль, что у Notes.Tests — методика «правка одного
`.cs` в вертикали Notes» совпадает для обоих).

## Нюанс: почему направление cold vs increment скачет

«cold» здесь = **холодный MSBuild-сервер**, а **не** чистые bin/obj: csc остаётся
инкрементальным и пропускает up-to-date-сборки. Поэтому:
- **Notes** (маленький граф): cold=10.39s < increment=13.12s. Cold почти не компилирует
  (граф маленький, всё up-to-date) — только релоад сервера + 103 теста; increment реально
  перекомпилирует Notes.
- **Images** (большой граф, тянет монолит): cold=21.90s > increment=17.01s. Полная
  переоценка большого графа (cold) дороже целевой перекомпиляции одной сборки (increment).
- **slnx**: cold=343s > increment=277s. Разница ≈ стоимость полной переоценки 72 проектов
  (cold) против перекомпиляции Notes + relink (increment); **оба** прогона доминирует
  исполнение тестов монолита (~250–300 c), а не сборка.

Вывод для фичи: wall-time полного прогона задаёт **исполнение** монолита, а не сборка;
разнос по вертикалям (шаг 3) сократит именно эту долю, если монолит перестанет быть
единым 3-GB testhost'ом.

## Предсуществующие провалы (состояние ветки, не от замеров)

`dotnet test` возвращает `testrc=1` — на ветке есть упавшие тесты, которые шаг 0.1
**не трогал** (правило: не править тесты). Констатация для ревью:
- `ClaudeHomeServer.Tests` (монолит): 11 упало из 9852 (31 skipped).
- `ClaudeHomeServer.CodeGraph.Tests`: 1 упало из 74.
- `ClaudeHomeServer.Images.Tests`: 1 упало из 733 — `LocalMediaHistorySourceTests.Expired_410`
  (тест монолита, выполняется внутри Images.Tests).
