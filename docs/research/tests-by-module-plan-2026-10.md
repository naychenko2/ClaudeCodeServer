# План: тесты по модулям, шаги 0→5

> Фича `27dd9c17`, разрез — [tests-by-module-cut-2026-10.md](tests-by-module-cut-2026-10.md).
> Рабочее место одно: worktree `/home/an/Sources/ClaudeCodeServer-tests-by-module`, ветка
> `feat/tests-by-module`. Исполнитель коммитит сам, как только критерии шага зелёные.

## Порядок — жёсткий

**0 → 1 → 2 → 3 → 4 → 5, строго последовательно, и подшаги внутри — тоже.** Все шаги правят
одни и те же `.csproj`, `ClaudeHomeServer.slnx` и `Helpers/`, параллель даст конфликты.
Следующая пачка стартует только после ревью Глеба по предыдущей (`accepted` или
`accepted_with_remarks`): доработка поверх чужих правок в тех же csproj дороже ожидания.

```mermaid
flowchart LR
  A["Пачка A: 0.1–0.2"] --> B["Пачка B: 1.1–1.4"] --> C["Пачка C: 2.1–2.2"]
  C --> D["Пачка D: 3.1–3.3 Llm"] --> E["Пачка E: 3.4–3.6"] --> F["Пачка F: 3.7–3.10"]
  F --> G["Пачка G: 3.11–3.14"] --> H["Пачка H: 3.15, 4.1–4.2"] --> I["Пачка I: 5.1–5.3"] --> Z["final: Александр"]
```

## Общий чек-лист каждого шага, меняющего тесты (обязателен, в критериях ниже — «Сверка»)

1. Сборка — `mcp__dev__build` по `backend/ClaudeHomeServer.slnx`.
2. **Сверка числа тестов:** `dotnet test backend/ClaudeHomeServer.slnx --no-build --list-tests`,
   имена отсортировать и сравнить `diff`'ом с эталоном шага 0
   (`docs/research/tests-by-module-baseline-tests.txt`). Совпадать должны сумма И сам набор
   имён — иначе потеря одного теста маскируется дублем другого. Эталон после шага не
   переписывается.
3. Новый `*.Tests.csproj` есть в `ClaudeHomeServer.slnx` (после шага 1.4 это держит guard).
4. Новой тест-сборке, которой нужны internal-типы, добавлен `InternalsVisibleTo` в продуктовую
   сборку (`<AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleToAttribute">`).
5. Тесты, опирающиеся на `ModuleLoader`, получают в новом csproj тот же `Copy`-таргет dll
   модуля, что в монолите (`CopyNotesModuleForTests` и соседи, строки ~171–215
   `ClaudeHomeServer.Tests.csproj`); `None … CopyToOutputDirectory` фикстур — туда же.
6. Перенос — `git mv` без правки тел тестов: платформонезависимость не трогаем.
7. Прогон затронутых проектов — `mcp__tests__run_tests` (target — csproj), зелёные.

Про «CI-зелёный» из разреза: ветку не пушим без просьбы, поэтому на шагах роль CI играет
локальный прогон slnx (на шагах 1.4, 2.2, 3.3, 3.6, 3.10, 3.14, 3.15, 4.2 — полный). Настоящий CI —
перед мержем, это пункт финального ревью.

---

## Пачка A — Денис, ревью Глеба

**0.1 Замеры** (~60 мин — прогоны долгие)
- Замерить wall-time `dotnet test` после `dotnet build-server shutdown` и инкрементально после
  правки одного `.cs` в вертикали — для `ClaudeHomeServer.Notes.Tests`,
  `ClaudeHomeServer.Images.Tests` и полного `ClaudeHomeServer.slnx`. Число testhost и пик
  памяти полного прогона. Способ замера записать рядом с числом — шаг 3.15 повторит его тем же
  способом.
- **Готово:** документ с тремя замерами × два режима, testhost, пик памяти, способ замера.
  Код не менялся.

**0.2 Эталон тестов и ранжирование** (~30 мин)
- Снять эталон `--list-tests` по slnx → `docs/research/tests-by-module-baseline-tests.txt`
  (отсортированные имена) + сумма.
