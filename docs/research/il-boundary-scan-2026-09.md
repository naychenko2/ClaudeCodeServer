# Разведка перед IL-сторожем: масштаб красного прогона

**Дата:** 2026-09-06. **Ветка:** `research/il-boundary-scan` (от master `a0d0b48c`).
**Задача:** `cd6dd658` — подготовка к `8beee75e` («сторож границ: перевести с рефлексии на IL-скан»).

Это **разведка**. Сторожа не правились, allow-list не трогался, в `Boundaries` ничего не
добавлено — 37 существующих boundary-тестов зелёные и не изменены. Итог — цифры и разбивка.

## Инструмент

Одноразовый probe: [`backend/ClaudeHomeServer.Tests/Services/IlBoundaryScanProbe.cs`](../../backend/ClaudeHomeServer.Tests/Services/IlBoundaryScanProbe.cs)
— четыре `[Fact]`, помечен в шапке как разведочный, в постоянные сторожа не встроен.

**Зависимостей не добавлено.** `Mono.Cecil` не понадобился: `MethodBody.GetILAsByteArray()`
+ ручной обход опкодов + `Module.ResolveMember(token, typeArgs, methodArgs)` — всё из BCL.
Якоря файл:строка — из portable PDB через `System.Reflection.Metadata` (тоже in-box).
`ClaudeHomeServer.Core` не тронут.

Из тел методов probe собирает: declaring-типы вызванных методов и полей
(`call`/`callvirt`/`ldsfld`/`ldfld`/…), операнды `ldtoken`/`newobj`/`castclass`/`isinst`/`box`/
`newarr`/`initobj`, **generic-аргументы инстанцированных методов** и типы локальных переменных.
Обходятся ВСЕ методы всех типов дерева namespace, включая вложенные async-state-машины
(`Foo+<BarAsync>d__12`) и замыкания (`Foo+<>c__DisplayClass3_0`) — именно там живут вызовы
из тел async-методов.

Из выдачи вычитается то, что текущий рефлексионный сторож **уже** видит (та же
`CollectReferencedTypes`: поля, параметры ctor, публичные свойства и сигнатуры публичных
методов) — в отчёт идут только НОВЫЕ рёбра.

### Ограничение probe (честно)

Якорь для async-метода и лямбды указывает на **первую sequence-point state-машины**, то есть
на строку объявления метода, а не на точную строку вызова. Пример: все три ребра
`Llm → Prompts.*` из `ClaudeSession+<RunTurnAsync>d__154` показаны как
`ClaudeSession.cs:2357` — это начало `RunTurnAsync`, вызовы внутри. Для постоянного сторожа
это надо доводить до IL-offset → sequence-point (mapping есть в том же PDB), для разведки
хватило.

## Числа

| Показатель | Значение |
|---|---|
| Сборок в скане | 2 (`ClaudeHomeServer`, `ClaudeHomeServer.Core`), PDB найдено 2/2 |
| Типов просмотрено / методов | 2014 / 14 939 |
| **Всего НОВЫХ уникальных рёбер** | **134** |
| — по 27 подсистемным вертикалям (`SubsystemBoundaryTests`) | 115 |
| — по корню `Services` (`RootSubsystemBoundaryTests`) | 19 |
| Попаданий (не-уникальных, с повторами по методам) | 484 |

Разбивка 115 рёбер вертикалей по цели:

| Цель | Рёбер |
|---|---|
| `ClaudeHomeServer.Protocol.*` (WS/stored-сообщения) | **52** |
| корень `ClaudeHomeServer.Services` (top-level типы) | **38** |
| другая вертикаль (`Services.X` → `Services.Y`) | **14** |
| `ClaudeHomeServer.Telemetry.*` | 5 |
| third-party (`Yarp.*`, `SmartReader.*`) | 5 |
| `ClaudeHomeServer.Controllers.*` | 1 |

Все 19 корневых рёбер — это `root → Protocol.Stored*Message` (18) плюс
`root → WebPush.VapidHelper` (`PushService..ctor @ Services/PushService.cs:15`).
**Новых межвертикальных рёбер из корня нет ни одного.**

### Циклы

