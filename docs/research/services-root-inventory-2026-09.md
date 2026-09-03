# Инвентарь корня `Services` и карта склеек перед волной 4

Статус: **разведка завершена, решения не приняты** — документ кладёт факты, вердикт по трём
спорным склейкам оставлен архитектору (раздел «Открытые вопросы»).
Дерево: `wt-flaky` (`C:/ClaudeHome/andrey/.worktrees/ai-home/wt-flaky`), ветка
`research/services-root`, база — `master` `b8ce8f85`.
Повод: этап 1 дорожной карты модульности
(`C:/Users/naych/.claude/plans/ClaudeCodeServer/modularity-roadmap-2026-09.md`), пункт
«Перед стартом — задача-разведка точных границ (склейки: BoardService→Tasks? и т.п.)».

Код не менялся: `git diff --stat` по `*.cs` пуст, единственный изменённый файл — этот.

## Как получены цифры

Все числа воспроизводимы. Команды выполнялись из корня worktree.

```bash
# Файлы и строки корня (только верхний уровень, без подпапок)
ls backend/ClaudeHomeServer/Services/*.cs | wc -l          # 123
wc -l backend/ClaudeHomeServer/Services/*.cs | tail -1     # 43482 total

# Коммиты по файлу за 3 месяца
git log --since="3 months ago" --oneline -- <файл> | wc -l

# Уникальные коммиты по группе (НЕ сумма по файлам — один коммит часто трогает несколько)
git log --since="3 months ago" --format=%H -- <файл1> <файл2> ... | sort -u | wc -l

# Метрика композиционного корня
wc -l backend/ClaudeHomeServer/Program.cs                          # 1544
grep -c 'builder\.Services\.Add' backend/ClaudeHomeServer/Program.cs  # 207
```

Граф связей строился не только глазами. Механика: из всех `.cs` бэкенда вырезались
комментарии и строковые литералы (блочные `/* */`, строчные `//`, обычные и verbatim
строки), затем собирались две таблицы — «какой тип где объявлен» и «какой тип в каком
файле упомянут», и соединялись в рёбра «владелец → владелец». Владелец файла — его
группа (для корня) либо подпапка `Services/X` / `Controllers` / `Protocol` / `Models` и
т.д. Тесты из графа исключены: они ссылаются на всё подряд и границу не определяют.

**Три поправки, без которых граф врёт** (каждая найдена и исправлена по ходу):

1. Комментарии дают ложные рёбра. В проекте принято ссылаться на соседний сервис в
   пояснении — `Services/Llm/Claude/ClaudeSession.cs:5091` упоминает
   `ExecutorStopClassifier` только в комментарии, кода там нет. Все рёбра ниже
   проверены на «не-комментарий» (`rg -n ... | grep -vE ':\s*//'`).
2. Одинаковые короткие имена типов (`Result`, `Model`, `Entry`, `Failure`, `Index`,
   `Pending`, `Release`, `Target`, `Source`, `Program`, `Snapshot`, `NotFound`) объявлены в
   нескольких местах и склеивают несвязанные владельцев. Исключены из графа списком.
3. Обращение к свойству неотличимо от имени типа. Ребро «Specialty ← `PromptSnapshotStore`
   по типу `Applied`» оказалось ложным: `PromptSnapshotStore.cs:76` пишет `draft.Applied`
   (свойство), а `Applied` как record объявлен в `SpecialtyTemplatesService.cs:18`. В карту
   связей это ребро НЕ вошло.

Поэтому ключевые рёбра (все входящие в группы из спины) перепроверены поштучно чтением
файла, а не только графом.

## Сводка

| | |
|---|---|
| Файлов в корне | **123** |
| Строк в корне | **43 482** |
| Групп-кандидатов выделено | **16** |
| Файлов в группах | 94 (27 054 строки, 62% корня) |
| Файлов вне групп (спина) | **29** (16 428 строк, 38% корня) |
| Из них `SessionManager.cs` | 10 111 строк — 62% всей спины и 23% всего корня |

Главный вывод по метрике: **цель «корень < ~15k» вынесением 16 групп не достигается**
(останется 16 428). Не хватает ~1,4k строк, и добрать их можно только переселением
хвоста спины в уже существующие вертикали — разбор в разделе «Прогноз метрики».

Второй вывод, важнее первого: **сегодняшний сторож границ корня ссылки внутри корня
не считает нарушением** (`RootSubsystemBoundaryTests`, правило `isPeerRoot`). Поэтому
цена шага волны 4 измеряется не строками, а числом рёбер «спина → группа», которые после
переезда придётся вносить в allow-list. У шести групп таких рёбер ноль — с них и стоит
начинать.

Расхождения с цифрами карты объяснимы составом групп и разобраны в конце раздела
«Инвентарь».

## Инвентарь корня

Столбцы: файл, строк, коммитов за 3 месяца **по этому файлу**, группа. Порядок — по
группам, внутри группы по убыванию строк.

