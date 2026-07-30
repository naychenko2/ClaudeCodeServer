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
- **Неймспейс контура** (`Dify:Namespace`, дефолт пусто): Dev и Prod на одном Dify не пересекаются —
  непустой неймспейс (напр. `dev`) прозрачно префиксует имена датасетов (`dev:{user}:…`) и
  ограничивает листинг своим контуром; реализовано целиком внутри KnowledgeService, потребители
  работают с логическими именами. Прод без префикса скрывает чужие контуры через
  `Dify:ForeignNamespaces` (напр. `["dev"]`). Воркспейсы Dify через dataset-API недоступны
  (console-only, в CE урезаны) — поэтому изоляция именами.
