# Метрики этапа «исполнитель / ревью / человек» — мини-разрез (D0)

Мини-разрез к эпику «Процессы фичи» (`a149e308`), задача `044606ee`. Основание — строка D1 §8 разреза `docs/research/ai-fabrika-workflow-cut-2026-11.md`. Решение Андрея: на экране этапа время, ходы, токены и ожидание человека показываются **раздельно** по исполнителю, ревью и человеку.

## 0. Главное до деталей

1. **Новой модели хранения не нужно. Предпосылка §8 D1 «`AwaitingHumanSince` только на эпике» неверна** (отмечаю явно, это моя же формулировка):
   - пару `TaskItem.AwaitingHumanSince/Reason` пишет `TaskManager.SetAwaitingHuman` на **любую** задачу: через MCP `tasks_update(awaitingHuman)` и через `report_up(blocker)` по задаче чата;
   - каждое изменение пары уже ложится в журнал фичи событиями `waiting` / `waiting_cleared` с `taskId` (`TaskManager.WriteJournalTransitions`);
   - устарел только комментарий «у эпика» в `TaskItem.cs:181`.
2. **Время исполнителя и ревью берём из того же журнала**: записи `turn` с `taskId`. **Токены — из `SpendRecord.TaskId`.** У задачи ревью свой чат-исполнитель (`LinkedSessionId`), а `SpendMapping` пишет `TaskId = session.TaskId`. Поэтому расход ревью уже лежит под id задачи ревью. **Разметка «ревью/исполнитель» в записи расхода не нужна:** роль выводится из задачи (`Role`, `ReviewOf`). Писать её в запись значило бы завести вторую точку правды.
3. **Правка данных одна** — аддитивное поле `Initiator` в проекции `FeatureSpendRecord` (шов Core). Без него не посчитать реплики человека. Новых сторов, событий шины и писателей нет.
4. **Метрики считает чистая функция проектора** `FeatureStageMetrics.Build`, по образцу `FeatureStageIntervals`. Интервалы ожидания она берёт **из той же функции, что и `FeatureTimeline`** (её выносим, §5, И4). Иначе «Ждали вас» по фиче и сумма по этапам разойдутся.

## 1. Определения

**Задача этапа `T`** — задача фичи со `Stage` (B2). У фич до B2 — любая задача фичи, кроме `feature`/`consult` (§6).

**Ревью этапа `R(T)`** — задачи `role=review`, у которых `T.Id ∈ ReviewOf`:
- `qa`/`final` с пустым `ReviewOf` проверяют фичу целиком: это отдельные этапы, в `R(T)` они не входят;
- `rework` — это новая задача и, значит, свой этап (B2: слепок берёт текущую версию). Склейку «шаг + доработки» по `StepRef` (`run.StepChains`) D1b не делает: это свод по цепочке, а не метрика этапа (§7).

**Окно этапа:** `[T.CreatedAt, max(T.CompletedAt, CompletedAt задач R(T)) ?? now]`. `live = true`, пока `T` или любая задача `R(T)` не в `Done`.

| Доля | Время (минуты, wall-clock) | Ходы | Токены / стоимость |
|---|---|---|---|
| **Исполнитель** | объединение интервалов `turn` с `taskId = T` плюс живой хвост (правило `FeatureStageIntervals`: `max(ClaudeStartedAt, конец последнего закрытого хода)..now`), **минус** интервалы ожидания человека по `T` | число записей `turn` с `taskId = T`; из них отдельно `errorTurns` с `outcome ∈ {error, interrupted, cancelled}` | записи расхода с `TaskId = T` |
| **Ревью** | то же по задачам `R(T)` (объединение по всем, минус их ожидание) | `turn` с `taskId ∈ R(T)` | записи с `TaskId ∈ R(T)` |
| **Человек** | объединение `waiting..(waiting_cleared \| now)` по `T` и `R(T)` в пределах окна, **минус** интервалы очереди фич (`held(queue)..hold_cleared`, прецедент R-Q3B-4) | `waits` — число событий `waiting`; `replies` — ходы с `Initiator = human` в чатах `T` и `R(T)` (по расходу) | **нет**: реплика человека тратит токены агента, эти токены входят в долю исполнителя или ревью — по задаче чата |

