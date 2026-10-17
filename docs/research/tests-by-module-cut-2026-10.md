# Разрез: тесты по модулям

> Вынесен из задачи `29f55d95` (role=`cut`, done) в `docs/research/`
> для оркестровки фичи `27dd9c17-d2da-4faa-8184-7e9cfd848584`.

## Факты (17.10)

- 24 тестовых проекта уже есть. Монолит `ClaudeHomeServer.Tests`: 746 .cs, ~185k строк; 210 файлов поднимают `WebApplicationFactory`.
- На монолит ссылаются 10 модульных тестовых проектов: Notes, Tasks, Memory, Knowledge, Spend, Turn, Dossiers, Images, ImageEditor, Devices. На Main ещё: CodeGraph, Git, Prompts, Video (и большинство из первых десяти). Итог: прогон любого из них собирает Main и весь монолит.
- Что модульные тесты берут из монолита: `Helpers/` (TestWebApplicationFactory, TestLauncherFactory, FakeHostEnvironment, FakeFeatureFlagGate, AgentReleaseFixture, TestConfig, TestFs, FakeDifyHttpClient, LocalMediaFakes…).
- В монолите лежат тесты вынесенных вертикалей: Llm (~49 `Claude*` + Provider/Fallback/Subscription/Transcript), Personas (27), Team (13), Mcp (13) + Services/Gateway (13), ChatContext (10), Architecture (9), TestRuns (8), LocalStats (7), Watchdog (5), Deploy, RemoteCommands, Telemetry (21). У Llm нет своего тестового проекта.

## Шаги (порядок обязателен)

**0. Базовая линия.** Замерить wall-time `dotnet test` (после `dotnet build-server shutdown` и инкрементально после правки одного .cs в вертикали) для Notes.Tests, Images.Tests, полного slnx; число testhost и пик памяти полного прогона. Записать в `docs/research/`.

**1. TestKit.**
- `ClaudeHomeServer.TestKit` → ссылки только Core (+ xunit/Moq/FA). Перенести фейки без зависимости от Main.
- `ClaudeHomeServer.TestKit.Host` → Main + TestKit: `TestWebApplicationFactory`, `TestLauncherFactory`, всё, что тянет типы Main.
- Монолит и модульные проекты ссылаются на TestKit*, ссылки `→ ClaudeHomeServer.Tests` удалить все.
- Guard-тест: ни один `.csproj` не ссылается на `ClaudeHomeServer.Tests.csproj`; TestKit не ссылает на Main.
- Готово: `dotnet test ClaudeHomeServer.Notes.Tests` не компилирует `ClaudeHomeServer.Tests` (видно по выводу сборки), все тесты зелёные, число тестов в полном прогоне не изменилось.

**2. Отвязка от Main.** Для каждого модульного тест-проекта: если Main нужен только ради хелперов — заменить на TestKit. Main остаётся только там, где реально поднимается хост. Готово: список «кому Main нужен и почему» в результате задачи.

**3. Разнос монолита.** Новые проекты по вертикалям, а не по папкам: `Llm.Tests` первым, затем Personas, Team, Mcp+Gateway, остальные — по частоте правок (`git log --since=1.month` по файлам вертикали). Интеграционные тесты, которым нужен весь хост, остаются в монолите (впоследствии — `Integration.Tests`). Одна вертикаль — одна задача-шаг.
- Готово на каждом шаге: сумма тестов до/после совпадает (сверка `dotnet test --list-tests`), CI-зелёный, проект добавлен в slnx.

**4. Guards.Tests.** `SubsystemBoundaryTests`, `SubsystemBoundaryCoverageTests`, `IlBoundaryRegressionTests`, `McpToolsetStabilityTests`, `ProjectMapHygieneGuardTests` — в отдельный проект. Статические конструкторы с `typeof(...).Assembly` перенести целиком; после переноса — мутационная проверка, что сторож не вакуумный (убрать одну сборку из форс-загрузки → должен покраснеть).

**5. Цикл разработки.** Скрипт «затронутые тестовые проекты по git diff» (граф ProjectReference, без внешних зависимостей) + правило в шаблоне задач исполнителей: на итерации — только затронутые, slnx — перед мержем. Агентские сборки Bash — `-v q -clp:ErrorsOnly`. Проверить влияние `InternalsVisibleTo` на reference assemblies замером (только констатировать, не чинить без данных).

## Инварианты под угрозой

- Сторожа границ проходят вакуумно без форс-загрузки сборок (ADR-014) — шаг 4.
- Копирование dll динамических модулей в bin тест-проекта (прецедент Notes→Knowledge.Tests): при переносе тестов, полагающихся на ModuleLoader, повторить `<Content>`/копирование.
- Память при полном прогоне: больше testhost; `-maxcpucount:6` в Directory.Build.rsp не трогать, пик перемерить после шага 3.
- CI (`ci.yml`) гоняет slnx — новые проекты обязаны попасть в slnx, иначе выпадут из CI молча. Добавить guard: каждый `*.Tests.csproj` в каталоге есть в slnx.
- Платформонезависимость тестов (CI на Linux) — при переносе не менять.
- `InternalsVisibleTo` в продуктовых сборках: новые тест-сборки надо добавить, иначе internal-тесты не скомпилируются.

## Исполнители

Шаги 0–4 — Денис (бэкенд, .NET), ревью Глеба на каждом шаге; шаг 5 — Марк (скрипт/CI) + Софья (правило в шаблоне задач). Финальное ревью — Александр. Исполнять последовательно в одном worktree: шаги трогают одни и те же `.csproj` и slnx.
