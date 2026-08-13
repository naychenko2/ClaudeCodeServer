# Заметки и Знания (Dify RAG)

> Подробная документация подсистем «Заметки» и «Знания». Выжимка и инварианты —
> в [CLAUDE.md](../../CLAUDE.md). Читать перед правками в `NotesService`, `KnowledgeService`,
> `ProjectKnowledgeSyncService`, `features/notes/`, `features/knowledge/`.

## Заметки (Obsidian-совместимая база знаний)

Раздел «Заметки» (4-й хаб-таб): markdown-vault со связями
`[[wikilinks]]`, backlinks, unlinked mentions и графом. Заметки — настоящие `.md` файлы:
личный vault `data/notes/{userId}` + `notes/` проектов владельца (в дереве файлов папка
всегда видна первой как «Заметки»). Единый per-owner граф; изоляция — сервисный JWT
(как задачи).

- **Бэкенд**: [NotesService.cs](../../backend/ClaudeHomeServer/Services/NotesService.cs) —
  скан источников (кэш TTL 2с), парсер frontmatter/`[[links]]`/inline-`#тегов`, резолв
  с коллизиями (`[[Проект/Имя]]`), backlinks, unlinked mentions, авто-обновление входящих
  ссылок при переименовании, фрагменты `#заголовок`/`^блок` (ExtractFragment), шаблоны
  `templates/` ({{title}}/{{date}}/{{time}}), daily notes `Journal/YYYY-MM-DD.md`.
  [NotesController.cs](../../backend/ClaudeHomeServer/Controllers/NotesController.cs) —
  `/api/notes/*` (CRUD, resolve, attachment, graph, sources, caps, templates, daily,
  link-mention, semantic, reindex, suggest-links/tags, daily/summary); realtime —
  `NotesChangedMessage` в группу user_*.
- **Семантика (Dify RAG)**: [NotesKnowledgeService.cs](../../backend/ClaudeHomeServer/Services/NotesKnowledgeService.cs) —
  dataset per-owner «{username}:notes», дифф-синхронизация по хешам (дебаунс 15с на
  мутации), store `data/notes-knowledge.json`; `KnowledgeService.RetrieveAsync` —
  Dify retrieve. Без `Dify:ApiKey` — тихо выключено (`caps.semantic=false`).
- **ИИ-фичи**: [NotesAiService.cs](../../backend/ClaudeHomeServer/Services/NotesAiService.cs)
  (модель `Notes:AiModel`, дефолт haiku) — предложение связей, авто-теги, конспект дня;
  one-shot вызовы через общий [OneShotClaudeRunner.cs](../../backend/ClaudeHomeServer/Services/Llm/OneShotClaudeRunner.cs)
  (на нём же TaskAiService).
- **MCP**: [mcp/notes-server/index.js](../../mcp/notes-server/index.js) (без зависимостей) —
  notes_list/search/read/create/update/backlinks/graph/delete/semantic_search; подключение
  как tasks-server (env NOTES_API_URL/TOKEN/PROJECT_ID в BuildTurnMcpConfig + подсказка
  в системный промпт).
- **Фронт**: [features/notes/](../../frontend/src/features/notes/) — NotesPage (список по
  источникам, поиск с операторами `tag:`/`source:` и режимом «По смыслу», граф с
  drag-pin/фильтрами), NoteView (просмотр/правка, backlinks, упоминания с «Связать»,
  локальный граф, ✨-кнопки), NoteEditor — CodeMirror 6 c live preview (скрытие маркеров,
  интерактивные чекбоксы, Ctrl+клик по ссылке) и автокомплитом `[[`/`#`;
  [MarkdownViewer.tsx](../../frontend/src/components/MarkdownViewer.tsx) — рендер wikilinks
  (живая/призрачная/внешняя), embeds `![[…]]`, hover-preview; стор
  [lib/notes.ts](../../frontend/src/lib/notes.ts) (realtime notes_changed).

## Знания