**Что такое «ход».** Один завершённый ход CLI в чате, привязанном к задаче: одна запись журнала `turn` = одно событие `TurnCompleted` для `TurnSeq` сессии. Субагенты внутри хода ходом не считаются: их время уже внутри интервала хода, а токены — в записи расхода хода.

**Чего в долях нет:**
- `device_wait` (ожидание устройства локального проекта) — это не человек. В `FeatureTimeline` он сложен с ожиданием, но в блоке этапа его нет; если понадобится — отдельной строкой позже;
- ожидание **на эпике** (мерж, решение по фиче) — это ожидание фичи, его показывает шапка, к этапу оно не относится;
- зазор «шаг сдан → ревью запущено» и паузы между ходами — это `idleMinutes`, ничья доля.

**Перекрытия.** Приоритет тот же, что в `FeatureTimeline` (major 1): ожидание сильнее работы, ход внутри ожидания в работу не идёт. Доли **не обязаны** давать окно в сумме: ревью может идти параллельно доработке. Поэтому `idleMinutes = elapsed − |объединение трёх долей|`, а не остаток от суммы.

**Общее ревью** (`ReviewOf` из N > 1 задач) **не делим**: показываем целиком с `sharedWith: N`. Прецедент деления есть (`SharedSpendSplitter` делит общий чат поровну), но на экране одного этапа поделённые минуты одного ревью — выдумка. Итог фичи от этого не меняется: на уровне фичи ревью считается один раз.

**Минуты** — `FeatureTimeMath.Minutes` от объединённых интервалов (`MergeIntervals`), как у `StageInterval`. Своей арифметики округления не заводим.

## 2. Где хранится ожидание человека по этапу

**Решение: поле на задаче плюс журнал — оба уже есть.** Поле `AwaitingHumanSince/Reason` даёт «ждёт сейчас», журнал `waiting/waiting_cleared` — историю и минуты. Пишет одна пара методов `TaskManager` под `_awaitLock`.

| Путь | Действие | Точка в коде | `task_changed` |
|---|---|---|---|
| MCP `tasks_update(awaitingHuman: {ask})` | set | `TasksToolset.cs:675` → `SetAwaitingHuman` | да (`TasksToolset.cs:680`) |
| MCP `tasks_update(awaitingHuman: null)` | clear | `TasksToolset.cs:678` → `ClearAwaitingHuman(id)` | да |
| `chats_report_up(blocker: true)` из чата задачи | set | `SessionMessagingService.TrySetAwaitingHumanForBlocker` (`:237`) | да |
| человек написал в чат задачи | clear, если ожидание не новее ответа | `AwaitingHumanAnswerSubscriber.HandleHumanMessage` → `ClearAwaitingHuman(id, answeredAt)` | да |
| перезапуск исполнителя | clear | `TaskManager.MarkClaudeStarted` (`TaskManager.cs:1630`) | у вызывающего — **проверить в D1b** |
| переход задачи в `Done` | clear | `TaskManager.Update` (`TaskManager.cs:1244`) | у вызывающего (REST PUT, MCP `tasks_update`/`tasks_complete`) |
| поля `AwaitingHumanSince/Reason` в DTO `Create`/`Update` | прямая запись мимо `_awaitLock` | `TaskManager.cs:442`, `:1231` | — |

Последняя строка — не боевой путь: поля DTO помечены `[JsonIgnore]`, REST и MCP их не принимают, в продовом коде вызовов нет (Grep). Но журнал они пишут, а лок обходят. **В D1b — сторож:** тест, что тела REST `POST/PUT /api/tasks` с `awaitingHumanSince` поле не меняют. Убирать поля из DTO — вне шага.

