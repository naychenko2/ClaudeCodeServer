# Редактор картинок (ClaudeHomeServer.ImageEditor)

> Этот файл — вынесенная часть корневого `CLAUDE.md`: он загружается, только когда идёт работа с файлами этой папки.

Правка картинок проекта моделями (fal, Higgsfield, «Локальные модели») и без ИИ (обрезка,
поворот, размер, сжатие), чат картинки с агентом, персонажи, версии файлов. За флагом
`image-editor`. Решения — [ADR-017](../../docs/adr/ADR-017-image-editor.md) (v1) и
[ADR-018](../../docs/adr/ADR-018-image-editor-v2.md) (v2; §10 — вынос в модуль, §11 — локальные
модели); описание для человека — [docs/features/image-editor.md](../../docs/features/image-editor.md).

**Форма — динамический модуль, как Notes и Spend** (ADR-018 §10.1): Main ссылается на проект с
`ReferenceOutputAssembly="false"` и типов модуля не видит, dll копируется в
`modules/image-editor/` двумя целями MSBuild (`Build` и `Publish` — при одной `publish -o` молча
теряет dll), `ModuleLoader` грузит её по записи `DynamicModules` с ключом `imageeditor`. Фронт —
MF-remote `frontend/modules/image-editor` над кодом `frontend/src/features/imageEditor`.

Состав: `Controllers/` (ручки `image-editor/*` и `chats`), `Jobs/` (исполнитель задач
`ImageEditJobService`, шаги без ИИ, `InputFitter`, запись в проект), `Providers/` (драйверы
`IImageEditor`), `Marks/` (пометки → промпт), `Chats/` (состояние редактора, трекер переименований),
`Mcp/` (тулсет агента `image-editor`), `Characters/`, `Versioning/`, `Contracts/`. Тесты —
`ClaudeHomeServer.ImageEditor.Tests`; тесты контроллера и `McpToolsetStabilityTests` остались в
`ClaudeHomeServer.Tests`.

**Швы в Core** — модуль ссылается ТОЛЬКО на Core, всё остальное приходит через них:

- `IImageRaster` (`Services.Images.Editing.Raster`) — растр на SkiaSharp, реализация в `Images`.
  Необязателен: без `Images` ручки `transform` и `jobs` отвечают `503 raster_unavailable`, а не 500;
- `IImagePlaceSettings` — умолчание места `image-editor` из настроек генератора (`Images`);
- `IImageChatSessions` — создание чата картинки, `SetImageChatPath`, запись в ленту (адаптер в Main
  поверх `SessionManager`, правило выбора персоны живёт внутри адаптера);
- `IDelegatedTurnGate` — вердикт `DelegatedTurnGate` для тулсета (гейт в Main);
- `ILocalImageMedia` — движок local-media (адаптер `LocalImageMediaAdapter` в `Images`). Нет `Images`
  — шва нет, драйвер `local` получает `null` и скрыт в каталоге.

Прочие швы (`IHiggsfieldAccess`, `IProjectFiles`, `ISpendCollector`, `ISessionDirectory`, …)
перечислены в `ImageEditorSubsystem`. `ProjectLinkGuard` тоже лежит в Core.

Инварианты:

- **Перезаписи нет никогда.** Любая запись в проект — `FileMode.CreateNew`: следующая версия
  (`hero.v2.png`), «Нарисовать» (`-2`, `-3`), «Сохранить как» (занятое имя — `409 name_taken` с
  `suggestion`, а не тихий номер: имя человек выбрал явно). Флага перезаписи нет и не заводим.
- **Тихого перехода на другого поставщика нет.** Отказ поставщика возвращает котировку соседа,
  чтобы человек повторил одним кликом; ни ручка, ни тулсет второй раз сами не запускают.
- **Трата пишется всегда при принятии задачи, на владельца**, в общий `ISpendCollector`; запуск
  агентом помечен `Initiator = Agent`. У «Локальных моделей» трата пишется с нулевой суммой, а не
  пропускается. Сбой после принятия компенсируется минусом.
- **Агент — не больше `MaxLaunchesPerTurn = 2` запусков за ход**, общий лимит на всех поставщиков;
  плюс потолки исполнителя (2 задачи на владельца, 4 на инстанс). Выключатель инстанса —
  `ImageEditor:AgentLaunch`.
- **На делегированном ходу запуск — fail-closed** через `IDelegatedTurnGate`: нет сессии-вызывателя
  — отказ, а не пропуск.
- **Состав `tools/list` не зависит от хода**: сервер `image-editor` едет только в чат картинки
  (`Session.ImageChat`), по флагу владельца и при тулсете в реестре (`BuildImageEditorContext` в
  Main). Иначе CLI перезапустится со всеми MCP-серверами.
- **Состояние редактора (`image-editor-state`) едет хвостом хода ВСЕГДА** (`PromptSection.InTurnTail`),
  у любого провайдера, а не только при `RecallInTurnText`: секция меняется каждый ход и в
  системном блоке обнуляла бы prefix cache всей истории.
- **Пути из запроса — только через `ProjectLinkGuard.ResolveInside`**: `SafePath.Join` сравнивает
  строки и не видит символическую ссылку наружу, поэтому ни один сегмент ниже корня проекта не
  может быть ссылкой.
- **Отключаемость — 404, а не 500**: `DynamicModules[imageeditor].Enabled=false` (dll не грузится)
  или `Subsystems:ImageEditor:Enabled=false` (`Register` не вызывается) — ручек нет.
- **Своих пакетов у модуля нет**: он грузится в дефолтный контекст, и зависимость вне замыкания
  Main на проде не найдётся. Нужен пакет (как SkiaSharp) — он ложится в `Images` за швом, а не
  сюда. Сторож — `DynamicModulePackagesGuardTests`.

Поставщики (`Providers/`), каталог моделей с `caps` — `ImageEditCatalog`:

- **fal** — `FalImageEditor`, ключ инстанса.
- **Higgsfield** — `HiggsfieldImageEditor` поверх `HiggsfieldMcpClient`, платит аккаунт админа.
  **Аргументы `generate_image` с 2026-09 лежат внутри `params`**: плоские Higgsfield отвергает
  «params: Invalid input», и котировка остаётся без цены. Запуск — с явным `use_unlim: false`.
- **«Локальные модели»** (`local`) — `LocalImageEditor` над `ILocalImageMedia`: Qwen-Image 2.1
  (генерация, правка, кисть; маска — образцом, кроме «удали», там серая заливка `EraseMask`) и
  FaceDetailer (`EnhanceFaces`). Котировка без цены: `free`, время по замерам и длина очереди
  ComfyUI; не ответил ComfyUI — `provider_unavailable`. Граф — только из шаблонов `ComfyWorkflows`.

**Перед правками — прочитай [ADR-018](../../docs/adr/ADR-018-image-editor-v2.md)**, §7 «Инварианты
под угрозой и сторожа» и §10.4 «План переноса».
