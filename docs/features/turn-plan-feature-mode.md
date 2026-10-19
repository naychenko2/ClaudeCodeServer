# Режим «Исполнение фичи» в «Плане хода» (`turn-plan-feature-mode`)

> Разрез и целевой образ — [turn-plan-feature-mode-cut-2026-10.md](../research/turn-plan-feature-mode-cut-2026-10.md);
> макеты — [v1](../mockups/turn-plan-feature-mode-v1.html) / [v2](../mockups/turn-plan-feature-mode-v2.html).
> Агрегат — [FeatureRun.cs](../../backend/ClaudeHomeServer.Tasks/Projector/FeatureRun.cs) и
> [FeatureProjector.cs](../../backend/ClaudeHomeServer.Tasks/Projector/FeatureProjector.cs);
> фронт-стор — [featureRuns.ts](../../frontend/src/lib/featureRuns.ts).

## Зачем режим

Когда чат ведёт фичу задачами команды, человек видит в нём только план хода оркестратора
(«3/6 · Жду отчёта Дениса») — это план разговора, а не фичи: по нему не понять, на каком этапе
фича, сколько шагов принято, сколько раз её возвращали и не ждёт ли она его самого. Режим
«Исполнение фичи» показывает то же, что «Пульт куратора», но в двух местах, где план уже живёт —
пилюле и карточке плана. Новых панелей и экранов нет (features.md: «Отдельной панели под план
хода нет и не планируется»).

## Что видит пользователь

- **Переключатель** «План хода · Исполнение фичи» — в шапке меню пилюли и в шапке карточки
  `TodoPlanView`. Рендерится, только если чат ведёт фичу; иначе меню и карточка — как раньше.
  Режим по умолчанию: «фича», если она есть, иначе «план»; выбор видят и пилюля, и карточка.
- **Пилюля** (`TurnPlanPill`) в режиме фичи: значок маршрута, затем
  `фича · 8/9 ↺3 · Производство · Денис`. Счётчик и ↺ не сжимаются, текст этапа обрезается
  многоточием. Ждёт человека — синий ромб, текст `ждёт вас · мерж` в `C.info`; исполнитель упал —
  красный квадрат, `стоит · Денис` в `C.dangerText`. Когда пилюля стоит в режиме «План хода», а
  фича ждёт, на значке появляется точка: сигнал не теряется.
- **Тело режима** — одно в меню и в карточке, сверху вниз: название фичи и ветка (mono);
  плашка-тревога (ждёт вас / исполнитель упал) с действиями; «Сейчас» (кто, шаг, что делает,
  сколько идёт); «Советы оркестратора» (1–3 строки, у каждого действие); «Конвейер» — 7 этапов
  вертикально: Разрез → План → Производство (линии «Бэк · Денис», «Фронт · Кира») → Ревью →
  Проверка → Финальное ревью → Мерж.
- **Детали** раскрываются под строкой (аккордеон, не экран): у этапа — задачи и проходы с
  вердиктами; у «Производства» — шаги по линиям; у шага — задачи, круги ревью «вернул → принял»
  с текстом вердикта и «Открыть задачу →».
- **Карточка `TaskCreatedView`** получает строку-чип «Шаг Ш7 фичи · Производство, бэк»; клик
  переключает живую карточку плана в режим фичи (сигнал фокуса шага).
- **Фич-флаг** `turn-plan-feature-mode` — dark launch, выключен по умолчанию, включается в меню
  «Экспериментальные функции». Флагом закрывается только UI: REST и параметры MCP от него не
  зависят, поэтому при выключенном флаге карточка плана и меню работают байт-в-байт как раньше.

## Контракт `GET /api/tasks/features?sessionId=`

Эндпоинт — [TasksController.GetFeatures](../../backend/ClaudeHomeServer/Controllers/TasksController.cs)
(маршрут `features` под `api/tasks`). С `sessionId` — фичи именно этого чата, без — все фичи
владельца. `[Authorize]`, выборка только по `OwnerId` из claim. Подсистема `Tasks` выключена →
честный `503`, а не `500` (ADR-014). Возвращает список `FeatureRun` — по одному на фичу.

`FeatureRun` — чистая проекция задач трекера (эпик + дочерние) в конвейер фичи. Это **не** второй
источник правды: он держит ссылки на `TaskItem` и вычисляет только то, что не лежит в полях.

