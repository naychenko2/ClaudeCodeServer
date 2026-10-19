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

Колонки «0.1» — базовая линия (2026-10-17); колонки «3.15» — перемер после пачек D–G
(2026-10-19, см. «Шаг 3.15» ниже).

| Цель | cold 0.1 | increment 0.1 | cold 3.15 | increment 3.15 |
|---|---:|---:|---:|---:|
| `ClaudeHomeServer.Notes.Tests` | **10.39 s** | **13.12 s** | **17.91 s** | **13.13 s** |
| `ClaudeHomeServer.Images.Tests` | **21.90 s** | **17.01 s** | **17.73 s** | **17.56 s** |
| `ClaudeHomeServer.slnx` (полный, 24 тест-проекта) | **343.23 s** | **277.48 s** | **317.55 s** | **299.82 s** |

Команды (одинаковые для cold и increment, различается только префикс — см. «Методика»):
- `dotnet test backend/ClaudeHomeServer.Notes.Tests --nologo -v q`
- `dotnet test backend/ClaudeHomeServer.Images.Tests --nologo -v q`
- `dotnet test backend/ClaudeHomeServer.slnx --nologo -v q`

## Полный прогон (slnx): testhost и пик памяти

| Прогон | Пик RSS 0.1 | Max conc 0.1 | Samples 0.1 | Пик RSS 3.15 | Max conc 3.15 | Samples 3.15 |
|---|---:|---:|---:|---:|---:|---:|
| cold | **3 089 208 КБ (~2.95 GB)** | **4** | 644 | **4 228 228 КБ (~4.03 GB)** | **19** | 583 |
| increment | **3 250 964 КБ (~3.10 GB)** | **8** | 521 | **4 127 564 КБ (~3.94 GB)** | **17** | 553 |

**Как читать (0.1):** пик памяти даёт **один** самый большой testhost — монолит
`ClaudeHomeServer.Tests.dll` (9852 теста), который в момент пика держит ~3 GB RSS.
Сумма по остальным testhost'ам мала. Число concurrent testhost (4–8) — отдельная ось:
это одновременно живые testhost-процессы; потолок `-maxcpucount:6` — это потолок
**узлов MSBuild**, а testhost-процессы могут его кратковременно превышать (linger
после завершения VSTest проекта), поэтому observed max (8) > 6.

**Как читать (3.15):** пик суммарного RSS вырос (cold 2.95→4.03 GB, increment 3.10→3.94 GB),
и max concurrent testhost вырос (4→19, 8→17). Причина — **параллелизм**: после переноса
~10 тест-проектов из монолита одновременно живых testhost стало 17–19 (было 4–8); пик —
сумма RSS всех живых testhost в момент пика. Сам монолит стал меньше (тесты вынесены),
но сумма по большему числу testhost'ов выше. См. «Шаг 3.15» для вывода и эскалации.

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

## Шаг 3.15: перемер после пачек D–G (после шага 3)

> Повтор замеров шага 0.1 **тем же скриптом** `measure.sh` (методика выше) после переноса
> ~10 модульных тест-проектов из монолита (пачки D–G, шаги 3.7–3.14). Та же ветка
> `feat/tests-by-module`, тот же worktree. На шагах 3.7–3.14 код продукта и `.csproj`/`.slnx`
> менялись (вынос тестов), но методика замера неизменна.

### Окружение (шаг 3.15)

| Параметр | Значение |
|---|---|
| .NET SDK | `10.0.112` (`dotnet --version`) |
| Машина | 24 ядра, 121 GiB RAM (WSL2, Linux) |
| Worktree / ветка | `/home/an/Sources/ClaudeCodeServer-tests-by-module`, `feat/tests-by-module` |
| Дата/время замера | 2026-10-19, ~08:57–09:50 MSK |
| Состояние сервера сборки | **cold** — после `dotnet build-server shutdown`; **increment** — тёплый (после cold той же цели) |
| Загрузка машины | **неравномерная**: на машине крутился локальный LLM-сервер (vLLM: 2×`Worker_TP` + `EngineCore`, суммарно ~150 GB RSS) + `llama-server`; 1-min load скакал от ~4 до ~24 на 24 ядра. Влияет на wall-time (см. вывод). |

