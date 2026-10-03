# ADR-023: Контекст хода и строка контекста над полем ввода

- **Статус:** разрез принят 2026-10-02. **Дополнение 1** (2026-10-03, Александр): одна панель «Контекст»
  вместо трёх и чипы действий вместо списков операций — раздел «Дополнение 1» в конце; он **отменяет** `mode`
  в `ContextKindApi` (§2.2) и **заменяет** фазы §5. **Дополнение 2 принято** 2026-10-03 (Александр, решения
  Андрея; ответы на точки ожидания — Софья по рекомендациям плана): умолчание действия, «Чем» в «Чате»,
  параметры на кнопке, расхождения с кодом, порядок мержей — раздел «Дополнение 2» в конце. Раскладка по
  задачам — [план реализации](../research/turn-context-implementation-plan-2026-10.md)
- **Основа:** заметка «Концепция строки контекста над полем ввода» (Софья, согласована с Андреем 2026-10-02),
  [ADR-021](ADR-021-audio-editor-and-generation-panel.md) §3 (каркас панели генерации, `genPanelDismissed`),
  ADR-022 «Видео» (ветка `feat/video-editor`: следование панели, `preset`/`returnTo`),
  [ADR-014](ADR-014-internal-subsystems.md) (подсистемы и сторожа границ), [ADR-013](ADR-013-turn-event-bus.md)
  (шина хода и контрибьюторы промпта)
- **Флаг:** `composer-context-row` (`Default: false`); снимается в фазе 4

## Контекст

Над полем ввода сейчас переключатель полос «Git ▾ / Руки / Картинки / Звук» (на ветке видео ещё «Видео»):
одна полоса за раз, правило старшинства, память выбранной полосы и свёрнутости в `localStorage`. Выбор «что
в работе» каждая вертикаль держит сама, и держит по-разному:

| Что | Где сейчас | Свойство |
|---|---|---|
| Фокус картинки | `data/image-threads/{owner}/{session}.json`, поле `Focus` | сервер, ревизия, `image_thread_changed` |
| Фокус звука | `data/audio-threads/{owner}/{session}.json`, поле `Focus` | сервер, ревизия, `audio_thread_changed` |
| Фокус видео (ветка) | `data/video-threads/…`, составной `{sceneId, filmPath}` | сервер |
| Образцы картинок (стиль, предмет, лицо) | `_samples` в памяти вкладки фронта | **теряются на перезагрузке, агент о них не знает** |
| Персонаж картинок | `ImageProjectPrefs.CharacterSlug` — **на проект**, не на чат | сервер |
| Входы звука (`Inputs`: голос, образец, куски) | настройки нити + префы режима; часть — `localStorage` `cc_audio_inputs:` | запуском не читаются, их подставляет панель |
| Полоса над полем | `cc-composer-strip:{session}` | память «какая полоса» |
| Режим поля «Чат \| Картинка» | `useState` в `Composer` | теряется на перемонтировании (чинит `fix/composer-modes`) |
| Ветка git | `ProjectGitBar` | производное, не выбор |

Агенту контекст уезжает тремя хвостовыми блоками (`image-editor-state` 700, `audio-editor-state` 705,
`video-editor-state` 707 на ветке) плюс правило `local-media-default` 710. Образцов и персонажа в них нет, а
два «В работе» (картинка и звук одновременно) — норма. «Человек видит то же, что получит Claude» сейчас не
выполняется ни в одной точке.

## Решение

### 1. Модель контекста чата

**Контекст хода = Где + С чем + Чем + Плюс.** Из четырёх частей хранится только то, что выбирает человек или
агент: **основной объект** (0..1) и **референсы** (0..N). «Где» (ветка) и «Чем» (исполнитель) — производные,
их не храним.

| Часть | Хранится | Источник правды | Кто считает подпись |
|---|---|---|---|
| Где | нет | git дерева чата (worktree сессии) | фронт, из данных `ProjectGitBar` |
| С чем | да, `Primary` | стор контекста чата | вертикаль-владелец вида, **на бэкенде** |
| Чем | нет | префы вертикали по режиму (как сейчас) | вертикаль-владелец вида основного объекта, **на бэкенде** |
| Плюс | да, `Refs[]` | стор контекста чата | вертикаль-владелец вида, **на бэкенде** |

**Стор — в спине на сервере, кит на фронте — его зеркало.** Хвост хода собирается на сервере, агент меняет
контекст своими инструментами, у хода исполнителя задачи нет браузера, — поэтому стор на фронте (как
`composerStrips`) не годится: он и есть то «два источника правды», от которого уходим.

- Core, namespace `ClaudeHomeServer.Services.ChatContext` (новый, добавить в `CoreAllowedNamespaces`).
- Файл `data/chat-context/{ownerId}/{sessionId}.json` на `JsonFileStore`, ревизия на запись, `409` со свежим
  состоянием при чужой ревизии (как у нитей). **Не `sessions.json` и не поле `Session`**: правка контекста не
  трогает `UpdatedAt`, архив и сортировку.
- Жизненный цикл по шине: `session/deleted` — удалить файл; `session/branched` — скопировать (как нити).
- Бэкап: каталог в `data/` попадает в архив сам; формат аддитивный, `BackupSchema.Version` не растёт.

```csharp
namespace ClaudeHomeServer.Services.ChatContext;

// Состояние контекста чата. Kind — вид объекта, объявленный вертикалью ("image", "audio",
// "video-scene", "project-file", "image-character", "audio-voice"…). Ref — непрозрачный для спины
// JSON-объект: смысл знает только владелец вида ({threadId, versionId} у картинки, {slug} у голоса).
public sealed record ChatContextState(long Revision, ContextItem? Primary, IReadOnlyList<ContextItem> Refs);

public sealed record ContextItem(
    string Id,             // id элемента в контексте (для ✕ и undo), не id объекта
    string Kind,
    JsonObject Ref,
    string? Role,          // у Primary — null; у референса — роль из списка ролей владельца
    ContextActor By,       // Human | Agent — чип ✦
    DateTime AddedAt);

public enum ContextActor { Human, Agent }

// Читатель и писатель контекста. Проверку Kind/Role/Ref делает IContextKindProvider владельца,
// стор пускает только зарегистрированные виды.
public interface IChatContextStore
{
    ChatContextState Get(string ownerId, string sessionId);
    ChatContextState SetPrimary(string ownerId, string sessionId, ContextItem? item, long? revision);
    ChatContextState AddRef(string ownerId, string sessionId, ContextItem item, long? revision);
    ChatContextState RemoveRef(string ownerId, string sessionId, string itemId, long? revision);
    ChatContextState Clear(string ownerId, string sessionId, long? revision);
    // Объект исчез (нить удалена, файл удалён) — убрать его отовсюду без ревизии
    void Forget(string ownerId, string sessionId, Func<ContextItem, bool> match);
}
```

**Правила стора.**

- Основной объект — **один на чат поперёк вертикалей**: выбор звука снимает картинку. Это смена смысла (сейчас
  фокус картинки и звука живут одновременно), и она намеренная — иначе «С чем» не одно.
- Референсов не больше 16 (потолок `local_edit_image`), дубль «тот же Kind + Ref + Role» не добавляется.
- Один и тот же объект может быть основным и референсом одновременно — нет: при `SetPrimary` его референс
  снимается.
- Ревизия `null` у агента — без сверки (как `VideoSceneService` на ветке).
- Изменение рассылается SignalR-событием `chat_context_changed` владельцу (`ChatContextChangedMessage` в
  `Core/Protocol`, с полным DTO — см. ниже).
- Черновик «Новая картинка / Новый звук» — основной объект вида `image` / `audio` с нитью без файла: отдельной
  сущности «черновик» в сторе нет.

**Кто владеет видами.** Вертикаль объявляет виды на бэкенде и на фронте; спина знает только строки `Kind` и
вызывает владельца.

| Вид (`Kind`) | Владелец | `Ref` | Основным? | Роли референса |
|---|---|---|---|---|
| `image` | ImageEditor | `{threadId, versionId?}` | да | `style`, `object`, `face`, `frame-a`, `frame-b` |
| `image-character` | ImageEditor | `{slug}` | нет | `character` |
| `audio` | AudioEditor | `{threadId, versionId?}` | да | `reference`, `piece` |
| `audio-voice` | AudioEditor | `{slug}` | нет | `voice` |
| `video-scene`, `video-film` | VideoEditor (фаза 3) | `{sceneId}`, `{filmPath}` | да | — |
| `project-file` | спина (`ChatContext`) | `{path}` | нет | роль задаёт тот, кто принимает: см. ниже |

Роли референса принадлежат **принимающей** операции, а не виду: картинка-версия в контексте звука смысла не
имеет, а в контексте сцены она `frame-a`. Поэтому роль при «В контекст ▾» спрашивается по виду основного
объекта: владелец основного объекта отдаёт таблицу «какие виды и с какими ролями я принимаю». Без основного
объекта (чат «Чат») референс добавляется с ролью `null` — «для Claude», генераторам он не вход.

### 2. Контракты

#### 2.1. Бэкенд: вертикаль объявляет вид

```csharp
namespace ClaudeHomeServer.Services.ChatContext;

public interface IContextKindProvider
{
    IReadOnlyList<string> Kinds { get; }

    // Проверка Ref до записи: объект существует, принадлежит владельцу, путь внутри проекта
    // (ProjectLinkGuard), в личном чате — без проектных путей. null — годится, иначе текст отказа 400
    string? Validate(ContextScope scope, string kind, JsonObject reference);

    // ОДНА функция сводки: её текст и чип строки, и строка «Работаем с» панели, и строка хвоста хода.
    // Missing — объект пропал (чип серый «нет файла», в хвосте — «недоступен»)
    ContextItemSummary Describe(ContextScope scope, ContextItem item);

    // Для основного объекта своего вида: какие референсы принимает операция и как их зовут.
    // op — операция панели (null — режим по умолчанию). Пусто — референсы не принимаются
    IReadOnlyList<ContextRoleSpec> AcceptedRefs(ContextScope scope, ContextItem primary, string? op);

    // «Чем»: исполнитель и модель для основного объекта своего вида
    // («Авто · локально · Qwen-Image Edit · бесплатно»). null — не применимо
    string? DescribeExecutor(ContextScope scope, ContextItem primary);
}

public sealed record ContextScope(string OwnerId, Session Session, Project? Project);
public sealed record ContextItemSummary(string Label, string? Version, string? Thumb, bool Missing);
public sealed record ContextRoleSpec(string Role, string Title, IReadOnlyList<string> Kinds, IReadOnlyList<string> Ops);
```

Регистрация — `services.AddContextKindProvider<T>()` в `*Subsystem.Register` вертикали (по образцу
`AddPromptSectionContributor`). Спина собирает `IEnumerable<IContextKindProvider>`; выключенная вертикаль просто
не регистрирует провайдера, её элементы в контексте Describe получают `Missing` и в хвост не идут.
`project-file` — встроенный провайдер спины (`ProjectFileContextKind`, `ProjectLinkGuard.ResolveInside`).

> **Пересмотрено в Дополнении 2, §Р2 и §Р5:** поле `executor` из `ChatContextDto` **убрано** — строка в
> «Чате» «Чем» не показывает, а при выбранном действии берёт его из `quote`. `DescribeExecutor` остаётся: его
> читают хвост хода и `context_state`. В таблицу REST добавлена ручка `context/saved-files`. JSON-примеры
> контрактов (DTO, тело `409`) — в отдельном файле [ADR-023-contracts.md](ADR-023-contracts.md) (кладёт
> задача 1б-1 в ветке `feat/turn-context`).

**DTO для фронта** — `ChatContextDto { revision, primary, refs[], executor }`, где у каждого элемента уже
посчитаны `label`, `version`, `thumb`, `missing`, а у референса — `usedBy: string[]` (операции основного объекта,
которые его берут; пусто — чип серый «не используется в операции «…»»). **Фронт подписей не считает**:
так строка, панель и хвост физически не могут разойтись.

**REST** (в Main, `ChatContextController`, `[Authorize]`, владелец — из `sub`, сессия — `GetOwned`):

| Метод | Путь | Тело |
|---|---|---|
| GET | `api/chats/{sessionId}/context` | — |
| PUT | `api/chats/{sessionId}/context/primary` | `{ kind, ref, revision }` или `{ kind: null }` — снять |
| POST | `api/chats/{sessionId}/context/refs` | `{ kind, ref, role?, revision }` |
| DELETE | `api/chats/{sessionId}/context/refs/{itemId}?revision=` | — |
| DELETE | `api/chats/{sessionId}/context?revision=` | «Очистить контекст» |
| GET | `api/chats/{sessionId}/context/saved-files` | — → `[{path, threadKind, savedAt}]`, для `commitViaChat` (§3.3, `IChatSavedFiles`); **добавлено в Дополнении 2** |

