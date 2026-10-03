# Редактор звука (ClaudeHomeServer.AudioEditor)

> Этот файл — вынесенная часть корневого `CLAUDE.md`: он загружается, только когда идёт работа с файлами этой папки.

Озвучка, музыка и обработка звука прямо в чате — проекта или личном вне проекта: нити звука с
версиями-наборами файлов, поставщики fal, Higgsfield, Яндекс SpeechKit и «Локальные модели», монтаж и
склейка без ИИ (ffmpeg), библиотека «Голоса» проекта, агент с тулсетом `audio-editor`. За флагом
`audio-editor`. Решение — [ADR-021](../../docs/adr/ADR-021-audio-editor-and-generation-panel.md);
описание для человека — [docs/features/audio-editor.md](../../docs/features/audio-editor.md); аудио в
local-media — [ADR-020](../../docs/adr/ADR-020-local-media-audio.md).

**Форма — динамический модуль, как ImageEditor**: Main ссылается с `ReferenceOutputAssembly="false"`,
dll копируется в `modules/audio-editor/` двумя целями (`Build` и `Publish`), `ModuleLoader` грузит её по
записи `DynamicModules` с ключом `audioeditor`. Ссылка — **только на Core**, своих пакетов нет
(`DynamicModulePackagesGuardTests`). Фронт — MF-remote `frontend/modules/audio-editor` (манифест
подсистемы), код фичи — `frontend/src/features/audioEditor` (полоса, панель `sound`, «Голоса»); ручки —
раздел звука в [api.md](../../docs/architecture/api.md).

Состав: `Controllers/` (проектные `api/projects/{id}/audio-editor/*`, личные
`api/audio-editor/chats/{sessionId}/*`, схема «Дополнительно» `api/audio-editor/schema`, «Голоса»;
тело ручек общее — `AudioEditorEndpoints`, гейт — `AudioEditScopeGate`), `Threads/` (нити, фокус,
жизненный цикл по шине, `AudioThreadRecovery`), `Jobs/` (исполнитель `AudioEditJobService`, склейка
`AudioConcatService`, рабочая папка), `Engines/` (драйверы `IAudioEngine` и `DspAudioEngine` «Без ИИ»),
`Catalog/` (отобранные модели — данные), `Schema/` (схема параметров fal и проверка `params`), `Prefs/`
(выбор в полосе по режимам, `AudioOpInputs`), `Voices/`, `Mcp/` (тулсет), `Chats/` (блок хвоста хода).
Тесты — `ClaudeHomeServer.AudioEditor.Tests`; ручки, отключаемость и `McpToolsetStabilityTests` — в
`ClaudeHomeServer.Tests`.

**Область — `AudioEditScope.Of`**, единственная точка сопоставления «чат ↔ область»: у проекта — id
проекта, у личного чата — константа `personal` на владельца (не на чат: ветвление копирует нити). У
личной области `Project == null`, и всё, что читает диск проекта (файлы, `folder`, образцы путями,
«Голоса», local, сохранение), отказывает ДО `RootPath`. Хранилища: нити
`data/audio-threads/{owner}/{session}.json` (в бэкапе, TTL не чистит), рабочая папка
`data/audio-editor/{owner}/{job}` (7 дней, файлы версий живых нитей держатся), префы
`data/audio-editor-prefs/{owner}/{scope}.json` — отдельная запись на режим `voice` / `music` / `process`.

**Версия — набор файлов с ролями** (`main`, `stem:<имя>`, `score`, `subtitles`, `lyrics`, `text`,
`midi`, `model`, `index`), и **монтаж без ИИ — новая версия, а не шаг** (у картинок наоборот). Версия
хранит лицензию модели на момент запуска; склейка наследует лицензии всех кусков.

Инварианты:

- **Результат прямого `local_*` усыновляется** (`ILocalMediaAdopter` в Core, реализация `Threads/LocalAudioAdopter`):
  когда local-media собрал задачу чата, звуковые файлы результата получают нить по файлу и якорь `audio_thread` —
  та же карточка, что у `audio_generate`. Фокус человека не трогаем, второго якоря на тот же файл нет, флаг модуля
  и свой чат проверяются; вызов инструмента при известной нити (монтаж без ИИ, склейка) своей карточки не рисует.
- **Тихого перехода на другого поставщика нет.** Запуск — только по котировке и ровно на её паре
  «поставщик + модель»; отказ возвращает `RetryQuote` соседа с той же операцией и тем же видом голоса,
  запускает его только человек. Клон у одного поставщика — не клон у другого: клон MiniMax соседом на
  Qwen-клон не заменяется.