### Итог сравнения с baseline (0.1 → 3.15)

**Wall-time `dotnet test`:**

| Цель | cold 0.1 → 3.15 | increment 0.1 → 3.15 |
|---|---|---|
| `Notes.Tests` | 10.39 → 17.91 s (**+72%**) | 13.12 → 13.13 s (≈0) |
| `Images.Tests` | 21.90 → 17.73 s (**−19%**) | 17.01 → 17.56 s (+3%) |
| `slnx` (полный) | 343.23 → 317.55 s (−7%) | 277.48 → 299.82 s (+8%) |

**Полный прогон (slnx) — пик памяти и concurrent testhost:**

| Прогон | Пик RSS 0.1 → 3.15 | Max concurrent 0.1 → 3.15 |
|---|---|---|
| cold | 2.95 → 4.03 GB (**+37%**) | 4 → 19 |
| increment | 3.10 → 3.94 GB (**+27%**) | 8 → 17 |

### Вывод

1. **Пик суммарного RSS testhost вырос заметно** (cold +37%, increment +27% — оба > порога 20%).
   Причина — **параллелизм, а не размер отдельного testhost**: после выноса ~10 тест-проектов
   одновременно живых testhost стало 17–19 (было 4–8). Пик = сумма RSS всех живых testhost в
   момент пика; больше живых testhost → выше сумма. Сам монолит `ClaudeHomeServer.Tests.dll`
   стал меньше (тесты вынесены), но его «доля» в пике упала, а не пик в целом.
2. **Max concurrent testhost** вырос (4→19, 8→17) — прямое следствие числа тест-проектов.
3. **Wall-time полного прогона (slnx) — в пределах шума** (cold −7%, increment +8%): полный прогон
   задаёт **исполнение** самого медленного проекта (монолит остаётся крупнейшим), а не сборка;
   перекладка тестов между проектами не меняет суммарный объём работы. Расхождение с прогнозом
   0.1 («разнос сократит долю монолита») — потому что выросла **суммарная** доля пика памяти,
   а wall-time определяется **самым медленным** проектом, а не суммой.
4. **Wall-time Notes.Tests** — направление cold vs increment **инвертировалось**: в 0.1
   cold (10.39 s) < increment (13.12 s), в 3.15 cold (17.91 s) > increment (13.13 s). Increment
   стабилен (13.12→13.13 s), а cold вырос (+72%) — признак **загрузочной дисперсии** (vLLM-сервер
   на машине в момент cold-замера), а не систематического изменения: маленький граф Notes почти
   не компилируется, cold ≈ «релоад сервера + 103 теста», и он чувствителен к фоновой нагрузке.
5. **Wall-time Images.Tests** — cold упал (21.90→17.73 s, −19%), increment стабилен (+3%):
   в пределах шума, без систематического изменения.

### Эскалация (не фиксировать)

> **@Андрей:** пик суммарного RSS testhost при полном прогоне вырос **>20%** (cold 2.95→4.03 GB,
> increment 3.10→3.94 GB) и max concurrent testhost 4→19 / 8→17. Это **ожидаемое** следствие
> параллелизма (больше тест-проектов = больше живых testhost), а не утечка: отдельный testhost
> стал меньше, монолит перестал быть единым 3-GB процессом. Но **суммарный** пик памяти машины
> выше. Если пик ~4 GB на 24-ядерной/121-GiB машине критичен — решение за тобой (лимит
> параллелизма testhost, e.g. `VSTest`/MSBuild, или ничего). **Не фиксировал** по постановке.

## Предсуществующие провалы (состояние ветки, не от замеров)