Поля `FeatureRun`:

- `FeatureId`, `Epic` — id задачи-эпика и сам эпик (`role=feature`, `FeatureId=null`).
- `Steps`, `Reviews`, `Qa`, `Final`, `Merge` — задачи по ролям: шаги и доработки
  (`step`/`rework`), ревью (`review`), проверки (`qa`), финал (`final`, последний), мерж
  (`merge`, последний); `Final`/`Merge` — `null`, если таких задач нет.
- `AwaitingHumanSince`, `AwaitingHumanReason` — «ждёт вас» у эпика: с какого момента и почему.
- `Verdicts` — счётчики исходов витков ревью/проверки/финала: `Accepted`, `AcceptedWithRemarks`,
  `Returned`, `Withdrawn`; `Loops = Returned + AcceptedWithRemarks` (↺).
- `Progress` — доля решённых витков: (ревью/проверка/финал с вердиктом) / (все такие задачи), 0..1.
- `Ready` — готово: финал **закрыт** (`Done`) и принят (`accepted`/`accepted_with_remarks`).
- `Cut`, `Plan` — задачи ролей `cut`/`plan` с `FeatureId == epic.Id`.
- `Stages` — конвейер: 7 стадий в порядке жизненного цикла. Арифметика стадий — на бэке, фронт
  только показывает.
- `StepChains` — цепочки шагов: по одной на шаг (`stepRef`) — задача шага + её доработки
  (`rework`, тот же `stepRef`), по времени. Несут витки (связь ревью/проверки/финала через
  `ReviewOf`), исход, длительность, источник возврата и линии.
- `ReturnsBySource` — возвраты по источникам (ревью/проверка/финал): число возвратов и сколько из
  них открыто (их доработка ещё не `Done`) — по ним фронт красит бейдж в тревожный тон.
- `Lanes` — линии (`back`/`front`/`other`): исполнители (уникальные, по первому появлению) и
  счётчики; из этого фронт рисует «Бэк · Денис · 3 из 4».
- `Now` — «Сейчас»: все `InProgress`-задачи фичи, по убыванию `ClaudeStartedAt`; у закрытой фичи
  пусто. Стадия пилюли фронт берёт у первого элемента.
- `Attention` — тревога: `stopped` → `wait` явный → `wait` «мерж»; один элемент либо `null`.
- `StepsAccepted`, `StepsTotal` — счётчик шагов: принятые (`Done`) / все (из `StepChains`).
- `LastActivityAt` — последняя активность: максимум по `CompletedAt`/`ClaudeStartedAt` задач фичи
  (время мержа — это `CompletedAt` merge-задачи, уже учтено).
- `Closed` — закрыта: merge-задача `Done` или эпик `Done`.
- `Summary` — итог смерженной фичи: только у закрытой, иначе `null` (`MergedAt`,
  `DurationMinutes`, `Tasks`, `Steps`, `Loops`).

Поля `FeatureStage` (элемент `Stages`):

- `Key` — ключ стадии: `cut | plan | prod | review | qa | final | merge`.
- `State` — `done` (все задачи стадии закрыты), `active` (первая непройденная), `pending` (ещё не
  начата), `absent` (ноль задач, а следующая стадия уже начата).
- `Tone` — только у `active`, иначе `null`: `run` (идёт), `wait` (ждёт человека), `stopped`
  (исполнитель встал), `loop` (работа вернулась, доработка идёт).
- `Passes` — число проходов: у `prod` — 1 + возвраты (returned/awr у review/qa/final), у
  остальных — число задач.
- `TaskIds` — id задач стадии, в порядке, в котором их дал вызывающий.
- `RunningSince` — `ClaudeStartedAt` идущей (`InProgress`) задачи стадии; `null` — не идёт.
- `Rounds` — витки стадии: у `qa`/`final` каждая задача стадии — виток (формат как у цепочки
  шагов); у остальных стадий пусто.
- `LongestStepRef` — только у стадии `review`: шаг с наибольшим числом витков (строка «дольше всех
  D1c»); `null` — витков нет ни у одного шага.
- `PersonaIds` — исполнители стадии (уникальные `PersonaId`, в порядке первого появления);
  `TaskCount` — число задач стадии (подпись «1 задача», «3 задачи»).
