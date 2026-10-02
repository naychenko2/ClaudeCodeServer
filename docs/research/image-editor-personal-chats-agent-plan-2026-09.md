# План: тулсет `image-editor` у агента в личных чатах (2026-09-29)

Основание — [разрез](image-editor-personal-chats-agent-cut-2026-09.md) (задача b0959d22).
Исполнитель — Денис (бэкенд), работа в общем дереве master, worktree не нужен.

**Решение Андрея (закрыто):** в личном чате блок «Картинки в этом чате» есть в хвосте хода
**всегда** при флаге `image-editor` (и `ImageEditor:AgentLaunch=true`), даже без нитей и без
сохранённого выбора. В проектных чатах остаётся как было: только при `HasThreads || HasSaved`.
Развилка — по `ImageEditScope.Of(session).Project is null`, это свойство сессии.

## Шаг 1. Состав MCP — все точки сборки контекста одним коммитом

Файл: `backend/ClaudeHomeServer/Services/SessionManager.cs`.

1. `BuildImageEditorContext` (`:1128`) — убрать `string.IsNullOrEmpty(session.ProjectId)` из условия;
   остаются `ownerId`, флаг, тулсет в реестре. Комментарий над методом — «в любом чате владельца».
2. `EnsureProcessCoreAsync`, ветка «вне проекта» (`:5594–5650`) — завести
   `var imageEditorMcp = BuildImageEditorContext(entry.Info.OwnerId, entry.Info);`, передать
   `ImageEditorMcp: imageEditorMcp` в `LlmSessionContext` **и** `imageEditor: imageEditorMcp` в
   `HttpMcpActive(...)` (без второго при выключенных прочих HTTP-серверах сигнатура тоже разойдётся).
3. `StartNewSessionAsync` (`:4093`) и проектная ветка `EnsureProcessCoreAsync` (`:5664`) уже зовут —
   сверить, что после правки все три пути дают одинаковый контекст для личного чата.
4. `LlmSessionContext.cs:165` (`ImageEditorMcpContext`) — комментарий «в любом чате владельца».

Тесты (в том же коммите):
- `ImageEditorMcpContextTests.cs:60` — «вне проекта → null» переписать на «вне проекта → контекст есть».
- `McpToolsetStabilityTests` (`:381–383`) — `Contain("ProjectId")` → `NotContain("ProjectId")`,
  остальные проверки (`_currentTurn`, `Thread`, `IsBusy`, `TurnDelegation`) не трогать.
- **Новый сторож паритета** (source-сторож по образцу `McpToolsetStabilityTests`): в теле
  `StartNewSessionAsync` и в ОБЕИХ ветках `EnsureProcessCoreAsync` есть `ImageEditorMcp:` и
  `BuildImageEditorContext(`. Проверить мутацией: убрать передачу в ветке «вне проекта» — сторож
  краснеет.

Критерий: `dotnet build` зелёный, `dotnet test --filter "FullyQualifiedName~ImageEditorMcpContext|FullyQualifiedName~McpToolsetStability"` зелёный.

## Шаг 2. Тулсет: `Project` → `ImageEditScope`

Файл: `backend/ClaudeHomeServer.ImageEditor/Mcp/ImageEditorToolset.cs`.

1. `TryResolve` (`:512`) — `out ImageEditScope scope` вместо `out Project project`:
   чат свой (`GetOwned`) + флаг → при `ProjectId` проект свой → `ImageEditScope.Of(project)`,
   иначе `ImageEditScope.Of(session)`. Инлайнового `"personal"` не заводить. Текст отказа —
   «Чат не найден — инструменты редактора картинок недоступны.»
2. `ToolsFor` — тот же набор в обоих видах чатов (одинаковые схемы). В описаниях `file`,
   `folder`, `references`, `character` дописать «только в чате проекта».
3. Все `project.Id` → `scope.Key` (`FocusAsync`, `NewAsync`, `LaunchAsync`, `FailAsync`,
   `CancelAsync`, `DescribeState`), `ImageEditScope.Of(project)` в `LaunchAsync` → `scope`,
   `HumanChoice` → `_prefs.Get(ownerId, scope)`.
4. Обращения к диску (`:173`, `:209–212`, `:284`, `:472`) — под `scope.Project is { } project`,
   иначе отказ **до** `RootPath`:
   - `image_focus(file)` → «В чате вне проекта нет файлов проекта: заведи новую картинку (image_new).»
     С `threadId` — работает;
   - `image_new(folder)` непустой → отказ; без папки — черновик;
   - `SizeOf` и ветка `thread.File` в `LaunchAsync` — только при проекте;
   - `references`/`character` отсекает сборщик — проверить, что текст отказа понятен агенту.

Тесты (`ClaudeHomeServer.ImageEditor.Tests`):
- `ImageEditorToolsetTests.cs:230` — `Ctx(tail: "outside")` вместо пустого состава даёт 6 инструментов;
- личный чат: `image_new` без папки → черновик в нитях с ключом `personal`; `image_focus(file)` и
  `image_new(folder)` → отказ без обращения к диску; `image_generate(provider: local)` → задача
  с ключом `personal`, трата с `ProjectId = null`, лимит 2 за ход и отказ на делегированном ходу;