`dotnet test` возвращает `testrc=1` — на ветке есть упавшие тесты, которые шаг 0.1
**не трогал** (правило: не править тесты). Констатация для ревью:
- `ClaudeHomeServer.Tests` (монолит): 11 упало из 9852 (31 skipped).
- `ClaudeHomeServer.CodeGraph.Tests`: 1 упало из 74.
- `ClaudeHomeServer.Images.Tests`: 1 упало из 733 — `LocalMediaHistorySourceTests.Expired_410`
  (тест монолита, выполняется внутри Images.Tests).

## Ранжирование прочих вертикалей (шаг 0.2 — порядок шагов 3.7–3.14)

Методика: для каждой вертикали — `git log --since=1.month --name-only -- <продуктовые-пути>`
из корня worktree (окно 2026-09-17 → 2026-10-17). «Частота правок» = число **уникальных
продуктовых файлов**, затронных за месяц. Приоритет шага 3 = ранг по частоте правок (по
убыванию; при равенстве числа файлов — по числу коммитов). Выше частота — раньше вертикаль
выносится: активно правимый код даёт больше регрессий, изолированные быстрые тесты там ценнее.

| Вертикаль | Коммиты за месяц | Уникальные файлы в правках | Приоритет шага 3 |
|---|---:|---:|---|
| TestRuns | 12 | 22 | 1 → **3.7** |
| ChatContext | 23 | 21 | 2 → **3.8** |
| Architecture | 5 | 16 | 3 → **3.9** |
| LocalStats | 16 | 14 | 4 → **3.10** |
| Deploy | 9 | 5 | 5 → **3.11** |
| RemoteCommands | 3 | 5 | 6 → **3.12** |
| Watchdog | 4 | 3 | 7 → **3.13** |
| Telemetry | 2 | 2 | 8 → **3.14** |

Примечания:
- TestRuns (22 файла) и ChatContext (21 файл) — фактически пара наверху. ChatContext правится
  чаще по числу коммитов (23 против 12), но TestRuns задевает больше **уникальных** файлов,
  поэтому ранг 1 у TestRuns. Разница 22/21 — в пределах шума; порядок 3.7/3.8 взаимозаменяем.
- Этот ранг **заменяет** предварительный порядок в [плане](tests-by-module-plan-2026-10.md)
  (пачки F/G): пачка F = 3.7–3.10 (TestRuns, ChatContext, Architecture, LocalStats),
  пачка G = 3.11–3.14 (Deploy, RemoteCommands, Watchdog, Telemetry).
- Продуктовые пути, по которым считали: ChatContext → `Core/Services/ChatContext/`;
  LocalStats → `Llm/LocalStats/`; RemoteCommands → `Services/RemoteCommands/` +
  `Controllers/RemoteCommandsController.cs` + `Core/Models/RemoteCommandsOptions.cs`;
  Telemetry → `ClaudeHomeServer/Telemetry/` + `Core/Telemetry/`; Architecture, TestRuns,
  Watchdog, Deploy — собственные сборки (каталог проекта целиком).

## Шаг 4.2: мутационная проверка вакуумности 5 guard-тестов

> Цель: доказать, что **ни один** из 5 guard-тестов в `ClaudeHomeServer.Guards.Tests` не
> вакуумный — каждый реально ловит нарушение собственного инварианта. Метод: для каждого
> guard'а — **минимальная точечная** мутация, ломающая инвариант → guard обязан **покраснеть**
> → откат мутации → guard обязан **позеленеть**. Мутации и откаты — локальные операции в
> рабочей копии, в git **не** коммитятся: перед коммитом этого отчёта дерево чистое
> (проверено `git status --short` + `git diff --stat` — оба пусты).
>
> Прогон — `mcp__tests__run_tests` target `backend/ClaudeHomeServer.Guards.Tests` (99 тестов).
> Для guard'ов 1–2 — **полный** прогон (99), для 3–5 — **фильтрованный** (guard в изоляции):
> так мутация проявляется именно в целевом guard'е, а не в артефактах изоляции.

### Сводка