Граф строился как «объявленные в allow-list рёбра вертикаль→вертикаль (56 штук) + 14 новых
IL-рёбер». Новых ПАР вертикаль→вертикаль, которых не было в allow-list — **8**:

```
CodeGraph → Execution   (Execution.LocalProcessRunner)
CodeGraph → Knowledge   (Knowledge.WorkspaceKnowledgeStore)
Dossiers  → Backup      (Backup.BackupPaths)
Llm       → Git         (Git.GitService)
Llm       → Prompts     (Prompts.CoordinatorWriteGuard / ChatContextPrompts / VoicePrompts)
Llm       → Team        (Team.TeamImplementPrompts)
Turn      → Knowledge   (Knowledge.KnowledgeService)
Turn      → Prompts     (Prompts.OnboardingPrompts)
```

Пар-циклов на итоговом графе — **13**:

```
Dossiers ⇄ Knowledge     Llm ⇄ Prompts   ← НОВЫЙ (создан IL-видимостью)
Dossiers ⇄ Memory        Llm ⇄ Skills
Git ⇄ Llm  ← НОВЫЙ       Llm ⇄ Spend
Knowledge ⇄ Memory       Llm ⇄ Team      ← НОВЫЙ
Knowledge ⇄ Notes        Llm ⇄ Turn
Llm ⇄ Notes              Notes ⇄ Tasks
                         Team ⇄ Turn
```

**Главный вывод по циклам: IL-скан добавляет ровно 3 новых цикла (`Git ⇄ Llm`,
`Llm ⇄ Prompts`, `Llm ⇄ Team`), остальные 10 уже объявлены в allow-list и существуют
независимо от способа сканирования.** Волна 4 разрезала `Llm ⇄ Execution` — и по факту
это был единственный цикл, который блокировал переход; новых блокеров такого класса нет.

## Классификация рёбер по четырём корзинам

### 1. Ошибка адреса (лечится переносом типа) — 14 рёбер, один кластер

Четыре типа физически лежат в проекте-спине `ClaudeHomeServer.Core`, но в namespace
`ClaudeHomeServer.Services` (root) — и потому попадают под default-deny как «чужая
вертикаль», хотя по конструкции ADR-014 это и есть спина:

| Тип (в `ClaudeHomeServer.Core/Services/`) | Кто ссылается |
|---|---|
| `JsonFileStore` | **11 вертикалей**: Backup, Deploy, Desktop, Dossiers, Images, Knowledge, Llm, Memory, Notes, Tasks, Watchdog |
| `SsrfGuard` | Reader (`ReaderHttpHandlerFactory.cs:36`, `ReaderService.cs:204,296`) |
| `PermissionModeGuard` | Team (`TeamEnableService.cs:69`, `TeamStateService.cs:196`) |
| `TeamProtocolMarkers` | Team (`TeamTurnCompletionService.cs:165`) |

**Лечение одним движением, не 14:** признать спиной всё, что живёт в сборке
`ClaudeHomeServer.Core` (или перенести эти 4 файла в `Services.Composition`/новый
`Services.Core` namespace). Это снимает 14 рёбер из 134 (10%) и **обнуляет** самый
крикливый кластер. Прочие типы из `Core` (`Composition`, `Http`, `Mcp`) уже в
`SharedAllowedPrefixes` — эти четыре просто не доехали.

### 2. Шов (законная связь, надо объявить) — 96 рёбер

**2а. Protocol — 52 ребра.** Вертикаль публикует WS-событие: `SendAsync(new
XxxMessage(...))` в теле метода. Рефлексия видела эти типы только когда они переживали
`await` и попадали в поле state-машины; IL видит все. Самый толстый — Llm (27 рёбер:
`TextDeltaMessage`, `ToolUseMessage`, `ExitedMessage`, `PermissionRequestMessage`, …),
дальше Dossiers (4), Git (4), Knowledge (4), Desktop (4), Memory (5), Turn (1),
ProjectServices (2), Images (1), Watchdog (1), Tasks/Team/Prompts (0 новых).

Плюс 18 корневых `root → Protocol.Stored*Message` (`ChatHistoryService` и родня
разбирают транскрипт).