| Файл | Строк | Коммитов/3мес | Группа |
|---|---:|---:|---|
| PersonaBindingsService.cs | 1135 | 34 | Persona |
| PersonasCrudService.cs | 1135 | 1 | Persona |
| PersonaManager.cs | 1066 | 47 | Persona |
| PersonaMemoryService.cs | 902 | 27 | Persona |
| PersonaAutomationService.cs | 532 | 21 | Persona |
| PersonaAgentFileSync.cs | 375 | 15 | Persona |
| PersonaMemoryAutolearnService.cs | 288 | 14 | Persona |
| PersonaPromptBuilder.cs | 192 | 9 | Persona |
| PersonaMemoryConsolidationService.cs | 178 | 8 | Persona |
| PersonaAgentFileGenerator.cs | 172 | 12 | Persona |
| PersonaConsultantToolset.cs | 157 | 10 | Persona |
| DefaultAssistantProvisioner.cs | 127 | 3 | Persona |
| PersonaAutomationState.cs | 110 | 2 | Persona |
| PersonaAccessPolicy.cs | 92 | 7 | Persona |
| PersonaAskService.cs | 75 | 8 | Persona |
| PersonaProjectBindingsMigration.cs | 62 | 1 | Persona |
| GroupChatRouter.cs | 41 | 2 | Persona |
| PersonaMemoryScorer.cs | 40 | 3 | Persona |
| TeamWaveService.cs | 1235 | 21 | Team |
| TeamMemoryService.cs | 862 | 17 | Team |
| TeamPlanningService.cs | 463 | 13 | Team |
| TeamMemoryAutolearnService.cs | 236 | 8 | Team |
| TeamMemoryConsolidationService.cs | 168 | 4 | Team |
| TeamPlanFileRenderer.cs | 128 | 2 | Team |
| TeamWaveWatchdog.cs | 42 | 3 | Team |
| TeamMemoryScorer.cs | 32 | 2 | Team |
| TeamStaffNotes.cs | 21 | 2 | Team |
| TeamImplementSetupException.cs | 15 | 1 | Team |
| TaskExecutionService.cs | 1329 | 53 | Task |
| TaskManager.cs | 624 | 35 | Task |
| TaskAiService.cs | 252 | 8 | Task |
| ExecutorStopClassifier.cs | 147 | 3 | Task |
| TaskSchedulerService.cs | 144 | 11 | Task |
| BoardService.cs | 111 | 1 | Task |
| DefectRules.cs | 75 | 3 | Task |
| TaskRecurrenceCalculator.cs | 60 | 1 | Task |
| TaskDueCalculator.cs | 39 | 1 | Task |
| NotesService.cs | 1205 | 15 | Notes |
| NotesService.Annotations.cs | 687 | 8 | Notes |
| NotesKnowledgeService.cs | 269 | 7 | Notes |
| NotesAiService.cs | 236 | 6 | Notes |
| NoteTaskSyncService.cs | 176 | 6 | Notes |
| NoteTaskParser.cs | 117 | 3 | Notes |
| NoteExpiryService.cs | 87 | 1 | Notes |
| ProjectServiceDiscovery.cs | 906 | 7 | ProjSvc |
| DevServerService.cs | 604 | 13 | ProjSvc |
| ExternalPreviewStore.cs | 182 | 1 | ProjSvc |
| ExternalPreviewRouter.cs | 159 | 3 | ProjSvc |
| ExternalPreviewProxy.cs | 124 | 2 | ProjSvc |
| PortOwnerLookup.cs | 113 | 1 | ProjSvc |
| DevServerPortMemory.cs | 110 | 1 | ProjSvc |
| LaunchConfigService.cs | 107 | 1 | ProjSvc |
| LoopbackResolver.cs | 96 | 1 | ProjSvc |
| SpecialtySettingsStore.cs | 1081 | 10 | Specialty |
| SpecialtyPromptPresets.cs | 476 | 3 | Specialty |
| SpecialtyCatalog.cs | 141 | 6 | Specialty |
| SpecialtyTemplatesService.cs | 45 | 1 | Specialty |
| SpecialtySettingsLayer.cs | 15 | 1 | Specialty |
| SkillsService.cs | 385 | 7 | Skills |
| SkillsCliService.cs | 313 | 3 | Skills |
| SkillSuggestService.cs | 226 | 5 | Skills |
| PluginSkillLocalizer.cs | 149 | 3 | Skills |
| SkillTranslationService.cs | 145 | 5 | Skills |
| SkillGenerationService.cs | 115 | 3 | Skills |
| SubscriptionOAuthUsageService.cs | 484 | 9 | Subscription |
| ClaudeSubscriptionPool.cs | 379 | 16 | Subscription |
| SubscriptionUsageWarmupService.cs | 255 | 9 | Subscription |
| SubscriptionWindowMismatchGuard.cs | 161 | 1 | Subscription |
| SubscriptionActivityTracker.cs | 24 | 1 | Subscription |
| WorkflowAgentParser.cs | 472 | 13 | Workflow |
| WorkflowWatcher.cs | 349 | 8 | Workflow |
| WorkflowMetaResolver.cs | 153 | 5 | Workflow |
| FalImageService.cs | 187 | 5 | ImgTail |
| GlifAccountService.cs | 185 | 1 | ImgTail |
| GlifCostParser.cs | 177 | 1 | ImgTail |
| FalCostService.cs | 121 | 1 | ImgTail |
| FalAccountService.cs | 111 | 1 | ImgTail |
| NotificationStore.cs | 333 | 6 | Push |
| NotificationService.cs | 175 | 5 | Push |
| PushService.cs | 125 | 2 | Push |
| PushSubscriptionStore.cs | 85 | 2 | Push |
| ChatArchiveService.cs | 180 | 3 | Chat |
| ChatTaskExtractionService.cs | 149 | 8 | Chat |
| ChatHistoryService.cs | 142 | 8 | Chat |
| ChatTurnLoggerService.cs | 61 | 3 | Chat |
| ChatExpiryService.cs | 58 | 2 | Chat |
| ChatHistoryPaginator.cs | 49 | 1 | Chat |
| ChangelogService.cs | 538 | 14 | Changelog |
| ChangelogWarmupService.cs | 92 | 1 | Changelog |
| TerminalService.cs | 450 | 12 | Terminal |
| OutputRingBuffer.cs | 61 | 2 | Terminal |
| DailyBriefingService.cs | 330 | 14 | Briefing |
| DocumentAiService.cs | 158 | 4 | DocAi |
| MarkitdownService.cs | 79 | 2 | DocAi |
| SessionManager.cs | 10111 | 354 | — (спина) |
| ProjectManager.cs | 666 | 63 | — (спина) |
| TurnAccumulator.cs | 659 | 36 | — (спина) |
| FileService.cs | 650 | 23 | — (спина) |
| UserStore.cs | 613 | 28 | — (спина) |
| FileWatcherService.cs | 392 | 7 | — (спина) |
| JwtService.cs | 345 | 7 | — (спина) |
| ModelCatalogService.cs | 302 | 18 | — (спина) |
| ProjectEventLogService.cs | 287 | 4 | — (спина) |
| PromptSnapshotStore.cs | 273 | 2 | — (спина) |
| ArchivedTranscriptStore.cs | 215 | 1 | — (спина) |
| SessionMessagingService.cs | 198 | 2 | — (спина) |
| SessionSummaryService.cs | 166 | 9 | — (спина) |
| TimestampedConsoleWriter.cs | 154 | 3 | — (спина) |
| AppSettingsService.cs | 139 | 8 | — (спина) |
| ProjectPresetService.cs | 135 | 3 | — (спина) |
| UsageService.cs | 131 | 4 | — (спина) |
| SessionFileIndex.cs | 122 | 2 | — (спина) |
| ProjectFileSessionsIndex.cs | 111 | 2 | — (спина) |
| ProjectGroupManager.cs | 109 | 2 | — (спина) |
| UserHomeResolver.cs | 106 | 1 | — (спина) |
| UnifiedSearchService.cs | 97 | 3 | — (спина) |
| PromptAuditService.cs | 95 | 1 | — (спина) |
| SyncService.cs | 93 | 2 | — (спина) |
| ImageAssetHelper.cs | 84 | 2 | — (спина) |
| ConnectionDiagnostics.cs | 60 | 2 | — (спина) |
| SessionContextResolver.cs | 57 | 1 | — (спина) |
| FeatureFlagService.cs | 50 | 3 | — (спина) |
| SessionModeConflictException.cs | 8 | 1 | — (спина) |

Итого 123 строки таблицы, сумма столбца «строк» = 43 482.

### Агрегат по группам

Коммиты здесь — **уникальные** (`git log --format=%H -- <все файлы группы> | sort -u | wc -l`),
а не сумма по файлам: один коммит волны обычно правит несколько файлов группы, и сумма
по файлам завышает теплоту в полтора-два раза.

| Группа | Файлов | Строк | Уник. коммитов/3мес | Регистраций в `Program.cs` | Контроллеров-потребителей |
|---|---:|---:|---:|---:|---:|
| Persona | 18 | 6679 | **173** | 10 | 12 |
| Team | 10 | 3202 | 54 | 2 | 2 |
| Task | 9 | 2781 | **94** | 4 | 5 |
| Notes | 7 | 2777 | 42 | 5 | 6 |
| ProjSvc | 9 | 2401 | 19 | 6 | 1 |
| Specialty | 5 | 1758 | 15 | 2 | 4 |
| Skills | 6 | 1333 | 18 | 6 | 2 |
| Subscription | 5 | 1303 | 30 | 6 | 1 |
| Workflow | 3 | 974 | 21 | 0 (статические) | 1 |
| ImgTail | 5 | 781 | 8 | 3 | 2 |
| Push | 4 | 718 | 11 | 4 | 2 |
| Chat | 6 | 639 | 25 | 3 | 4 |
| Changelog | 2 | 630 | 15 | 1 | 1 |
| Terminal | 2 | 511 | 13 | 1 | 0 (через Hub) |
| Briefing | 1 | 330 | 14 | 1 | 1 |
| DocAi | 2 | 237 | 5 | 2 | 1 |
| **Итого группы** | **94** | **27 054** | — | 52 | — |
| — (спина) | 29 | 16 428 | 491 | 23 | — |

### Сверка с цифрами дорожной карты

Расхождения не случайны — они ровно на файлах, которые я отнёс в группу шире, чем
маска имени. Это подтверждает, что карта считала по префиксу имени файла.