- Ранжировать «прочие» вертикали шага 3 по частоте правок:
  `git log --since=1.month --name-only` по продуктовым файлам каждой из ChatContext,
  Architecture, TestRuns, LocalStats, Watchdog, Deploy, RemoteCommands, Telemetry. Таблица
  определяет порядок шагов 3.7–3.14.
- Файлы шага 0: `docs/research/tests-by-module-baseline-2026-10.md` (новый),
  `docs/research/tests-by-module-baseline-tests.txt` (новый).
- **Готово:** эталон с суммой; таблица ранжирования из 8 строк в baseline-документе.

## Пачка B — Денис, ревью Глеба (шаг 1, TestKit)

Совет исполнителю: namespaces переносимых хелперов не менять (`ClaudeHomeServer.Tests.Helpers`
и т.п.) — иначе правка `using` в сотнях файлов монолита и модулей, и шаг не влезет в окно.

**1.1 `ClaudeHomeServer.TestKit`** (~45 мин)
- Новый `backend/ClaudeHomeServer.TestKit/ClaudeHomeServer.TestKit.csproj`: ссылки только на
  `ClaudeHomeServer.Core` + xunit/Moq/FluentAssertions. Не тест-проект (`IsTestProject=false`).
- Разобрать `ClaudeHomeServer.Tests/Helpers/*` (26 файлов), `TestLauncherFactory.cs`,
  `FakeLlmSessionAdapter.cs`: что компилируется против одного Core — `git mv` в TestKit.
  Ожидаемо: `TestConfig`, `TestFs`, `FakeHostEnvironment`, `FakeFeatureFlagGate`,
  `FakeDifyHttpClient`, `NullDifyMetrics` — список подтверждает компилятор, не догадка.
- Монолит → `ProjectReference` на TestKit. slnx — добавить.
- **Готово:** TestKit собирается без ссылки на Main; таблица «хелпер → TestKit/остаётся» в
  отчёте; Сверка.

**1.2 `ClaudeHomeServer.TestKit.Host`** (~45 мин)
- Новый `backend/ClaudeHomeServer.TestKit.Host/…csproj`: Main + TestKit. Сюда
  `TestWebApplicationFactory`, `TestLauncherFactory` и всё оставшееся в `Helpers/`, что тянет
  типы Main (`SessionManagerTestAccess`, `DeviceWaitTestHost`, `LocalMediaFakes`, …).
- Если хелпер трогает internal Main — `InternalsVisibleTo` для `ClaudeHomeServer.TestKit.Host`
  в `ClaudeHomeServer/ClaudeHomeServer.csproj`.
- Монолит → TestKit.Host. slnx — добавить.
- **Готово:** `Helpers/` монолита пуст или содержит только монолитно-локальное (перечислено в
  отчёте с причиной); Сверка.

**1.3 Модульные проекты на TestKit\*** (~45 мин)
- Десять csproj: Notes, Tasks, Memory, Knowledge, Spend, Turn, Dossiers, Images, ImageEditor,
  Devices `.Tests` — ссылку `→ ClaudeHomeServer.Tests.csproj` заменить на TestKit или
  TestKit.Host (по тому, что реально используется). Ссылку на Main на этом шаге **не** трогать —
  это шаг 2. `CopyNotesModuleForTests` в Knowledge.Tests сохранить.
- **Готово:** `grep -l ClaudeHomeServer.Tests.csproj backend/*/*.csproj` пуст; Сверка.

**1.4 Guard-тесты сборки** (~40 мин)
- В монолите рядом с `CorePackageReferenceGuardTests.cs` — три проверки:
  ни один `.csproj` не ссылается на `ClaudeHomeServer.Tests.csproj`; `ClaudeHomeServer.TestKit`
  не ссылается на Main; **каждый `*.Tests.csproj` под `backend/` есть в
  `ClaudeHomeServer.slnx`** (guard из раздела инвариантов разреза).
- Мутация каждой проверки: временно вернуть ссылку / убрать проект из slnx → красный, вернуть.
- **Готово (критерий шага 1 из разреза):** вывод `dotnet build` `ClaudeHomeServer.Notes.Tests`
  не содержит `ClaudeHomeServer.Tests` (строку вывода — в отчёт); полный slnx зелёный;
  Сверка; три мутации покраснели.