**Что делает фронт при `task_changed`:**
- полезная нагрузка — задача целиком. Если `taskId` равен `T` или входит в `R(T)`, экран этапа перезапрашивает ручку этапа: числа считает только сервер, фронт их не досчитывает;
- живой счётчик «ждёт вас сейчас» тикает на клиенте от `waitingNow.since`. Это не арифметика долей, а показ одной метки (прецедент — шапка фичи).

Нового события шины и нового вида журнала нет.

## 3. Источник токенов и стоимости

`ISpendFeatureReader.Slice(ownerId = epic.OwnerId, taskIds = {T} ∪ R(T), sessionIds = [], excludeSessionIds = [], from = null)`, затем записи раскладываются по `TaskId`. Этого **достаточно**, разметка в `SpendRecord` не нужна (§0 п.2).

Правила — те же, что у `FeatureSpend`, через тот же признак:
- `costUsd` — только записи с надёжной ценой;
- `UnreliableCost` → `costPartial` и `hiddenCostModels` (единая точка `SpendCostReliability`);
- дневные агрегаты (`IsDaily`, старше `Spend:DetailDays`) несут `TaskId` и идут в токены как есть;
- общий оркестраторский чат (`Б9c`) этапу не атрибутируется: у него нет `TaskId` задачи этапа, и он остаётся в итоге фичи.

**Единственная правка:** в `FeatureSpendRecord` добавляется `Initiator` (аддитивно), его заполняет `SpendFeatureReader` из `SpendRecord.Initiator`. У дневных агрегатов инициатора нет (`DailySpendRow` его не сворачивает), поэтому при `IsDaily` в окне `replies` = `null`, а не заниженное число.

**Проверить в D1b тестом:** запуск исполнителя (`tasks_run_executor`, автозапуск, продолжение после паузы) пишет ход с `Initiator = agent`. Иначе каждый запуск посчитается репликой человека. Если запуск окажется `human`, это дефект разметки инициатора, а не повод вычитать первый ход в метриках.

Подсистема Spend выключена (шов `null`) → `tokens`/`costUsd`/`replies` = `null`, «≈», без 500. Прецедент — решение Андрея 20.10.

## 4. Контракт ответа для D1b

Блок `metrics` в ответе ручки экрана этапа (D1a). camelCase, null-поля сериализуются явно: фронт отличает «не считали» от нуля.

```json
"metrics": {
  "mode": "journal",
  "window": { "from": "2026-11-12T08:00:00Z", "to": "2026-11-12T11:30:00Z", "live": false, "elapsedMinutes": 210 },
  "executor": {
    "minutes": 74, "live": false,
    "turns": 18, "errorTurns": 1,
    "tokens": { "input": 120000, "output": 18000, "cacheRead": 2400000, "cacheCreation": 90000 },
    "costUsd": 3.41, "costPartial": false, "hiddenCostModels": [],
    "models": ["claude-opus-5-5"]
  },
  "review": {
    "minutes": 22, "live": false,
    "turns": 6, "errorTurns": 0,
    "tokens": { "input": 30000, "output": 4000, "cacheRead": 500000, "cacheCreation": 20000 },
    "costUsd": 0.0, "costPartial": true, "hiddenCostModels": ["qwen3.8-flash"],
    "models": ["qwen3.8-flash"],
    "tasks": [ { "taskId": "…", "verdict": "returned", "sharedWith": 1 } ]
  },
  "human": {
    "minutes": 41, "waits": 2, "replies": 3,
    "waitingNow": null
  },
  "idleMinutes": 73,
  "spendFrom": "2026-11-12"
}
```