Раздел-хаб «Знания» — единый менеджер баз знаний Dify, релевантных пользователю: личных
(`{username}:…` — заметок/проектов/памяти персон/самостоятельные) и публичных (глобальных,
`permission: all_team_members`). Dify — источник истины, отдельного JSON-стора нет: список
берётся из `KnowledgeService.ListDatasetsAsync()` и классифицируется по имени датасета + permission.

- **Бэкенд**: [KnowledgeBasesController.cs](../../backend/ClaudeHomeServer/Controllers/KnowledgeBasesController.cs)
  — `/api/knowledge/*` (list/get/create/delete, documents add(text/file)/delete, search).
  Классификация (`Classify`): `{user}:notes`→Заметки, `{user}:persona:{handle}`→Память персоны,
  `{user}:kb:{Title}`→Самостоятельная (deletable), `{user}:{project}`→Проект, `all_team_members`→Публичная
  (deletable). **Безопасность**: каждый `{id}`-эндпоинт резолвит датасет из `ListDatasetsAsync` и
  проверяет relevant текущему пользователю (иначе 403) — с общим Dify-ключом нельзя лезть в чужую
  only_me базу. Самостоятельные/публичные — создавать и удалять; привязанные (заметок/проектов/персон) —
  только управлять документами (DELETE базы → 403). Realtime — `KnowledgeChangedMessage` в группу `user_*`.
- **KnowledgeService** расширен: `CreateDatasetAsync(name, permission="only_me", description)`,
  `RetrieveAsync(…, searchMethod)` (`semantic_search` | `full_text_search` | гибрид с фолбэком),
  `DifyDatasetListItem` несёт `permission`/`document_count`/`created_at`/`description`.
- **Фронт**: [features/knowledge/](../../frontend/src/features/knowledge/) — KnowledgePage (сайдбар со
  сплиттером и общей шириной `cc_sidebar_width`, режимы pinned/collapsed/open, мобила list/item),
  KnowledgeList (группы Мои/Публичные + контекстное меню базы: правый клик/⋯), KnowledgeView
  (симметричный тулбар + документы + переключатель семантичный/полнотекстовый поиск),
  NewKnowledgeBaseDialog (видимость личная/публичная), AddDocumentDialog (текст/файл); стор
  [lib/knowledge.ts](../../frontend/src/lib/knowledge.ts) (realtime `knowledge_changed`). Без `Dify:ApiKey` —
  `GET /api/knowledge` → `{configured:false, items:[]}`, раздел показывает empty-state.