- **Деньги — только quote → job**: котировка живёт 10 минут и хранит всё, от чего зависит цена (текст,
  подводка, слова, длительность, итог `params`); запуск с другими значениями — отказ «Котировка не
  соответствует запросу» до поставщика. `params` проверяются и в котировке, и в
  запуске (неизвестный ключ, общее поле в `params`, тип, границы — `invalid_request` с именем поля до
  денег и очереди). Трата пишется в момент принятия задачи поставщиком, на владельца, в своей валюте
  (`CostUsd` fal, `CostCredits` Higgsfield, `CostRub` Яндекс с источником `tts`; не складываются), у local
  и «Без ИИ» — с нулём; в `Label` — эндпоинт и единица тарификации. «Не списано» от поставщика — минус.
- **Повтор у Higgsfield — только до создания задания**: единственный автоповтор — когда Higgsfield не
  знает образца из кеша (`IsMediaMissing`, фраза целиком) и запуск НЕ принят, кредиты не ушли; кеш
  вычищается, образец грузится заново, повтор ровно один. После `JobIds` задание создано — никаких
  повторов. Запуск — с явным `use_unlim: false`, аргументы внутри `params`.
- **Клон MiniMax пересоздаётся только кнопкой с ценой**: исполнитель его не создаёт и не пересоздаёт
  сам. Протух (7 дней без использования, `VoiceManifest.MiniMaxTtl`) или не создан —
  `voice_clone_stale` / `voice_clone_missing` с котировкой пересоздания ДО вызова поставщика;
  пересоздание — `POST voices/{slug}/recreate?provider=minimax` в две фазы (котировка, затем задача).
- **Кеш id поставщиков в голосе** меняет только `VoiceLibrary.Remember` по списку
  `AudioVoiceCacheEntry`; **пустой `Id` — удалить запись поставщика** (так движок гасит протухшее без
  прав на манифест). Новый поставщик в кеше обязан понимать пустой `Id` так же. Id поставщиков наружу
  (DTO, ответы агенту) не уходят.
- **Каждая внешняя ссылка — только через `SafeMediaDownloader`** (Core): скачивание результатов fal и
  Higgsfield и `UploadAsync` — PUT образца по адресу из ответа `media_upload` (https, `SsrfGuard` до
  запроса и в `ConnectCallback`, без прокси, без редиректов). Загрузчик у `FalAudioEngine` и
  `HiggsfieldMcpClient` — `init`, после создания singleton его не подменить. Потолок скачивания —
  `SafeMediaDownloader.AudioMaxBytes`.
- **Поля-ссылки схемы fal (`FalSchemaReader.IsLink`: `webhook*`, `callback*`, `url(s)`, `uri(s)`,
  `endpoint(s)` — сами и хвостом через `_`) — в `Reserved`**: в `params` их не принимаем, в форме не
  рисуем. Адрес в запрос кладёт только модуль — из входов каталога (`Source` / `Reference`).
- **`Inputs` отдельно от `Params`**: входы операции (язык, образец, кусок, куски склейки и стыки, голос,
  реплики) живут в настройках нити и префах по белому списку `AudioOpInputs`, в `params` модели не
  попадают и запуском не читаются; запуск переписывает настройки нити, но входы сохраняет.
- **Голоса — только в серверном проекте**: `voices/<slug>/` (манифест, до 5 образцов по 50 МБ, пара
  RVC). Личная область — `voices_project_only`, локальный проект — отказ `ProjectCapabilityGuard` до
  диска. Slug — белый список `[a-z0-9-]`, имена файлов даёт модуль.
- **Пути из запроса — только `ProjectLinkGuard.ResolveInside`** (файлы, папки, образцы, куски склейки,
  сохранение), путь версии — только внутри папки её задачи (`AudioVersionFiles.Resolve`).
- **Перезаписи нет**: сохранение — `CreateNew` всей группой (основной файл, файлы-спутники, папка
  `<имя>.stems/`), занятое имя у «Сохранить как» — `409 name_taken` с подсказкой.
- **Состав `tools/list` не зависит от хода, фокуса, режима, поставщика и нитей** — только от сессии,
  флага владельца и `AudioEditor:AgentLaunch` (без него остаются `audio_state`, `audio_focus`,
  `audio_new`, `audio_voices`, `audio_suggest_prompt`). Контекст сервера собирают все три точки
  `BuildAudioEditorContext` в `SessionManager`; сторож — `McpToolsetStabilityTests`. Схема
  `audio_generate` фиксированная, частные параметры — в `params` по схеме движка.