| Группа | Карта | Здесь | Разница объясняется |
|---|---|---|---|
| Task | 2448 | 2781 | +`BoardService` 111, +`DefectRules` 75, +`ExecutorStopClassifier` 147 = 333. `2781 − 333 = 2448` ✔ |
| Skills | 1184 | 1333 | +`PluginSkillLocalizer` 149. `1333 − 149 = 1184` ✔ |
| «Сервисы проекта» | ~2200 | 2401 | +`LaunchConfigService` 107, +`LoopbackResolver` 96 = 203. `2401 − 203 = 2198` ✔ |
| Persona | 6511 | 6679 | +`DefaultAssistantProvisioner` 127, +`GroupChatRouter` 41 = 168. `6679 − 168 = 6511` ✔ |
| Team | 3202 | 3202 | совпадает ✔ |
| Notes | 2777 | 2777 | совпадает ✔ |
| Subscription | 1303 | 1303 | совпадает ✔ |
| Specialty | 1758 | 1758 | совпадает ✔ |
| Chat | 639 | 639 | совпадает ✔ |
| Push | ~718 | 718 | совпадает ✔ |
| «Что нового» | ~630 | 630 | совпадает ✔ |
| Брифинг | ~330 | 330 | совпадает ✔ |
| Терминал | ~450 | 511 | карта считала только `TerminalService` 450; +`OutputRingBuffer` 61 |
| Документы AI | ~250 | 237 | `DocumentAiService` 158 + `MarkitdownService` 79 |
| Workflow | 963 | 974 | дрейф 11 строк с момента замера карты |

Счётчики коммитов у карты выше моих по группам, где я считал уникальные: Persona 139
против 173 (у меня группа шире), Task 82 против 94 (шире на 3 файла), Team 50 против 54.
Порядок теплоты между группами совпадает — на выводы это не влияет.

### Файлы вне групп — кандидаты в спину (29 файлов, 16 428 строк)

Сгруппированы по природе, а не по алфавиту.

Четыре корзины ниже не пересекаются и в сумме дают ровно 16 428
(`12 015 + 1 712 + 1 064 + 1 637`).

**Ядро сессии — трогать запрещено картой (12 015 строк):**
`SessionManager.cs` 10111, `TurnAccumulator.cs` 659, `PromptSnapshotStore.cs` 273,
`ArchivedTranscriptStore.cs` 215, `SessionMessagingService.cs` 198,
`SessionSummaryService.cs` 166, `SessionFileIndex.cs` 122, `ProjectFileSessionsIndex.cs` 111,
`PromptAuditService.cs` 95, `SessionContextResolver.cs` 57,
`SessionModeConflictException.cs` 8.

**Проектные сторы и файлы — трогать запрещено картой (1 712 строк):**
`ProjectManager.cs` 666, `FileService.cs` 650, `ProjectEventLogService.cs` 287,
`ProjectGroupManager.cs` 109. (`ProjectPresetService.cs` 135 — не здесь, он в
инфраструктуре ниже: его зависимость идёт на `Services.Docs`, а не на проектный стор.)

**Пользователи и доступ — трогать запрещено (1 064 строки):**
`UserStore.cs` 613, `JwtService.cs` 345, `UserHomeResolver.cs` 106.

**Общая инфраструктура, вопрос открыт (1 637 строк):**

| Файл | Строк | Кто использует | Замечание |
|---|---:|---|---|
| `FileWatcherService.cs` | 392 | `FileService`, `SessionHub`, `Knowledge`, `Mcp`, `TriggerSources`, `CodeGraph` | Настоящая спина: шесть независимых потребителей. |
| `ModelCatalogService.cs` | 302 | только `Program.cs`, 2 контроллера и **три файла `Services/Llm`** | Ни один потребитель не в корне. Кандидат в `Services.Llm`. |
| `TimestampedConsoleWriter.cs` | 154 | `Program.cs`, `McpTransportController`, `Services/Diagnostics/FileLog.cs` | Кандидат в `Services.Diagnostics`. |
| `AppSettingsService.cs` | 139 | 14 файлов по всем слоям | Настоящая спина. |
| `UsageService.cs` | 131 | `SessionManager` + **все 3 сервиса Subscription** | Пограничный: см. группу Subscription. |
| `UnifiedSearchService.cs` | 97 | `SearchController`, `Mcp/Http/WorkspaceToolset` | Сам зависит от Notes + Task. Разрез поперёк, см. ниже. |
| `SyncService.cs` | 93 | `FilesController`, `SyncController` | Изолирован, но своей группы нет (один файл). |
| `ImageAssetHelper.cs` | 84 | 2 контроллера, **`Services/Images` (2 файла)**, `PersonasCrudService` | Кандидат в `Services.Images`. |
| `ConnectionDiagnostics.cs` | 60 | `SessionHub` + **3 файла `Telemetry`** | Кандидат в `Telemetry`/`Diagnostics`. |
| `FeatureFlagService.cs` | 50 | 22 файла по всем слоям | Настоящая спина, самый широкий потребитель. |
| `ProjectPresetService.cs` | 135 | зависит от `Services.Docs` | Пограничный. |

**Спорные — в группы не отнесены сознательно:**

- `UnifiedSearchService.cs` (97) — конструктор `(NotesService, NotesKnowledgeService,
  TaskManager, ProjectManager)`. Это фасад поверх ДВУХ групп-кандидатов сразу. В Notes
  его класть нельзя (потянет Task), в Task нельзя (потянет Notes). Либо остаётся в спине
  как «сшивающий» тип, либо едет в `Services.Mcp` вслед за своим вторым потребителем
  `WorkspaceToolset`. Решение — за архитектором.
- `SessionFileIndex.cs` (122) — имя файла не совпадает с типом: внутри объявлен
  `SessionChangedPaths`, а типа `SessionFileIndex` не существует. При любом переносе
  файл ищется по имени и промахивается. Помечено, чтобы волна не потеряла его.
  Аналогично: `PersonaAutomationState.cs` содержит `AutomationStateStore` +
  `RuleRuntimeState` + `PersonaRuntimeState`; `ExternalPreviewProxy.cs` содержит
  `ExternalPreviewTransformer` + `ExternalPreviewResponses`.

## Группы: карта связей и вердикты

Обозначения: **спина** — файлы вне групп; **вертикаль** — существующая `Services/X`;
**группа** — другой кандидат волны 4.

Ключевой критерий вердикта — **сколько рёбер придётся внести в allow-list сторожей**
после переезда. Сегодня `RootSubsystemBoundaryTests` пропускает любые ссылки внутри корня
(правило `isPeerRoot`, строки 246–254). Как только группа уедет в `Services.X`, каждая
ссылка «спина → группа» станет нарушением и потребует явной записи.

### Persona — 18 файлов, 6679 строк, 173 коммита

**Исходящие.** Спина: `UserStore`, `ProjectManager`, `SessionManager`, `FileService`,
`AppSettingsService`, `UserHomeResolver`, `ProjectEventLogService`, `SessionSummaryService`.
Вертикали: `Memory` (19 типов — самая плотная связь), `Llm` (8), `Knowledge` (4),
`Prompts` (4), `Images` (4), `Dossiers` (2), `Execution`, `Mcp`, `Personas`, `Reader`,
`Tts` (по 1). Группы: `Specialty` (5 типов), `Notes` (3), `Push` (2), `Team` (1),
`Skills` (1), `Task` (1 — `TaskDueCalculator`), `ImgTail` (1).

**Входящие.** Спина: `SessionManager` — **8 типов** (`PersonaManager`,
`PersonaBindingsService`, `PersonaPromptBuilder`, `PersonaMemoryService`,
`PersonaAccessPolicy`, `PersonaAgentFileSync`, `PersonaConsultantToolset`,
`GroupChatRouter`) — все проверены как реальный код. Вертикали: `Mcp` (7), `Turn` (4),
`Memory` (3), `Knowledge` (2), `Desktop`, `Git`, `Images`, `Prompts`, `Spend`, `Tts`
(по 1). Контроллеров-потребителей — 12, больше всех.

**Отдельная сложность.** `PersonasCrudService.cs` инъектирует **семь DTO из
`ClaudeHomeServer.Controllers`** (`CreatePersonaRequest`, `UpdatePersonaRequest`,
`PersonaBindingRequest`, `AutomationRuleRequest`, `GenerateAvatarRequest`,
`SelectAvatarRequest`, `TeamMemberDraft`). Прецедент допуска есть — Knowledge получил
точечный allow-list на три DTO Controllers (`SubsystemBoundaryTests.cs:583-585`), — но
здесь их семь, и это самый большой такой список в проекте.