- `Verdict` — исход последней по времени закрытой задачи стадии: `returned` | `accepted_with_remarks`
  | `accepted` | `withdrawn`; `running`, если задача стадии идёт; `null` — стадия без задач или без
  вердикта.

## Контракт `GET /api/tasks/features/index?projectId=`

Индекс «чат → фичи» для фасета «Фичи» в списке сессий (флаг `features-overview`); решение и
отвергнутые варианты — [ADR-031](../adr/ADR-031-feature-as-dimension.md). Эндпоинт —
[TasksController.GetFeaturesIndex](../../backend/ClaudeHomeServer/Controllers/TasksController.cs),
DTO — [FeatureIndex.cs](../../backend/ClaudeHomeServer.Tasks/Projector/FeatureIndex.cs), расчёт —
`FeatureProjector.BuildIndexCore` в [FeatureProjector.cs](../../backend/ClaudeHomeServer.Tasks/Projector/FeatureProjector.cs),
зеркало на фронте — [featureIndex.ts](../../frontend/src/types/featureIndex.ts).

`[Authorize]`, владелец из claim, задачи — только `GetByOwner`. Подсистема `Tasks` выключена →
`503 { error }`, как у `GetFeatures`. От фич-флагов эндпоинт не зависит. Контроллер передаёт
проектору узкий снимок всех сессий владельца `FeatureIndexSession(Id, TaskId, ProjectId, Archived)`;
id чата, которого нет в снимке (чужой, удалённый), наружу не уходит.

**Область.** Без `projectId` — все проекты владельца и чаты вне проектов. С `projectId`:
`features[]` — эпик в проекте **или** хотя бы один чат фичи в проекте; `chats` — только сессии
этого проекта; `chatCount` считается в той же области. Фича, чей эпик удалён, не попадает в ответ
целиком, и её членство в `chats` тоже.

Ответ: `{ features: FeatureIndexRow[], chats: { [sessionId]: FeatureIndexChatEntry[] } }`.

Поля строки фичи (`features[]`, порядок — `OrderRuns`, фронт не пересортировывает):

| Поле | Тип | Смысл |
|---|---|---|
| `featureId` | string | id эпика |
| `title` | string | название эпика |
| `projectId` | string \| null | проект **эпика**; `null` — вне проектов |
| `bucket` | `attention \| running \| idle \| closed` | группа; та же функция `Bucket`, что задаёт порядок |
| `attention` | объект \| null | тревога; `null` — нет |
| `rail` | сегмент[7] | `cut, plan, prod, review, qa, final, merge` по порядку |
| `chips` | string[] | `open`, `waiting`, `stopped`, `merge`, `closed` |
| `stepsAccepted`, `stepsTotal` | number | шаги: принятые (`Done`) / все |
| `loops` | number | возвраты (`Verdicts.Loops`) |
| `now0` | объект \| null | первая `InProgress`-задача; `null` — фича не идёт |
| `lastActivityAt` | ISO \| null | максимум `CompletedAt`/`ClaudeStartedAt` |
| `summary` | `{ mergedAt: ISO \| null, durationMinutes }` \| null | только у закрытой |
| `keySessionId` | string \| null | ключевой чат; `null` — живых звеньев нет |
| `keyArchived` | boolean | ключевой чат в архиве (остаётся ключевым) |
| `keyProjectId` | string \| null | проект ключевого чата; `null` — вне проектов |
| `chatCount` | number | чатов фичи в этом ответе, архивные включены |

- `attention` — `{ kind: stopped | wait | merge, since: ISO | null, reason: string | null,
  personaId: string | null, sessionId: string | null }`. `merge` — это `wait` с причиной «мерж»
  из `FeatureRun.Attention`, маппинг на бэке (`AttentionOf`); у `merge` `reason = null`.
  `sessionId` — `LinkedSessionId` задачи. `since` бывает `null`.
- `rail[]` — `{ key, state: done | active | pending | absent, tone: run | loop | wait | stopped | null }`,
  `FeatureStage` один в один. **У закрытой фичи** (`bucket = closed`) рельса нормализуется
  (`RailOf`): всё, кроме `absent`, становится `done`, `tone = null` — иначе эпик, закрытый без
  merge-задачи, давал бы `merge = active, tone = run`.
- `chips` (`ChipsOf`): закрытая → `["closed"]`; иначе `"open"` плюс чип тревоги:
  `stopped` → `stopped`, `wait` «мерж» → `merge`, явный `wait` → `waiting`.