Путь один для проектного и личного чата: контекст ключуется сессией, область вертикаль выводит сама.
Локальный проект (ADR-016) — через `ProjectCapabilities`: `project-file` и проектные виды отказывают до диска.

#### 2.2. Фронт: стор в ките и слот `context-kind`

> **Пересмотрено в Дополнении 1, §Д2:** поле `mode` и переключатель «Чат | X» заменены чипами действий
> (`actions`); `open(…, 'object' | 'executor')` ведёт в единую панель `chatContext`, а не в панель вертикали.
>
> **Пересмотрено в Дополнении 2, §Р3 и §Р5:** `executors` строит строки под выбранное действие (§Д1), в «Чате»
> меню «Чем» нет. Пути: `Composer.tsx` и `ChatPanel.tsx` лежат в `components/`, а не в `components/chat/`;
> `createReleaseUndo` и `RELEASE_UNDO_MS` — в `components/generation/useReleaseUndo.ts`, не в ките.

- `frontend/src/lib/chatContext/` (экспорт через `lib/shell-kit/index.ts`): `useChatContext(sessionId)`,
  `setPrimary`, `attachRef(sessionId, {kind, ref, role?})`, `detachRef`, `clearContext`, `releasePrimary`
  с «Вернуть» на 4 с (`createReleaseUndo`, `RELEASE_UNDO_MS`). Кэш — `Map<sessionId, ChatContextDto>` поверх
  `useSyncExternalStore`, источник — GET + `chat_context_changed` + перечитка на `onReconnected` (как
  `threadStore` вертикалей).
- **Новый слот `context-kind`** (`SLOT_CONTEXT_KIND` в `registryCore.ts`) **заменяет `composer-strip` и
  `composer-mode`**:

```ts
export interface ContextKindApi {
  kinds: readonly string[];                 // те же строки, что у бэкенд-провайдера
  icon: (kind: string) => ReactNode;
  // Режим поля, когда основной объект — этого вида. Тот же ComposerModeApi без isAvailable/autoSelect:
  // доступность и самовключение теперь выводятся из основного объекта
  mode?: Omit<ComposerModeApi, 'isAvailable' | 'autoSelect'>;
  // Клик по чипу «С чем» / «Чем»: открыть панель вертикали на вкладке объекта или исполнителя
  open: (ctx: ContextKindCtx, item: ChatContextItem, what: 'object' | 'executor') => void;
  // Список исполнителей для меню «Чем» — те же строки, что ExecutorList в панели
  executors?: (ctx: ContextKindCtx) => { rows: readonly ExecutorRow[]; value: string; onChange: (id: string) => void } | null;
  // Вход «＋» композера и empty-state ленты: «Картинка», «Звук», «Видео»
  create?: { title: string; icon: ReactNode; run: (ctx: ContextKindCtx) => void };
}
export interface ContextKindCtx { projectId: string | null; sessionId: string; isMobile: boolean }
```

- **Хост — `components/chat/ContextRow.tsx`** (одна высота ≈ 30 px): чип «Где» (данные и меню — вынести из
  `ProjectGitBar` хук `useGitChip`), чип «С чем» (`label · version`, ✕ → `releasePrimary`), «Чем» (меню
  `ExecutorList` из `executors`), «Плюс» (чипы, схлопывание в «+N ›», список над строкой с ролями, «Очистить
  контекст»). Приоритет при нехватке ширины и вид на телефоне — по заметке-концепции, §4; геометрию даст макет.
- **Режим поля** выводится в `Composer`: есть основной объект и у его вида есть `mode` → переключатель «Чат | X».
  Самовключение — когда сменился **ключ основного объекта** (`kind:ref`), а не по `autoSelect` вертикали;
  ручной уход в «Чат» помнится на чат и ключ — это ровно `composerModeMemory` из `fix/composer-modes` (см. §5).

#### 2.3. Как наполняют контекст

| Источник | Действие | Вызов |
|---|---|---|
| Карточка ленты (вертикаль) | «Работать с этой» | `setPrimary` + `revealWorkspacePanel` (просьба — открывает и закрытую панель) |
| Карточка ленты | клик по карточке | `setPrimary` + `followSelection` (`ifOpen`: закрытую не открывает) |
| Карточка ленты | «В контекст ▾» | `attachRef` с ролью из `AcceptedRefs` основного объекта (одна роль — без вопроса) |
| «Файлы» (`FileExplorer`) | «Работать с этой» | `setPrimary` с видом владельца расширения (слот `image-editor`/`opener` → обобщить до `context-opener`: вертикаль говорит «этот путь мой» и превращает путь в `Ref` — у картинки это `POST threads {file}`, затем `image`) |
| «Файлы» | «В контекст ▾» | `attachRef({kind: 'project-file', ref: {path}})` — спине вертикаль не нужна |
| «Персонажи» (вкладка панели «Картинки») | «В контекст» / «В контексте ✓» | `attachRef({kind: 'image-character', ref: {slug}, role: 'character'})` |
| «Голоса» (вкладка панели «Звук») | «В контекст» / «В контексте ✓» | `attachRef({kind: 'audio-voice', ref: {slug}, role: 'voice'})` |
| Образцы (панель «Картинки», загрузка с диска) | «Добавить образец» | загрузка в рабочую папку модуля (`POST …/image-editor/uploads` → `uploadId`), затем `attachRef({kind: 'image', ref: {upload}})`: образец из `_samples` в памяти переезжает на сервер |
| Агент | `image_focus`, `audio_focus`, `video_*` | вертикаль пишет `SetPrimary(by: Agent)` |
| Агент | `context_attach`, `context_detach` | см. 2.4 |

«В контексте ✓» библиотека считает сама по `useChatContext` — своего стора у неё нет.

#### 2.4. Агент и ✦

- **`image_focus` / `audio_focus` остаются** (схемы не меняются): они проверяют нить своей вертикали и пишут
  `IChatContextStore.SetPrimary(..., By = Agent)`. Общий `context_focus` не заводим — см. «Отвергнуто».
- **Референсы агента** — новый маленький тулсет спины `turn-context` (Core, `Services/Mcp/Http`, маршрут
  `POST /mcp/turn-context/{sessionId}`):

  > **Пересмотрено в Дополнении 2, §Р5:** тулсеты продукта живут в Main `Services/Mcp/Http`, в Core — только
  > `IMcpToolset`. Тулсет `turn-context` — в Main, стор и контракты — в Core. `context_state` строку «Чем:»
  > сохраняет (§Р2).


| Инструмент | Контракт |
|---|---|
| `context_state` | что в строке: основной объект, референсы с ролями и `usedBy`, исполнитель — тем же текстом, что хвост |
| `context_attach` | `{ kind, ref, role? }` → элемент с `By = Agent` (чип ✦) |
| `context_detach` | `{ itemId }` |

  Имена не однокоренные с `*_focus`. Схема фиксированная; `kind` — строка, проверка — провайдером владельца.
  `context_attach`/`context_detach` — `[DenyOnDelegatedTurn]` (делегированный ход строку человека не трогает),
  `context_state` разрешён. Сервер едет по флагу владельца `composer-context-row` (свойство сессии, как
  `audio-editor`) во **все три** точки сборки `LlmSessionContext` в `SessionManager`; после снятия флага — всегда.
- Панель от действий агента **не двигается**: остаётся существующий `noteAgentPick` → подсказка «Claude взял в
  работу · Открыть · ✕». Человек выбрал тот же элемент — `By` становится `Human`, ✦ гаснет.

### 3. Стыки с бэкендом

#### 3.1. Хвост хода — одна точка сборки

Новый контрибьютор спины **`TurnContextContributor`** (`ClaudeHomeServer.Turn`, ключ `turn-context`, заголовок
«Контекст хода», `Order 690`, `Group "misc"`, `InTurnTail: true`). Включён при флаге владельца и непустом
контексте или проекте с git. Текст:

```
## Контекст хода
Где: ветка feat/video-editor (worktree чата)
С чем: картинка t3 — hero.png · версия 2 (поставил человек)
Чем: Авто · локально · Qwen-Image Edit · бесплатно
Плюс: Аня — персонаж (character); palette.png — образец стиля (style) ✦ поставил Claude;
      notes.md — файл проекта, для тебя (не вход генератора)
Строка контекста видна человеку; это ровно то, что в ней. Основной объект и референсы — входы
image_generate / audio_generate по умолчанию; явные аргументы инструмента их заменяют.
```

- Каждая строка «С чем» и «Плюс» — `Describe` провайдера владельца; «Чем» — `DescribeExecutor`. «Где» — ветка,
  **без числа изменений**: оно меняется каждый ход и раздувало бы хвост без пользы для агента.
- **Вертикальные блоки остаются, но худеют** при флаге: из `image-editor-state` / `audio-editor-state` (и
  `video-editor-state`) уходят строки «В работе: …» и «Выбор человека в полосе …»; остаются правила приоритета
  `*_generate` над `local_*`, журнал картинок «с прошлого сообщения» и список нитей. Без флага — как сейчас.
  Один общий блок вместо трёх не делаем: правила и журналы — знание вертикали, спина не должна их знать.
- Тексты, ссылающиеся на «полосу «Звук»/«Картинки»» (`ChoiceText`, `LocalMediaDefaultContributor`,
  `FocusIsNotBindingText`), переписываются на «строку контекста» в фазе 2.
- Кеш: хвост уже дедуплицируется хешем (`ClaudeSession._lastTurnRecallHash`) — неизменный контекст в ход не
  едет повторно. В системный блок секция не попадает никогда (`InTurnTail`).

#### 3.2. Входы операций читают контекст

Запуск человека при флаге несёт **`contextRevision`** вместо разобранных входов. Исполнитель вертикали сам
читает `IChatContextStore.Get` и раскладывает элементы по своим входам; ревизия не совпала — `409
context_changed` со свежим DTO (панель перерисует серые чипы и цену). Так хвост и запуск читают одно и то же.

| Вертикаль | Основной объект → | Референсы → | Где править |
|---|---|---|---|
| Картинки | `ThreadId` + `VersionId` (база правки) | `style`/`object`/`face` → `ReferencePaths` с `ReferenceRole`; `image-character` → `CharacterSlug` | `ImageEditLaunchAssembler` (`:105–125`, `:192–209`) |
| Звук | нить и версия | `audio-voice` → `inputs.voice`; `reference` → `inputs.referencePath`; `piece` → `inputs.pieces` (порядок — `AddedAt`) | `AudioEditJobService` + `AudioOpInputs` |
| Видео (фаза 3) | сцена | `frame-a`/`frame-b` (виды `image`, `project-file`) → `FrameRef` | `VideoSceneService` |

- **Персонаж переезжает из префов проекта в контекст чата.** `ImageProjectPrefs.CharacterSlug` при флаге не
  читается и **не засевает** новые чаты: невидимый в строке персонаж, который сам едет в генерацию, — ровно та
  неявность, которую убираем. Поле остаётся в JSON (старые файлы читаются), запись прекращается в фазе 4.
- `Inputs` звука перестают быть хранилищем выбора: голос, образец и куски — это референсы. В `Inputs` остаются
  только параметры операции (`language`, `startSec`/`endSec`, `joint(s)`, `dialogue`) — они настройки панели,
  не контекст. `localStorage` `cc_audio_inputs:` для `voiceModelPath`/`clipPaths` уходит в референсы тем же шагом.
- Референс, который операция не берёт, запуск не ломает: исполнитель его пропускает, DTO отдаёт `usedBy = []`.
- Агент: если `image_generate`/`audio_generate` вызван **без** `references`/`voice`/`character`, берутся
  референсы контекста (как у человека); явный аргумент, в том числе пустой список, их заменяет. Это правило
  пишется в хвост (см. 3.1) и в описание инструмента — **описание статично**, от хода не зависит.

#### 3.3. Git-чип и «Зафиксировать только этот чат»

- Чип «Где» — те же данные, что `ProjectGitBar` (ветка, worktree хода, `workingDiffStat`, к публикации). Меню:
  «Зафиксировать только этот чат», «Зафиксировать всё дерево», «Опубликовать N», «Показать изменения» — те же
  обработчики `handleCommitOwn` / `handleCommitAll` из `ChatPanel`.