**Вердикт: не отделяется одним шагом.** Держится тремя вещами: (1) восемь типов, которые
дёргает `SessionManager` — а его карта трогать запрещает; (2) 19 типов из вертикали
`Memory`, причём `MemorySubsystem` уже регистрирует шесть Persona/Team-сервисов, живущих
в корне (см. врезку ниже); (3) семь DTO из Controllers. Группа самая горячая (173
коммита) и самая выгодная по строкам, но идти в неё первой — значит собрать в один шаг
все три сложности сразу. Дробить: сначала Specialty (Persona зависит от него, не
наоборот), потом сама Persona.

> **Врезка: часть корня уже принадлежит вертикали.** `Services/Memory/MemorySubsystem.cs`
> регистрирует шесть типов, физически лежащих в корне: `PersonaMemoryService`,
> `PersonaMemoryConsolidationService`, `PersonaMemoryAutolearnService`,
> `TeamMemoryService`, `TeamMemoryConsolidationService`, `TeamMemoryAutolearnService`
> (1 434 строки). Это единственный случай на весь корень (проверено перебором всех
> реализаций `IAppSubsystem`). Шапка `MemorySubsystem.cs` сама называет это временной
> аранжировкой: «тип переезжает только вместе со всеми своими потребителями и тестами —
> отдельная задача». Для волны 4 это значит: у Persona и Team **уже есть готовый адрес**
> для memory-части, и её можно увезти в `Services.Memory` отдельным дешёвым шагом, не
> дожидаясь решения по остальной группе.

### Team — 10 файлов, 3202 строки, 54 коммита

**Исходящие.** Спина: `SessionManager`, `ProjectManager`, `UserStore`, `FileService`,
`ProjectEventLogService`, `SessionSummaryService`. Вертикали: `Memory` (19 — тот же
плотный узел, что у Persona), `Llm` (3), `Knowledge` (2), `Prompts` (2), `Desktop`,
`Mcp`, `Reader` (по 1). Группы: `Task` (4 типа, включая `TaskExecutionService`),
`Persona` (1), `Notes` (1), `Push` (1), `Specialty` (1), `Chat` (1).

**Входящие.** Спина: `SessionManager` — 4 типа (`TeamPlanningService` — 14 упоминаний
кода, `TeamPlanFileRenderer`, `TeamStaffNotes`, `TeamImplementSetupException`).
Вертикали: `Memory` (3), `Dossiers`, `Knowledge`, `Mcp` (по 1). Контроллеров — 2.

**Ключевая деталь.** Цикл `Team → SessionManager → Team` разорван намеренно и явно:
`SessionManager.cs:6590` держит хук-свойство
`Func<Session, TeamImplementPlan, TeamWaveTrigger, Task>? TeamWaveStarter`, которое
`TeamWaveService` назначает при старте (вызовы — `SessionManager.cs:6343` и `:6501`).
Комментарий над ним (`:6585-6589`) прямо говорит: «назначается TeamWaveService при
старте — так разрывается цикл зависимостей (TaskExecutionService → SessionManager)».
Это готовый шов — Team уже спроектирована на отделение.

**Вердикт: отделяется с двумя швами.** Швы: (1) `Memory` — увозится вместе с
Persona-memory тем же приёмом (адрес готов); (2) `SessionManager` → 4 типа планирования
волн. Хук `TeamWaveStarter` уже развязал главное направление; остаётся внести 4 типа в
allow-list либо оформить их таким же интерфейсом-швом. Зависимость `Team → Task` (4 типа)
разрешается порядком: Task выносится раньше Team.

### Task — 9 файлов, 2781 строка, 94 коммита

**Исходящие.** Спина: `SessionManager`, `ProjectManager`, `UserStore`,
`ProjectEventLogService`, `ModelTier`/`ModelTiers`. Вертикали: `Llm` (6), `Execution` (3),
`Spend` (1), `Prompts` (1). Группы: `Notes` (3), `Persona` (3), `Push` (2),
`Specialty` (1), `Briefing` (1).

**Входящие.** Спина: `TaskManager` из `SessionContextResolver`, `SessionMessagingService`,
`UnifiedSearchService` — **один тип, три файла**. Вертикали: `Mcp` (7 типов — самый
плотный потребитель), `Dossiers` (1), `Spend` (1), `TriggerSources` (1). Группы:
`Team` (4), `Notes` (3), `Briefing` (2), `Persona` (1). Контроллеров — 5.

**Вердикт: отделяется с одним швом.** Шов ровно один и он именной — `TaskManager`
(разбор ниже). Всё остальное входящее идёт из вертикалей, где allow-list — штатный
механизм, и из групп волны 4, что решается порядком. Обратные зависимости Task на
Persona/Notes/Push при этом никуда не денутся и потребуют записей в его собственном
allow-list — но это уже нормальная жизнь вертикали, а не блокер границы.

### Notes — 7 файлов, 2777 строк, 42 коммита

**Исходящие.** Спина: `ProjectManager`, `UserStore`, `FileService`,
`ProjectEventLogService`. Вертикали: `Llm` (3), `Knowledge` (2 — `KnowledgeService`,
`IKnowledgeSyncParticipant`), `CodeGraph` (1), `Reader` (1). Группы: `Task` (3 типа —
`TaskManager`, `CreateTaskRequest`, `UpdateTaskRequest`, все в `NoteTaskSyncService`).

**Входящие.** Спина: `NotesKnowledgeService` ← `SessionManager`, `SessionSummaryService`,
`UnifiedSearchService`; `NotesService` ← `SessionSummaryService`, `UnifiedSearchService` —
**два типа**. Вертикали: `Mcp` (5 — целый тулсет `notes`), `Turn` (1 —
`NotesRecallContributor`), `Llm` (1 — `ChatDigestService`), `Knowledge` (1),
`TriggerSources` (1). Контроллеров — 6.

**Вердикт: отделяется с одним-двумя швами.** Швы: (1) `NotesKnowledgeService` +
`NotesService`, которые дёргает связка `SessionManager`/`SessionSummaryService`;
(2) `NoteTaskSyncService` — файл целиком про мост «заметка ↔ задача», и он тянет три
типа Task. Второй шов легко снимается порядком (Task раньше Notes) либо переносом
`NoteTaskSyncService` в Task, но тогда Task потянет Notes — направление выбирает
архитектор. Замечу: `Knowledge` уже держит `NotesKnowledgeService` в своём allow-list,
то есть половина работы по этому шву сделана.

### ProjSvc («Сервисы проекта») — 9 файлов, 2401 строка, 19 коммитов

**Исходящие.** Спина: `ProjectManager`, `FileService`, `JwtService`. Вертикали:
`Execution` (4 типа). Группы: `Terminal` (1 — `OutputRingBuffer`).

**Входящие из спины и вертикалей: НЕТ ни одного.** Единственные потребители — 13
инъекций в `PreviewController`, `Program.cs` и один `SessionHub`. Ни `SessionManager`,
ни одна вертикаль на группу не ссылаются.

**Вердикт: отделяется чисто.** Лучший кандидат волны по соотношению «объём / риск»:
2401 строка, ноль входящих рёбер из спины, ноль из вертикалей. Единственное — надо
решить судьбу `OutputRingBuffer`: он общий у `DevServerService` и `TerminalService`
(это прямо написано в его шапке). Варианты: увезти Terminal и ProjSvc одним шагом, либо
оставить `OutputRingBuffer` в спине как общий примитив. Группа холодная (19 коммитов) —
выигрыш по скорости правок будет скромным, но как первый шаг она обкатает конвейер
волны без риска.

### Specialty — 5 файлов, 1758 строк, 15 коммитов

**Исходящие.** Спина: `UserStore`, `ModelTier`. Вертикали: `Llm` (3), `Prompts` (1),
`Mcp` (1).