**Это НЕ 70 индивидуальных решений, а одно продуктовое:** `ClaudeHomeServer.Protocol` —
разделяемый контракт WS, ровно того же класса, что `ClaudeHomeServer.Models`. В волне 3
префикс `ClaudeHomeServer.Protocol` сознательно СНЯЛИ у Git/Spend/Watchdog/Terminal/Images,
чтобы ловить новые зависимости. С IL-сканом эта политика становится
неподъёмной: точечный allow-list разрастётся до ~70 записей и будет требовать правки на
каждое новое WS-событие. Решение (для `8beee75e`, не для этой разведки): либо вернуть
`ClaudeHomeServer.Protocol` в `SharedAllowedPrefixes`, либо резать протокол на
per-vertical поддеревья (`Protocol.Git`, `Protocol.Desktop`, …). Первое — одна строка,
второе — большая отдельная работа.

**2б. Telemetry — 5 рёбер.** `Knowledge`/`Memory` → `DifyErrorCategorizer` + `ServerMetrics`,
`Llm` → `TurnTelemetry` (`ClaudeSession.cs:4676`). Наблюдаемость — спина по определению;
лечится одной записью `ClaudeHomeServer.Telemetry` в `SharedAllowedPrefixes`.

**2в. Third-party — 5 рёбер.** `Reader → SmartReader.{Reader,Article}`,
`Modules → Yarp.ReverseProxy.{Forwarder,Transforms}`, `ProjectServices →
Yarp.ReverseProxy.Forwarder.HttpTransformer`. Прецедент уже есть (`AngleSharp` у Reader,
`Yarp.ReverseProxy.Configuration` у Modules) — дописать существующие префиксы до
`SmartReader` и `Yarp` целиком.

**2г. Корневые синглтоны-спина — 24 ребра** (38 минус 14 из корзины 1). Ровно те же
`FileService`/`PersonaManager`/`UserStore`/`SessionSummaryService`/`ModelTier`/… , которые
уже объявлены точечно у 20+ вертикалей — просто в новых местах:
`Git → FileService` (`GitService.cs:72`), `Git → PersonaManager` (`GitServerService.cs:212`),
`Llm → FileService`/`SessionSummaryService` (`ChatDigestService.cs:48`)/`PromptSnapshotStore`
(`ClaudeSession.cs:3907`)/`SpecialtyCatalog+Entry` (`SpecialtySettingsStore.cs:541`),
`Team → FileService` (`TeamPlanFileRenderer.cs:112`)/`SpecialtyCatalog`
(`TeamPlanningService.cs:146`)/`SessionModeConflictException`,
`Knowledge → FileMutationKind`, `Tasks/Modules → ModelTier`, `Modules → UserStore`
(`ModuleGatewayMiddleware.cs:27`), `TriggerSources → GroupChatRouter`
(`MentionTriggerSource.cs:18`), `Prompts → PersonaConsultantToolset`
(`OmcPersonaRouting.cs:114`), `ProjectServices → FileService`,
`Dossiers/Git/Turn → SessionChangedPaths`, `Images → ImageAssetHelper`
(`ImageBackfillService.cs:268`), `Watchdog → SessionMessagingService+SendOutcome+{Completed,
Queued,Running}` (в allow-list уже есть `+SendOutcome`, но не вложенные-в-вложенные
подтипы record-иерархии — это дописать одну строку с `+`-префиксом).

Механическая работа: дописать `AllowedExactNamespaces`. Ни одного архитектурного вопроса.

### 3. Инверсия стека (вертикаль тянет то, что должно быть в спине) — 3 ребра

| Ребро | Якорь | Почему инверсия |
|---|---|---|
| `Dossiers → Backup.BackupPaths` | `InstanceSecretsProvider.cs:46` | путь к секретам инстанса — инфраструктурный примитив, не логика Backup. Тот же класс, что `Deploy → Backup.InstanceLock`, у которого в CLAUDE.md уже висит TODO на шов |
| `ProjectIcons → Backup.{BackupCore,BackupContext}` | `ProjectIconMigration.cs:55` | снятие снимка перед миграцией. Ровно то, о чём предупреждает комментарий в `Boundaries.ProjectIcons` (в allow-list уже стоит `Backup.BackupResult` — возвращаемый тип, единственное, что видела рефлексия) |
| `CodeGraph → Execution.LocalProcessRunner` | `TypeScriptGraphProvider.cs:149` | `ResolveExecutable("node")` — резолв исполняемого файла, задача слоя Execution; статический вызов, уже описан в комментарии `Boundaries.CodeGraph` |