- «Только этот чат» остаётся **поручением агенту** (`commitViaChat`), нового эндпоинта коммита не заводим:
  атрибуция путей по ленте ненадёжна (внешние правки), это уже разобрано в `ChatPanel.tsx:1176–1180`. Новое —
  промпт поручения дополняется списком «сохранённые в этом чате версии» (файлы, которые человек сохранил из нитей
  чата): вертикаль отдаёт их через `IContextKindProvider`-соседа `IChatSavedFiles` (Core, `IEnumerable<>`), спина
  склеивает. Без этого агент не узнает, что `hero.png` — тоже «правка этого чата».

### 4. Инварианты под угрозой и сторожа

| Инвариант | Как держим | Сторож |
|---|---|---|
| Состав `tools/list` не зависит от хода | `turn-context` — фиксированная схема; включение — флаг владельца (свойство сессии); `*_focus` и `*_generate` схем не меняют; `ToolsFor` не читает стор контекста | `McpToolsetStabilityTests`: новый тулсет в обходе `ToolsFor`; проверка трёх точек `BuildTurnContextContext` в `SessionManager` по образцу `:401`/`:436` |
| Границы вертикалей (ADR-014) | стор, контракты и `project-file` — в Core `Services.ChatContext`; вертикали знают только строки `Kind` друг друга, кадр видео из картинки — через существующий `IImageFrameSource` | строка `ChatContext` в `CoreAllowedNamespaces`; `SubsystemBoundaryTests` без новых allow; мутация: ссылка AudioEditor на тип провайдера ImageEditor краснеет |
| Один владелец вида | спина отказывает при старте, если два провайдера объявили один `Kind` | `ContextKindRegistryTests` (дубль → исключение при сборке реестра) |
| Строка = хвост = панель | подписи считает только `Describe`/`DescribeExecutor` на бэкенде; фронт рисует `label` из DTO | `TurnContextParityTests`: для набора элементов текст хвоста содержит ровно `label` из DTO; фронтовый юнит — `ContextRow` не форматирует подписи (нет импорта форматтеров вертикалей) |
| `UpdatedAt` не двигается | отдельный файл стора, не `sessions.json` | тест: `SetPrimary`/`AddRef`/`Clear` не меняют `Session.UpdatedAt` и `IsArchived` |
| Prefix cache | `TurnContextContributor` — `InTurnTail: true`, системного блока не касается | `ClaudeSessionPromptSectionsOrderTests`: секция `turn-context` только в хвосте при любом `RecallInTurnText` |
| Отключаемость | выключенная вертикаль не регистрирует провайдера; её элементы — `Missing`, в хвост не идут, ручки контекста не 500 | `ChatContextDisabledVerticalTests` (`Subsystems:AudioEditor:Enabled=false` при элементе `audio` в файле) |
| Делегированный ход | `context_attach`/`context_detach` — `[DenyOnDelegatedTurn]`, `DelegatedTurnGate` fail-closed | тест тулсета |
| Пути | `project-file` и все `Ref` с путями — `ProjectLinkGuard.ResolveInside`; личный чат — отказ до `RootPath`; локальный проект — `ProjectCapabilities` | тесты `Validate` провайдеров |
| Изоляция владельцев | файл ключуется `ownerId`; сессия — `GetOwned` | тест: чужая сессия → 404 |
| Агент не открывает панель | `By = Agent` → только `noteAgentPick` | e2e фазы 2 (Вера) |

> **Пересмотрено в Дополнении 2, §Р2:** «строка = хвост» держится по «С чем» и «Плюс». «Чем» — намеренное
> исключение: в «Чате» строка его не рисует, хвост и `context_state` пишут. `TurnContextParityTests` сверяет
> `label` из DTO с хвостом только по строкам «С чем» и «Плюс».

### 5. Фазы, файлы и что ломается

> **Заменено Дополнением 1, §Д5.** Текст ниже — исходный разрез; модель стора, хвост и `contextRevision` из
> него остаются в силе, раскладка по фазам и списки удаляемого — по §Д5.

**Перед фазой 1 — `fix/composer-modes` влить, а не закрывать.** Коммит `b2bdfb0a2` и незакоммиченная память
`composerModeMemory` чинят живой дефект (возврат в чат сам включает режим поверх ручного «Чат», мигает панель
режима) на старых полосах, на которых «Видео» доживёт до фазы 3. Память режима по чату переходит в новую модель
почти без изменений: ключом повода становится `kind:ref` основного объекта. Условие — e2e
`composer-modes-follow-strip.spec.ts` зелёный на 1440 и 360.

**Фаза 1 — стор и строка (Денис, Кира).** Флаг `composer-context-row`.

- Денис: `Core/Services/ChatContext/` (`IChatContextStore`, `ChatContextStore` на `JsonFileStore`, модели,
  `IContextKindProvider`, `ProjectFileContextKind`, `IChatSavedFiles`); `Core/Protocol` — `ChatContextDto`,
  `ChatContextChangedMessage`; `Controllers/ChatContextController.cs`; жизненный цикл на `session/deleted`,
  `session/branched`; флаг в `FeatureFlagCatalog.All`. **Засев**: GET чата без файла возвращает производное
  состояние из фокусов вертикалей (картинка, если есть, иначе звук) и ничего не пишет; файл появляется на первой
  записи. Провайдеры `image` и `audio` в вертикалях — только `Validate`/`Describe` (без `AcceptedRefs`).
- Денис: **двойная запись фокуса**. `ImageThreadService`/`AudioJobThreads` при смене фокуса пишут и своё `Focus`
  (старые полосы, флаг выключен), и `SetPrimary` (флаг включён). Чтение DTO вертикали при флаге — проекция из
  контекста: `focus = primary.kind == своё ? primary.threadId : null`. Без флага — своё поле, как сейчас.
- Кира: `lib/chatContext/*`, `SLOT_CONTEXT_KIND`, `components/chat/ContextRow.tsx`, хук `useGitChip` из
  `ProjectGitBar.tsx`; `Composer.tsx` — режим из основного объекта; `ChatPanel.tsx` — при флаге `ContextRow`
  вместо `ComposerStripHost`. Витрина `#/ui-kit` — раздел «Строка контекста».
- Кира: «Руки» — `handsStripManifest.tsx` при флаге вкладывает пилюлю в `composer-chip` («губа» поля) вместо
  `composer-strip`; `applyHandsFocus` (`requestStrip`/`releaseStrip`) при флаге не вызывается. Состояние рук в
  контекст не входит. В фазе 1, а не позже: иначе включённый флаг отнимает руки.

**Фаза 2 — «Картинки» и «Звук» переезжают (Денис, Кира).**

- Денис: провайдеры полностью (`AcceptedRefs`, `DescribeExecutor`, `usedBy`), `TurnContextContributor`,
  похудание `ImageEditorStateContributor`/`AudioEditorStateContributor` и правка текстов
  `LocalMediaDefaultContributor` под флагом; `contextRevision` в запусках (`ImageEditLaunchAssembler`,
  `AudioEditJobService`); загрузка образцов в рабочую папку; тулсет `turn-context` и его проводка в три точки
  `SessionManager`; `image_focus`/`audio_focus` → `SetPrimary(By = Agent)`; `IChatSavedFiles` у обеих вертикалей.
- Кира: вклады `context-kind` в `features/imageEditor/manifest.tsx` и `features/audioEditor/manifest.tsx`
  (режим переезжает из `composer-mode`); «В контекст ▾» на карточках (`ThreadCard`, `VersionCards`, звуковые
  карточки), в «Файлах» (`FileExplorer.tsx`, обобщённый `context-opener`), в «Персонажах» (`CharactersPanel`) и
  «Голосах» (`SoundPanel` вкладка `voices`); строка «Работаем с» панелей читает `useChatContext`;
  `_samples` и `CharacterSection` — на референсы; `cc_audio_inputs:` для путей — на референсы; из полос остаётся
  только код старого пути под выключенным флагом.

**Фаза 3 — «Видео» (Кира, Денис).**

- «Видео» — **после мержа блока 5** `feat/video-editor`: провайдеры `video-scene`/`video-film` (составной фокус
  `{sceneId, filmPath}` распадается: основным становится то, что выбрал человек последним; **уточнено в §Д4**:
  «Фильм» — редактор «Монтаж», не состояние панели), роли `frame-a`/`frame-b` вместо `FrameSlot`
  внутреннего выбора, похудание `VideoEditorStateContributor`, вклад `context-kind` вместо `composer-strip`
  `'video'` и `composer-mode` `'scene'`.

**Фаза 4 — приёмка и снятие флага (Вера, затем Кира и Денис).** Сценарии v4 и v5, обе темы, 360/800/1024/десктоп;
критерий — число кликов не растёт, строка и «Работаем с» совпадают, хвост равен строке. Затем удаляются:
`lib/composerStrips.ts`, `components/chat/ComposerStripHost.tsx`, слоты `composer-strip` и `composer-mode`,
`requestStrip`/`releaseStrip`/`notifyComposer` из кита, ключи `cc-composer-strip*` (чистка на старте),
`ImagesStrip.tsx`/полоса звука/`VideoStrip.tsx` видео-редактора, старые строки хвостов, чтение собственных
`Focus` вертикалей (поле в JSON остаётся — старые файлы обязаны читаться), запись `CharacterSlug` в префы.

**Что ломается у кого (при включённом флаге):**

| Вертикаль | Что ломается | Как чиним |
|---|---|---|
| Картинки | полоса с `SettingsPanel`-сводкой и переключателем «Создать / Править» над полем; `_samples` в памяти; персонаж на проект; самовключение через `autoSelect` + `requestImageMode` | сводка — чипы строки; переключатель — только в панели (у v5 следует из контекста); образцы и персонаж — референсы; режим — из основного объекта |
| Звук | полоса с режимами «Голос / Музыка / Обработка»; одновременный фокус со звуком и картинкой; входы-пути в `localStorage` | режимы — только в панели; основной объект один; пути — референсы |
| Видео | ничего до фазы 3: блок 5 заканчивает на `composer-strip` `'video'` и `composer-mode` `'scene'`, хост полос при выключенном флаге живёт. При включённом флаге до фазы 3 сцена в строке не видна — **флаг до фазы 3 включаем только у тех, кто не работает с «Видео»** (Андрей решает сам по тумблеру) | фаза 3 |
| Руки | пропадает полоса «Руки» (при флаге хост полос не рисуется) | в фазе 1 пилюля рук ставится сразу — это один вклад в `composer-chip`, переносим его в фазу 1, чтобы флаг не отнимал руки |
| Git | `ProjectGitBar` как полоса исчезает | чип «Где» с тем же меню |

### 6. Отвергнуто

1. **Оставить переключатель полос и добавить чипы.** Тот же выбор показан в двух местах (полоса и чипы),
   остаются память «какая полоса», правило старшинства и вертикальные сторы — пункты 1, 6, 7 списка «Чего быть
   не должно». Чипы стали бы третьим представлением, а не заменой.
2. **Стор контекста только на фронте** (как `composerStrips`) с отправкой состава в тексте хода клиентом. Агент
   меняет контекст инструментами, ход исполнителя задачи идёт без браузера, два устройства расходятся, — хвост
   обязан собираться на сервере из того же стора, что читает запуск.
3. **Поле контекста в `Session`.** Каждая правка переписывает `sessions.json` целиком и рядом с `UpdatedAt` —
   риск сдвинуть сортировку, архив и непрочитанность; плюс нагрузка на самый горячий файл.
4. **Общий `context_focus` вместо `image_focus`/`audio_focus`.** Проверка нити и черновика — знание вертикали;
   общий инструмент получил бы схему-объединение всех видов и ломал бы привычку агента и тесты тулсетов. Новые
   инструменты нужны только для референсов, которых сейчас у агента нет вовсе.
5. **Один общий блок хвоста вместо вертикальных.** Правила приоритета `*_generate` над `local_*` и журнал
   картинок — знание вертикали; затягивание их в спину нарушает ADR-014. Общий блок — только состав контекста.
6. **Подписи чипов считает фронт** (как `sceneChip`, «Авто → local» в полосе). Две реализации одной сводки —
   строка и хвост разойдутся на первой же правке формата. Сводку считает бэкенд, фронт рисует.
7. **Персонаж по-прежнему на проект.** Невидимый вход генерации, который не виден в строке и не виден агенту.
8. **Свой вид «черновик».** Черновик — нить без файла; отдельная сущность удвоила бы жизненный цикл.
9. **Закрыть `fix/composer-modes`.** Видео живёт на старых полосах ещё минимум до фазы 3, дефект там живой, а его
   память режима переиспользуется новой моделью.