- `now0` — `{ personaId: string | null, stage: string, stepRef: string | null }`.
- Ключевой чат (`KeyChain`/`KeySession`): первое живое звено цепочки `epic.SourceSessionId` →
  `epic.ExecutorLaunchedBySessionId` → самый частый `ExecutorLaunchedBySessionId` дочерних задач
  (при равенстве — меньший id) → `null`. Удалённый или чужой чат пропускается.

Записи `chats[sessionId][]` — `{ featureId, kind, taskRole, stepRef, lane }`, на пару
«чат × фича» одна запись с сильнейшим видом, порядок — по силе, при равенстве — по порядку фичи в
`features[]`:

- `curator` — чат завёл эпик (`epic.SourceSessionId`);
- `orchestrator` — `ExecutorLaunchedBySessionId` эпика или дочерней задачи фичи;
- `executor` — `Session.TaskId` → задача фичи или сам эпик; только у него заполнены `taskRole`,
  `stepRef`, `lane` (поля задачи), у остальных — `null`.

Вид `key` есть в типах и в шкале силы, но в Б3 в `chats` не отдаётся: ключевой чат берётся из
`keySessionId` строки. `SourceSessionId` дочерних задач членством не считается.

## Контракт `GET /api/tasks/features/{featureId}`

Страница фичи (волна 2, [features-overview-2-proposal.md](../mockups/features-overview-2-proposal.md) п. 2):
одна фича по id эпика. Эндпоинт — [TasksController.GetFeature](../../backend/ClaudeHomeServer/Controllers/TasksController.cs).
`[Authorize]`, владелец из claim (не из маршрута); от фич-флагов не зависит. Подсистема `Tasks`
выключена → `503 { error }` (ADR-014, как у `GetFeatures`/`GetFeaturesIndex`).

**Ответ — обёртка** `{ run, events, eventsTotal }`:

| Поле | Тип | Смысл |
|---|---|---|
| `run` | `FeatureRun` | агрегат фичи: `FeatureProjector.Project(featureId, ownerId)` (контракт — выше, «`GET /api/tasks/features?sessionId=`»); новой арифметики нет |
| `events` | `FeatureEvent[]` | лента: `FeatureProjector.BuildEvents(run, снимок сессий)`, порядок `At desc` (при равенстве — `taskId`) |
| `eventsTotal` | number | число событий **до** обрезки `eventsLimit` |

`?eventsLimit=`: не задан → `100`; `all` или `0` → все; `N` (целое > 0) → `N`; прочее → `100`.

**Коды:** нет эпика / чужой / удалён → один `404` (не отличаем: это же состояние «Фичу удалили» на
фронте); подсистема выключена → `503`.

Поле `FeatureEvent` — `{ at, kind, taskId, role, stepRef?, personaId?, sessionId?, round?, reason? }`
(контракт — [FeatureEvents.cs](../../backend/ClaudeHomeServer.Tasks/Projector/FeatureEvents.cs)).
`sessionId` наружу только если чат в снимке владельца, иначе `null` (fail-closed, как `KeySession`).

**Ограничения ленты** (чистая проекция, журнала нет): видна **последняя пара старт/стоп** задачи
(`TaskItem` держит одно `ClaudeStartedAt`/`ExecutorStoppedAt`); **нет снятого «ждёт вас»** (`waiting`
есть, пока `AwaitingHumanSince` стоит); **нет переносов** (смена исполнителя отдельного события не даёт)
и **нет автора кнопки** (кто запустил/снял — не атрибутируется).

## Правила

- **Мерж `done`** — только по задаче `role=merge` со статусом `Done`. Финал зелёный, а merge-задачи
  ещё нет → стадия «Мерж» `active`, а не `done` ([FeatureProjector.cs:164-168](../../backend/ClaudeHomeServer.Tasks/Projector/FeatureProjector.cs)).
- **Приоритет `Attention`** — `stopped` → `wait` явный → `wait` «мерж». `stopped` — `InProgress`-задача
  с `ExecutorStoppedAt` позже `ClaudeStartedAt` (защита от stale-пометки); `wait` явный —
  `AwaitingHumanSince` у эпика или задачи (ждущий дольше всех); `wait` «мерж» — вычисляется, не
  хранится: финал принят, а merge-задачи нет или она не `Done`
  ([FeatureProjector.cs:82-149](../../backend/ClaudeHomeServer.Tasks/Projector/FeatureProjector.cs)).
