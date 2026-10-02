# Редактор видео (ClaudeHomeServer.VideoEditor)

> Этот файл — вынесенная часть корневого `CLAUDE.md`: он загружается, только когда идёт работа с файлами этой папки.

Сцены и фильм прямо в чате — проекта или личном вне проекта: **сцена** — один клип между кадром A и кадром B
с версиями-вариантами, поставщики `local` (своя видеокарта), fal и Higgsfield. **Фильм** (`.film`, сборка
ffmpeg без ИИ), швы к «Картинкам» и «Звуку» и агент `video-editor` — блоки 2 и 3, в этой папке их пока нет.
За флагом `video-editor`. Решение — [ADR-022](../../docs/adr/ADR-022-video-editor.md), там же
**раздел «Контракты»** с JSON-примерами каждого DTO: его сверяет `VideoContractExamplesTests`, правка контракта
после КТ-1 — отдельным коммитом `refactor(videoEditor): контракт …`.

**Форма — динамический модуль, как AudioEditor**: Main ссылается с `ReferenceOutputAssembly="false"`, dll
копируется в `modules/video-editor/` двумя целями (`Build` и `Publish`), `ModuleLoader` грузит её по записи
`DynamicModules` с ключом `videoeditor`. Ссылка — **только на Core**, своих пакетов нет
(`DynamicModulePackagesGuardTests`). Выключается `DynamicModules[videoeditor].Enabled=false` или
`Subsystems:VideoEditor:Enabled=false` — ручки тогда 404 (`VideoEditorDisabledTests`).

Состав: `Contracts/` (DTO, маршруты `VideoEditorRoutes`, события, коды `VideoEditorErrors`, `recordType`
ленты), `Controllers/` (проектные `api/projects/{id}/video-editor/*`, личные `api/video-editor/chats/{sessionId}/*`;
тело ручек общее — `VideoEditorEndpoints`, гейт — `VideoEditScopeGate`), `Scenes/` (нити, фокус, жизненный цикл
по шине, `VideoThreadRecovery`), `Jobs/` (исполнитель `VideoEditJobService`, следы в нитях и ленте
`VideoJobThreads`, чтение кадров `VideoFrameReader`, рабочая папка), `Engines/` (драйверы `IVideoEngine`:
`LocalVideoEngine`, `FalVideoEngine`, `HiggsfieldVideoEngine`), `Catalog/` (порядок поставщиков, «Авто», подбор
модели; каталоги fal и Higgsfield), `Prefs/`. Тесты — `ClaudeHomeServer.VideoEditor.Tests`; ручки,
отключаемость и сторожа — в `ClaudeHomeServer.Tests`.

**Область — `VideoEditScope.Of`**, единственная точка сопоставления «чат ↔ область»: у проекта — id проекта,
у личного чата — константа `personal` на владельца (не на чат: ветвление копирует нити). У личной области
`Project == null`, и всё, что читает диск проекта (кадры-файлы, папки, фильмы, сохранение, local), отказывает
ДО `RootPath`. Локальный проект (ADR-016) закрыт атрибутом `ProjectCapability(FileBound)` на проектном
контроллере и `ProjectCapabilities` в `LocalVideoEngine` — инлайнового `IsLocal` не заводить. Хранилища: нити
`data/video-threads/{owner}/{session}.json` (в бэкапе, TTL не чистит), рабочая папка
`data/video-editor/{owner}/{job}/{variant}/clip.mp4` (7 дней, клипы версий живых сцен держатся; исключена из
бэкапа константой `VideoEditorPaths.WorkspaceDirName` в Core), префы `data/video-editor-prefs/{owner}/{scope}.json`
(одна запись на область, режимов нет). `BackupSchema.Version` остаётся 9.

Инварианты:

- **Тихого перехода на другого поставщика нет.** Запуск — только по котировке и ровно на её паре «поставщик +
  модель»; отказ возвращает `RetryQuote` соседа, запускает его только человек. Явно выбранного поставщика не
  подменяем; «Авто» перебирает `VideoCatalog.AutoCandidates` — **единственную точку порядка**, local первым только
  при флаге владельца `local-media-default`.
- **Деньги — только quote → job**: котировка живёт 10 минут, одноразовая, выписана на сцену и пару «поставщик +
  модель». Трата пишется в момент **принятия** задачи поставщиком (`VideoProgress.Accepted`), на владельца, в
  своей валюте (`CostUsd` fal, `CostCredits` Higgsfield, 0 у local), с `Initiator`. Отчёт очереди локальной
  карты деньгами не считается; остановленное до принятия не списывается; «не списано» от поставщика после
  принятия — запись с минусом. Потолка трат агента нет (решение Андрея): «Потрачено на фильм» — отображение.
- **Драйвер не читает диск проекта**: кадры читает `VideoFrameReader` через `ProjectLinkGuard.ResolveInside` и
  отдаёт байтами. Кадр из «Картинок» (`kind=image`) хранится, но снять его нельзя до шва блока 2 —
  `frame_unavailable`.
- **Модуль пишет в проект только в `video/**` и `music/**`**; папка сцены вне них — `outside_allowed_folders`
  до записи. Перезаписи нет (кроме `.film` под ревизией — блок 2).
- **Повтор у Higgsfield — только до создания задания**; запуск с `use_unlim: false`, аргументы в `params`,
  каталог — живой `models_explore type:video` с кешем 30 минут, отбор по ролям `start_image` / `end_image`.
- **Версия клипа живёт столько же, сколько нить**: `VideoThreadStore.ReferencedJobs` удерживает рабочие
  задачи от чистки; запуск, оборванный рестартом, — `interrupted`, готовые версии остаются.
- `recordType` ленты (`VideoThreadRecordTypes`) не удалять и не переименовывать никогда.
- Потолки задач: 2 на владельца, 4 на инстанс, у local — одна съёмка за раз (GPU).

Граница блока 1: нет `Films/`, `Assembly/`, `Mcp/`, `Chats/`, `IVideoDsp`, `IMediaEvents`, `IImageFrameSource`,
`IAudioTrackSource` и постера версии — это блоки 2 и 3.