10. **Дополнить ADR-021, а не заводить ADR-023.** Решение режет не только панель генерации: git, руки, хвост хода
    спины, новый MCP-сервер, персонажи и входы операций трёх вертикалей. ADR-021 §3 остаётся в силе в части
    каркаса панели и `genPanelDismissed`.

## Последствия

- У «С чем работаю» один источник правды на сервере; строка, панель, хвост и запуск читают его же.
- Меняется смысл для всех вертикалей: основной объект один на чат, персонаж — на чат, образцы переживают
  перезагрузку и видны агенту.
- Новая спинная подсистема в Core (`Services.ChatContext`) и новый MCP-сервер `turn-context`; три вертикали
  получают по провайдеру видов.
- Переходный период (фазы 1–3) держит двойную запись фокуса и два пути хвоста под флагом; фаза 4 удаляет старое.

## Открытые вопросы для макета Майи

- Геометрия строки на 360 (git иконкой, горизонтальная прокрутка чипов) и раскрытый список «+N ›».
- Вид серого чипа «не используется в операции» и выбор роли в «В контекст ▾» на телефоне.

---

## Дополнение 1 (2026-10-03): одна панель «Контекст» и чипы действий

**Основа:** решения Андрея после разреза (постановка задачи от 2026-10-02), макет
[composer-actions-v1](../mockups/composer-actions-v1-proposal.md) (разделы 1–3 и 6) и строка
[composer-context-row-v1](../mockups/composer-context-row-v1-proposal.md). Модель стора (§1), контракт бэкенда
(§2.1), наполнение (§2.3), агент (§2.4), хвост и `contextRevision` (§3) **не меняются**. Меняются фронт
(§2.2), фазы (§5) и объём.

Два решения:

1. **Одна панель «Контекст» вместо «Картинок», «Звука» и «Видео».** Операций в ней нет, «Ещё настроек» со
   схемой модели нет. Библиотеки «Персонажи», «Голоса», «Файлы» — отдельные панели с «В контекст / В контексте ✓».
2. **Чипы действий над текстом поля** вместо переключателя «Чат | Картинка» и вместо списков операций.
   Длинный хвост операций (кавер, мастеринг, в MIDI, доаранжировать, диалог…) из UI уходит, но остаётся у агента.

Факты по коду на 2026-10-03, на которые опирается дополнение:

- **Ключ `context` в `PANEL_KEYS` занят** панелью «Персона» (досье собеседника; `panelCatalog.ts:131`,
  бейджи в `useSessionPanels.tsx:67`, `artifacts/meta.tsx:17`). Отсюда новый ключ, см. §Д1.
- `fix/composer-modes` уже влит в master (`b2bdfb0a2`, `bac70c9aa`, мерж `617ee18d6`). Предусловие §5 выполнено.
- Блок 5 «Видео» закрыт 2026-10-02 (задача `bd61d9e5`), правки по дизайн-ревью тоже закрыты (`e8cf181e`).
  Открыта одна доработка Киры, `959c4c02` (чат в ручках фильма, загрузка кадра, тексты ленты). Ветка
  `feat/video-editor`: 48 коммитов, во фронте 72 файла и +6249 строк.

### Д1. Панель `chatContext` — хост-компонент оболочки

**Ключ — `chatContext`, заголовок «Контекст»**, иконка `SlidersHorizontal`. Ключ `context` занимать нельзя:
переименование панели «Персона» сломало бы сохранённые раскладки (`panelStackState` хранит ключи в
`localStorage`) и код бейджей, а новый ключ стоит одной строки в каталоге. Ключ совпадает с namespace стора
(`Services.ChatContext`) и не путается с «Персоной».

**Панель рисует оболочка, а не вертикаль.** Файл — `components/generation/ContextPanel.tsx`, каркас
`GenerationPanel`. Это панель оболочки, как `files`: в каталог она прописана напрямую, через слот
`workspace-panel-def` не регистрируется. Вертикали отвечают только за свой вид, через слот `context-kind`.
Секции:

| Секция | Кто рисует | Откуда данные | Что даёт вертикаль |
|---|---|---|---|
| Где | каркас | `useGitChip` (тот же, что у чипа строки) | ничего |
| С чем | каркас: рамка карточки, `label · version`, ‹ ›, ✕, кнопка «Открыть редактор» | `ChatContextDto.primary` (подписи из `Describe`) | `preview` (миниатюра, волна, кадр с ▶) и `editor` (подпись, подсказка, `open`) |
| Чем | каркас: `ExecutorList` | `executors(ctx, actionId)` из `context-kind` | строки под **выбранное действие**, а не под режим |
| Плюс | каркас: пилюли с ролями, серость с причиной, «Добавить из… ▾» | `ChatContextDto.refs[].usedBy` | ничего: роли и `usedBy` уже посчитаны на бэкенде (§2.1) |
| Параметры запуска | каркас: контролы **закрытого набора** | `params(ctx, actionId)` | список параметров, сама их не рисует |
| Низ | каркас: цена с расшифровкой, кнопка, прогресс, результат | `useActionRun` (см. §Д2) | `quote` и `launch` действия |

**Контракт вертикали для панели** — дополнения к `ContextKindApi`:

> **Пересмотрено в Дополнении 2, §Р1 и §Р2:** поле `defaultAction` **удалено** — умолчание выводит хост из
> порядка `actions`. Секция «Чем» в «Чате» — пустое состояние «Исполнитель появится, когда в поле выбрано
> действие»; `executors` зовётся только с `actionId` выбранного `run`-действия.

```ts
export interface ContextKindApi {
  kinds: readonly string[];
  icon: (kind: string) => ReactNode;
  actions: (ctx: ContextKindCtx, s: KindState) => readonly ContextAction[];   // §Д2
  defaultAction?: (s: KindState) => string | null;   // null — «Чат»; у агентского объекта хост берёт «Чат» сам
  preview: (ctx: ContextKindCtx, item: ChatContextItem) => ReactNode;          // только картинка, волна, кадр
  editor?: (ctx: ContextKindCtx, item: ChatContextItem) => { label: string; hint: string; open: () => void } | null;
  executors?: (ctx: ContextKindCtx, actionId: string) => ExecutorListModel | null;
  params?: (ctx: ContextKindCtx, actionId: string) => readonly LaunchParam[];
  create?: { title: string; icon: ReactNode; run: (ctx: ContextKindCtx) => void };
  // mode — УДАЛЁН (было в §2.2); open(…, 'object' | 'executor') — тоже: клик по чипу строки открывает chatContext
}
// Закрытый набор параметров: новые значения добавляются правкой типа, а не вкладом вертикали
export type LaunchParam =
  | { kind: 'variants'; min: 1; max: number; value: number }
  | { kind: 'duration'; options: readonly number[]; value: number }
  | { kind: 'aspect'; options: readonly string[]; value: string }
  | { kind: 'fromQuestion'; label: string };          // «9:16 · выбирается под чипами» — только подпись
```

Почему у «Параметров» закрытый набор, а не `ReactNode` от вертикали: свободный слот за неделю снова
превратится в «Ещё настроек» со схемой модели. Решение постановки «схемы модели в панели нет» держит тип, а
не дисциплина.

**Библиотеки — отдельные панели:**

| Библиотека | Ключ | Откуда | «В контекст» |
|---|---|---|---|
| Персонажи | `characters` (ключ в каталоге уже есть) | вкладка панели `images` → самостоятельная панель вклада ImageEditor | `attachRef({kind: 'image-character', role: 'character'})` |
| Голоса | `voices` (**новый**) | вкладка `voices` панели `SoundPanel` (`VoicesTab`) → самостоятельная панель вклада AudioEditor; туда же «Обучить голос» (§Д3) | `attachRef({kind: 'audio-voice', role: 'voice'})` |
| Файлы | `files` | как есть | «В контекст ▾» и «Работать» (§2.3) |

«Добавить из… → из „Персонажей“ / из „Голосов“» зовёт `revealWorkspacePanel('characters' | 'voices')`.
Вкладки над панелью в макете — это обычная стопка панелей зоны, своих вкладок у хоста нет.

**Что становится с механикой панелей:**

| Что | Сейчас | При флаге `composer-context-row` | После снятия флага |
|---|---|---|---|
| `PANEL_KEYS` | `images`, `sound` (+ `videoEditor` на ветке) | добавлен `chatContext`; три старых ключа вне `allowedKeys` экрана: в рельсе их нет, панели не монтируются | три ключа удалены; `LEGACY_KEY_ALIASES` сводит `images`/`sound`/`videoEditor` → `chatContext`, чтобы сохранённая раскладка не теряла место панели (`migrateLegacyKey` уже это умеет) |
| `GEN_PANEL_KEYS` (две копии: `panelCatalog.ts:213` и `genPanelDismissed.ts:16`) | `['images', 'sound']` | `['chatContext']` при флаге, старые ключи без флага | одна копия, `['chatContext']` |
| `EXCLUSIVE_PANEL_SETS` | «справа одна панель генерации» | набор из одного ключа — исключать некого | удаляется вместе с `panelRivals`, если других наборов не появится |
| `genPanelDismissed` | ключ `{session}:images` / `{session}:sound` | ключ `{session}:chatContext`; старые записи не мигрируем: это признак вида, пусть доживут в списке из 500 | так же |
| `genPanelPlacement` (планшетная зона держит поток) | для `GEN_PANEL_KEYS` | для `chatContext` | так же |
| `revealWorkspacePanel(key, tab)` | вертикали зовут с `'images'`/`'sound'` и вкладкой `'settings'`/`'voices'` | вертикали зовут **`revealContextPanel(sessionId, opts)`** из кита: тонкая обёртка над `revealWorkspacePanel('chatContext', …)`, `tab` не нужен (вкладок нет) | сигнатура `revealWorkspacePanel(key, tab)` остаётся для прочих панелей; вызовы с ключами генерации запрещены сторожем (§Д6) |
| `followSelection(panelKey, …)` | по ключу вертикали | ключ всегда `chatContext`; параметр уходит | так же |

**`returnTo` и `preset` при одной панели.** На ветке видео это пара «открой чужую панель с заготовкой и дай
ссылку назад» (`genPanelReturn.ts`, `preset.ts` у картинок и звука). Когда панель одна, «чужой панели» нет, а
«назад» означает **вернуть прежний основной объект**. Поэтому:

- `returnTo` → **`ContextReturn { prev: ChatContextItem; label: string }`**. Состояние вида на фронте (как
  сейчас), ключ — сессия, а не панель. Модуль кита `lib/chatContext/contextReturn.ts`: `setContextReturn(sessionId, r)`,
  `useContextReturn(sessionId)`, `clearContextReturn(sessionId)`. Читает его секция «С чем» каркаса, пишут вклады
  вертикалей (меню кадра, «Сочинить под фильм»). Ссылка «К сцене «…»» в секции «С чем» вызывает `setPrimary(prev)`.
  Новым основным объектом через «Работать с этой» ссылка снимается, как сейчас снимается показом без `returnTo`.
- `preset` → **предвыбор действия**: `{ actionId, prefill?: string, params?: Partial<…> }`. Он уходит в память
  действия поля (§Д2), а не в панель: `presetAction(sessionId, objectKey, preset)` в
  `lib/chatContext/actionMemory.ts` (там же память выбора чипа, бывшая `composerModeMemory`). Хост применяет
  предвыбор один раз, когда `primary` становится `objectKey`. Панели нечего предзаполнять: операций и форм в ней нет.
- **«Нарисовать кадр в Картинках»** (меню «Кадр A/B ▾» → «Править в „Картинках“» или «Нарисовать»):
  `createImageThread({draftFolder})` → `setPrimary(image-черновик)` → предвыбор `{ actionId: 'draw' }` →
  `ContextReturn(prev = сцена)`. `bindFrame` (`imageFrames.ts`) остаётся: первая готовая версия становится
  кадром. Привязка по-прежнему живёт в памяти вкладки и теряется на перезагрузке. Это известный остаточный
  риск ветки, дополнение его не вводит и не чинит.
- **«Сочинить под фильм»** (`film/compose.ts`): черновик звука → `setPrimary(audio-черновик)` → предвыбор
  `{ actionId: 'song', prefill: soundPreset(...).prompt }` → `ContextReturn(prev = фильм)`. Музыка в фильм
  кладётся теми же швами блока 2.