| # | Guard | Инвариант | Мутация (локальная, откатана) | Красный прогон | Зелёный после отката |
|---|---|---|---|---|---|
| 1 | `SubsystemBoundaryTests` | вертикаль не ссылается на чужие вертикали | `Video → Notes` (ProjectReference + тип-метод) | 98/99, 1 упал | 99/99 |
| 2 | `SubsystemBoundaryCoverageTests` | каждая IAppSubsystem/namespace покрыта в `Boundaries` | новый namespace + `IAppSubsystem` | 97/99, 2 упали | 99/99 |
| 3 | `IlBoundaryRegressionTests` | IL-скан видит 6 известных швов | разрыв seam 5 (`Execution → TranscriptRoots`) | 3/4, 1 упал | 4/4 |
| 4 | `McpToolsetStabilityTests` | состав tools/list не зависит от хода | литерал `turnText` в теле `BuildTurnMcpConfig` | 45/46, 1 упал | 46/46 |
| 5 | `ProjectMapHygieneGuardTests` | в карте CLAUDE.md нет мёртвых ссылок | мёртвая ссылка на несуществующий ADR | 1/2, 1 упал | 2/2 |

### 1. SubsystemBoundaryTests — «вертикаль → чужая вертикаль»

- **Мутация:** `ClaudeHomeServer.Video.csproj` — `<ProjectReference>` на `ClaudeHomeServer.Notes.csproj`;
  `VideoSubsystem.cs` — приватный метод `_MutationProbe(ClaudeHomeServer.Services.Notes.NotesSubsystem n)`
  (тип-параметр виден IL-сканеру). `Notes` не в allow-list вертикали `Video`.
- **Красный (98/99, 1 упал):**
  `Vertical_НеСсылаетсяНаДругиеВертикали(Video)`:
  «Video: `ClaudeHomeServer.Services.Video.VideoSubsystem` ссылается на
  `ClaudeHomeServer.Services.Notes.NotesSubsystem` из чужой вертикали (нет в
  AllowedNamespacePrefixes/AllowedExactNamespaces)».
- **Откат → 99/99.**

### 2. SubsystemBoundaryCoverageTests — «полнота таблицы Boundaries»

- **Мутация:** новый файл `backend/ClaudeHomeServer/Services/MutationCoverageProbe.cs` —
  namespace `ClaudeHomeServer.Services.ZzzMutationProbe` + `ZzzMutationProbeSubsystem : IAppSubsystem`
  (подсистема, **не** заведённая в `Boundaries`).
- **Красный (97/99, 2 упали)** — **оба** метода класса, оба на `ZzzMutationProbe`:
  - `AllServicesNamespaces_AreCoveredByBoundaryOrExclusion`: «ClaudeHomeServer.Services.ZzzMutationProbe,
    but found at least one item».
  - `AllIAppSubsystemImplementations_HaveBoundaryEntry`: «ClaudeHomeServer.Services.ZzzMutationProbe,
    but found at least one item».
- **Откат (удаление файла) → 99/99.**

### 3. IlBoundaryRegressionTests — «IL-скан видит 6 известных швов»

- **Мутация:** `DockerProcessRunner.cs` (Execution) — закомментирован вызов
  `TranscriptRoots.AddAllowedRoot(projectsDir);` — единственный шов `Execution → TranscriptRoots`
  (seam 5 из 6) разорван.
- **Красный (3/4, 1 упал):** `IlScan_ВидитВсеСемьИзвестныхШвов`:
  «Не найдено: **Execution → TranscriptRoots: IL-скан НЕ видит**».
- **Откат → 4/4.**
- **Грабли (отличие от кандидата в постановке):** мутация «удалить строку форс-загрузки из
  стат. конструктора» guard **не** красит — строки форс-загрузки в `IlBoundaryRegressionTests`
  **избыточны**: xunit при обнаружении инстанциирует **все** классы сборки, и стат. конструктор
  `SubsystemBoundaryTests` форс-загружает те же сборки (Execution/Reader/Memory/Llm/…). Поэтому
  надёжнее ломать **самый шов** (реальную ссылку), а не механизм форс-загрузки.

### 4. McpToolsetStabilityTests — «состав tools/list не зависит от хода»