- **Синхронизация «файл проекта ↔ документ БЗ»** —
  [ProjectKnowledgeSyncService.cs](../../backend/ClaudeHomeServer/Services/ProjectKnowledgeSyncService.cs):
  карта `WorkspaceKnowledge.Docs` (relativePath → {DocId, Hash}), дифф по хешам с дебаунсом 15с —
  правка → переиндексация (delete+create с восстановлением тегов), удаление файла → удаление
  документа, перенос/переименование (файла и папки) → миграция ключей, перенос мимо API —
  детект по хешу среди хинтов ватчеров; индексация идемпотентна (повтор = обновление, дубли
  bootstrap'ом схлопываются). Триггеры: `FileService.OnMutated` (UI/API/OnlyOffice/upload),
  `FileWatcherService`, события хода Claude (`ProjectKnowledgeTurnSync`), сверка в GetStatus.
  Lifecycle-каскады: удаление проекта → датасет+wkStore (учёт шаринга RootPath) + notes-синк +
  проектные персоны; смена RootPath → `WorkspaceKnowledgeStore.Move`; rename проекта/handle
  персоны → best-effort `RenameDatasetAsync` (PATCH); удаление пользователя →
  [UserKnowledgeCascade.cs](../../backend/ClaudeHomeServer/Services/UserKnowledgeCascade.cs) (персоны +
  сторы + все датасеты `{username}:*`).
- **Восстановление документов, упавших при индексации** — реконсайлер, отдельный раздел ниже.
- **Неймспейс контура** (`Dify:Namespace`, дефолт пусто): Dev и Prod на одном Dify не пересекаются —
  непустой неймспейс (напр. `dev`) прозрачно префиксует имена датасетов (`dev:{user}:…`) и
  ограничивает листинг своим контуром; реализовано целиком внутри KnowledgeService, потребители
  работают с логическими именами. Прод без префикса скрывает чужие контуры через
  `Dify:ForeignNamespaces` (напр. `["dev"]`). Воркспейсы Dify через dataset-API недоступны
  (console-only, в CE урезаны) — поэтому изоляция именами.

## Восстановление error-документов Dify (реконсайлер)

**Проблема.** Индексация в Dify двухфазная: синхронный `create_by_text`/`create_by_file`
(документ принят, `indexing_status: "waiting"`) и асинхронная celery-задача (parse → split →
**embed** → completed/error). Падение провайдера эмбеддингов происходит во второй фазе — уже
после успешного HTTP-ответа. Все пути записи CCS писали `{DocId, Hash}` в локальный стор сразу
после create, то есть **принимали подтверждение приёма за подтверждение индексации**. Документ
в статусе `error` для дифф-синка невидим навсегда: хеш содержимого совпадает, синк его
пропускает. Наружу это выглядит как молчаливая деградация — recall персоны слепнет, поиск по
проекту неполный, никакой ошибки при этом нигде нет.

**Решение** — [KnowledgeIndexReconciler.cs](../../backend/ClaudeHomeServer/Services/Knowledge/KnowledgeIndexReconciler.cs)
(`BackgroundService`): нового механизма записи не появилось, замкнута уже существующая
идемпотентная петля дифф-синка «сброшенный хеш → пересоздать» (тот же приём, что в
`HandleRename`, `BootstrapDocsAsync`, `Backup/PostRestoreHook.ResetWorkspaceDocs`). Цикл одного
обхода цели:

1. `ListAllDocumentsAsync(datasetId, status: "error")` — судьба документов берётся из самого
   Dify. Фильтр страхуется клиентской проверкой `IndexingStatus`: старая версия могла
   проигнорировать `?status=`.
2. `ResolveAsync(docIds)` участника — чистое чтение: сопоставление DocId со стабильными
   **ключами записей** владельца (id записи памяти / noteId / относительный путь файла).
3. `InvalidateAsync(keys)` — сброс `Hash=""` **заменой** объекта `DocRef` (снапшот активного
   синка — поверхностная копия, правка поля изменила бы его на лету), затем `KickSync()` —
   штатный дебаунс-синк сам удалит error-документ и пересоздаст его из источника истины.

Состояние живёт в самом Dify: список error-документов и есть персистентная очередь ретраев —
**нового хранилища нет**, бэкап не затронут, рестарт CCS ничего не теряет.

**Участники** — [KnowledgeSyncParticipant.cs](../../backend/ClaudeHomeServer/Services/Knowledge/KnowledgeSyncParticipant.cs):
`PersonaMemoryService`, `TeamMemoryService`, `DossierStore`, `NotesKnowledgeService`,
`ProjectKnowledgeSyncService` — каждый отдаёт `ListTargets()` (датасет + владельцы + `Label`
вида `persona:{id}` / `team:{key}` / `dossiers:{key}` / `notes:{userId}` / `project:{path}`).
Чтение и запись разделены намеренно: без этого невозможны ни режим наблюдения, ни карантин.
`KickSync` нельзя звать под `_syncLock` владельца — дедлок с его же `SyncAsync`; порядок строго
`ResolveAsync` → `InvalidateAsync` (локи отпущены) → `KickSync`.

**healable / unhealable.** Сопоставленные со стором документы (healable) CCS пересоздаёт из
источника истины. Несопоставимые (unhealable) — «сироты» (delete при пересоздании
best-effort — если он упал, документ остался, а ссылок на него нет) и ручные документы
«Знаний» (текст/файл ушёл в Dify, у CCS источника нет). Их реконсайлер **не трогает** — только
считает и логирует; автозачистка сирот сознательно не делается (риск снести живое выше пользы).
Для ручных документов видимость закрывает UI: текст ошибки Dify рядом с бейджем статуса — в
разделе «Знания» (`KnowledgeView`, лечение только вручную: удалить и загрузить заново) и в
панели знаний проекта (`KnowledgePanel`, там у файловых документов есть кнопка повтора
индексации — delete + indexFile, прообраз того же приёма).

**Режимы** (`Dify:Reconcile:Mode`, дефолт **`off`** — dark launch: дев-стенд на копии боевого
`data/` не должен лечить боевые датасеты):

| Режим | Что делает |
|---|---|
| `off` | ни одного обращения к Dify |
| `observe` | читает, считает метрики, уведомляет владельца — сторы не мутирует (базовая цифра до первой мутации) |
| `heal` | плюс сброс хешей и пинок синка |

Прочие настройки секции: `Interval` (тик сервиса, 5 мин), `TargetInterval` (базовый период
обхода цели, 15 мин), `MaxPerCycle` (100 инвалидаций за тик на все цели), `MaxBackoff` (2 ч),
`MaxAttemptsPerEntry` (5). Режим читается на каждом тике, горячая смена **сбрасывает
backoff-состояние**: в `observe` число healable по определению не уменьшается, и без сброса к
моменту включения `heal` все цели доползли бы до `MaxBackoff`.

**Тонкости, из-за которых оно работает как задумано:**

- **Backoff per-target, не глобальный.** У каждой цели свой `NextDueAt`; healable не
  уменьшилось — её личный период ×2 до `MaxBackoff`, уменьшилось — сброс к базовому. Один
  залипший датасет не тормозит остальные. Unhealable в расчёт не входят — иначе неустранимый
  «пол» из сирот держал бы цель на `MaxBackoff` вечно.
- **`recovered` считается по исчезновению**, а не по попытке: ключ восстановлен, когда на
  следующем обходе он пропал из error-множества. Инкремент в момент инвалидации был бы
  счётчиком попыток и при лежащем провайдере раздувался бы кратно числу циклов.
- **Карантин «ядовитых» записей.** In-memory счётчик попыток по `Label:EntryKey` (по DocId
  нельзя — он меняется при каждом пересоздании). Классификация по тексту ошибки Dify
  (`IsTransientError`): «connection refused»/timeout/unavailable — временная, до
  `MaxAttemptsPerEntry` попыток; прочее (вероятно контентная) — до 2. Дальше ключ отбрасывается
  сразу после `ResolveAsync`, **до** мутации, и не жрёт `MaxPerCycle`. Карантин — до рестарта.
- **Упавшая цель не обрывает тик** (try/catch на каждую), тайминги — через `TimeProvider`,
  решение о следующем периоде — чистая функция `NextInterval` (тесты backoff не тайминговые).

**Видимость.** Метрики — `ccs.dify.error_documents` (гейдж, теги `dataset_type` и `healability`)
и `ccs.dify.documents_recovered` (счётчик), регистрируются в `Telemetry/GaugeRegistrar.cs` из
снапшота реконсайлера; подробности — [observability/overview.md](../observability/overview.md#метрики-реконсайлера-знаний).
Владельцам целей уходит уведомление (`NotificationService`, категория «Алерт», источник
«Знания», без push — индексация не пожар, будить телефон незачем),
когда healable-ошибки держатся ≥2 обходов подряд, не чаще раза в сутки на владельца — дедуп
in-memory, после рестарта уведомление может продублироваться (осознанный компромисс: стор ради
дедупа не окупается).

**Включение на бою** — операционный порядок: дождаться конца ручной переиндексации (две
лечилки одновременно удаляют то, что чинит вторая) → `Mode=observe`, снять базовые цифры →
бэкап `data` (смена режима не откатывает уже сброшенные хеши) → `Mode=heal`. Откат —
`Mode=observe` (видимость остаётся) или `off`, без деплоя.