- `usePanelReturnTo(key)` / `usePendingPreset(key)` / `consumePreset` при флаге не читаются. После снятия
  флага `genPanelReturn.ts` переписывается в `contextReturn.ts` (около 40 строк) или удаляется.

### Д2. Чипы действий — контракт вида

**Действие ≠ операция.** Действие — это то, что видит человек («Изменить»). Операция — то, что принимает API
(`edit` | `inpaint`). Действие **резолвит** операцию по состоянию объекта. Так `pickOp` перестаёт быть
отдельным режимом «Авто» и становится частью действия «Изменить».

```ts
export type ActionKind = 'run' | 'editor' | 'menu';
export interface ContextAction {
  id: string;                      // стабильный на вид: 'edit', 'removeBg', 'stems', 'shoot', 'frameB', 'build', 'montage'
  kind: ActionKind;                // run — режим (радио); editor — вход в редактор; menu — меню (кадр)
  label: string;                   // «Изменить отмеченное»
  hint: string;                    // тултип чипа
  // только у run:
  op?: string;                     // уже резолвленная операция вертикали: 'inpaint', 'separate', 'shoot'
  text?: 'required' | 'optional' | 'none';
  placeholder?: string;            // «Что изменить на картинке…»
  question?: { param: string; title: string; options: readonly { value: string; label: string }[] }; // один, первое предвыбрано
  disabledReason?: string;         // серая кнопка с причиной: «Добавьте куски через «В контекст»»
  // только у editor:
  open?: () => void;
  // только у menu:
  items?: () => readonly ContextMenuItem[];
}
// KindState — то, от чего зависит строка: DTO основного объекта и локальное состояние вида
// (маска редактора картинки, роли версии звука `stem:*`, снята ли сцена)
```

**Правила хоста `ComposerActionRow`** (`components/chat/ComposerActionRow.tsx`, рисуется внутри `Composer`
над текстом):

> **Пересмотрено в Дополнении 2, §Р1 и §Р3:** выбор по умолчанию у объекта человека — первое `run`-действие без
> `disabledReason`, иначе «Чат» (не `defaultAction(s)`). Подпись кнопки несёт и текущие параметры
> («Вариантов», «Длительность»); источник — `useActionRun.label`.

- строка есть только при `primary != null` и у вида есть `actions`;
- первый чип **«Чат» добавляет хост**, вертикаль его не объявляет. Вертикаль отдаёт не больше **5** действий,
  итого с «Чатом» не больше 6. Лишнее хост **не обрезает молча**: в dev он бросает исключение, сторож — тест
  (§Д6);
- выбранный чип — радио, только среди `run` и «Чата». `editor` и `menu` выбор не меняют;
- `question` рисуется строкой под чипами, значение уходит в `params[question.param]`;
- выбор по умолчанию: объект, который поставил человек, → `defaultAction(s)`, объект агента (`By = Agent`) →
  всегда «Чат». После запуска выбор остаётся, если в новом состоянии есть действие с тем же `id`; иначе «Чат»;
- **память выбора** — `composerModeMemory` из `fix/composer-modes`, ключ меняется на `{sessionId}:{kind:ref}`, а
  значение с id режима на id действия. Ручной уход в «Чат» держится, пока ключ объекта тот же (поведение
  `bac70c9aa` сохраняется);
- при текущем действии `run` поле берёт у него `placeholder`, а кнопка — подпись `✦ {label} · {цена}`.
  «Чат» отправляет сообщение агенту, как сейчас.

**Что заменяет слот `composer-mode` окончательно.** Слот удаляется в фазе 4. Поля `ComposerModeApi`
расходятся так:

| Поле `ComposerModeApi` | Куда |
|---|---|
| `title`, `icon`, `strip`, `isAvailable`, `autoSelect` | нет: доступность — наличие основного объекта, повод — смена `kind:ref` |
| `placeholder`, `submitLabel`, `hint` | `ContextAction.placeholder` + подпись хоста из `useActionRun` (`hint` над полем больше не нужен: его роль у чипов) |
| `onSubmit` | `launch` вида (ниже), зовёт его хост |
| `prefill`, `draftKey` | хост: `prefill` — из предвыбора (`preset`, §Д1), черновик текста — по ключу `{kind:ref}:{actionId}` (`genDrafts`) |
| `emptySubmit` («↻ Ещё 2») | **уходит**: действия с `text: 'none'` запускаются при пустом поле штатно, повтор прошлого — через агента или снова чипом |
| `onTextChange` | не нужен: цену считает `useActionRun` от текста поля |

**Как действие превращается в запуск — одна точка.** Хук кита `useActionRun(sessionId)` возвращает
`{ action, label, quote, state: idle|running|done|error, progress, result, run(text) }`. Им пользуются **и**
кнопка поля, **и** низ панели: вторая логика запуска невозможна по построению. `run` зовёт вклад вида:

```ts
launch(ctx, { op, text, params, contextRevision }): Promise<LaunchHandle>
```

Вертикаль шлёт в свой существующий эндпоинт запуска `op` + `params` (варианты, длительность, пропорции, набор
стемов) + `contextRevision`. Входы (нить, версия, маска, референсы, голос, куски, кадры) **не передаются**:
бэкенд читает их из стора по ревизии (§3.2). `409 context_changed` → хост перечитывает DTO, кнопка
пересчитывает цену, запуск не повторяется сам. Цена — те же `estimate`/`quote` вертикали, но по `op` действия,
а не по выбору в панели.

**Чем это отличается от сегодняшнего:**

| Сейчас | Что делает | Стало |
|---|---|---|
| `pickOp(hasImage, hasMask)` (`imageEditor/format.ts:85`) | «Авто»: операция по холсту | остаётся чистой функцией **внутри** `actions` картинки: «Изменить» → `edit`, при маске — `inpaint` и подпись «Изменить отмеченное» |
| `PanelOp`, `PANEL_OPS`, `resolveOp`, `opBlockReason`, `opHint`, `EDIT_MODES` (`panel/panelOp.ts`) | выбор операции и режима подбора в панели | удаляются. Причина серости переезжает в `disabledReason`, `runVerb` — в `label` |
| `ImageModeSwitch` «Создать / Править», `modeState` | режим картинки | нет: черновик даёт «Нарисовать», картинка с файлом даёт «Изменить…» |
| `opGroups.ts` `opOptions` (`Select` с группами «Новый / С выбранным / Без ИИ / Из нескольких») | 27 операций звука по трём режимам | удаляется. Пять действий на состояние (§Д3), остальное — у агента |
| `AudioMode` voice/music/process, `SoundModeSwitch`, `panelOps`, `HIDDEN_OPS`, `pillOf`, `SPEAK_SOURCES` | режим звука и пилюли | для человека режима нет. `AudioMode` остаётся **внутренним** ключом префов исполнителя (`opInfo(op).mode` → префы «Чем»), чтобы не мигрировать префы |
| Источник голоса «Готовый диктор / По описанию / Образец» | переключатель внутри «Озвучить» | резолв `op` по референсам: есть `audio-voice` → `speak` с голосом; есть `reference` → `cloneVoice`; иначе `speak` диктором по умолчанию. `designVoice` — только агенту |

#### Д2.1. Запуск: эндпоинты и тела

Новых эндпоинтов запуска нет. Существующие котировки и запуски получают при флаге поле
**`ContextRevision`** (`long?`). Если оно задано, сервер берёт входы из стора по этой ревизии (§3.2), а поля
входов в теле **не ждёт и игнорирует** (нить, версия, сцена, референсы, голос, куски, кадры, персонаж). Не
совпала ревизия — `409 context_changed`. Без поля — старое поведение, им пользуются старые панели и агент.
У звука и видео запуск идёт строго по `QuoteId`, поэтому ревизию фиксирует котировка: запуск с `QuoteId`,
посчитанным на другой ревизии, — тоже `409 context_changed`.

| Вид | Цена (котировка) | Запуск | Что из тела уходит при `ContextRevision` | Что остаётся в теле |
|---|---|---|---|---|
| `image` | `POST api/projects/{p}/image-editor/quote`, `ImageEditQuoteRequest` (`ImageEditDtos.cs:54`) + `SessionId`, `ContextRevision` | `POST …/image-editor/jobs` + `ContextRevision` | `References`, `HasCharacter`, `Width`/`Height` (сервер считает их по стору) | `Op` (резолв действия), `Count`, `Provider`/`Model` из «Чем», `HasMask`/`HasAnnotations`/`Removal` и сами отметки (`getThreadMarks`): это состояние редактора, а не контекст; промпт; пропорции у `outpaint` |
| `audio` | `POST api/projects/{p}/audio-editor/quote`, `AudioQuoteRequest` (`AudioEditContracts.cs:59`) + `ContextRevision` | `POST …/audio-editor/jobs`, `AudioJobInput` по `QuoteId` + `ContextRevision` | `ThreadId`, `BaseVersionId`, `VoiceKind` (выводится из референса `audio-voice` или `reference`), `Voice`, входы-пути | `Operation` (резолв действия), `Count`, `Text`/`Prompt`/`Lyrics`, `Language`, `StartSec`/`EndSec` (выделение на волне для `repaint`, состояние редактора), `Fields` (`stemSet`) |
| `audio` · Свести | — (без ИИ) | `POST …/sessions/{s}/threads/{t}/mix` + `ContextRevision` | — (`t` в маршруте — значение из DTO, сервер сверяет его со стором) | как сейчас |
| `audio` · Склеить | — | `POST …/sessions/{s}/concat` + `ContextRevision` | список кусков: это референсы роли `piece` в порядке `AddedAt` | `joint` |
| `video-scene` | `POST api/projects/{p}/video-editor/quote`, `VideoQuoteRequest` (ветка, `VideoQuoteLaunch.cs:9`) + `ContextRevision` | `POST …/video-editor/jobs`, `VideoLaunchRequest` по `QuoteId` + `ContextRevision` | `SceneId` (из стора), кадры (референсы `frame-a`/`frame-b`) | `Count`, `DurationSec`, `Aspect`, `Sound`, `Provider`/`Model`; текст поля — в `Params.request` (просьба поверх текста сцены) |
| `video-film` · Собрать | — (без ИИ) | `POST …/video-editor/films/build?path=` + `ContextRevision` | — (`path` в query — значение из DTO, сервер сверяет) | — |

У личного чата пути те же, что у `Personal*Controller` (`api/…-editor/chats/{sessionId}/…`). Обрезка,
затухание (`threads/{t}/edit`) и «Без ИИ» картинки (`transform`) вызываются из редакторов, а не из чипов.
Их тела не меняются.

> **Пересмотрено в Дополнении 2, §Р2:** поля `executor` в `ChatContextDto` нет. В «Чате» чип «Чем» строка не
> рисует (в лестнице он 0 px), секция панели — пустое состояние. «Чем» есть только при выбранном действии и
> только из `quote` по его `op`; в шаге 1ф «Чем» в строке не показывается вовсе.

**Откуда «Чем».** Строка «Чем» в чипе строки и секция «Чем» в панели берут данные из **`quote` по `op`
выбранного действия**: ответ `quote` дополняется строками исполнителей (`ExecutorRow[]` с «Авто → сейчас: …»,
ценой и причиной серости). Это работа Дениса в шаге 2б. Сегодня строки собирают `executorRows.ts`
вертикалей из каталога. Их можно оставить на фронте как функцию `(catalog, op)`, если Денис не успевает, но
источник в итоге один. Поле `executor` в `ChatContextDto` (§2.1) показывает исполнителя **«Чата»**, то есть
первого действия (вопрос макета §7.2), и строка без выбранного действия читает его. При выбранном действии
приоритет у `quote`. **В шаге 1ф `quote` не вызывается:** строка показывает только `ChatContextDto.executor`,
чтение `quote` по действию добавляют шаги 2к, 2з и 3 вместе с действиями своего вида.

#### Д2.2. Каталог действий по состояниям (источник для тестов §Д6)

> **Пересмотрено в Дополнении 2, §Р1:** колонка «По умолч.» ниже недействительна — действующая таблица
> умолчаний в §Р1 Дополнения 2 (у звука это не «Чат»: черновик — `speak`, речь — `speakMore`, песня — `stems`,
> стемы — `mix`; у фильма — `build`). Чипы черновика звука подтверждены: «Озвучить · Песня · Эффект».

`T` — текст: `req` нужен, `opt` по желанию, `—` не нужен. «По умолч.» — `defaultAction` для объекта,
выбранного человеком. Объект агента всегда начинает с «Чата».