- **Тон активной стадии** выводится из `Attention`, а не отдельным расчётом: `Attention.Kind ==
  "stopped"` → `stopped`, `wait` → `wait`, иначе `loop`/`run`. Отдельного расчёта тона быть не
  должно — иначе тон и `Attention` разъезжаются (MAJOR финального ревью).
- **Порядок фич** (правило v2) — 4 группы, внутри каждой свой ключ: 1) не закрытые с `Attention` —
  по `since` ascending (ждущая дольше всех — первая); 2) не закрытые идущие — по `max
  ClaudeStartedAt` descending; 3) не закрытые прочие — по `LastActivityAt` descending; 4) закрытые —
  в конец, по `LastActivityAt` descending. Пилюля показывает `runs[0]`, поэтому порядок
  определяет бэк, фронт его не пересортирует ([FeatureProjector.cs:740-773](../../backend/ClaudeHomeServer.Tasks/Projector/FeatureProjector.cs)).
- **Источник возврата** — только по `ReviewOf`: ближайшая (последняя по времени) связанная
  задача-возврат, её роль → `review`/`qa`/`final`; связи нет → `null` (догадок не делаем).
- **Шаг = `stepRef`**: по нему доработка склеивается со своим шагом; в цепочку входят задача шага
  (`step`) и её доработки (`rework`, тот же `stepRef`).
- **«С правками» считается возвратом**: вердикт `accepted_with_remarks` — работа вернулась к
  исполнителю, поэтому идёт в `Loops`/↺.
- **`Ready`** = финал закрыт (`Done`) + принят (`accepted`/`accepted_with_remarks`).

## Принцип: бэк считает, фронт показывает

Агрегат — одна чистая функция в вертикали `Tasks` (`FeatureProjector.Build`), на ней золотые тесты.
Фронт **не пересчитывает** конвейер: [featureRuns.ts](../../frontend/src/lib/featureRuns.ts)
держит полученное в сторе (`useSyncExternalStore`) и раздаёт хуком `useFeatureRuns`. Бэк уже
считает `verdicts`/`progress`/`ready`/`stages` — фронт только их рисует.

- **Пилюля всегда `runs[0]`** — порядок отсортировал бэк; выбор фичи (чипы) влияет только на тело
  в меню/карточке, пилюля его не слушает.
- **Выбор фичи общий** между карточкой и меню: один выбор видят обе поверхности, как `TodoList`
  сейчас общий у меню и карточки.
- **Новых хранимых полей и эндпоинтов нет**: фича — задача-эпик в том же трекере (хранение, бэкап,
  изоляцию, `task_changed` и доску достает даром), а `GET /api/tasks/features` — единственный
  эндпоинт. Живое обновление — по существующему `task_changed` (дебаунс ~500 мс), нового
  SignalR-события нет.

## Что вынесено за волну и почему

Выкатку не блокирует ничего из списка: флаг выключен, и каждый пункт честно деградирует.

- **Постоянное хранение режима на чат** — сейчас режим/выбор/фокус живут в памяти вкладки
  (`featureRuns.ts`), не в `localStorage`; переживает ли режим перезагрузку — до снятия флага.
- **«Попросить Марка» как merge-задача** — сейчас кнопка только подставляет текст в композер;
  создание задачи `role=merge` напрямую — следующий этап.
- **Фильтр трекера по фиче** — «Задачи фичи в трекере →» сейчас ведёт на эпик; фильтр по `featureId`
  — следующий этап.
- **«Принять» (этап 2.6)** — `POST …/accept` с закрытием эпика; замыкает жизненный цикл, заглушки
  удалены.
- **Мерж по событию из git, ревью вне трекера, советы оркестратора** — исследование, не этап: каждый
  пункт вводит второй источник правды о мерже/ревью и требует отдельного ADR.

Техдолг (после мержа): m1–m5 финального ревью (свести предикаты «вернули», порезать `Build` на
`BuildStages`/`BuildAttention`/`BuildSummary`), разрез [PipelineFlow.tsx](../../frontend/src/components/chat/feature/PipelineFlow.tsx)
на `pipeline/pure.ts`/`glyphs.tsx`/`lanes.tsx`/`details.tsx`, чистка срезов стора при
удалении/архивации чата.