**Входящие.** **`Llm` — 4 типа из 5 файлов**: `SpecialtySettingsStore` нужен
`ModelAssignmentResolver`, `LocalActionRouter`, `PresetStore`, `GlmModelAliasMigration`;
`PresetStore` дополнительно берёт `ModelRoutePreset`, `PresetScope`,
`SpecialtySettingsLayer`. Спина: `SessionManager` → `SpecialtySettingsStore`.
Группы: `Persona` (5 типов), `Task` (1), `Team` (1). `Turn` (1). Контроллеров — 4.

**Вердикт: отделяется с одним, но толстым швом — `Llm`.** `SpecialtySettingsStore`
(1081 строка) — это по факту часть механики разрешения моделей, а не отдельная фича:
матрицы моделей по уровням и `DefaultTier` специальности (ADR-007 §2) читает именно
`ModelAssignmentResolver`. Честная альтернатива выделению — увезти Specialty целиком
в `Services.Llm`, где уже живут все его потребители. Тогда корень теряет 1758 строк
без создания новой вертикали и без единой записи в allow-list `Llm`. Рекомендую этот
вариант, но решение — за архитектором (вопрос №4).

### Skills — 6 файлов, 1333 строки, 18 коммитов

**Исходящие.** Спина: `ProjectManager`. Вертикали: `Execution` (3), `Llm` (2),
`Reader` (1). Группы: `Persona` (1 — `PersonaManager` в `SkillSuggestService`).

**Входящие.** Спина: нет прямых, кроме `SessionManager` → `SkillsService`.
Вертикали: `Llm` (1 — `LlmSessionAdapterFactory`), `Turn` (1 —
`PersonaLayerContributor`). Группы: `Persona` (1 — из `PersonaBindingsService`
и `PersonasCrudService`). Контроллеров — 2.

**Вердикт: отделяется с одним швом** — `SessionManager` → `SkillsService`. Взаимная
ссылка с Persona (Skills → `PersonaManager`, Persona → `SkillsService`) — двусторонняя
и потребует записи в обоих allow-list, но ни одна сторона другую не блокирует.

### Subscription — 5 файлов, 1303 строки, 30 коммитов

**Исходящие.** Спина: `UserStore`, **`UsageService`**. Вертикали: `Llm` (3),
`Execution` (1), `Knowledge` (1). Группы: `Push` (1 — `NotificationService`).

**Входящие.** Вертикали: **`Llm` — `ClaudeSubscriptionPool` в трёх файлах**
(`FallbackLlmSessionAdapter`, `LlmSessionAdapterFactory`, `OneShotClaudeRunner`) плюс
`SubscriptionActivityTracker` в `OneShotClaudeRunner`. Спина: `SessionManager` → те же
два типа. Контроллер — 1 (`UsageController`).

**Отдельный факт.** `UsageService.cs` (131 строка, в списке спины) используют
`SessionManager`, `ClaudeSubscriptionPool` и **все три остальных сервиса Subscription**
(`SubscriptionWindowMismatchGuard`, `SubscriptionUsageWarmupService`,
`SubscriptionOAuthUsageService`). За вычетом `SessionManager` — это файл группы,
а не спины.

**Вердикт: как у Specialty — отделяется, но естественный адрес не «новая вертикаль», а
`Services.Llm`.** Ротация подписок пула — часть механики фолбэка хода (CLAUDE.md,
раздел «LLM-провайдеры»), и её три главных потребителя уже лежат в `Llm`. Выделение
отдельной вертикали `Subscription` заставит `Llm` держать allow-list на неё, тогда как
перенос внутрь `Llm` не требует ни одной записи. Вопрос №4 к архитектору тот же.

### Workflow — 3 файла, 974 строки, 21 коммит

**Исходящие.** Только `Protocol` (5 типов) и `Reader` (1). Спины нет вовсе.

**Входящие.** **Только `Llm` и `Execution`**: `WorkflowAgentParser` ← `SubagentStreamWatcher`,
`TranscriptProbe`, `ClaudeSession`, `DockerProcessRunner`; `WorkflowWatcher` и
`WorkflowMetaResolver` ← `ClaudeSession`. Ни `SessionManager`, ни одна другая вертикаль.
Контроллер — 1 (`WorkflowController`).

**Особенность.** В DI не регистрируется вовсе — типы статические, `Program.cs:860-872`
только присваивает им статические `Log` и `AllowedRoots`. Переезд не затронет
композиционный корень (0 регистраций к переносу), но статическая инициализация
в `Program.cs` останется и её надо будет либо оставить, либо оформить методом подсистемы.

**Вердикт: отделяется чисто, но адрес — `Services.Llm`, а не своя вертикаль.**
Все до единого потребители кода — внутри `Llm`/`Execution`. Отдельная вертикаль
`Workflow` немедленно потребует allow-list в `Llm` на три типа; перенос внутрь `Llm`
не требует ничего. Тот же класс решения, что Specialty и Subscription.

### ImgTail (Fal/Glif) — 5 файлов, 781 строка, 8 коммитов

**Исходящие.** Вертикаль `Images` (3 типа). Спина: минимально.

**Входящие.** **Вертикаль `Images` — 2 типа**, причём `ImagesSubsystem.cs` ссылается на
все пять файлов группы (`FalImageService`, `FalCostService`, `FalAccountService`,
`GlifAccountService`). Спина: `SessionManager` → `FalCostService`, `GlifAccountService`,
`GlifCostParser` (3 типа, все — реальный код учёта трат). Контроллеров — 2
(`FalController`, `GlifController`).

**Вердикт: это не группа волны 4, это хвост уже существующей вертикали `Images`.**
Драйверы генераторов лежат в `Services/Images`, а их аккаунт-сервисы и парсеры цен
остались в корне. Перенос — механический, адрес известен, новая подсистема не нужна.
Один шов: три типа, которые читает `SessionManager` ради учёта стоимости
(`Services.Images` уже в `RootAllowedSubVerticalPrefixes`, то есть корню туда ссылаться
разрешено — шов закроется сам).

### Push / уведомления — 4 файла, 718 строк, 11 коммитов

**Исходящие.** Спина: `ProjectManager`, `JwtService`. Группы: `Persona` (1 —
`PersonaManager` в `NotificationService`).

**Входящие.** Вертикали: `Knowledge` (2), `Mcp` (2), `Deploy` (1), `Telemetry` (1).
Спина: `SessionSummaryService` → `NotificationService`. Группы: `Task` (2),
`Briefing` (2), `Persona` (2), `Team` (1), `Chat` (1), `Subscription` (1).
Контроллеров — 2.

**Важно.** `NotificationService` и `NotificationStore` **уже перечислены в
`ExcludedRootTypes`** сторожа корня (`RootSubsystemBoundaryTests.cs:154-155`) как
backbone-типы, «которые по построению держат ссылки на все вертикали». Комментарий там
же предупреждает: запись в `ExcludedRootTypes` — это исключение из проверки, то есть
слепое пятно.

**Вердикт: отделяется, но потребует правки самого сторожа.** Группу дёргают шесть
других групп волны 4 и четыре вертикали — это типичный «горизонтальный сервис». Вынос
в вертикаль означает: убрать два типа из `ExcludedRootTypes` и завести им нормальный
per-vertical allow-list со всеми потребителями. Работа скорее в тесте, чем в коде.
Порядок: после того, как выехали Task, Team, Persona, Briefing, Chat, Subscription —
иначе список потребителей придётся переписывать дважды.

### Chat — 6 файлов, 639 строк, 25 коммитов

**Исходящие.** Спина: `SessionManager`, `ProjectManager`, `UserStore`,
`SessionSummaryService`, `ProjectEventLogService`, `FeatureFlagService`. Вертикали:
`Llm` (2), `Reader` (1). Группы: `Persona` (1), `Push` (1), `Team` (1).

**Входящие.** Спина: `ChatHistoryService` ← `SessionManager`, `TurnAccumulator`,
`ProjectFileSessionsIndex`. Вертикали: `Llm` (1 — `LlmSessionAdapterFactory`),
`Turn` (1 — `PersonaRecallContributor`), `Spend` (2 файла). Контроллеров — 4.