| Вид · состояние | Признак состояния | Действия (id · вид · op · T · вопрос) | По умолч. |
|---|---|---|---|
| `image` · черновик | у нити нет версии | `draw` · run · `generate` · req | `draw` |
| `image` · с файлом | есть версия, отметок нет | `edit` · run · `edit` · req; `removeBg` · run · `removeBackground` · —; `upscale` · run · `upscale` · —; `outpaint` · run · `outpaint` · — · «Пропорции: 16:9 · 9:16 · 1:1 · 4:3»; `mark` · editor (`openEditor(…, {tool: 'brush'})`) | `edit` |
| `image` · с отметками | `getThreadMarks(t).marks.length > 0` | как «с файлом», но `edit` → подпись «Изменить отмеченное», `op = inpaint` | `edit` |
| `audio` · черновик | у нити нет версии | `speak` · run · `speak`/`cloneVoice` · req; `song` · run · `song` · req; `sfx` · run · `sfx` · req (**предложение**, открытый вопрос 1) | «Чат» |
| `audio` · речь | версия без ролей `stem:*`, `thread.settings.mode = voice` | `speakMore` · run · `speak`/`cloneVoice` · req; `convert` · run · `convertVoice` · —; `denoise` · run · `denoise` · —; `stems` · run · `separate` · — · «Набор: Вокал + минус · 4 стема · 6 стемов · Караоке» | «Чат» |
| `audio` · песня | версия без `stem:*`, `thread.settings.mode = music` | `repaint` · run · `repaint` · req (серая без выделения: «Выделите кусок на волне в редакторе»); `stems`; `denoise`; `concat` · run · `concat` · — (серая без кусков: «Добавьте куски через «В контекст»») | «Чат» |
| `audio` · со стемами | у текущей версии есть роли `stem:*` | `mix` · run · `mixStems` · —; `concat` | «Чат» |
| `video-scene` · не снята | у сцены нет клипа | `shoot` («Снять») · run · съёмка · opt; `frameA` · menu; `frameB` · menu | `shoot` |
| `video-scene` · снята | клип есть | `shoot` с подписью «Переснять»; `frameA`; `frameB` | `shoot` |
| `video-film` | — | `build` («Собрать», при готовом файле — «Пересобрать») · run · сборка · —; `montage` · editor («Монтаж») | «Чат» |

Признаки читаются из сторов вертикалей. Звук — `features/audioEditor/thread/threadStore`: `AudioThread.settings.mode`
и роли файлов текущей версии `AudioThreadVersion.files[].role`. Сцена — наличие версии у нити сцены
(`store/videoStore`). Отметки картинки — `getThreadMarks` (`imageEditor/thread/threadStore`).

У звука с исходной версией, которая получена не озвучкой и не песней (загружена файлом), `mode` нити нет.
Такая версия считается «песней»: набор действий шире, `repaint` честно серый, пока нет выделения.

**Мост в `Composer` на переходный период (шаг 1ф).** При флаге и непустом `primary`: если вклад
`context-kind` вида объявляет `actions`, рисуется `ComposerActionRow`, а `composer-mode` для этого вида не
читается. Если `actions` нет (вертикаль ещё не переехала), работает старый путь «Чат | X» из `composer-mode`.
Решение принимается по виду в одной функции `composerSurfaceFor(kind)`, её держит юнит-тест. Без флага —
только старый путь.

### Д3. Операции, уходящие из UI

> **Подтверждено в Дополнении 2, §Р6:** чипы черновика «Новый звук» — «Озвучить · Песня · Эффект»; «Обучить
> голос» — кнопкой в панели «Голоса» (и у агента); `enhanceFaces` и `EditMode` — только агенту.

**Правило:** из API, MCP-схем, каталогов моделей (`caps.ops`) и исполнителей **не удаляется ничего**. Уходит
только UI выбора. Агент работает словами по-прежнему. Состав `tools/list` не меняется, поэтому
`McpToolsetStabilityTests` и тесты тулсетов не трогаются.

**Картинки** (`ImageEditOp`: generate, edit, inpaint, outpaint, removeBackground, upscale, enhanceFaces):

| Остаётся чипом | Уходит из UI |
|---|---|
| Нарисовать (`generate`), Изменить / Изменить отмеченное (`edit`/`inpaint`), Убрать фон, Увеличить (`upscale`), Дорисовать (`outpaint`, вопрос «Пропорции»), ✎ Отметить (editor) | `enhanceFaces` (только агент); режим подбора `EditMode` (Быстро / Точно / Фотореализм — UI шлёт `auto`); «размер оригинала» `matchSourceSize` (UI шлёт умолчание); «Без ИИ» (обрезать, повернуть, формат) — **в редактор картинки** через «Открыть редактор», не агенту |

**Звук** (27 записей `OPS` в `audioEditor/ops.ts`):

| Состояние | Чипы (кроме «Чата») |
|---|---|
| Черновик «Новый звук» | **в макете не задано, предложение:** Озвучить (`speak`/`cloneVoice` по референсу) · Песня (`song`) · Эффект (`sfx`) |
| Речь | Озвучить ещё (`speak`) · Сменить голос (`convertVoice`) · Убрать шум (`denoise`) · Стемы (`separate`, вопрос «Набор») |
| Песня | Перегенерировать кусок (`repaint`, кусок — выделение на волне) · Стемы · Убрать шум · Склеить (`concat`, серая без кусков) |
| Версия со стемами | Свести (`mixStems`) · Склеить |

Уходят из UI: `dialogue`, `designVoice` (как отдельный выбор), `cover`, `outpaint` («Продолжить»), `extract`,
`lego`, `complete`, `upsample`, `master`, `transcribe`, `align`, `toMidi`, `normalize` — **только агент**.
`trainVoice` — кнопка «Обучить голос» в панели «Голоса»: это действие библиотеки, а не объекта. Без неё
обучение у человека пропадает совсем; если Андрей решит иначе, это тоже только агент.

**«Без ИИ» звука:**

- `trim` и `gainFade` — **в редактор звука** (волна и кусок, «Открыть редактор»). Поля `OpFields` для обрезки
  и затухания переезжают туда, запуск — тот же `POST …/dsp` без ИИ. **Это новая работа Киры**: сейчас обрезка
  — операция панели, а не поверхность редактора.
- `normalize` — только агенту. Чипом её не делаем: результат на слух почти не отличим, а место в строке
  занимает.
- `mixStems` и `concat` остаются чипами («Свести», «Склеить»), исполнитель в «Чем» — одна строка «Без ИИ · на
  сервере · бесплатно».

**Видео:** Снять / Переснять (`shoot`) · Кадр A ▾ · Кадр B ▾ (menu) · Собрать / Пересобрать (`build`) · Монтаж
(editor). Текст сцены, «Развернуть», сценарий фильма, монтажный список и музыка уезжают в редакторы «Сцена» и
«Монтаж».

**Карточки ленты:** у карточек картинки, звука и сцены остаются «Работать с этой» и «В контекст ▾». Меню
«Обработать ▾» (`audioEditor/thread/ThreadCard.tsx`) и быстрые действия на карточках при флаге не рисуются,
после снятия флага удаляются.

**Затронутые тесты фронта** (переписать при флаге или удалить в фазе 4, бэкенд не затронут):

- юниты: `panelOp.test.ts`, `OpSection.test.ts`, `panelV5.test.ts`, `ImagesPanel.test.ts`, `imageMode*.test.ts`,
  `modeState.test.ts`, `settingsToggle.test.ts`, `sheetReveal.test.ts` (картинки); `variantA.test.ts`,
  `model.test.ts` (части `panelOps`), `settings.test.ts`, `reveal.test.ts`, `strip/summary.test.ts` (звук);
  `composerModes.test.ts` (ключ памяти); `genPanelDismissed.test.ts`, `panelExclusive.test.ts`;
- e2e: `image-panel-v5`, `image-panel-legacy`, `audio-panel-a`, `audio-mode-switch`, `audio-mode-keep`,
  `audio-ui-merged`, `composer-modes-follow-strip`, `composer-mode-autosize` — заменяются набором
  `context-actions-*.spec.ts` по сценариям 1–9 макета. `audio-editor-card`, `audio-agent-cards`,
  `audio-editor-music` — правка селекторов карточек;
- новые: каталог действий по видам (§Д6), `useActionRun` (паритет), `ContextPanel` (секции от вида, закрытый
  набор параметров).

### Д4. «Видео»: блок 5 доделывается как есть и переезжает в фазе 3

**Рекомендация: блок 5 не останавливать.** Он уже закрыт, открыта только доработка `959c4c02`. Её стоит
доделать и влить ветку как есть. Бэкенд блоков 1–3 (сцены, фильм, сборка, швы, тулсет `video-editor`, блок
хвоста) переезжает без потерь. Выбрасывается только оболочка панели, и стоимость её уже заплачена. Если
остановиться сейчас, потеря не уменьшится, а фаза 3 останется без базы: провайдеры `video-scene`/`video-film`
опираются на сервисы и стор ветки.

**Как переезжает:**

| Сейчас на ветке | В модели «Контекст + чипы» |
|---|---|
| панель `videoEditor`, вкладки «Сцена» / «Фильм» | панель `chatContext`. Основной объект — сцена (`video-scene`) **или** фильм (`video-film`), но не оба: составной фокус `{sceneId, filmPath}` распадается. Фильм становится основным через «Работать с этой» на карточке фильма. Сцена становится основной через карточку сцены или через «Работать со сценой» в редакторе «Монтаж»; тогда ставится `ContextReturn(prev = фильм)`, ссылка «К фильму «…»». Чип «Монтаж» — `editor`, основной объект он **не меняет**. Фильм, к которому относится сцена, провайдер `video-scene` показывает в `Describe` («сцена 3 · утро-в-горах»), отдельным полем стора он не хранится |
| нижняя кнопка «Снять» панели | чип «Снять» / «Переснять» → `useActionRun` → тот же эндпоинт съёмки с `contextRevision` |
| `FrameSlot` A → B крупно в теле | чипы-меню «Кадр A ▾» / «Кадр B ▾»: шапка с именем кадра, «Править в „Картинках“», «Из проекта», «Кадр B прошлой сцены», «Убрать кадр». Логика пунктов — `scene/actions.ts` (`drawInImages`, загрузка, `ProjectPicker`) — переиспользуется; роли `frame-a`/`frame-b` — референсы (§1) |
| текст сцены, «Развернуть», длительность | текст — редактор «Сцена» («Открыть редактор»), поле добавляет просьбу (`text: 'optional'`); длительность — `LaunchParam duration` |
| вкладка «Фильм» (`FilmTab`, `FilmList`, `ScriptView`) | **редактор «Монтаж»**: отдельная поверхность, вход — чип «Монтаж» или «Открыть монтаж» в «С чем». Компоненты переносятся в неё, не переписываются |
| сборка фильма | чип «Собрать» / «Пересобрать», «Чем» — «Без ИИ · на сервере» |
| `VideoStrip` + `strip/summary`, `composer/sceneMode` (`composer-strip` `'video'`, `composer-mode` `'scene'`) | вклад `context-kind` вида `video-scene`/`video-film` |
| `genPanelReturn`, `preset.ts` картинок и звука | `ContextReturn` + предвыбор действия (§Д1) |

**Что из блока 5 становится выброшенной работой** (оценка по `git diff --stat master...feat/video-editor`):

| Файлы | Строк | Судьба |
|---|---|---|
| `panel/VideoPanel.tsx`, `panel/SceneTab.tsx`, `panel/FrameSlot.tsx`, `panel/VideoSheet.tsx`, `panel/primitives.tsx` | ≈ 500 | **выбрасываются**: хост и тело вкладки заменены каркасом `chatContext` и чипами |
| `strip/VideoStrip.tsx`, `strip/summary.ts` + тест | ≈ 290 | **выбрасываются**: полосы нет |
| `composer/sceneMode.tsx` | 86 | **выбрасывается**: слота `composer-mode` нет |
| `lib/genPanelReturn.ts` + тест, `preset.ts` картинок и звука, правки `ImagesPanel`/`SoundPanel` под пресет | ≈ 270 | **выбрасываются или ужимаются** до `contextReturn.ts` |
| правки `panelCatalog.ts`, `panelExclusive.test.ts` под `videoEditor` | ≈ 30 | откатываются в фазе 4 |
| `e2e/video-editor.spec.ts` + `videoPanelMock.ts` | 966 | **около половины переписывается**: шаги панели и полосы. Мок API и шаги ленты остаются |
| `film/FilmTab.tsx`, `FilmList.tsx`, `ScriptView.tsx` | ≈ 670 | **переносятся** в редактор «Монтаж»: меняется хост, а не логика |
| `panel/SceneText.tsx`, `ProjectPicker.tsx`, `useScene.ts` | ≈ 290 | переносятся: в редактор «Сцена» и в меню кадра |
| `store/*`, `api.ts`, `scene/*`, `film/model.ts`, `feed/*`, `imageFrames.ts`, сборка `modules/video-editor` | ≈ 2800 | остаются как есть |
| бэкенд блоков 1–3 | весь | остаётся. Худеет только `VideoEditorStateContributor` (как у картинок и звука, §3.1) |