- **Агент — не больше `MaxLaunchesPerTurn = 2` платных запусков за ход** (счётчик на сессию, сброс по
  `TurnCompleted`). Монтаж без ИИ (`op` `trim | gainFade | normalize | mixStems`) и `audio_concat` лимит не
  расходуют, но **делегированный и реакционный ход закрыт fail-closed для всего, что запускает**,
  включая монтаж и склейку (`IDelegatedTurnGate`; нет сессии-вызывателя — отказ). **Сохранения у
  агента нет и не заводим.** `audio_generate` без `threadId` — отказ.
- **Правило «звук через редактор» едет хвостом хода** (`audio-editor-state`, `InTurnTail`, `Order 705`) и
  только при сервере `audio-editor` в ходе (`HasAudioEditorMcp`): прямые `local_*` для звука не
  скрываются (иначе `tools/list` общего `local-media` зависел бы от второго флага), приоритет держит
  текст. Парное правило в `LocalMediaDefaultContributor` (Images) читает те же флаг и тумблер по строке:
  типов модуля оно не видит.
- **Потолки исполнителя**: 2 задачи на владельца, 4 на инстанс, у local — одна тяжёлая (`AudioCaps.HeavyOps`:
  обучение RVC, `extract` / `lego` / `complete` ACE-Step) — `heavy_busy`, на ручке 429. local — только
  серверный проект (`PersonalScopeReason`, `DeviceProjectReason`), потолок одного запуска — 60 минут.
- **Потолки размеров**: входной звук ручек, правки без ИИ, склейки и запуска агентом — 200 МиБ,
  отказ ДО чтения в память (ffmpeg получает файл целиком); тело запуска — 500 МиБ; образец, который
  едет `data:` URI (клон MiniMax, Chatterbox, Qwen-клон fal), — 20 МБ с отказом до запроса, иначе
  запрос висит до таймаута; склейка — 2–20 кусков.
- **Нет ffmpeg или выключена Images — `503 dsp_unavailable`**, а не 500; выключенные Images / Tts /
  нет ключа fal / нет доступа Higgsfield — драйвер `Enabled=false`. Параметры конструкторов от
  отключаемых вертикалей — только nullable.
- **Раскладка стемов — `AudioCaps.StemSet`, а не id модели**: панель «Что получить» подставляет модель
  `separate` по набору (`vocals | 4 | 6 | karaoke`). Новая модель разделения без `StemSet` в сегменты не
  встанет: панель её не подставит, выбрать её можно только в «Исполнителе».
- **Отключаемость — 404, а не 500**: `DynamicModules[audioeditor].Enabled=false` или
  `Subsystems:AudioEditor:Enabled=false`.

Поставщики (`Engines/`), порядок «Авто» — `AudioCatalog.ProviderOrder` (fal → Higgsfield → local, Яндекс
вне списка — после них; local последним намеренно, доступность GPU мигает):

- **fal** — `FalAudioEngine`, ключ инстанса, REST `queue.fal.run`; отбор моделей — `AudioCatalog.Fal.cs`,
  «Дополнительно» — по OpenAPI эндпоинта (`FalSchemaReader`).
- **Higgsfield** — `HiggsfieldAudioEngine` поверх Core-клиента `HiggsfieldMcpClient`: только речь и
  клон, живой каталог `models_explore` с кешем 30 минут, «Game pipeline only» — серые с причиной,
  инструмента отмены у Higgsfield нет.
- **Яндекс SpeechKit** — `YandexAudioEngine` за швом `ITtsEngine`: только озвучка готовым диктором,
  голос из библиотеки не берёт.
- **«Локальные модели»** — `LocalAudioEngine` за швом `ILocalAudioMedia` (адаптер в Images ходит в
  ComfyUI напрямую, мимо `LocalMediaCollector`); граф — только из шаблонов `ComfyWorkflows`.
- **«Без ИИ»** — `DspAudioEngine` и `AudioConcatService` за швом `IAudioDsp`: не `IAudioEngine`, без
  котировки и исполнителя, вызывающий ждёт итог.

**Перед правками — прочитай [ADR-021](../../docs/adr/ADR-021-audio-editor-and-generation-panel.md)**
(§2 модель модуля, §5 тулсет, §6 «Инварианты под угрозой и сторожа») и образец картинок —
[ImageEditor/CLAUDE.md](../ClaudeHomeServer.ImageEditor/CLAUDE.md).