- **Мутация:** `ClaudeSession.cs` (Llm) — в тело `BuildTurnMcpConfig` добавлена строка
  `var turnText = 0;` (литерал `turnText` в теле метода).
- **Красный (45/46, 1 упал):** `СоставИнструментовХода_НеЗависитОтСостоянияХода`:
  `NotContain("turnText")` — «Did not expect code … `var turnText = 0;` …».
- **Откат → 46/46.**
- **Ответ по «нулю форс-загрузки»:** guard **не** 0-форс-загрузочный. Его защита **source-based**:
  `[Fact]` (не skippable) требует, чтобы файл `ClaudeSession.cs` **нашёл**ся (`path.Should().NotBeNull`)
  **и** метод `BuildTurnMcpConfig` **существовал** (`signature.Success.Should().BeTrue`), и затем
  проверяет чистоту тела от `turnText`/`_currentTurnAgentDepth`/`_currentTurnSuppressTasksExecute`.
  Мутация **нашлась** (в отличие от контингента «мутация не нашлась» в постановке) — guard не вакуумный.

### 5. ProjectMapHygieneGuardTests — «гигиена карты CLAUDE.md»

- **Мутация:** `CLAUDE.md` (корень) — добавлена строка с мёртвой ссылкой
  `[ADR-033-mutation-probe.md](docs/adr/ADR-033-mutation-probe.md)` (файла нет).
- **Красный (1/2, 1 упал):** `КартыРепозитория_НеСодержатМёртвыхСсылокИИмпортов`:
  «CLAUDE.md:498 — ссылка на «docs/adr/ADR-033-mutation-probe.md», файла нет».
  Тест размера (`КартаРепозитория_НеПревышаетПотолокСтрок`) остался зелёным (497 < 530).
- **Откат → 2/2.**

### Вывод

Все 5 guard'ов **не вакуумны**: каждый покраснел на точечной мутации, ломающей его инвариант,
и позеленел после отката. Ни один guard не «проходит молча» при сломанной проверке.

## Шаг 5.3а — перемер по правильным сценариям

> Сценарии выбраны так, чтобы правка **не** была невидимой для сборки (правка `NotesController`
> из 0.1 — ref-assembly: Main с `ReferenceOutputAssembly="false"` не пересобирается, поэтому
> метрика ничего не говорила о цене итерации). Здесь меряется то, что реально платит
> исполнитель на итерации: правка в Llm-вертикали и в её тестах.

**«До»** — worktree на `a5dea0112` (базовая линия 0.2; относительно родителя коммит только
добавляет документ, код тот же). Тест `InotifyLeakRegressionTests` и `ClaudeSession` там уже
лежат в `ClaudeHomeServer.Llm`/монолите: тесты вертикали живут в `ClaudeHomeServer.Tests`.
**«После»** — `feat/tests-by-module` (HEAD `c324f5634`): те же тесты в `ClaudeHomeServer.Llm.Tests`.

| Сценарий | Правка |
|---|---|
| 1 | в конец `InotifyLeakRegressionTests.cs` дописана строка-комментарий (меняется контент и mtime) |
| 2 | в последнюю перегрузку `ClaudeSession.BuildBareModeArgs` (`internal`, видна по IVT) добавлен опциональный параметр `int measureMarker = 0`; вызывающие код не менялись |

После каждого замера правка откатывается `git checkout`. Продуктовый код в итоге не тронут
(`git status` чист).

### Методика

Скрипт `measure53.sh` (лежит вне репозитория, `/tmp`): перед замером — тёплая сборка тест-проекта
(не замеряется); затем правка; `T = wall(dotnet build <тест-проект> -v q -clp:ErrorsOnly)` —
**сборка после правки**, 3 повтора на ячейку. Чистый прогон — отдельно, один раз на состояние:
`wall(dotnet test <тест-проект> --no-build)`. Итог итерации ≈ сборка + прогон.
Всё на переднем плане, `dotnet build-server` не гасился (режим increment).

### Результат

Wall-time сборки после правки (секунды; 3 повтора → медиана):