**Итог для Андрея:** из ≈ 6250 строк фронта ветки выбрасывается ≈ 1200 строк компонентов (≈ 19 %) плюс
≈ 480 строк e2e. Ещё ≈ 960 строк переезжают в новые хосты. Остановка блока 5 сейчас эту долю не уменьшит: код
уже написан. Она только отложит появление «Видео» у людей до конца фазы 3.

### Д5. Фазы и объём (заменяет §5)

Флаг один — `composer-context-row`. Всё новое в UI идёт за ним. Без флага всё работает как сегодня: панели,
полосы и `composer-mode`.

> **Пересмотрено в Дополнении 2, §Р1, §Р2, §Р7:** в 1ф «Чем» в строке нет (только «Чат»), `executors(actionId)`
> вида появляются в 2к, 2з и 3 вместе с `quote` по `op`; умолчание — `pickDefaultAction` хоста, а не
> `defaultAction` вида. После фазы 2 — промежуточный мерж в master под выключенным флагом (ТО-4а).

| Шаг | Кто | Что | Зависит от | Готово, когда |
|---|---|---|---|---|
| 0 | — | `fix/composer-modes` влит | — | **выполнено** (`617ee18d6`) |
| 0в | Кира; мерж — по просьбе Андрея | доделать `959c4c02`, ревью Глеба, влить `feat/video-editor` **как есть** | ревью Глеба | ветка в master; зелёные `dotnet test`, `npm run build`, `lint:design`, `video-editor.spec.ts`. **Идёт до 1ф**: 1ф правит `panelCatalog.ts`, `genPanelDismissed.ts` и `registryCore.ts`, которые ветка тоже трогает. Если Андрей отложит мерж, 1ф начинается на master, а конфликт решает тот, кто вливает ветку позже |
| 1б | Денис | §5 фаза 1 бэкенд без изменений: стор, DTO, контроллер, `ProjectFileContextKind`, двойная запись фокуса, засев, флаг | — | тесты стора и `ChatContextController` |
| 1ф | Кира | `lib/chatContext/*` (с `contextReturn.ts` и `actionMemory.ts`), `ContextRow` (строка контекста, `useGitChip`), «Руки» в `composer-chip`; **плюс** каркас `ContextPanel` (Где, С чем без `preview`, Плюс, пустые состояния), ключ `chatContext`, `revealContextPanel`, `GEN_PANEL_KEYS` при флаге; **плюс** хост `ComposerActionRow` и `useActionRun` с одним «Чатом» и мостом: у вида без `actions` при флаге работает старый `composer-mode` | 1б (DTO), 0в | витрина `#/ui-kit` «Строка контекста» и «Панель Контекст»; e2e: флаг включён — строка, панель и «Чат» на 1440 и 360 |
| 2б | Денис | §5 фаза 2 бэкенд **без изменений по объёму**: провайдеры целиком, `TurnContextContributor`, похудание блоков, `contextRevision` в запусках, загрузки образцов, тулсет `turn-context`. **Новое:** запуск картинки и звука принимает `op` + закрытый набор `params` + `contextRevision` без входов. `estimate`/`quote` по `op` | 1б | тесты запуска по ревизии, `409 context_changed`, паритет хвоста |
| 2к | Кира | картинки: `actions` (3 состояния §Д2.2: черновик, с файлом, с отметками), `preview`, `editor` (маска и «Без ИИ»), `executors(actionId)`, `params`; панель `characters` самостоятельной; «В контекст ▾» на карточках, «Работать с этой», меню карточек убрать при флаге; `_samples` и персонаж → референсы | 1ф, 2б | сценарии 1–4, 6, 7 макета e2e на 1440/1024/360 |
| 2з | Кира | звук: `actions` (черновик, речь, песня, стемы), резолв источника голоса, `question` «Набор», серость «Склеить»; редактор звука с обрезкой, затуханием и выделением куска (**новая поверхность** `features/audioEditor/editor/`, вход — `openEditor(sessionId, threadId)` в `thread/threadStore`, как у картинки `imageEditor/thread/threadStore.ts:208`; собирается из `player/AudioWave` и `player/selection`, поля обрезки — из `panel/OpFields`, запуск — `POST …/threads/{t}/edit`); панель `voices` с «Обучить голос»; меню «Обработать ▾» убрать при флаге; `cc_audio_inputs:` → референсы | 1ф, 2б | сценарий 5 макета; обрезка из редактора даёт версию |
| 3 | Кира, Денис | видео по §Д4. **Денис:** провайдеры `video-scene`/`video-film`, `ContextRevision` в `VideoQuoteRequest`, `VideoLaunchRequest` и `films/build` по §Д2.1, похудание `VideoEditorStateContributor`, `video_*` → `SetPrimary(By = Agent)`. **Кира:** `actions` сцены и фильма по §Д2.2, меню кадров, редактор «Сцена» (текст сцены, `SceneText`), редактор «Монтаж» (перенос `FilmTab`/`FilmList`/`ScriptView`, кнопка «Работать со сценой» в списке сцен фильма), `ContextReturn` для «Нарисовать кадр» и «Сочинить под фильм» | **0в**, 2б, 2к, **2з** (предвыбор `song` у «Сочинить под фильм») | сценарий 8 макета; e2e «Сочинить под фильм» доводит до чипа «Песня» с предвыбором |
| 4 | Вера, затем Кира и Денис | приёмка всех 9 сценариев в обеих темах на 360/800/1024/десктопе, снятие флага, удаление (ниже) | 2к, 2з, 3 | клики не растут против макета; строка = «С чем» = хвост |

**Сторожа §Д6 по шагам:** `contextPanelHost.test.ts`, скан `features/**` и правило ESLint для хостов — в 1ф;
паритет `useActionRun` и `composerSurfaceFor` — в 1ф; `contextActions.catalog.test.ts` растёт вместе с видами:
картинка в 2к, звук в 2з, видео в 3 (каждый шаг добавляет свои строки §Д2.2 и мутацию «седьмое действие»);
проверка `op ∈ каталог` — там же. Шаги 1ф, 2к и 2з — фазы, а не задачи на один заход: их режет Егор, граница
разреза — вид и секция панели.

**Убрано против §5:** правка вкладов `features/*/manifest.tsx` под `context-kind.mode`, режим «из основного
объекта» в `Composer` и строка «Работаем с» внутри панелей вертикалей: их место заняли `actions`, хост
`ComposerActionRow` и секция «С чем» каркаса.

**Добавлено против §5:** каркас `ContextPanel` и закрытый набор параметров; `ComposerActionRow` и
`useActionRun`; каталоги действий трёх видов; панели `characters` и `voices` самостоятельными; редактор звука
с обрезкой; редакторы видео «Сцена» и «Монтаж»; `ContextReturn` и предвыбор; новый e2e-набор по макету.
Объём фронта растёт примерно на одну фазу Киры, бэкенд почти не меняется (только `op` + `params` в
запуске).

**Удаляется после снятия флага** — к списку §5 фазы 4 добавляется:

- панели `ImagesPanel` (с `CreateBody`, `EditBody`, `OpSection`, `BodyParts`, `panelOp.ts`, `panelOpen.ts`),
  `SoundPanel` (с `SoundSheet`, `OpFields`, `opGroups.ts`, `opRequest.ts`, частями `model.ts`: `panelOps`,
  `HIDDEN_OPS`, `pillOf`, `SPEAK_SOURCES`), `VideoPanel` (с `SceneTab`, `FrameSlot`, `VideoSheet`);
- ключи `images`, `sound`, `videoEditor` (через алиасы в `chatContext`), `EXCLUSIVE_PANEL_SETS`, вторая копия
  `GEN_PANEL_KEYS`;
- `ImageModeSwitch`, `SoundModeSwitch`, `modeState`, `composer/imageMode.tsx`, `composer/soundMode.tsx`,
  `composer/sceneMode.tsx`, `ComposerChip` картинки в части режима, слот `composer-mode` и `ComposerModeApi`;
  из `composerModes.ts` остаётся только память (переименовать в `composerActionMemory.ts`);
- полосы `ImagesStrip`, `SoundStrip`, `VideoStrip` с их `summary.ts`;
- меню «Обработать ▾» и быстрые действия карточек;
- `genPanelReturn.ts`, `preset.ts` картинок и звука (если не стали `contextReturn.ts`).

`pickOp`, `opInfo`, `OPS`, `NO_AI_OPS`, `AudioMode` и каталоги моделей остаются: на них стоят `actions`, «Чем»
и агентские карточки.

### Д6. Инварианты и сторожа (дополняют §4)

| Инвариант | Как держим | Сторож |
|---|---|---|
| Один хост панели генерации | `chatContext` прописан в каталоге оболочкой; вертикали не вкладывают `workspace-panel-def` с телом генерации и не зовут `revealWorkspacePanel` с ключами `images`/`sound`/`videoEditor`/`chatContext` — только `revealContextPanel` | юнит `contextPanelHost.test.ts`: после снятия флага в `PANEL_KEYS` нет трёх старых ключей, `GEN_PANEL_KEYS` = `['chatContext']`; скан исходников `features/**`: нет литералов ключей генерации в вызовах `revealWorkspacePanel` (мутация: вернуть вызов в `drawInImages` — красный) |
| Единственный владелец действий вида | действия вида объявляет только его вклад `context-kind`; реестр фронта бросает исключение на дубль `kind` (зеркало `ContextKindRegistryTests`); хост не импортирует `ops.ts`/`panelOp.ts` вертикалей | юнит реестра на дубль; правило ESLint `no-restricted-imports` для `components/chat/ComposerActionRow.tsx` и `components/generation/ContextPanel.tsx` |
| Паритет кнопки поля и панели | одна точка `useActionRun`, обе кнопки читают её `label`/`quote`/`state` и зовут её `run` | юнит: поле и низ панели на одном состоянии дают одинаковую подпись, `run` идёт в один `launch`; e2e: тело запроса из кнопки поля и из кнопки панели совпадает побайтно |
| Потолок 6 и «Чат» первым | хост сам ставит «Чат»; вертикаль отдаёт ≤ 5; лишнее — исключение в dev, не обрезка | `contextActions.catalog.test.ts`: для каждого вида и каждого состояния из фикстур (картинка, с отметками, черновик, речь, песня, стемы, черновик звука, сцена не снята / снята, фильм) — чипов ≤ 6, первый «Чат», выбран ровно один, `id` уникальны; мутация: седьмое действие у картинки — красный |
| Чип не выдумывает операцию | `op` любого `run`-действия входит в каталог операций вертикали (`ImageEditOp`, `OPS`, операции видео) | тот же тест: `op ∈ каталог`; связь с API — каталоги общие с запуском |
| Убранное из UI не убрано у агента | MCP-схемы и `caps.ops` не трогаются | `McpToolsetStabilityTests` и тесты тулсетов без правок (их правка в фазах 2–4 — сигнал ошибки) |
| Параметры — закрытый набор | `LaunchParam` — дискриминированное объединение, у вертикали нет `ReactNode`-слота в «Параметрах» | компиляция (`npx tsc -b`); юнит `ContextPanel`: неизвестный `kind` параметра не рисуется |
| Агентский объект не включает действие | `By = Agent` → выбран «Чат» | юнит хоста + сценарий 6 e2e |

### Д7. Отвергнуто (дополняет §6)

11. **Оставить три панели с общим каркасом.** Основной объект один на чат (§1), поэтому в каждый момент
    содержательна ровно одна из трёх панелей. Три ключа означали бы три входа в рельсе, набор
    взаимоисключения, три признака `genPanelDismissed` и `returnTo` между панелями: «Нарисовать кадр в
    Картинках» уводит человека из «Видео» в чужую панель, сцена пропадает с глаз. Режимы («Создать / Править»,
    «Голос / Музыка / Обработка», «Сцена / Фильм») остались бы зеркалом чипов. Различие между видами
    умещается в `preview` и `editor` карточки «С чем», и для этого хватает слота.