## Пачка C — Денис, ревью Глеба (шаг 2, отвязка от Main)

На каждый проект: убрать `ProjectReference` на `ClaudeHomeServer/ClaudeHomeServer.csproj`,
TestKit.Host → TestKit; собралось и зелёное — оставить, нет — вернуть и записать причину
(какой тип Main нужен, поднимает ли хост). Правило разреза: Main остаётся только там, где
реально поднимается хост.

**2.1** (~45 мин) Notes, Tasks, Memory, Knowledge, Spend, Turn, Dossiers `.Tests`.
**2.2** (~45 мин) Images, ImageEditor, Devices, CodeGraph, Git, Prompts, Video `.Tests`.
- **Готово по каждому:** строка таблицы «проект → Main нужен да/нет → почему (тип/хост)»;
  Сверка. На 2.2 — полный slnx зелёный, итоговая таблица 14 строк в отчёт задачи (это критерий
  шага 2 из разреза).

## Шаг 3 — разнос монолита. Одна вертикаль — один шаг

Каждый подшаг 3.x: новый `backend/ClaudeHomeServer.<Вертикаль>.Tests/…csproj` (ссылки —
продуктовая сборка вертикали + TestKit или TestKit.Host; на Main — только если тестам нужен
хост), `git mv` файлов, slnx, `InternalsVisibleTo`, Copy-таргеты модулей. Интеграционные
тесты, которым нужен весь хост, остаются в монолите — список оставленных с причиной в отчёт.
**Критерий каждого 3.x:** Сверка (сумма и набор имён = эталон), проект в slnx (guard 1.4
зелёный), новый проект зелёный, монолит зелёный.

### Пачка D — Денис, ревью Глеба (Llm, ~90 файлов — режу на три)
**3.1** (~45 мин) создать `ClaudeHomeServer.Llm.Tests`; перенести первую половину
`Services/ClaudeSession*Tests.cs` (по алфавиту до `ClaudeSessionLocalEngineBusyTests.cs`
включительно — 21 из 40). `InternalsVisibleTo` для Llm.Tests в `ClaudeHomeServer.Llm` (там уже стоит
комментарий про `ClaudeHomeServer.Tests`).
**3.2** (~45 мин) остаток `ClaudeSession*` (19 из 40) и прочие `Claude*Tests.cs` (9).
**3.3** (~45 мин) Provider/Fallback/Subscription/Transcript и остальное Llm в `Services/`
(~40 файлов: отбирать по namespace продуктового типа, а не по имени файла). Полный slnx.

### Пачка E — Денис, ревью Глеба
**3.4** (~45 мин) `ClaudeHomeServer.Personas.Tests` (~31 файл).
**3.5** (~30 мин) `ClaudeHomeServer.Team.Tests` (13 файлов; Team — папка Main, не отдельная
сборка: проект ссылается на Main, это ожидаемо).
**3.6** (~45 мин) `ClaudeHomeServer.Mcp.Tests`: `Services/Mcp/` (9) + `Services/Gateway/` (13).
Полный slnx.

### Пачки F и G — Денис, ревью Глеба (порядок — по таблице шага 0)
**3.7–3.14** — по одной вертикали из: ChatContext (10), Architecture (9), TestRuns (8),
LocalStats (`LocalStats/` + `Services/LocalStats/`), Watchdog (5), Deploy (`Deploy*Tests.cs`),
RemoteCommands (`Services/RemoteCommands/` + `RemoteProcessRunner*`), Telemetry (21).
Первые четыре по рангу — пачка F (3.7–3.10, полный slnx на 3.10), вторые четыре — пачка G
(полный slnx на 3.14).
Особые места:
- Architecture — динамический модуль: перенести `CopyArchitectureModuleForTests`.
- TestRuns — фикстуры `Services\TestRuns\Fixtures\**`; LocalStats — `Fixtures/LocalStats/**`.
- Watchdog/Deploy/RemoteCommands — если вертикаль окажется папкой Main, а не сборкой, проект
  ссылается на Main; это не нарушение.

