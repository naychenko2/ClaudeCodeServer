# Разрез: агент с тулсетом `image-editor` в личных чатах вне проекта (2026-09-29)

Задача трекера b0959d22. Продолжение [разреза личных чатов](image-editor-personal-chats-cut-2026-09.md)
(его п. 1.6 сознательно оставил агента без тулсета). Здесь — где решается состав MCP, безопасно ли
пустить сервер в личный чат и какой минимальный путь.

## 1. Где решается «ехать ли серверу `image-editor` в этот чат»

Решений **два слоя**, и оба сейчас режут личный чат:

| Слой | Место | Условие сейчас |
|---|---|---|
| Конфиг хода (есть ли сервер у CLI) | `SessionManager.BuildImageEditorContext` (`SessionManager.cs:1128`) | `ownerId`, **`session.ProjectId` не пуст**, флаг `image-editor`, тулсет в реестре |
| Состав `tools/list` самого сервера | `ImageEditorToolset.ToolsFor` → `TryResolve` (`ImageEditorToolset.cs:512`) | чат свой, **`ProjectId` не null**, флаг, проект свой — иначе `[]` |

Плюс **три точки сборки `LlmSessionContext`** в `SessionManager`:

- `StartNewSessionAsync` (`:4093`) — общая для проектных и личных чатов, зовёт `BuildImageEditorContext`
  и передаёт `ImageEditorMcp` (`:4147`);
- `EnsureProcessCoreAsync`, ветка **проекта** (`:5664`, `:5714`) — зовёт и передаёт;
- `EnsureProcessCoreAsync`, ветка **вне проекта** (`:5594–5650`) — **не зовёт и `ImageEditorMcp` не
  передаёт вовсе** (как и `LocalMediaMcp`, `CodeGraph`, `Architecture`).

Последнее — главная ловушка: если расширить только `BuildImageEditorContext`, личный чат получит
сервер на первом ходу (`StartNew`), а после перезапуска процесса (`EnsureProcess`) потеряет его. Два
пути дадут разную сигнатуру запуска (`ClaudeSession.BuildLaunchSignature`) и разный состав у одного
чата — ровно «No such tool available» посреди разговора.

## 2. Стабильность `tools/list`: свойство сессии, не хода

Все условия — свойства **сессии** (`ProjectId` фиксируется при создании и не меняется), **владельца**
(флаг) и **процесса** (модуль загружен, `ImageEditor:AgentLaunch`). Ни хода, ни фокуса, ни нитей в
цепочке нет: гейт делегирования (`IDelegatedTurnGate`) и лимит `MaxLaunchesPerTurn` живут в
`CallAsync`, не в составе. Сторожа `McpToolsetStabilityTests` (`СерверРедактораКартинок_…`,
`СоставToolsFor_Тулсетов_НеЧитаетСостояниеХода`) проверяют именно это.

Вывод: **снять условие `ProjectId` безопасно** для инварианта — чат не может «переехать» из личного в
проектный, значит состав у него на всю жизнь один. Разница между личным и проектным составом
(если её заводить) тоже законна — это свойство сессии.

Единственный побочный эффект — **однократный** перезапуск CLI у уже идущих личных чатов владельцев с
флагом `image-editor` на первом ходу после выкатки (в сигнатуре появится новый сервер). Это то же, что
при любом новом сервере или повороте флага, отдельных мер не требует.

## 3. Что уже готово на бэкенде (ИЛ-1/ИЛ-2 прошлой итерации)

Всё ниже тулсета уже ключуется **областью** (`ImageEditScope`), а не проектом:

- `ImageEditLaunchAssembler.LaunchAsync(ownerId, ImageEditScope, …)` — у личной области отказывает
  по образцам путями, персонажу, файлу исходника **до** `RootPath`;
- `ImageThreadService.*(ownerId, scopeKey, …)`, `IImageEditJobs.Get/Quote/Cancel(ownerId, scopeKey, …)`,
  `ImageEditSteps.Open(ownerId, scopeKey, …)` — строковый ключ, `"personal"` принимают;
- `ImageProjectPrefsService.Get(ownerId, ImageEditScope)` — у личной персонаж всегда `null`;
- трата пишет `ProjectId = null` (`ImageEditScope.ProjectIdOf`), потолки исполнителя — по владельцу;
- `DelegatedTurnGateAdapter` решает по `callerSessionId`, от проекта не зависит;
- драйвер `local` (`LocalImageEditor` над `ILocalImageMedia`) работает в личной области уже сейчас —
  человек рисует «Локальными моделями» из полосы. MCP `local-media` для этого **не нужен**.

Значит, новой бизнес-логики нет: работа — перевести тулсет и блок хвоста хода с `Project` на
`ImageEditScope` и развести проектные ветки.

## 4. Что менять

### 4.1. Main: `SessionManager`

1. `BuildImageEditorContext` — убрать `string.IsNullOrEmpty(session.ProjectId)` из условия
   (остаются `ownerId`, флаг, реестр).