- `review: null` — у этапа нет задач ревью (это не «0 минут»).
- `human.waitingNow: { "taskId", "since", "reason" }` — ожидание идёт сейчас на `T` или на задаче из `R(T)`.
- `mode: "approx"` — журнал неполный (§6): `minutes` у исполнителя приближённые, у ревью и человека — `null`; `turns`/`errorTurns`/`waits` — `null`. Токены и `replies` от журнала не зависят и считаются как в `journal`.
- `tokens`, `costUsd`, `replies`, `spendFrom` = `null` — Spend выключен или записей нет. `spendFrom` — с какой даты в срезе есть данные, как `FeatureSpend.From`.
- `verdict` — `Verification.Verdict` задачи ревью (`TaskVerdicts`), `null` — ещё не вынесен.

C#-типы — публичные записи `StageMetrics`, `StageMetricsShare`, `StageMetricsHuman`, `StageMetricsReviewTask` в новом файле рядом с `StageIntervals.cs` (прецедент: публичный контракт рядом, функция `internal`).

## 5. Инварианты и пути

**И1. Метрики этапа только читают.** Функция чистая: `(T, R(T), journal, spendRecords, queueIntervals, now) → StageMetrics`. Ни `TaskManager`, ни журнала, ни стора расхода у неё нет — гарантия сигнатурой, как у `FeatureStageIntervals`.

**И2. Ожидание этапа пишет только пара `SetAwaitingHuman`/`ClearAwaitingHuman`, и каждое изменение попадает в журнал.** Пути — таблица §2. Что закрывает D1b:

| Путь | Закрытие в D1b |
|---|---|
| MCP set/clear, `report_up`, ответ человека | существующие тесты FS-E1/E2/E3b; проверить, что каждый ассертит `waiting`/`waiting_cleared` в журнале; недостающее — дописать |
| `MarkClaudeStarted`, переход в `Done` | тест «был `waiting` → перезапуск/закрытие → в журнале `waiting_cleared`»; проверить, что вызывающий шлёт `task_changed` |
| DTO `Create`/`Update` | сторож REST «поле не принимается» (§2) |

**И3. Ручка этапа не отдаёт чужого.** `T` ищется только внутри `run` фичи, полученного тем же гейтом, что `GET features/{featureId}` (`ProjectFull`, owner/`IProjectAccess`, единый 404). `R(T)` ⊂ `run.Reviews`, срез расхода — с `ownerId = epic.OwnerId`.

| Путь чтения метрик | Закрытие |
|---|---|
| ручка экрана этапа D1a (REST, под `[Authorize]`) | гейт фичи + `T ∈ run` (иначе тот же 404) |
| MCP | инструмента нет и в этом шаге не заводим |
| SignalR | `task_changed` несёт задачу, метрик в нём нет |

Тест: `taskId` задачи другой фичи того же владельца → 404. Задача чужого владельца → 404.

**И4. Арифметика ожидания — в одной точке.** Цикл `waiting…(waiting_cleared | now)` с обрезкой по окну и вычетом очереди сейчас живёт внутри `FeatureTimeline.BuildJournal` (`FeatureTimeline.cs:~400–430`, плюс `GroupByTask`, `FirstAfter`, `NetSpan`, `QueueIntervals`). D1b выносит его в `FeatureTimeMath` (например, `WaitingIntervals(entries, taskIds, from, to, now, queue)`) и зовёт из обоих мест. Поведение `FeatureTimeline` не меняется: существующие `FeatureTimelineJournalTests` и `FeatureQueueTraceTests` остаются зелёными без правок. Сторож — тест: у фичи из одного этапа без ожиданий на эпике `human.minutes == timeline.totals.waitingMinutes`.

**И5. Роль расхода определяется задачей, а не записью.** В `SpendRecord` новых полей нет; разметка «ревью/исполнитель» = `TaskId → Role/ReviewOf`.

### Файлы для D1b (бэк)