12. **Действия в выпадающем меню кнопки отправки** (split button). То, что сделает запуск, не видно, пока меню
    не открыто, и это ломает принцип «человек видит то, что получит». Каждая смена действия — два клика вместо
    одного. Вопросу параметра («Пропорции», «Набор») негде жить. На телефоне меню открывается над клавиатурой
    и закрывает поле. Основная часть кнопки двусмысленна: непонятно, повторяет она прошлое действие или шлёт
    сообщение.
13. **Хвост операций в чип «Ещё ▾».** Это возврат списка из 27 операций звука, только в другом месте. Потолок
    6 перестаёт что-либо значить, а редкие операции агент делает словами не хуже.
14. **Действия объявляет бэкенд в DTO контекста** (как подписи, §2.1). Часть состояния, от которого зависит
    строка, живёт только на фронте (маска редактора). Подписи чипов статичны, а цена всё равно идёт через
    `quote`. Хвосту хода выбранный чип не нужен: в «Чате» запуска нет. Бэкенд проверяет `op` при запуске, этого
    достаточно.
15. **Переименовать панель «Персона», чтобы освободить ключ `context`.** Пропали бы сохранённые раскладки
    пользователей и переписывался бы код бейджей. Новый ключ `chatContext` стоит одной строки.
16. **`ReactNode`-слот вертикали в «Параметрах запуска».** Это обходной путь обратно к «Ещё настроек» со
    схемой модели, от которой постановка отказалась.

### Открытые вопросы дополнения (Андрею)

> **Закрыты в Дополнении 2:** вопросы 1–3 — §Р6, вопрос 4 — §Р1–§Р3 (`defaultAction` из контракта удалён).

1. **Чипы черновика «Новый звук»** в макете не заданы. Предложение: Озвучить · Песня · Эффект. Без этого
   решения шаг 2з начинать нельзя.
2. **«Обучить голос»** — кнопкой в панели «Голоса» (предложение) или только агенту?
3. **`enhanceFaces` и режимы подбора `EditMode`** уходят к агенту (по постановке «схемы модели нет»).
   Подтвердить, что «Фотореализм» для людей не нужен кнопкой.
4. Вопросы макета §7 (чип по умолчанию, «Чем» в «Чате», «Вариантов» только в панели) остаются за Андреем;
   в контракте они уже учтены как `defaultAction` и `params`.

---

## Дополнение 2 (2026-10-03): решения Андрея по макету §7 и точкам ожидания

**Основа:** решения Андрея из постановки (задача `2f1faa30`), раскладка Егора —
[план реализации](../research/turn-context-implementation-plan-2026-10.md) (§0, §2, §6). Ответы на точки ожидания
ТО-0, ТО-2, ТО-2а, ТО-3 и ТО-4 дала Софья по рекомендациям плана: Андрей поручил ей вести фичу до прода.
Модель стора (§1), хвост (§3.1) и `contextRevision` (§3.2, §Д2.1) **не меняются**. Меняются умолчание действия,
место «Чем», подпись кнопки, а ещё фиксируются расхождения с кодом и порядок мержей. Пересмотренные места выше
помечены врезками «Пересмотрено в Дополнении 2».

### Р1. Умолчание действия (ТО-0 принято)

**Правило хоста.** Объект поставил человек — выбрано **первое `run`-действие без `disabledReason`** в порядке
`actions`, если такого нет — «Чат». Объект поставил агент (`By = Agent`) — всегда «Чат». Правило «после запуска
выбор остаётся, если в новом состоянии есть действие с тем же `id`, иначе „Чат“» и память выбора (§Д2) не
меняются. Предвыбор (`preset`, §Д1) по-прежнему сильнее умолчания.

**Контракт.** Поле `defaultAction` из `ContextKindApi` (§Д1) **удалено**: умолчанием служит порядок действий
в каталоге. Вертикаль ставит первым то действие, которое должно включаться само. Функция хоста одна —
`pickDefaultAction(actions, by)`, её держит юнит хоста (сценарий 6 макета для агента).

**Колонка «По умолч.» §Д2.2, пересчитанная по правилу:**

| Вид · состояние | По умолч. (объект человека) | Почему |
|---|---|---|
| `image` · черновик | `draw` | первое `run` |
| `image` · с файлом | `edit` | первое `run` |
| `image` · с отметками | `edit` (`inpaint`, «Изменить отмеченное») | первое `run` |
| `audio` · черновик | `speak` («Озвучить») | первое из подтверждённых чипов «Озвучить · Песня · Эффект» (§Р6) |
| `audio` · речь | `speakMore` | первое `run` |
| `audio` · песня | `stems` | `repaint` сер без выделения; с выделением на волне — `repaint` |
| `audio` · со стемами | `mix` | первое `run` |
| `video-scene` · не снята / снята | `shoot` | первое `run` |
| `video-film` | `build` | первое `run`; `montage` — `editor`, выбором не бывает |

Следствие, принятое явно: у **песни** после «Работать с этой» кнопка поля сразу запускает разделение на стемы без
текста. Это допустимо, потому что кнопка подписана `✦ Стемы · бесплатно` и видно, что произойдёт.

### Р2. «Чем» в режиме «Чат» (ТО-0 принято)

- В «Чате» строка **чип «Чем» не рисует**; лестница схлопывания считает его шириной 0. Чип появляется только при
  выбранном `run`-действии, данные — из `quote` по `op` этого действия (§Д2.1).
- Секция «Чем» панели `chatContext` в «Чате» — пустое состояние: «Исполнитель появится, когда в поле выбрано
  действие».
- Поле `executor` из `ChatContextDto` **убрано** (§2.1, §Д2.1). `IContextKindProvider.DescribeExecutor`
  остаётся.
- **Намеренное исключение из инварианта «строка = хвост».** Хвост хода (§3.1) и `context_state` (§2.4) строку
  «Чем:» сохраняют: агенту исполнитель нужен, чтобы выбрать и описать вызов `*_generate`, а человеку в «Чате»
  он ничего не говорит — запуска нет. Паритет строки и хвоста держится по «С чем» и «Плюс»;
  `TurnContextParityTests` (§4) сверяет только их.

### Р3. Параметры на кнопке

«Вариантов» и «Длительность» живут **только в панели** (секция «Параметры запуска», §Д1). Текущее значение видно
на кнопке поля и в низу панели. Подпись строит одна точка, `useActionRun.label`, — второй форматтер подписи
запрещён тем же сторожем паритета (§Д6). Формат задаёт макет Майи (шаг 0б); рабочее предложение плана:
`✦ Изменить · 3 вар. · $0.12`, `✦ Снять · 8 с · $1.60`. При одном варианте число не пишется, цена не режется
никогда, на 360 первым сокращается подпись действия.

### Р4. Лестница не зависит от панели

Открытый вопрос 3 макета строки закрыт: **нет**. Порядок схлопывания строки — как в макете Майи (сначала слова
«Чем» и ветки, потом референсы). Открытость панели `chatContext` в функцию лестницы не входит, входит только
ширина строки. Сторож — юнит лестницы.

### Р5. Расхождения ADR с кодом (сверено с master `c6b78e74f`)

| В ADR | В коде | Как читать ADR |
|---|---|---|
| `components/chat/Composer.tsx`, `components/chat/ChatPanel.tsx` | `frontend/src/components/Composer.tsx`, `frontend/src/components/ChatPanel.tsx` | старые файлы правятся на месте; новые `ContextRow` и `ComposerActionRow` кладутся в `components/chat/` |
| тулсет `turn-context` — «Core, `Services/Mcp/Http`» (§2.4) | тулсеты продукта — в Main `backend/ClaudeHomeServer/Services/Mcp/Http/`, в Core только `IMcpToolset` | тулсет — в Main; стор, контракты и `project-file` — в Core |
| `createReleaseUndo`, `RELEASE_UNDO_MS` — «кит» (§2.2) | `frontend/src/components/generation/useReleaseUndo.ts` | `lib/chatContext` импортирует оттуда; перенос в кит — не цель фичи |
| ручки для «сохранённых в чате файлов» нет (§3.3) | `IChatSavedFiles` нужен фронту для `commitViaChat` | в таблицу REST §2.1 добавлена `GET api/chats/{sessionId}/context/saved-files` → `[{path, threadKind, savedAt}]` |

JSON-примеры контрактов (`ChatContextDto` с объектом картинки и референсами, серый референс с `usedBy: []`, тело
`409 context_changed`) живут в отдельном файле [ADR-023-contracts.md](ADR-023-contracts.md), а не в этом ADR. Файл
кладёт задача 1б-1 в ветке `feat/turn-context` вместе с тестом `ChatContextContractsTests`.

### Р6. Открытые вопросы Дополнения 1 (ТО-2 принято)

1. Чипы черновика «Новый звук»: **«Чат · Озвучить · Песня · Эффект»** (`speak`/`cloneVoice`, `song`, `sfx`).
   Пометка «предложение» в §Д2.2 и §Д3 снята.
2. «Обучить голос» (`trainVoice`) — **кнопкой в панели «Голоса»** и у агента.
3. `enhanceFaces` и режимы подбора `EditMode` — **только агенту**, из UI уходят (UI шлёт `auto`).

### Р7. Порядок мержей и выкатки (ТО-2а, ТО-3, ТО-4)

- **ТО-2а.** `fix/composer-memory` (чистка памяти поля и полос при удалении чата и выходе) ведёт соседний чат.
  Если ветка не попадёт в master до 1ф-1, хук чистки переносит к себе 1ф-1, чтобы `actionMemory` чистилась
  так же.
- **ТО-4а — принят.** Промежуточный мерж `feat/turn-context` в master под **выключенным** флагом после фазы 2:
  фича трогает горячие файлы (`Composer.tsx`, `ChatPanel.tsx`, `SessionManager.cs`), а три фазы без мержа
  дают гарантированный тяжёлый конфликт.
- **ТО-3, ТО-4.** Андрей разрешил мержи в master и выкатку в прод. Флаг `composer-context-row` уезжает в прод
  выключенным, снимается только после приёмки Веры (шаг 4). После снятия — удаление старого пути по §Д5.

### Что проверяет, что Дополнение 2 выполнено

- В `ContextKindApi` нет `defaultAction`, в `ChatContextDto` нет `executor` (КТ-1, КТ-4).
- Юнит хоста: умолчания по таблице §Р1 для всех состояний фикстур `contextActions.catalog.test.ts`, у агента —
  «Чат».
- Юнит строки: в «Чате» чипа «Чем» нет, ширина лестницы не меняется от открытости панели.
- `TurnContextParityTests`: паритет по «С чем» и «Плюс», строка «Чем:» в хвосте присутствует.

## Дополнение 3 (2026-10-03, Софья): роли по операциям и выбор человека

По итогам ревью 2б-1 (Глеб) и правок Дениса.

**Какие операции основного объекта берут какие роли** (источник правды — `AcceptedRefs` провайдеров; `ImageEditorToolset.RefOps` — список операций картинки с референсами):

| Основной | Операция | Роли референсов |
|---|---|---|
| `image` | `generate`, `edit`, `inpaint` | `style`, `object`, `face` (виды `image`, `project-file`), `character` (`image-character`) |
| `image` | `outpaint`, `removeBackground`, `upscale`, `enhanceFaces` | нет |
| `audio` | `speak`, `dialogue` | `voice` (`audio-voice`) |
| `audio` | `convertVoice` | `voice` (`audio-voice`), `reference` (`audio`, `project-file`) |
| `audio` | `concat` | `piece` |
| `audio` | `separate`, `denoise` и остальные | нет |

**Основным может быть не каждый вид:** `CanBePrimary` — да у `image` и `audio`, нет у `image-character`, `audio-voice`, `project-file`; отказ 400 `kind_not_primary`.

**Агент не перезаписывает выбор человека:** `SetPrimary` с `By = Agent` на объект, уже основной с `By = Human`, ничего не меняет (✦ не зажигается, ревизия не растёт). Обратный переход — человек подтверждает объект агента — делает `By = Human`.

**«Чем» у звука** считается той же цепочкой, что запуск: режим нити (по умолчанию `voice`) → `Resolve` «нить → префы режима → каталог».

**`step?` в `ContextKindApi`** (принято по ревью 1ф-3): вид может отдать переход по версиям основного объекта (‹ ›) — функцию шага; хост рисует стрелки в секции «С чем», данные о версиях — от вида, не от хоста.