| Сценарий | Тест-проект | До (повторы) | До, медиана | После (повторы) | После, медиана |
|---|---|---|---:|---|---:|
| 1. тест-файл | монолит → `Llm.Tests` | 22.6 / 27.4 / 29.8 | **27.4** | 13.5 / 20.8 / 15.3 | **15.3** |
| 2. сигнатура в `Llm` | монолит → `Llm.Tests` | 32.3 / 40.5 / 32.9 | **32.9** | 32.2 / 39.3 / 26.3 | **32.2** |

Чистый прогон тест-проекта, в котором лежат тесты Llm:

| Состояние | Проект | Тестов | Wall | Результат |
|---|---|---:|---:|---|
| До | `ClaudeHomeServer.Tests` (монолит) | 9852 | **300.7 s** | 9810 ✔, 31 пропущено, **11 упало** (предсуществующие, см. «Предсуществующие провалы») |
| После | `ClaudeHomeServer.Llm.Tests` | 2042 | **207.0 s** | 2038 ✔, 4 пропущено, 0 упало |

Итого на итерацию «правка тестового файла Llm → собрать и прогнать» (сценарий 1):
**до ≈ 27 + 301 ≈ 328 s, после ≈ 15 + 207 ≈ 222 s** (−32%). Выигрыш — от того, что тестовая
сборка перестала тащить монолит и 9852 теста, а не от скорости компиляции.

### Выводы

1. **Сценарий 1 (тест-файл):** сборка после правки быстрее в ~1.8 раза (27.4 → 15.3 s): пересобирается
   маленький `Llm.Tests`, а не монолит. Прогон — 207 против 301 s, и он зелёный.
2. **Сценарий 2 (сигнатура в Llm):** **выигрыша по сборке нет** (32.9 → 32.2 s, в пределах шума).
   `Llm.Tests` ссылается на `Main`, поэтому правка в `Llm` всё равно перекомпилирует `Llm` → `Main`
   → тест-проект; разнос тестов эту цепочку не сокращает. Зато `scripts/tests-affected.sh` на
   правку `ClaudeSession.cs` называет **26 из 39** тест-проектов: тот, кто сменил публичную/internal
   сигнатуру, должен прогнать их все (или осознанно сузить), а не только `Llm.Tests`.
3. **Шум:** на машине параллельно работали чаты и локальный LLM-сервер; 1-мин load average во время
   замеров — 24–36 на 24 ядра. Разброс повторов внутри ячейки (до ±30%) сопоставим с разницей
   в сценарии 2, поэтому вывод «нет выигрыша» — «нет заметного», а не «ровно ноль».
4. Базовые числа «до» получены в отдельном worktree `/tmp/ccs-before` (после замеров удалён);
   его холодная сборка монолита — 109 s.

## Шаг 5.3б — параллельность testhost

> Вопрос шага: почему `-maxcpucount:6` из `backend/Directory.Build.rsp` не держит число
> testhost (полный прогон давал 17–19, инцидент 21.09 был именно про это) и как это ограничить.

### Причина

`BuildConcurrencyGate` тут ни при чём: он считает только запуски с меткой `ProcessSpec.Heavy`
(целые прогоны/прогрев worktree) и внутрь одного `dotnet test` не заглядывает.
xunit-флаги (`parallelizeTestCollections`, `maxParallelThreads`) ограничивают потоки
**внутри** одного testhost и число testhost не меняют. Настоящая причина — в `dotnet test`:

```
$ dotnet test backend/ClaudeHomeServer.Notes.Tests --no-build -v:diag | grep "Аргументы командной строки"
MSBuild.dll -maxcpucount --verbosity:m -tlp:default=auto --property:VSTestNoBuild=true …
... Некоторые параметры командной строки были считаны из файла автоматического ответа
    ".../backend/Directory.Build.rsp"
```