**Вердикт: группа неоднородна, как единое целое не отделяется.** Шесть файлов делятся
на три разные вещи: `ChatHistoryService` (142) — стор истории, его читают
`SessionManager`, `TurnAccumulator`, `Spend`, `Turn`, `Llm`: это спина, и он уже стоит
в `ExcludedRootTypes` сторожа (строка 152); `ChatArchiveService` (180) +
`ChatExpiryService` (58) + `ChatHistoryPaginator` (49) — жизненный цикл чата, ближе
всего к `SessionManager`; `ChatTaskExtractionService` (149) — отдельная фича «задачи из
чата», зависит от Task-модели; `ChatTurnLoggerService` (61) — логирование в проектный
лог. Общее у них только префикс имени. Рекомендую группу расформировать, а не выносить.

### Changelog («Что нового») — 2 файла, 630 строк, 15 коммитов

**Исходящие.** Спина: `FileService`. Вертикали: `Llm` (2), `Reader` (1).
Группы: `Notes` (1).

**Входящие из спины и вертикалей: НЕТ.** Единственный потребитель — `HistoryController`
плюс две регистрации в `Program.cs`.

**Вердикт: отделяется чисто.** Наряду с ProjSvc — самый безопасный кандидат. Малый
объём (630), но нулевой риск.

### Terminal — 2 файла, 511 строк, 13 коммитов

**Исходящие.** Вертикаль `Execution` (4 типа). Спина: `ProjectManager`.

**Входящие.** Только `SessionHub` (2 типа) и `ProjSvc` (`OutputRingBuffer` из
`DevServerService`). Ни `SessionManager`, ни вертикалей.

**Вердикт: отделяется чисто, в паре с ProjSvc.** Единственная связь — общий
`OutputRingBuffer`. Логично увозить обе группы одним шагом (2912 строк суммарно) либо
оставить `OutputRingBuffer` общим примитивом в спине.

### Briefing — 1 файл, 330 строк, 14 коммитов

**Исходящие.** Спина: `ProjectManager`, `UserStore`, `AppSettingsService`,
`ProjectEventLogService`. Вертикали: `Llm` (2). Группы: `Task` (2 —
`TaskManager`, `TaskDueCalculator`), `Push` (2), `Notes` (1), `Persona` (1).

**Входящие.** Только `BriefingController` и `TaskSchedulerService` (группа Task).

**Вердикт: отделяется чисто по входящим, но это лист-потребитель.** Один файл, который
читает половину продукта и которого не читает почти никто. Ценность отдельной вертикали
из одного файла сомнительна; естественнее — доехать в Task (его единственный не-контроллерный
потребитель `TaskSchedulerService` там) либо остаться в корне до появления соседей.

### DocAi («Документы AI») — 2 файла, 237 строк, 5 коммитов

**Исходящие.** Вертикаль `Llm` (2). Группа `Notes` (1).

**Входящие.** `FilesController`, `Mcp` (1 тулсет), `Team` (1).

**Вердикт: отделяется чисто.** Самая маленькая группа; `MarkitdownService` — чистая
обёртка внешней утилиты без зависимостей вообще. Кандидат на присоединение к
`Services.Docs` (уже существует) вместо новой вертикали.

## Разбор трёх склеек из карты

### 1. `BoardService` — это Tasks или спина?

**Факты.** 111 строк, **1 коммит за 3 месяца** (самый холодный файл группы).
Конструктор: `BoardService(TaskManager tasks, SessionManager sessions, PersonaManager personas)`.
Потребители — исчерпывающе: `BoardController.cs`, `HomeController.cs`, `Program.cs`
(одна регистрация) и упоминание в комментарии `RootSubsystemBoundaryTests.cs:34`.
Больше нигде. Собственный комментарий файла (строка 7): «Данные — только чтение из
существующих TaskManager + SessionManager».

**Аргумент.** Это read-only проекция для одного экрана («доска агентов»): берёт задачи,
сопоставляет с живыми сессиями, раскладывает по колонкам. Своего состояния нет, стора
нет, никто из спины и ни одна вертикаль на него не ссылается. Он не «часть Tasks» в
смысле владения данными — он потребитель Tasks и Sessions одновременно, ровно как
`UnifiedSearchService` является потребителем Notes и Tasks.

**Наблюдение (не вердикт).** По зависимостям `BoardService` симметричен
`UnifiedSearchService`: оба — тонкие фасады над двумя чужими доменами, оба живут ради
одного контроллера. Их логично решить одним правилом, а не по отдельности. Если Tasks
станет вертикалью, `BoardService` внутри неё потянет `SessionManager` — но это
«вертикаль → спина», штатный допуск, а не нарушение. Если же оставить его в корне, он
потянет `TaskManager` из вертикали — «корень → вертикаль», и это уже потребует записи
в `RootAllowedSubVerticalPrefixes`. Второе дороже первого.

### 2. `TaskManager` — вертикаль Tasks или стор спины?

**Факты.** 624 строки, 35 коммитов. Потребители вне группы Task
(`rg -l -w TaskManager`, за вычетом тестов и комментариев):

| Потребитель | Слой | Характер использования |
|---|---|---|
| `Services/Dossiers/DossierCaptureService.cs:41,52` | вертикаль | поле + конструктор |
| `Services/Dossiers/DossierRecallService.cs:40` | вертикаль | опциональный параметр |
| `Services/Spend/SpendAnalyticsService.cs:66` | вертикаль | конструктор |
| `Services/TriggerSources/TaskStatusTriggerSource.cs:11` | вертикаль | конструктор |
| `Services/Mcp/Http/TasksToolset.cs:36` | вертикаль | конструктор |
| `Services/Mcp/Http/WorkspaceToolset.cs:70` | вертикаль | конструктор |
| `Services/SessionContextResolver.cs:21` | **спина** | конструктор |
| `Services/SessionMessagingService.cs:20` | **спина** | конструктор |
| `Services/UnifiedSearchService.cs:12` | **спина** | конструктор |
| `Services/NoteTaskSyncService.cs:14` | группа Notes | конструктор |
| `Services/DailyBriefingService.cs:22,43` | группа Briefing | поле + конструктор |
| `Controllers/*` | контроллеры | `Tasks`, `Projects`, `Models`, `Spend`, `Board` |

Плюс `Models/TaskItem.cs`, `Models/Session.cs` — только комментарии.

**Аргумент «за спину».** Три файла спины (`SessionContextResolver`,
`SessionMessagingService`, `UnifiedSearchService`) держат его в конструкторе. Он уже
внесён в `ExcludedRootTypes` сторожа корня (`RootSubsystemBoundaryTests.cs:151`) —
то есть де-факто признан backbone-типом. Кроме того, `TaskManager` в конструкторе
устанавливает **три статических резолвера на модели `Session`** (строки 32–36):
`Session.TaskSourceSessionResolver`, `Session.TaskDelegationDepthResolver`,
`Session.TaskDoneResolver`. Это делает его инициализацию частью запуска доменной модели
сессии, а не изолированной фичи.

**Аргумент «за вертикаль».** Шесть из одиннадцати не-контроллерных потребителей —
уже выделенные вертикали (`Dossiers`, `Spend`, `TriggerSources`, `Mcp` ×2), для которых
ссылка «вертикаль → корень Services» и так штатна. Три «спинных» потребителя тонкие:
`SessionContextResolver` — 57 строк, `SessionMessagingService` — 198,
`UnifiedSearchService` — 97; ни один не является ядром. `TaskManager` владеет
собственным стором (`data/tasks.json`) и событием `TaskCompleted` — это полноценный
домен, а не общий примитив.

**Факты в пользу «не спина» я считаю весомее, но вердикт — архитектору** (вопрос №1).
Отмечу отдельно: три статических резолвера на `Session` — это скрытая связь, которую
рефлексионные сторожа не видят (они читают поля и сигнатуры, не тела методов).
При выносе Tasks в вертикаль эта инициализация останется работать, но станет
невидимой связью «вертикаль → модель спины», и её стоит зафиксировать явно.