2. Ветка «вне проекта» `EnsureProcessCoreAsync` — звать `BuildImageEditorContext(entry.Info.OwnerId,
   entry.Info)` и передать `ImageEditorMcp:` в `LlmSessionContext`, **синхронно с `StartNewSessionAsync`**.
3. Комментарии у `BuildImageEditorContext` и `ImageEditorMcpContext` (`LlmSessionContext.cs:165`) —
   «в любом чате владельца», а не «чата проекта».

### 4.2. Модуль: `ImageEditorToolset`

`TryResolve` отдаёт `ImageEditScope` вместо `Project`:

- чат свой (`GetOwned`) + флаг → `ProjectId` есть → проект свой → `ImageEditScope.Of(project)`;
  `ProjectId == null` → `ImageEditScope.Of(session)` (личная). Инлайнового `"personal"` не заводить —
  только `ImageEditScope.Of`.
- Все обращения `project.Id` → `scope.Key` (`FocusAsync`, `NewAsync`, `LaunchAsync`, `FailAsync`,
  `CancelAsync`, `DescribeState`), `ImageEditScope.Of(project)` в `LaunchAsync` → `scope`.
- Ветки «диск проекта» — отказ **до** `RootPath` при `scope.Project is null`:
  - `image_focus` с `file` → отказ «в личном чате нет файлов проекта: заведи новую картинку
    (image_new)»; с `threadId` — работает;
  - `image_new` с непустым `folder` → отказ; без папки — черновик (как ручка `open` c `draftFolder: ""`);
  - `SizeOf` — только при `scope.Project`; у личных нитей файла и так нет;
  - `LaunchAsync`, ветка `thread.File` (`ReadProjectImageAsync(project.RootPath, …)`) — под
    `scope.Project`; `references` и `character` отсечёт сборщик (уже умеет), но текст отказа
    стоит проверить на понятность агенту;
  - `HumanChoice` → `_prefs.Get(ownerId, scope)`.
- Сообщение отказа `TryResolve` — «Чат не найден…» вместо «Чат проекта не найден».

**Состав инструментов:** симметрично HTTP-маршруту личного чата. Сохранения у агента и так нет
(ADR-019, решение 1), персонажей как инструмента тоже нет. Все шесть (`image_state`, `image_focus`,
`image_new`, `image_generate`, `image_cancel`, `image_suggest_prompt`) включать можно: проектные
аргументы (`file`, `folder`, `references`, `character`) у личной области отказывают на вызове.

Рекомендация — **одинаковые схемы** в обоих видах чатов (меньше кода, одна поверхность для сторожей).
Альтернатива — урезанные схемы для личного чата (без `file`/`folder`/`references`/`character`):
модель реже ошибается аргументом, но это второй набор схем и развилка в `ToolsFor`. Законно (свойство
сессии), но я бы не делал в первой итерации — достаточно уточнить описания: «путь картинки
**проекта**; в чате вне проекта не используется».

### 4.3. Модуль: `ImageEditorStateContributor` (блок «Картинки в этом чате»)

- `IsEnabled`: убрать `ProjectId is not null`; `HasThreads` — без условия на проект;
  `HasSavedPrefs` — по области: у личной `store.Exists(ownerId, "personal")`. Сейчас
  `ImageProjectPrefsService.HasSaved(ownerId, projectId)` ищет проект через `IProjectManager` и для
  `"personal"` вернёт `false` — нужна перегрузка `HasSaved(ownerId, ImageEditScope)`.
- `BuildAsync`: область через `ImageEditScope.Of(session)` (для проекта — с проектом, чтобы
  персонаж проверялся на диске), `jobs.Get(ownerId, scope.Key, …)`, `prefs.Get(ownerId, scope)`.
- Тексты для личной области:
  - `DraftFolderText` — «человек сохранит её в корень проекта» → у личной «человек скачает её»;
  - `PriorityRule` — «в этом проекте» → «в этом чате»; упоминание `local-media` для личного чата
    лишнее (его там нет), но безвредно.

Сверка с утренней доработкой (`3e3a3724`, приоритет `image_generate`): она сделала правило
приоритета частью этого же блока и завязала его на «нити или сохранённый выбор». Новое решение
ничего не дублирует — оно лишь пускает блок в личный чат теми же условиями.

### 4.4. Встроенный системный промпт (`ProjectManager.BuiltInSystemPrompt`)

Сейчас там: «local-media есть только в чатах проектов… в чате вне проекта его нет по построению».
Именно по этой фразе агент в «Росинке» честно ответил «локально нельзя». Без блока хвоста хода
(личный чат без нитей и без сохранённого выбора — ровно этот случай) агент так и ответит, даже
получив `image_generate`.

Нужна одна оговорка, **условная** (промпт общий для всех, у многих флага нет): «Если в чате есть
инструменты image-editor, локальная картинка вне проекта рисуется через image_new → image_generate
с provider local». Промпт стабилен от хода к ходу — prefix cache не страдает (меняется один раз с
выкаткой).