`dotnet` сам передаёт MSBuild **голый `-maxcpucount`** (= число ядер, здесь 24). Файл `.rsp`
читается (MSBuild это подтверждает), но голый ключ из командной строки перебивает
`-maxcpucount:6` оттуда. Явный ключ в командной строке перебивает уже голый:
`dotnet test … -m:6` даёт ровно 6. Так что лимит, введённый 2026-09-22, для `dotnet test`
по решению не работал с самого начала.

Диагностические прогоны первых 60 с (потомки процесса, `--no-build`): `-m:1` → 1 testhost,
`-m:2` → 2, `-m:6` → 6.

### Как мерили (отличие от 0.1 и 3.15)

Сэмплер раз в 0.5 с считает `testhost.dll`, `vstest.console.dll` и узлы MSBuild только среди
**потомков** запущенной `dotnet test`-команды (`ps -eo pid,ppid,rss,args` + обход дерева от
корня). В 0.1/3.15 считался весь `ps` машины, а на ней параллельно работают другие чаты со своими
прогонами — те цифры (4–8 и 17–19) могли включать чужие testhost. Прогон — `dotnet test
backend/ClaudeHomeServer.slnx --no-build --nologo -v q` на собранном дереве (HEAD `e549caceb`).

### Результат: до и после

| | До (голый `-maxcpucount` от dotnet) | После (`-m:6`) |
|---|---:|---:|
| Макс. одновременных testhost | **24** | **6** |
| Макс. одновременных vstest.console | 24 | 6 |
| Макс. узлов MSBuild | 23 | 5 |
| Пик суммарного RSS testhost | **6 045 600 КБ (5.77 GB)** | **3 747 640 КБ (3.57 GB)** |
| Wall полного прогона | 267.9 s | 288.3 s |
| Итог | 38 проектов ✔, **1 упал**: `VideoAssembleIsolationTests.Сборка_фильма_идёт_в_scope_slice_под_занятым_слотом` | все ✔ (код выхода 0) |

Число одновременных testhost по времени от старта прогона (первые секунды — все проекты разом):

| t, c | 12–22 | 32 | 41 | 58 | 67–190 | 196–265 |
|---|---:|---:|---:|---:|---:|---:|
| до | 21 | 8 | 4 | 2 | 2 | 1 |
| после (t=22/44/68/91…) | 6 | 5 | 3 | 2 | 2 | 1 |

### Вывод

1. Потолок testhost **24 → 6** (≤6 достигнуто), пик памяти −38%, цена — wall +7% (268 → 288 s),
   что в пределах шума на машине с параллельной нагрузкой (как и в 3.15). Хвост прогона
   (1–2 testhost: монолит и `Llm.Tests`) задаёт wall в обоих случаях — ограничение стартового
   всплеска его не удлиняет.
2. В прогоне без ограничения упал один тест монолита (`VideoAssembleIsolationTests`) — при 24
   одновременных testhost; с `-m:6` он зелёный. Один прогон — не доказательство флака, но
   совпадает с профилем «чувствителен к нагрузке»; отдельный дефект не заводился.
3. **Что изменено:** `TestRunService.DotnetSpec` (инструмент `run_tests`, фазы build/list/test)
   добавляет `-m:6` (`MaxCpuCountArg`); тест `MaxCpuCountArg_СовпадаетСПотолкомВDirectoryBuildRsp`
   держит число равным значению в `.rsp`; комментарий в `Directory.Build.rsp` поправлен — он
   обещал то, чего файл для `dotnet test` не делает. Проверка мутацией: `-m:7` → краснеют
   `MaxCpuCountArg_…` и `Spec_ФазыКонвейера`.
4. **Что осталось без правки (вне постановки):** `mcp__dev__build` (`DotnetBuildService`) тоже
   зовёт `dotnet build` без ключа — 23 узла MSBuild вместо 6; прямые `dotnet test`/`dotnet build`
   в Bash и CI-команда `dotnet test backend/ClaudeHomeServer.slnx --no-build` — без `-m:6`
   (на CI 4 ядра, голый ключ даёт 4 — безвредно). Работающий сервер подхватит правку
   `run_tests` только после выкатки: замер «после» сделан теми же аргументами через Bash.