### 3. `TaskExecutionService` — Tasks, спина или разрез поперёк?

**Факты.** 1329 строк (второй по размеру в корне), **53 коммита** — горячий.
Конструктор принимает 17 зависимостей: `TaskManager`, `SessionManager`, `PersonaManager`,
`IHubContext<SessionHub>`, `PushService`, `NotesKnowledgeService`, `NotificationService`,
`ILogger`, `IConfiguration`, `Llm.UserModelTierResolver`, `Llm.LlmProviderRegistry`,
`PersonaAgentFileSync`, `Execution.ILauncherFactory`, `SpecialtySettingsStore`,
`Llm.ModelAssignmentResolver`, `Spend.TaskPromptMetricsStore`,
`Llm.Claude.SubagentRunLog`.

Это **пять групп волны 4 сразу** (Task, Persona, Push, Notes, Specialty) плюс четыре
вертикали (`Llm`, `Execution`, `Spend`) плюс спина (`SessionManager`) плюс `Hubs`.

Кто ссылается на него в реальном коде: `TaskManager`, `TaskSchedulerService`,
`TeamWaveService` (группа Team), `TasksController`, `SessionMessagesController`,
`ModelsController`, `Mcp/Http/TasksToolset`, `Program.cs`. Все остальные попадания
(`ClaudeSession`, `FallbackLlmSessionAdapter`, `ILlmSessionAdapter`,
`UserModelTierResolver`, `Memory/AutolearnGate`, `Protocol/ServerMessage`,
`Models/TaskItem`, `Models/Session`) — **только комментарии**, проверено построчно.

**Аргумент.** Это не стор задач и не общий примитив — это **оркестратор запуска
Claude-исполнителя**: собирает системный промпт постановки, поднимает сессию, следит за
её ходом через `SessionManager.OnSessionMessage`, разбирает обрывы сабагентов,
классифицирует терминальные отказы, доставляет доклад постановщику. По природе он ближе
к `SessionManager` и `Services.Llm`, чем к `TaskManager`.

Показательно: собственный комментарий `SessionManager.cs:6585-6589` описывает
`TeamWaveStarter` как способ разорвать «цикл зависимостей (TaskExecutionService →
SessionManager)» — то есть цикл между этим сервисом и ядром сессий уже известен и уже
обходится хуком.

**Наблюдение (не вердикт).** По зависимостям это **разрез поперёк**, а не член Tasks:
из 17 зависимостей на группу Task приходится одна (`TaskManager`). Если увезти его
внутрь вертикали Tasks, она немедленно получит исходящие рёбра на Persona, Push, Notes,
Specialty, `Llm`, `Execution`, `Spend`, `Hubs` и `SessionManager` — и станет самой
связанной вертикалью в проекте, что противоречит правилу «вертикаль не зависит от
другой вертикали напрямую». Если оставить в корне — корень сохранит 1329 горячих строк,
и цель по метрике станет ещё недостижимее.

Третий путь, который стоит взвесить: этот файл — кандидат не в волну 4, а в **этап 4**
(расщепление Session-ядра), где он естественно ложится в слой «приём хода / жизненный
цикл CLI-процесса» рядом с `SessionManager`. Решение — архитектору (вопрос №3).

## Предлагаемый порядок волны 4

Критерий сортировки — не только строки, а **число рёбер «спина → группа»**, которые
после переезда придётся вносить в allow-list, потому что именно они превращают переезд
в правку сторожей и чужих файлов. Группы с нулём таких рёбер идут первыми.

### Волна 4A — чистые (0 входящих из спины и вертикалей)

| # | Группа | Строк | Коммитов | Рёбер из спины | Обоснование |
|---|---|---:|---:|---:|---|
| 1 | **ProjSvc + Terminal** | 2912 | 32 | 0 | Самый крупный чистый кусок. Единственная связь — общий `OutputRingBuffer`, поэтому одним шагом. Обкатывает конвейер волны без риска. |
| 2 | **Changelog** | 630 | 15 | 0 | Ноль входящих, два файла, один контроллер. Дешевле не бывает. |
| 3 | **DocAi** | 237 | 5 | 0 | Присоединить к существующей `Services.Docs`, а не заводить вертикаль. |

Итог 4A: **3779 строк**, ни одной записи в allow-list сторожей.

### Волна 4B — переселение в существующие вертикали (новых подсистем не создаём)

Это не «выделение», а перенос хвостов к их владельцам. Дешевле выделения: адрес готов,
allow-list не растёт, `Program.cs` не трогается (регистрации уже в подсистемах или
переезжают туда же).

| # | Что | Куда | Строк | Обоснование |
|---|---|---|---:|---|
| 4 | **ImgTail** (5 файлов Fal/Glif) | `Services.Images` | 781 | `ImagesSubsystem` уже ссылается на все пять. `Services.Images` уже в allow-list корня. |
| 5 | **Workflow** (3 файла) | `Services.Llm` | 974 | Все потребители кода — `Llm`/`Execution`. 0 регистраций в DI. |
| 6 | **Memory-хвост Persona/Team** (6 файлов) | `Services.Memory` | 2634 | `MemorySubsystem` уже их регистрирует; его шапка сама называет текущее положение временным. |
| 7 | **Subscription** | `Services.Llm` | 1303 | Три главных потребителя — внутри `Llm`. Плюс `UsageService` (131) как файл группы. |
| 8 | **Specialty** | `Services.Llm` | 1758 | `SpecialtySettingsStore` — часть механики разрешения моделей (ADR-007 §2), 4 потребителя в `Llm`. |

Итог 4B: **7450 строк** (без `UsageService`), новых вертикалей ноль.

Состав шага 6 (`wc -l` по шести файлам, которые регистрирует `MemorySubsystem`):
Persona-часть 1368 (`PersonaMemoryService` 902, `PersonaMemoryAutolearnService` 288,
`PersonaMemoryConsolidationService` 178) + Team-часть 1266 (`TeamMemoryService` 862,
`TeamMemoryAutolearnService` 236, `TeamMemoryConsolidationService` 168) = **2634**.
Эти строки в шагах 12–13 ниже уже вычтены из Team и Persona, чтобы не считать дважды.

### Волна 4C — выделение с разрешением склеек

| # | Группа | Строк | Коммитов | Предусловие |
|---|---|---:|---:|---|
| 9 | **Task** | 2781 | 94 | Закрыт вопрос №1 (`TaskManager`) и №3 (`TaskExecutionService`). Идёт раньше Notes/Team/Briefing, потому что они на него ссылаются. |
| 10 | **Notes** | 2777 | 42 | После Task — снимается шов `NoteTaskSyncService`. Шов `NotesKnowledgeService` ← `SessionManager` остаётся. |
| 11 | **Skills** | 1333 | 18 | Один шов (`SessionManager` → `SkillsService`). Может идти раньше — порядок с Task/Notes не связан. |
| 12 | **Team** (без memory-хвоста) | 1936 | 54 | `3202 − 1266` (шаг 6). После Task (4 типа). Хук `TeamWaveStarter` уже готов. |
| 13 | **Persona** (без memory-хвоста) | 5311 | 173 | `6679 − 1368` (шаг 6). Последняя из крупных: после Specialty (8), Skills (11), Notes (10). Останется разобрать 8 типов `SessionManager` и 7 DTO Controllers. |
| 14 | **Push** | 718 | 11 | Строго последняя: её дёргают шесть групп волны 4. Требует правки `ExcludedRootTypes` сторожа. |

Итог 4C: **14 856 строк**. Суммарно 4A+4B+4C = `3779 + 7450 + 14 856` = **26 085**.
Это все 16 групп (27 054) минус Chat (639) и Briefing (330), которые выносить не
рекомендую: `26 085 + 969 = 27 054` ✔

### Не выносить

- **Chat** (639) — группа неоднородна, общий только префикс имени; расформировать
  (`ChatHistoryService` → спина, остальное — по адресам).