Все три — известные, названные в комментариях сторожа заранее. Лечение — вынос трёх
примитивов в спину (снимок бэкапа, путь секретов, резолв исполняемого файла), по образцу
`TranscriptRoots` из волны 4. Это единственная корзина, где нужны настоящие правки кода.

### 4. Цикл — 3 НОВЫХ ребра

| Цикл | Новое ребро | Обратное (уже объявлено) |
|---|---|---|
| `Git ⇄ Llm` | `Llm → Git.GitService`, `ClaudeSession.cs:2357` (внутри `RunTurnAsync`) | `Git → Llm` префикс (`ICheapTextRunner` в `GitAiService`) |
| `Llm ⇄ Prompts` | `Llm → Prompts.{CoordinatorWriteGuard, ChatContextPrompts, VoicePrompts}` — `ClaudeSession.cs:2101,2357` | `Prompts → Llm.Claude.SubagentRunPassport` |
| `Llm ⇄ Team` | `Llm → Team.TeamImplementPrompts`, `ClaudeSession.cs:2032` (`HandleControlRequestAsync`) | `Team → Llm` префикс |

Плюс `Turn → Knowledge.KnowledgeService` (`NotesRecallContributor.cs:44`,
`PersonaRecallContributor.cs:66`) и `CodeGraph → Knowledge.WorkspaceKnowledgeStore` — новые
межвертикальные, но **не циклы** (Knowledge на Turn/CodeGraph не смотрит).

Отдельно `Team → Controllers.TaskHubExtensions` (`TeamWaveService.cs:165,370,649`) —
**инверсия слоёв сервис→контроллер**, брат-двойник уже задокументированного
`Tasks → TaskHubExtensions` (в allow-list Tasks, с пометкой «⚠ ИНВЕРСИЯ СЛОЁВ, разбор —
этап 4»). Тот же extension-метод, тот же диагноз, разбор той же задачей.

## Производительность

Замер на этой машине (Debug, тёплый прогон, `Probe_Производительность`):

```
рефлексия по сигнатурам: 250 мс (49 058 ссылок)
IL-скан тел методов:     321 мс (13 872 метода, 281 963 ссылки)
загрузка PDB (2 шт):      20 мс
```

**Полный прогон probe с классификацией и якорями — 550 мс** (`Probe_ПечатаетРёбраИзТелМетодов`),
xunit отчитался о тесте за 592 мс.

**Вердикт: скан НЕ делает сторожа медленным.** IL добавляет ~320 мс к нынешним ~250 мс,
итого ~600 мс на всю сборку. Это на порядок дешевле любого `WebApplicationFactory`-теста и
даже не приближается к порогу `[Trait("Category","Slow")]`. Выносить IL-скан в отдельную
категорию **не нужно** — он живёт в каждом прогоне. Дополнительно: 37 boundary-тестов сейчас
идут 1.6 с суммарно (xunit-параметризация), IL-версия уложится в ~2 с.

Единственная оговорка: если сторож захочет ТОЧНЫЕ якоря (IL-offset → sequence-point вместо
первой точки метода), добавится разбор `SequencePoints` по каждому методу — это ещё
~100–200 мс и заметно больше кода. Для сообщения об ошибке хватает `Тип.Метод`, точная
строка нужна человеку, а не тесту.

## Вердикт по слепым пятнам из CLAUDE.md

Все проверены точечно (`Probe_СлепыеПятна`), с обходом вложенных state-машин:

| Слепое пятно | Вердикт | Где нашлось |
|---|---|---|
| `DeployHost.cs:47` → `GitService.IsGitRepo` | **ВИДИТ** | `DeployHost..ctor`, `<GitSnapshotAsync>d__5.MoveNext` |
| `DeployHost.cs:122` → `Backup.InstanceLock.TryAcquireDeploy()` | **ВИДИТ** | `DeployHost.TryLockAgent` |
| `ReaderService.cs:220,305-307` → `SsrfGuard.*` | **ВИДИТ** | `<ReadImageCoreAsync>d__14`, `<WalkToFinalResponseAsync>d__16` |
| `Memory → SessionSummaryService` (зафиксированный шов) | **ВИДИТ** | `<LearnSafeAsync>d__17.MoveNext` |
| `Execution → TranscriptRoots` (зафиксированный шов) | **ВИДИТ** | `DockerProcessRunner.EnsureProfile` |
| `Llm → SpecialtyCatalog` (зафиксированный шов) | **ВИДИТ** | 6 методов `SpecialtySettingsStore` |
| `Tasks → Controllers.TaskHubExtensions` (инверсия) | **ВИДИТ** | `<ProcessReminderAsync>d__17.MoveNext` |

**Probe не декоративный.** Все семь швов, которые прежние волны объявляли «вслепую, для
будущего IL-скана», IL-скан действительно находит — то есть эти записи в allow-list
перестанут быть мёртвыми комментариями и начнут работать как гейт.

Важная деталь для реализации: **все они, кроме двух, живут в ВЛОЖЕННЫХ типах**
(async-state-машины и `<>c__DisplayClass`). Сторож, который обойдёт только методы
самого класса, увидит 2 из 7 — то есть окажется декоративным. Обход nested-типов
обязателен (у нас он бесплатен: `Foo+<Bar>d__1` имеет тот же `Namespace`, что `Foo`,
и попадает в выборку `TypesUnder` автоматически).

## Вердикт по `sp.GetRequiredService<T>()`

**Задача `8beee75e` ошибалась: `GetRequiredService<T>` ВИДЕН.** Опровергнуто фактом.

Механика: `GetRequiredService<T>` — generic-метод, в IL стоит `call` с MethodSpec-токеном;
`Module.ResolveMember(token, typeArgs, methodArgs)` возвращает инстанцированный `MethodInfo`,
у которого `GetGenericArguments()` = `[T]`. Declaring-тип (`Microsoft.Extensions.*`) —
спина, а вот `T` — нет.

Доказательство из живого кода: ребро **`Modules → ClaudeHomeServer.Services.UserStore`**
в списке — единственный источник этой связи это
`ctx.RequestServices.GetRequiredService<UserStore>()` в
`ModuleGatewayMiddleware.cs:84` (probe показывает якорь `ModuleGatewayMiddleware+<Invoke>d__2
@ ModuleGatewayMiddleware.cs:27` — начало async-метода `Invoke`, вызов внутри).
`UserStore` не встречается в этом типе ни полем, ни параметром, ни в сигнатуре — только
как generic-аргумент DI-резолва.

Следствие для `8beee75e`: DI-форвардеры вида `services.AddSingleton<IFoo>(sp =>
sp.GetRequiredService<Foo>())` станут ВИДИМЫ сторожу. Это скорее плюс (`SpendStore`-инвариант
проверится сам), но нужно помнить: `Program.cs` живёт в namespace без `Services.*` и в выборку
не попадает, а вот форвардеры внутри `XxxSubsystem.Register` — попадают.

## Предлагаемая разбивка `8beee75e` по шагам

Порядок выбран так, чтобы каждый шаг заканчивался зелёным прогоном, а не «полкрасного».
Сначала снимаем шум (шаги 1–2, ~65% рёбер), только потом включаем скан.