### 4.5. Фронт (не обязателен для MVP)

`ComposerChip.tsx:31` — у личной области подсказка «пометки уйдут со следующим запуском в режиме
«Картинка»», и пометки агенту вложением там намеренно не уходят (CLAUDE.md модуля). С агентом их
можно выровнять с проектом. Отдельный пункт плана, в минимальный путь не входит.

### 4.6. Тесты, которые закрепляют старое поведение и пойдут под правку

- `ImageEditorMcpContextTests.cs:60` — «вне проекта → null» → «вне проекта → контекст есть»;
- `ImageEditorToolsetTests.cs:230` — `Ctx(tail: "outside")` пустой → 6 инструментов;
- `McpToolsetStabilityTests.СерверРедактораКартинок_…` — `body.Should().Contain("ProjectId")` станет
  ложным; заменить на `NotContain("ProjectId")` (сервер во всех чатах владельца) — остальные
  проверки (`_currentTurn`, `Thread`, `IsBusy`, `TurnDelegation`) остаются.

Новые сторожа:

1. **Паритет двух путей сборки контекста**: тест, что обе ветки `EnsureProcessCoreAsync` и
   `StartNewSessionAsync` передают `ImageEditorMcp:` (по образцу source-сторожей в
   `McpToolsetStabilityTests`) — ловит ловушку из п. 1.
2. Тулсет в личном чате: `image_new` без папки → черновик; `image_focus(file)` и `image_new(folder)`
   → отказ **без обращения к диску**; `image_generate` с `provider: local` → задача с ключом
   `personal`, трата с `ProjectId = null`, лимит 2 за ход и делегированный ход работают.
3. Изоляция: личный чат владельца A через хвост маршрута владельца B — пустой состав (уже есть для
   проектного, повторить для личного).
4. Блок хвоста хода в личном чате: есть при нитях и при сохранённом личном выборе, нет без обоих.

## 5. Риски

| Риск | Серьёзность | Мера |
|---|---|---|
| Ветка «вне проекта» `EnsureProcessCoreAsync` не передаёт сервер → состав прыгает между первым ходом и перезапуском | высокая, если забыть | правка 4.1.2 + сторож паритета |
| Разовый перезапуск CLI у идущих личных чатов после выкатки | низкая | штатно, как у любого нового сервера |
| Тулсет где-то дочитывает `project.RootPath` без проверки области → NRE вместо отказа | средняя | компилятор поможет (`Project?`), плюс тесты п. 4.6.2 |
| `HasSaved` по строке проекта молча `false` для `"personal"` → блок не появится | средняя | перегрузка по области |
| Агент на «нарисуй» в личном чате без блока по-прежнему уходит в fal | не баг, а поведение | блок и правило приоритета появляются только при сохранённом выборе или нитях — как в проекте; менять ли это для личных — решение Андрея (см. ниже) |
| Траты агента в личном чате | низкая | тот же путь, что у человека: `Initiator = Agent`, лимит 2/ход, потолки исполнителя, fail-closed на делегированном ходу |
| Персона `ReadOnly` получит `image_generate` в личном чате | как в проекте | сейчас RO-гейта у `image-editor` нет и в проектных чатах; отдельный вопрос, не этой задачи |

## 6. Минимальный путь (для плана)

1. `SessionManager`: снять `ProjectId` в `BuildImageEditorContext`, передать `ImageEditorMcp` в ветке
   «вне проекта» `EnsureProcessCoreAsync`; сторож паритета; правка `McpToolsetStabilityTests`.
2. `ImageEditorToolset`: `TryResolve` → `ImageEditScope`; `project.Id` → `scope.Key`; отказы проектных
   аргументов до `RootPath`; уточнить описания схем; тесты личного чата.
3. `ImageEditorStateContributor` + `ImageProjectPrefsService.HasSaved(ownerId, scope)`: блок в личном
   чате, тексты для личной области.
4. `BuiltInSystemPrompt`: условная оговорка про локальную генерацию через `image_generate` вне проекта.
5. Живая проверка: личный чат с флагом → «нарисуй локально» → `image_new` → `image_generate(provider
   local)` → версия в карточке, «Скачать» работает; перезапуск бэкенда → следующий ход без «No such
   tool available».
6. (Отдельно) фронт: пометки личного чата уходят агенту вложением, как в проекте.

Шаги 1–3 — один исполнитель бэкенда, шаг 4 — одна строка, но меняет общий системный промпт,
поэтому ревью обязательно.

## 7. Вопрос к Андрею

Нужен ли личному чату блок «Картинки в этом чате» (и правило приоритета `image_generate`) **всегда**
при флаге, а не только при нитях или сохранённом выборе? Тогда «нарисуй» без уточнений в личном чате
пойдёт через полосу (умолчание админа места `image-editor`), а не в fal. В проектах сейчас так не
сделано сознательно (без выбора человека — старое правило glif/fal).