**3.15** (~45 мин, пачка H — первым шагом, до 4.1) Перемер по методике шага 0: пик памяти и число testhost полного
прогона, wall-time трёх замеров. `-maxcpucount:6` в `Directory.Build.rsp` не трогать.
Дописать в `tests-by-module-baseline-2026-10.md` колонку «после шага 3». Если пик вырос
заметно — констатировать и эскалировать, не чинить в этой фиче.
- **Готово:** таблица до/после; полный slnx зелёный; Сверка.

## Пачка H — Денис, ревью Глеба (3.15 выше + шаг 4)

**4.1 `ClaudeHomeServer.Guards.Tests`** (~45 мин)
- `git mv` из `ClaudeHomeServer.Tests/Services/`: `SubsystemBoundaryTests.cs`,
  `SubsystemBoundaryCoverageTests.cs`, `IlBoundaryRegressionTests.cs`,
  `McpToolsetStabilityTests.cs`, `ProjectMapHygieneGuardTests.cs` + их помощник
  `BoundaryIlScanner.cs`. Статические конструкторы с `typeof(...).Assembly` — целиком, ни одной
  строки не потерять (сверить число строк форс-загрузки до/после в отчёте).
- Проверить, что относительные пути к `CLAUDE.md`/репозиторию у `ProjectMapHygieneGuardTests`
  резолвятся из нового bin.
- **Готово:** slnx, Сверка, проект зелёный.

**4.2 Мутационная проверка не-вакуумности** (~30 мин)
- Для каждого сторожа с форс-загрузкой: убрать одну сборку из статического конструктора →
  сторож обязан покраснеть → вернуть. Вывод красного прогона — в отчёт.
- **Готово:** по каждому из пяти сторожей — зафиксирован красный на мутации (или явное
  обоснование, почему у сторожа нет форс-загрузки); полный slnx зелёный.

## Пачка I — шаг 5, ревью Глеба

**5.1 Скрипт затронутых тест-проектов** — Марк (~60 мин)
- Скрипт по `git diff` → список `*.Tests.csproj` через граф `ProjectReference` (транзитивно,
  обратное замыкание), без внешних зависимостей. Место — рядом с прочими скриптами
  (`scripts/`), имя согласовать в отчёте.
- **Готово:** три прогона в отчёте — правка в `ClaudeHomeServer.Notes` → Notes.Tests (+ кто
  транзитивно); правка в Core → все тест-проекты; правка только в `docs/` → пусто.

**5.2 Замер `InternalsVisibleTo` на reference assemblies** — Марк (~30 мин)
- Замер: инкрементальная сборка зависимого тест-проекта после правки internal-члена против
  правки приватного тела метода в сборке с IVT. **Только констатировать**, не чинить.
- Строка про `-v q -clp:ErrorsOnly` для агентских сборок Bash — передать Софье в 5.3.
- **Готово:** числа замера в `tests-by-module-baseline-2026-10.md`.

**5.3 Правило в шаблоне задач исполнителей** — Софья (~30 мин, после 5.1: нужно имя скрипта)
- В шаблоне: на итерации — только затронутые проекты (скрипт 5.1), полный slnx — перед мержем;
  агентские сборки Bash — `-v q -clp:ErrorsOnly`.
- **Готово:** правило в шаблоне, ссылка на место в отчёте.

## Финал — Александр (`role=final`)

Ревью собранной фичи; пункт «CI-зелёный на ветке» проверяется здесь (пуш — по просьбе
человека).

## Бюджет контекста (Денис)

Шаги нарезаны под 30–60 тыс. токенов: файлы поимённо, перенос — `git mv` без чтения тел.
Самые рискованные по окну — 1.1/1.2 (разбор 26 хелперов) и 3.3 (отбор Llm-файлов по
namespace): исполнителю не читать файлы целиком, классифицировать по `using` и ошибкам
компилятора.

## За скобками

- `Integration.Tests` из остатка монолита — разрез называет его «впоследствии», в план не входит.
- Перенос новых guard'ов шага 1.4 в `Guards.Tests` — разрез их в шаге 4 не перечисляет.
- Починка роста памяти и IVT по итогам замеров — только констатация.
