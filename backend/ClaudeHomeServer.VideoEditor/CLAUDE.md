# Редактор видео (ClaudeHomeServer.VideoEditor)

> Этот файл — вынесенная часть корневого `CLAUDE.md`: он загружается, только когда идёт работа с файлами этой папки.

Сцены и фильм прямо в чате — проекта или личном вне проекта: **сцена** — один клип между кадром A и кадром B
с версиями-вариантами, поставщики `local` (своя видеокарта), fal и Higgsfield. **Фильм** (`.film`, сборка
ffmpeg без ИИ) и швы к «Картинкам» и «Звуку» — блок 2 (`Films/`, `Assembly/`); агент `video-editor` — блок 3 (`Mcp/`, `Chats/`).
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

**Фильм (блок 2, ADR-022 §2–§4).** `Films/`: формат `.film` (`FilmFormat`: `cuts = items − 1`, неизвестная `schema` —
только чтение), атомарная запись под ревизией (`FilmStore`: temp + rename, ревизия — хеш содержимого, чужая правка —
`409 revision_conflict`; единственное место, где модуль переписывает существующий файл проекта), патч
(`FilmPatcher`: `add`/`remove`/`move`/`cut`/`trim`/`music`, всё или ничего), `FilmService` (список, состояние, патч,
пометки, траты), «Сохранить сцену» (`FilmSceneSaver`: `scene-NN.mp4` → `.v2` → … `CreateNew`, кадры-нити в `кадры/`,
сама встаёт в фильм), подписки на соседей (`FilmFrameFollower`, `FilmMusicComposer`, `FilmMediaSubscriber`),
регистрация одной строкой `AddFilms()` (`FilmRegistration`). `Assembly/`: `FilmAssembler` и `FilmBuildRegistry`.
Ручки — `FilmController` (проектные) и `PersonalFilmController` (те же маршруты у личного чата отвечают
`personal_scope_no_films`).

- **Писать можно только в `video/**` и `music/**`**, каждый путь — через `FilmService.ResolveInside`
  (`ProjectLinkGuard.ResolveInside`): символическая ссылка наружу — `outside_allowed_folders`. Фильм — `video/<папка>/*.film`.
- **«Устарел» и «● обновлена» не хранятся**: `FilmStaleness` считает их из `builds[].sourceHash` (отпечаток входов:
  aspect, строки с размером файла, склейки, музыка) и из mtime файла сцены против времени последней сборки. «Переснять»
  у строки — из нити сцены (`VideoStale`), чей `SavedFiles` содержит файл строки.
- **Состояние вне файла** — `data/video-films/{owner}/{projectId}/{filmKey}.json` (`FilmSideStore`, `filmKey` — хеш
  пути): траты версий сцен фильма (копятся, не убывают, потолка нет; «GPU-секунды» — время локального запуска с
  очередью, оценка), пометки «✦ Claude», нить звука, из которой фильм ждёт музыку.
- **Сборка**: `IVideoDsp.AssembleAsync` берёт слот единого `BuildConcurrencyGate` ДО старта и запускает `Heavy`-спеку
  через `ILauncherFactory.Local` (в `ccs-agents.slice` при включённой изоляции); второго семафора нет, поверх слота —
  потолок модуля в `FilmBuildRegistry` (1 на владельца, 2 на инстанс). Итог `film.mp4` → `film.v2.mp4`, в `.film`
  дописывается `builds[]` с хешем входов НА МОМЕНТ плана (правка за время сборки делает фильм устаревшим).
- **Соседи — только швы и события Core**: `IImageFrameSource`, `IAudioTrackSource`, `IMediaEvents`, `IVideoDsp` —
  все необязательные параметры (нет Images/«Картинок»/«Звука» — `dsp_unavailable` / `provider_unavailable`, не 500).
  Хранилищ `image-threads` и `audio-threads` модуль не читает никогда — сторож `VideoModuleIsolationGuardTests`
  (строки и типы в тексте кода плюс ссылки сборки).
- **Лента — одна точка записи на действие для человека и агента** (требование Андрея 2026-10-02): сохранение сцены
  (`FilmSceneSaver` → `video_saved`), правка (`FilmService.PatchAsync` → `video_note`) и сборка (`FilmAssembler` →
  `video_film_built`, отказ — `video_note`) пишут `module_record` сами, а не контроллер; `recordType` одинаков при
  `initiator = human` и `agent`, различие только в `data.initiator`. Чат для записи — необязательный `sessionId`
  (query у PATCH и POST build; чужой чат игнорируется). Тулсет блока 3 обязан звать эти же методы. Сторож —
  `FilmFeedParityTests`.
- Потолки: до 50 сцен, клип до 300 МБ (сохранение и сборка), `VideoEditor:AssembleTimeoutMinutes` = 20.

**Агент (блок 3, ADR-022 §5).** `Mcp/VideoEditorToolset` — MCP-сервер `video-editor`, `Chats/VideoEditorStateContributor` —
блок хвоста хода `video-editor-state`. Описание — [video-editor.md](../../docs/features/video-editor.md).

- **У тулсета нет своего пути исполнения.** Каждый инструмент зовёт тот же сервис, что REST-ручка человека
  (`VideoSceneService`, `VideoEditJobService`, `FilmSceneSaver`, `FilmService`, `FilmAssembler`), а запись ленты пишут
  сервисы. Тулсет НЕ пишет `module_record` сам и не заводит своих `recordType`; различие — только `initiator = agent`.
  Сторож — `VideoEditorToolsetFeedParityTests` (каждый инструмент против настоящего контроллера).
- **Потолка трат и лимита запусков за ход нет** (решение Андрея): `MaxLaunchesPerTurn` из звука не переносить; темп
  держит правило «после сцены — спроси» в блоке хвоста.
- **Состав `tools/list`** — только сессия, флаг владельца и `VideoEditor:AgentLaunch`; пять пишущих и тратящих
  (`shoot`, `cancel`, `save_scene`, `film_edit`, `film_build`) идут через `IDelegatedTurnGate` в `CallAsync`, не в составе.
- **Запись агента** — только `video/**` и `music/**`, `CreateNew`, `.film` под ревизией (её агент берёт из `video_state`).
- Новая операция над нитями сцен — в `VideoSceneService`, а не в контроллере: иначе ручка и инструмент разойдутся.

Граница блока 3: постера версии нет — это фронт.