- **Briefing** (330) — один файл-лист; доехать вместе с Task либо оставить.
- **BoardService**, **UnifiedSearchService** — фасады над двумя доменами; решаются
  одним правилом вместе (вопрос №2).

## Прогноз метрики

Цель карты: **корень `Services` < ~15 000 строк**.

| Сценарий | Вынесено | Осталось в корне | Цель <15k |
|---|---:|---:|---|
| Сейчас | 0 | 43 482 | — |
| После 4A | 3 779 | 39 703 | нет |
| После 4A+4B | 11 229 | 32 253 | нет |
| После 4A+4B+4C (рекомендуемый план) | 26 085 | **17 397** | **нет, промах на 2 397** |
| Все 16 групп целиком (+ Chat 639, Briefing 330) | 27 054 | **16 428** | **нет, промах на 1 428** |

**Цель вынесением групп не достигается ни в одном сценарии.** Даже если выносить всё
подряд, включая Chat и Briefing (чего я не рекомендую), остаётся 16 428.

**Что именно остаётся** (разбор для нижней строки, 16 428 — то есть после выноса
вообще всех групп; в рекомендуемом плане к этому добавляются ещё Chat 639 и
Briefing 330):

- `SessionManager.cs` — **10 111 строк, 62% остатка**. Один файл выбирает две трети
  бюджета. Карта его трогать запрещает (это этап 4).
- Ядро сессии без `SessionManager` — 1 904.
- Проектные сторы и файлы — 1 712.
- Пользователи и доступ — 1 064.
- Общая инфраструктура — 1 637.

Проверка: `10 111 + 1 904 + 1 712 + 1 064 + 1 637 = 16 428` ✔ (состав корзин — в
разделе «Файлы вне групп»).

**Как цель всё же достижима без входа в этап 4.** Нужно добрать ~1,4k строк переселением
хвоста спины к его настоящим владельцам. Кандидаты с уже готовым адресом (у каждого
проверены потребители):

| Файл | Строк | Адрес | Почему |
|---|---:|---|---|
| `TurnAccumulator.cs` | 659 | `Services.Turn` | Потребители: `ClaudeSession`, `SessionManager`, `TaskExecutionService`, `Protocol` |
| `ModelCatalogService.cs` | 302 | `Services.Llm` | Все не-контроллерные потребители — в `Llm` |
| `PromptSnapshotStore.cs` | 273 | `Services.Turn` / `Prompts` | Потребители: `ClaudeSession`, `Turn/TurnEvents` |
| `ArchivedTranscriptStore.cs` | 215 | `Services.Llm.Claude` | Потребители: `ClaudeRuntimeSettings`, `SessionManager` |
| `TimestampedConsoleWriter.cs` | 154 | `Services.Diagnostics` | Уже используется из `Diagnostics/FileLog.cs` |
| `SessionFileIndex.cs` | 122 | `Services.Turn` | Тип внутри — `SessionChangedPaths` |
| `PromptAuditService.cs` | 95 | `Services.Prompts` | Потребитель — `Spend/TaskPromptMetricsStore` |
| `ImageAssetHelper.cs` | 84 | `Services.Images` | `ImagesSubsystem` и `ImageBackfillService` уже его зовут |
| `ConnectionDiagnostics.cs` | 60 | `Telemetry` | Три файла `Telemetry` — его потребители |
| **Итого** | **1 964** | | |

Арифметика по сценариям — важно, что одного этого переселения мало:

| Сценарий | Осталось | Цель <15k |
|---|---:|---|
| 4A+4B+4C + переселение 1 964 | `17 397 − 1 964 = 15 433` | **нет, промах на 433** |
| То же + Briefing (330) доезжает с Task | `15 433 − 330 = 15 103` | **нет, промах на 103** |
| То же + Chat расформирована (5 файлов из 6 уезжают, 497; `ChatHistoryService` 142 остаётся) | `15 103 − 497 = 14 606` | **да, запас 394** |

То есть **цель достижима без единой правки `SessionManager`**, но только если сделать
все три вещи: волны 4A–4C, переселение 1 964 строк хвоста спины и расформирование
группы Chat (вопрос №5). Запас при этом 394 строки — меньше одного среднего файла,
так что любой новый сервис в корне снова выбьет метрику за порог.

**Честная оговорка.** Даже при 14 606 корень на 69% состоит из одного файла
(`SessionManager` 10 111). Метрика «строк в корне» будет формально закрыта, но
заявленная цель модульности — «фича трогает ≤3 файла» — этим не достигается: пока
`SessionManager` держит 354 коммита за 3 месяца и 8 типов Persona, 4 типа Team,
по 2–3 типа Notes/Subscription/ImgTail, любая правка вертикали продолжит задевать его.
Реальный выигрыш даст этап 4, а не этап 1. Рекомендую зафиксировать это ожидание
до старта волны, чтобы результат не выглядел провалом на фоне закрытой метрики.

## Открытые вопросы к архитектору

1. **`TaskManager` — вертикаль или спина?** Факты: 6 из 11 не-контроллерных
   потребителей — уже выделенные вертикали; 3 — тонкие файлы спины (57/198/97 строк);
   тип уже стоит в `ExcludedRootTypes` сторожа. Дополнительно: он ставит три статических
   резолвера на `Models/Session` (`TaskManager.cs:32-36`) — скрытая связь, невидимая
   рефлексионным сторожам. Если вертикаль — эту инициализацию надо зафиксировать явно.

2. **`BoardService` и `UnifiedSearchService` — одно правило на двоих?** Оба —
   read-only фасады над двумя чужими доменами (Task+Session и Notes+Task), оба живут
   ради одного контроллера, у обоих нет своего состояния. Решать их по отдельности
   значит с высокой вероятностью получить два разных ответа на один вопрос.
   Замечу: оставить их в корне дороже, чем увезти, — «корень → вертикаль» требует записи
   в `RootAllowedSubVerticalPrefixes`, а «вертикаль → спина» штатно разрешено.

3. **`TaskExecutionService` — волна 4 или этап 4?** 1329 строк, 53 коммита,
   17 зависимостей, из них на Task — одна. Внутри вертикали Tasks он сделает её самой
   связанной в проекте (рёбра на Persona, Push, Notes, Specialty, Llm, Execution, Spend,
   Hubs, SessionManager). Цикл с `SessionManager` уже известен и обходится хуком
   `TeamWaveStarter`. Не логичнее ли отдать его в этап 4, в слой «приём хода»?

4. **Три группы (Specialty, Subscription, Workflow) — новые вертикали или переезд
   в `Services.Llm`?** У всех трёх главные потребители кода уже внутри `Llm`.
   Выделение отдельных вертикалей немедленно потребует allow-list в `Llm` на них;
   перенос внутрь — ни одной записи. Суммарно 4035 строк. Но это увеличит `Llm`
   с 18 496 до ~22,5k, то есть создаст в вертикали ту же проблему, от которой уходим
   в корне. Что дороже?

5. **Группа Chat — расформировать?** Шесть файлов делятся на четыре разные вещи,
   общее — только префикс имени. `ChatHistoryService` уже в `ExcludedRootTypes`.
   Подтверждаете ли вывод «группы Chat не существует»?

6. **Правило для `ExcludedRootTypes`.** Сегодня в списке 9 типов, из них
   `NotificationService`/`NotificationStore` (группа Push) и `ChatHistoryService`
   (группа Chat) — кандидаты волны. Комментарий в сторожа предупреждает, что запись
   там — слепое пятно. Нужно ли волне 4 отдельным критерием сокращать этот список,
   или он остаётся как есть до этапа 4?

7. **Ожидание по метрике.** Одними волнами 4A–4C цель «<15k» не берётся: остаётся
   17 397. Она закрывается только связкой «волны + переселение 1 964 строк хвоста
   спины + расформирование Chat» и даёт 14 606 с запасом всего 394 строки. Готовы ли
   зафиксировать это до старта — вместе с тем, что 69% корня останется
   `SessionManager`, а выигрыш по «фича ≤3 файла» придёт только на этапе 4?