- изоляция: личный чат владельца A через токен владельца B — пустой состав.

Критерий: сборка и `--filter "FullyQualifiedName~ImageEditorToolset"` зелёные.

## Шаг 3. Блок «Картинки в этом чате» для личного чата

Файлы: `ImageEditor/Chats/ImageEditorStateContributor.cs`, `ImageEditor/Prefs/ImageProjectPrefs.cs`.

1. `ImageProjectPrefsService.HasSaved(ownerId, ImageEditScope scope)` — перегрузка: у личной
   области `store.Exists(ownerId, ImageEditScope.Personal)`; строковую версию оставить для проекта
   (сейчас на `"personal"` она молча `false`).
2. `IsEnabled`:
   - общее: `ownerId`, флаг;
   - проект (`ProjectId != null`): как сейчас — `HasThreads || HasSaved`;
   - личный чат: **всегда при `_agentLaunch`**; при `AgentLaunch=false` — как у проекта
     (блок без `image_generate` бесполезен и сослался бы на несуществующее).
   `HasThreads` — без условия на проект.
3. `BuildAsync` — область через `ImageEditScope.Of(session)` (у проекта — `Of(project)`, чтобы
   персонаж проверялся на диске), `jobs.Get(ownerId, scope.Key, …)`, `prefs.Get(ownerId, scope)`.
   Третья ветка для личного чата без нитей и без сохранённого выбора: новый `RenderEmpty`
   (заголовок, «В работе: ничего не выбрано», правило приоритета) — **без** `ChoiceText`/`ChoiceRule`:
   выбора человека нет, фраза «это выбор человека» была бы неправдой.
4. Тексты личной области:
   - `PriorityRule` → параметризовать: у личной «Картинки в этом чате рисуй через image_new →
     image_generate… Просьба нарисовать локально / на своей видеокарте / бесплатно — это
     image_generate с provider local» и без упоминания local-media (его там нет); проектный текст
     не менять (на него завязан утренний фикс `3e3a3724` и его тесты);
   - `DraftFolderText` у личной — «человек скачает её», а не «сохранит в корень проекта».

Тесты (`ImageProjectPrefsTests` или новый набор рядом): личный чат — блок есть без нитей и без
выбора (форма `RenderEmpty`), есть при нитях, есть при сохранённом личном выборе (с `ChoiceText`);
при `AgentLaunch=false` и без нитей/выбора — нет; проектный чат без нитей и выбора — по-прежнему
нет (регрессия утреннего решения). Мутация: вернуть `ProjectId is not null` в `IsEnabled` —
личные тесты краснеют.

## Шаг 4. Встроенный системный промпт

Файл: `backend/ClaudeHomeServer/Services/ProjectManager.cs:13`.

Фразу «local-media есть только в чатах проектов (результат ложится в папку проекта), в чате вне
проекта его нет по построению» дополнить: «…; вне проекта локальную картинку рисуют инструменты
image-editor, если в чате виден блок «Картинки в этом чате»». Промпт общий для всех — меняется
один раз с выкаткой, prefix cache не страдает. Правка общего промпта — **обязательное ревью**
(Глеб/Александр) до коммита.

## Шаг 5. Документация

- `backend/ClaudeHomeServer.ImageEditor/CLAUDE.md` — убрать пункт «Агента с тулсетом в личном
  чате нет осознанно», переписать инварианты «Состав `tools/list`…» («в любой чат владельца») и
  «Состояние редактора…» (личный чат — всегда при флаге и `AgentLaunch`, проект — нити или выбор).
- ADR-019 — строка в «Изменение 29.09»: агент в личном чате, развилка блока по виду чата.

## Шаг 6. Проверка

1. `dotnet build` всего решения; `dotnet test` наборов `ImageEditor*`, `McpToolsetStability*`,
   `SubsystemBoundary*`; перед коммитом — полный прогон.
2. **Живой прогон на дев-стенде** (флаг `image-editor` включён, в полосе личного чата ничего не
   настроено, `image-editor-prefs/{owner}/personal.json` отсутствует):
   - новый чат вне проекта → «нарисуй кота» → вызовы `image_new` → `image_generate`, НЕ glif/fal;
     версия появилась в карточке, «Скачать» работает;
   - тот же чат → «нарисуй собаку локально» → `image_generate` с `provider: local`, НЕ
     `local_generate_image`; задача в очереди ComfyUI, версия в карточке;
   - перезапуск бэкенда → следующий ход в том же чате без «No such tool available» (проверка
     ветки `EnsureProcessCoreAsync`);
   - контроль регрессии: чат проекта без нитей и без выбора → «нарисуй кота» идёт как раньше
     (glif/fal).
   Итог прогона — список фактических вызовов инструментов из ленты в отчёте.
3. Коммиты по шагам (1 — отдельным, он и есть «синхронная правка всех точек»), Conventional
   Commits по-русски.

## Вне этой задачи

- Фронт: пометки личного чата вложением агенту (`ComposerChip.tsx:31`) — отдельная задача.
- RO-гейт `image-editor` для персон `ReadOnly` — нет и в проектах, отдельный вопрос.
