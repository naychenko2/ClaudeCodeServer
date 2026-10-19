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