| Файл | Что |
|---|---|
| `backend/ClaudeHomeServer.Tasks/Projector/FeatureStageMetrics.cs` (новый) | `internal static Build(...)` по §1 |
| `backend/ClaudeHomeServer.Tasks/Projector/StageMetrics.cs` (новый) | публичные записи контракта §4 |
| `backend/ClaudeHomeServer.Tasks/Projector/FeatureTimeMath.cs` | вынесенный расчёт интервалов ожидания (И4) |
| `backend/ClaudeHomeServer.Tasks/Projector/FeatureTimeline.cs` | переход на вынесенную функцию, без смены поведения |
| `backend/ClaudeHomeServer.Core/Services/Spend/ISpendFeatureReader.cs` | `FeatureSpendRecord.Initiator` |
| `backend/ClaudeHomeServer.Spend/SpendFeatureReader.cs` | заполнение `Initiator` |
| `backend/ClaudeHomeServer.Core/Models/TaskItem.cs` | комментарий `:181`: «у любой задачи; у эпика — merge/decision» |
| `backend/ClaudeHomeServer/Controllers/TasksController.cs` | проводка `metrics` в ручку этапа — **у того из D1a/D1b, кто сливается вторым** |
| `backend/ClaudeHomeServer.Tasks.Tests/FeatureStageMetricsTests.cs` (новый) | доли, перекрытие «ожидание сильнее работы», живой хвост, общее ревью, `approx`, `IsDaily` → `replies: null`, сторож И4 |
| `backend/ClaudeHomeServer.Spend.Tests/Services/SpendFeatureReaderTests.cs` | `Initiator` доезжает |
| тест в `ClaudeHomeServer.Tests` | И2 (DTO-сторож, `waiting_cleared` на перезапуске и в `Done`), И3 (404), запуск исполнителя → `Initiator = agent` |

### Файлы для D1d (фронт)

| Файл | Что |
|---|---|
| тип ответа ручки этапа (заводит D1a; иначе — `frontend/src/types/featureStats.ts`) | `StageMetrics` по §4 |
| `frontend/src/components/feature/FeatureStageMetrics.tsx` (новый) | три колонки или плитки: исполнитель / ревью / человек, плюс строка простоя; на мобиле — столбиком |
| переиспользовать | `formatDuration` (`FeatureStatsBits.ts`), `formatTokens` (`FeatureStatsFormat.ts`), `ReviewKv` (`FeatureReviewParts`), правило «`null` — словом, а не нулём» из `FeatureReviewSpend.tsx` |

Фронт ничего не досчитывает. Единственное исключение — тикающее «ждёт сейчас» от `waitingNow.since`.

## 6. Миграция и легаси

Миграции данных нет: всё считается из уже записанного.

| Этап | Что показываем |
|---|---|
| задача создана после B2 (`Stage` есть), журнал полный | `mode: "journal"`, все поля |
| задача фичи до B2 (без `Stage`) | считаем так же: этапом считается задача по `Role`. Экран этапа для таких задач открывает D1a; метрикам `Stage` не нужен |
| журнал фичи неполный (нет `created` в начале или `Truncated`, тот же гейт, что у `FeatureTimeline`) | `mode: "approx"`: исполнитель — `[ClaudeStartedAt, CompletedAt ?? now]` с пометкой «≈» (только последний запуск — `ClaudeStartedAt` переписывается при перезапуске), ревью и человек по минутам — `null`, `waitingNow` — из поля задачи, токены и `replies` — из расхода |
| расход за окном детализации (`IsDaily`) | токены есть, `replies: null` |
| ожидания, поставленные до FS-E1/E2 (до появления `SetAwaitingHuman`) | их нет в журнале — человек показывает 0 минут при `mode: "journal"`. Различить «не ждали» и «не записывали» нельзя; честнее подписи «по данным с …» (`window.from`) ничего не придумать |

## 7. Что НЕ делаем

- Новое поле или новый вид журнала для ожидания этапа.
- Поле роли в `SpendRecord`.
- Деление общего ревью между этапами.
- Свод по цепочке «шаг + доработки» (`StepChains`) на экране этапа — это отдельный вопрос показа для D1d или следующей итерации.
- MCP-инструмент метрик этапа.
- Учёт `device_wait` и ожиданий эпика в доле человека.