| Шаг | Что | Рёбер снимает | Оценка |
|---|---|---|---|
| **1. Спина: Core + Telemetry + third-party** | `ClaudeHomeServer.Core` (или перенос 4 файлов) → спина; `ClaudeHomeServer.Telemetry` → `SharedAllowedPrefixes`; префиксы `SmartReader`/`Yarp` дописать | 24 (14+5+5) | **~1 ч**, правок кода ноль |
| **2. Решение по `Protocol`** | Развилка: вернуть префикс `ClaudeHomeServer.Protocol` в спину (1 строка) ИЛИ резать протокол по вертикалям. **Требует решения архитектора, не исполнителя.** | 70 (52+18) | **~1 ч** при возврате префикса; резать протокол — отдельная задача на 1–2 дня, в `8beee75e` не влезает |
| **3. Включить IL-скан, снять корневые швы** | Обход тел методов + nested-типов в обоих сторожах; дописать ~24 точечных `AllowedExactNamespaces` по факту прогона (включая `+SendOutcome+*`) | 24 | **~3 ч** (сам скан ~150 строк + механическая дописка) |
| **4. Объявить 3 новых цикла и 2 новых межвертикальных** | `Git ⇄ Llm`, `Llm ⇄ Prompts`, `Llm ⇄ Team` + `Turn → Knowledge`, `CodeGraph → Knowledge`. С обоснованием в комментарии и TODO на шов — как сделано для 10 уже объявленных циклов | 6 | **~1 ч** |
| **5. Инверсии стека — вынести примитивы в спину** | `BackupCore.Snapshot`/`BackupContext`/`BackupPaths` и `LocalProcessRunner.ResolveExecutable` → спина (по образцу `TranscriptRoots`); `Team/Tasks → TaskHubExtensions` — **не трогать**, у него уже есть отдельная задача этапа 4 | 3 (+1 отложено) | **~3 ч**, единственный шаг с правками рантайма |

**Итого 1–4 (без шага 2-«резать протокол» и без 5): ~6 ч.** Шаг 5 можно вынести
в отдельную задачу — он не блокирует включение сторожа (три ребра объявляются как швы
с TODO, ровно как `Deploy → Backup.InstanceLock` живёт сейчас).

**Реалистичная оценка `8beee75e`: один день, не неделя.** Риск «первый красный прогон
неподъёмный» не подтвердился: 134 ребра, из которых 94 (70%) снимаются четырьмя строками
в `SharedAllowedPrefixes`, 24 — механической дописью, и только 9 требуют думать.

## Что из плана `8beee75e` делать НЕ надо

1. **Не тащить `Mono.Cecil`.** BCL хватает: `GetILAsByteArray` + `Module.Resolve*`.
   Пакет в тестовый проект был разрешён — но он не нужен, а без него нет ни версии
   к сопровождению, ни риска для offline-сборки.
2. **Не выносить IL-скан в отдельную категорию тестов и не делать его «медленным».**
   Замер: +320 мс. Он живёт в каждом прогоне.
3. **Не готовить обходной путь для `sp.GetRequiredService<T>()`.** Он виден. Пункт плана
   «вероятно, останется невидимым» — снять.
4. **Не расписывать 70 точечных допусков под `Protocol`.** Это не 70 решений, а одно:
   протокол — спина или нет. Вручную перечислять WS-события = гарантированная поломка на
   каждом новом событии.
5. **Не разбирать `Team/Tasks → Controllers.TaskHubExtensions` в рамках этой задачи.**
   Инверсия «сервис → контроллер» уже названа и отложена на этап 4; сторож должен её
   объявить швом с TODO, а не чинить.
6. **Не расширять скан до точных якорей файл:строка на первой итерации.** Для падения
   теста достаточно `Тип.Метод`; sequence-point-mapping — украшение сообщения, стоит
   отдельного шага, если человеку окажется неудобно.
7. **Не менять политику «префикс `Protocol` снят ради ловли новых зависимостей»,
   пока не принято решение по п.4** — иначе шаги 2 и 3 будут переписывать друг друга.

## Полный список 134 рёбер

Машинная выдача (все якоря, до 4 примеров на ребро) воспроизводится прогоном:

```powershell
cd backend
dotnet test ClaudeHomeServer.Tests/ClaudeHomeServer.Tests.csproj `
  --filter "FullyQualifiedName~IlBoundaryScanProbe" -l "console;verbosity=detailed"
# отчёт также пишется в %TEMP%\il-boundary-probe.txt
```

Сгруппированный по корзинам список печатает `Probe_КлассификацияИЦиклы`; в разделах выше
процитированы все 14 межвертикальных, все 3 инверсии, все 3 новых цикла, весь кластер
ошибки адреса и представительная выборка корневых швов — то есть **все рёбра, требующие
решения**. Однотипные 70 `→ Protocol.*` и 5 `→ Telemetry.*` перечислены счётом, а не
поимённо: они закрываются одним решением каждое.
