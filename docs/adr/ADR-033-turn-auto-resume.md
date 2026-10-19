# ADR-033: Авто-возобновление ходов после перезапуска сервера

**Статус:** Принято
**Дата:** 2026-10-19 (D1.1 — синхронизация документа с реализацией после архитектурного ревью А)
**Принимающие решение:** Андрей (владелец продукта), Александр (архитектор)
**Автор:** Софья (продакт-аналитик / оркестратор)
**Связанные артефакты:**

- [`SessionManager.cs`](../../backend/ClaudeHomeServer/Services/SessionManager.cs) — `LoadSessions` (захват `Working` → `Orphaned` + снимок кандидатов), `TakeRestartResumeCandidates`, `ResumeAfterRestartAsync`, сброс `RestartResumeAttempts` в `OnMessageAsync` на `ResultMessage`, `EnsureAccumulatorAsync`.
- [`TurnAutoResumeService.cs`](../../backend/ClaudeHomeServer/Services/TurnAutoResume/TurnAutoResumeService.cs) — фоновый `BackgroundService` (ветка, не отдельная сборка).
- [`TurnAutoResumeEligibility.cs`](../../backend/ClaudeHomeServer/Services/TurnAutoResume/TurnAutoResumeEligibility.cs) — чистая функция отбора, без side-effect.
- [`SessionManagerTurnResumeHost.cs`](../../backend/ClaudeHomeServer/Services/TurnAutoResume/SessionManagerTurnResumeHost.cs) — боевая реализация шва, namespace `ClaudeHomeServer.Services` (корень).
- [`ITurnAutoResumeHost.cs`](../../backend/ClaudeHomeServer.Core/Services/Composition/ITurnAutoResumeHost.cs) — интерфейс-шов ядра; адаптер живёт в `Services` намеренно, чтобы вертикаль `TurnAutoResume` не ссылалась на `SessionManager` напрямую (прецедент `ILauncherFactory`, ADR-016).
- [`ProjectDeviceGate.cs`](../../backend/ClaudeHomeServer.Core/Services/Execution/ProjectDeviceGate.cs) — `IProjectDeviceGate.Check(project).IsReady` (онлайн устройства).
- [`TurnResumePrompts.cs`](../../backend/ClaudeHomeServer.Prompts/Services/Prompts/TurnResumePrompts.cs) — текст директивы `ServerRestartResume`.
- [ChatItemView.tsx](../../frontend/src/components/chat/ChatItemView.tsx) — ветка `work_loop_stopped` с `reason === 'server_restart'` (отдельная инфо-пилюля), регресс-тест F1 в [`ChatItemView.test.ts`](../../frontend/src/components/chat/__tests__/ChatItemView.test.ts).

**Состояние реализации (на ветке `feat/turn-auto-resume`):**
бэк — B1–B4 + B2.1 + B2.2 + B-minor (`SessionManager`-часть: захват кандидатов в `LoadSessions`, шов `ITurnAutoResumeHost`, `ResumeAfterRestartAsync`, сброс счётчика на `ResultMessage`, `IsTailIntactForResume`, `EnsureAccumulatorAsync` до карточки; сервис — `TurnAutoResumeService` с порядком `ApplicationStarted` → `AbortMarkersWritten` → `Warmup` → цикл);
фронт — F1 (ветка `server_restart` в `ChatItemView.tsx`) + F1.1 (регресс-тест рендера карточки);
ADR-review — отдельная запись (этот документ);
финальное ревью — A1 (закрыто: `ShutdownInterruptedAt` удалено, см. §«Последствия»).

## Контекст

Активные ходы регулярно обрываются посреди инструмента (`Bash`, `Edit`, `Write`,
`deploy_start`): выкатка ADR-010, OOM под нагрузкой, питание машины, обновление
CLI. После обрыва пользователь видит в ленте карточку «прерванный ход» и
должен сам нажать «Продолжить» — то есть ход, который шёл сам, без явного
клика, и оборвался по внешней причине, требует реакции человека. Это
дефект опыта и источник «потерянных» ходов после рестарта.

Транскрипт хода (`~/.claude/projects/<slug>/{csid}.jsonl`) при этом цел —
CLI пишет его инкрементально, и `--resume <csid>` поднимает процесс с того
же места; этим уже пользуется ручное «Продолжить». Задача фичи — поднять
ту же операцию автоматически для ходов без явного визита пользователя.

Делать это нужно осторожно: автозапуск LLM без явного действия пользователя
с возможностью повтора неидемпотентных действий требует жёсткого сторожа
crash-loop и узкого набора исключений.

## Решение

### Сервис `TurnAutoResumeService : BackgroundService`

`TurnAutoResumeService` заводится как фоновый hosted-сервис
(`Services/TurnAutoResume/TurnAutoResumeService.cs`). `ExecuteAsync` —
строго в порядке:

1. **`ApplicationStarted`** — до него сервер ещё не принимает трафик.
2. **`AbortMarkersWritten`** — `Task` фоновой записи маркеров обрыва из
   `LoadSessions`. Ожидание гарантирует, что карточка/«Продолжи» не
   обгонят маркер прерывания в истории (см. §«Гонка с маркером обрыва»).
3. **`WarmupSeconds`** (по умолчанию 15 с) — пауза, чтобы `SessionManager`
   завершил десериализацию `sessions.json` и поднял сторожей чатов.
4. **Цикл по кандидатам**.

После `Warmup` сервис берёт у `SessionManager` снимок кандидатов через
`ITurnAutoResumeHost.TakeCandidates()` (одноразово, очищается при выдаче —
повторный вызов тем же процессом отдаёт пустой массив) и обходит их с
паузой `GapSeconds` (по умолчанию 5 с) между отправками. Потолок за один
старт — `MaxPerBoot` (по умолчанию 10). Ход, валящий сервер на каждой
попытке, отбивается сторожем `RestartResumeAttempts >= 1` (см. ниже).

Для каждого кандидата сервис шлёт «Продолжи» через штатный
`ITurnAutoResumeHost.ResumeAsync(id)`, под капотом это
`SessionManager.ResumeAfterRestartAsync(sessionId)`. Доставка —
`SendMessageAsync(auto, DeliveryCause.ServerRestartResume)`:
`auto` нужен, чтобы пройти `DeviceWaitGate` для локальных проектов;
`cause` атрибутирует доставку как авто-возобновление, а не как клик
пользователя. Существующий `enum DeliveryCause` (`ServerRestartResume`)
расширять не пришлось.

### Источник истины «оборван рестартом»

Реестр кандидатов на возобновление — **список в памяти текущего старта
сервера**, не новый файл состояния. Две развилки правды и так дают
расхождения, третья (`bootId`/`server-state.json`) нам не нужна.

**Формула кандидата** в реализации — простая и проверяется в
`SessionManager.LoadSessions` (`SessionManager.cs:1416–1423`):

```
кандидат := (исходный Status == Working в sessions.json на момент LoadSessions)
            И ни одна из eligibility-проверок ниже не отбила
            (см. §«Исключения v1»)
```

Две инварианты делают формулу честной (дефект: фича не срабатывала при штатном
рестарте). **Штатная остановка не пишет статусы**: `SessionManager.KillAllProcesses`
поднимает признак остановки первой строкой, и `OnMessageAsync` при нём не переводит
статус по `ErrorMessage`/`ExitedMessage` ни в памяти, ни на диске — иначе умирающий
процесс перетёр бы `Working` в `Error` и кандидатов не осталось. При остановке не
пишутся только обрывы, а `result` пишется: ход честно завершился, и продолжать его
после рестарта нельзя. **Владелец резолвится
через `ResolveOwnerId`**: у проектной сессии `OwnerId` пустой, владелец берётся
из проекта; проверка флага `turn-auto-resume` идёт по резолвленному владельцу,
а не по пустому `OwnerId` (шов `ITurnAutoResumeHost.IsEnabledFor(Session)`).

Конкретные правила, как они работают в коде:

- **Структурно отсекаются `Waiting`, `Starting`, `Orphaned`**: цикл
  `LoadSessions` переводит живой статус (`Working` / `Starting` /
  `Waiting`) в `Orphaned`, и в снимок кандидатов попадают только сессии с
  исходным `Working`. Это не правило в `Eligibility`, а маппинг в
  `LoadSessions` (см. `SessionManager.cs:1407–1423`).
- **`RestartResumeAttempts` уже `≥ 1`** — `Eligibility.Reason =
  already_attempted`. Crash-loop guard, см. §«Сторож crash-loop».
- **`TeamImplement != null`** — `Eligibility.Reason = team_implement`.
  У штаба «Командной реализации» свой сторож волн, двойной перезапуск
  после падения волны даст несогласованное состояние.
- **Групповой чат** (`session.Participants.Count >= 2`) — `Eligibility.
  Reason = group_chat`. Rejoin-цикл по членам группы в v1 не трогаем.
- **Локальный проект с офлайн-устройством** — `Eligibility.Reason =
  device_offline_v1_excluded`. Условие: `ProjectCapabilities.IsDeviceBound
  (project) && !deviceOnline`, где `deviceOnline` — `IProjectDeviceGate.
  Check(project).IsReady` через шов `ITurnAutoResumeHost.IsDeviceOnline`.
  Инлайновых `if (project.IsLocal)` не заводим (ADR-016 §4): единственная
  точка правды — `ProjectCapabilities`.
- **Последний `tool_use` без result — `deploy_start`/`deploy_rollback`** —
  `Eligibility.Reason = deploy_loop`. Проверка в `Eligibility` через
  `ITurnAutoResumeHost.LastTurnWasDeployOrRollback(session, project)`,
  см. §«Сценарии отказа». Возобновлять запрещено: возобновлённый агент
  снова выкатит, и так по кругу.
- **Чат в архиве** (`session.IsArchived` — `ArchivedAt != null &&
  UpdatedAt <= ArchivedAt`) — `Eligibility.Reason = archived`. Включено
  в v1 (было отдельным пунктом в постановке A1; закрыто в коде).

Конкретные **коды причин** (константы для тестов и метрик) перечислены в
`TurnAutoResumeEligibility.cs`: `already_attempted`, `team_implement`,
`group_chat`, `device_offline_v1_excluded`, `deploy_loop`, `archived`.

### Гонка с маркером обрыва `AppendTurnAbortedAsync`

Контракт `AppendTurnAbortedAsync` раньше вызывался через
`_ = Task.Run(...)` (fire-and-forget). В авто-возобновлении это создаёт
гонку: авто-отправка «Продолжи» может уйти раньше маркера, и в истории
окажется:

```
[user]: Продолжи (авто)
[assistant]: ...
[user]: [server_restart] ход был прерван
```

— то есть маркер приехал позже «продолжающего» сообщения и разрушил
контекст.

**Изменение в реализации:** `SessionManager` хранит фоновый `Task` записи
маркеров в свойстве `AbortMarkersWritten` (`SessionManager.cs:1442–1451`).
Сервис ждёт его до первой отправки (`TurnAutoResumeService.cs:44`). Для
ходов с уже завершённой записью маркера это no-op (`Task.CompletedTask`);
для свежеоборванных — точка синхронизации. Порядок в сервисе:
`ApplicationStarted` → `AbortMarkersWritten` → `Warmup` → цикл.

Поведение продукта не меняется, кроме самой гонки: на стороне
`SessionManager` — это просто переход от fire-and-forget к
сохранённому `Task` (а не «`await` везде»; большинство вызывающих мест
остаются без `await` и не должны меняться).

### Сторож crash-loop

`Session.RestartResumeAttempts` — счётчик авто-запусков на сессии.
Поведение в реализации:

- **+1 в `SessionManager.ResumeAfterRestartAsync`** (`SessionManager.cs:7060`),
  не в сервисе: инкремент идёт рука об руку с гидратацией аккумулятора
  и записью карточки. Сервис только запрашивает `ResumeAsync` и
  обрабатывает исход.
- **Сброс ТОЛЬКО на `ResultMessage`** в `SessionManager.OnMessageAsync`
  (`SessionManager.cs:9818–9826`): нормальный финиш хода обнуляет
  счётчик; `ErrorMessage` и `ExitedMessage` без предшествующего
  `ResultMessage` — не сбрасывают. Это безопаснее для crash-loop, но
  блокирует повторное авто-возобновление после отказа — см.
  §«Сценарии отказа», п. 5.
- **В `Eligibility` отбивается значение `>= 1`** (`TurnAutoResumeEligibility.cs:42`):
  попытка была, нового авто-старта не даём.

Это лечит ход, валящий сервер сам (OOM посреди хода, рекурсивный
crash-loop): первый старт отправил авто-ход, тот снова уронил сервер,
второй старт видит счётчик `>= 1` и не трогает сессию — пользователь
должен разобраться руками.

### UX-карточка

Перед фактической отправкой `ResumeAfterRestartAsync` вызывает
`AddWorkLoopStoppedNoticeAsync(sessionId, entry, "server_restart", ...)`.

**Текст карточки** (формируется на сервере, фронт не пересобирает,
`SessionManager.cs:7055–7056`):

> «Сервер был перезапущен — продолжаю ход с того же места»

**Гидратация истории.** Карточка пишется в аккумулятор истории чата
(`AddWorkLoopStoppedNoticeAsync` в `SessionManager.cs:7198–7204` —
`acc.Append(new StoredWorkLoopStoppedMessage(reason, text))`); метод
молча выходит, если `entry.Accumulator is null`. Чтобы карточка увиделась,
`ResumeAfterRestartAsync` обязан вызвать `EnsureAccumulatorAsync(entry)`
**до** карточки (`SessionManager.cs:7052`). Речь про **аккумулятор
истории** (`SessionManager.cs:5990`), не про `Services.ChatContext`
(другая подсистема, ADR-023) — имя «ChatContext» в этом контексте было бы
ошибкой.

**Доставка до фронта.** Пара `reason` + `text` летит в `StoredWorkLoopStoppedMessage`
→ `BroadcastAsync` клиенту как `WorkLoopStoppedMessage(reason, text)`.
Никаких новых `ServerMessage`-типов ради этой карточки нет.

**Фронт** (`frontend/src/components/chat/ChatItemView.tsx`) получил
**отдельную** ветку в `case 'work_loop_stopped'` для `reason === 'server_restart'`
(`ChatItemView.tsx:1991–2004`): инфо-пилюля со значком `RotateCcw`, цвет
`C.infoBg`/`C.info`, текст сервера, без кнопок. Сброс счётчика
происходит в `SessionManager.OnMessageAsync`, не по шине. Регресс-тест F1 живёт в
[`ChatItemView.test.ts`](../../frontend/src/components/chat/__tests__/ChatItemView.test.ts)
(`describe('ChatItemView — work_loop_stopped: server_restart')`).

### Шов `ITurnAutoResumeHost`

Вертикаль `TurnAutoResume` не может ссылаться на `SessionManager` и
`ProjectManager` напрямую — сторож `SubsystemBoundaryTests` этого не
позволит (вертикали не должны зависеть друг от друга, ADR-014 §«Правило
зависимостей»). Всё нужное сервису вынесено в интерфейс-шов
[`ITurnAutoResumeHost`](../../backend/ClaudeHomeServer.Core/Services/Composition/ITurnAutoResumeHost.cs)
в Core (`Services/Composition`). Реализация —
[`SessionManagerTurnResumeHost.cs`](../../backend/ClaudeHomeServer/Services/TurnAutoResume/SessionManagerTurnResumeHost.cs),
адаптер тонко оборачивает `SessionManager`, `ProjectManager`,
`FeatureFlagService` и `IProjectDeviceGate`. Namespace адаптера —
`ClaudeHomeServer.Services` (корень), как у `ILauncherFactory`
(ADR-016), потому что адаптер знает корневой сервис.

Локальность проекта проверяется через `ProjectCapabilities.IsDeviceBound`
и `deviceGate.Check(project).IsReady` — **не** через инлайн
`project.IsLocal && !deviceOnline`. Проверка транскрипта на устройстве
(`SessionManagerTurnResumeHost.LastTurnWasDeployOrRollback`) отдельно
проверяет `ProjectCapabilities.TranscriptOnServer(project)` и для
локальных проектов сразу отдаёт `false` — сервер транскрипт всё равно не
читает (ADR-016), а путать «транскрипта нет» с «транскрипт битый» нельзя.

### Промпт продолжения

В [`TurnResumePrompts.cs`](../../backend/ClaudeHomeServer.Prompts/Services/Prompts/TurnResumePrompts.cs)
функция `ServerRestartResume()` возвращает короткую директиву агенту:

> Сервер был перезапущен посреди твоего хода, транскрипт цел. Сначала
> проверь результат последнего действия — не повторяй вслепую то, что
> могло уже выполниться; затем продолжи задачу.

Это **не** приказ «возобнови любой ценой»: явное напоминание свериться
с состоянием предохраняет от повтора `Write`/`Bash`/`deploy_start`
вслепую.

### Конфиг и фич-флаг

Фич-флаг `turn-auto-resume`:

```csharp
new FeatureFlagDefinition(
    Key: FeatureFlagKeys.TurnAutoResume,
    Title: "Продолжать ходы после перезапуска сервера",
    Description: "Автоматически дотягивать прерванные активные ходы
                  через Claude CLI resume после рестарта. Default false, per-owner.",
    Default: false,
    Stage: "dev"),
```

`default: false` сознательно: это **автозапуск LLM без явного клика
пользователя** с возможным повтором неидемпотентных действий — флаг
первым обкатаем на себе.

Конфиг секции `TurnAutoResume` в `appsettings.json`:

```json
{
  "TurnAutoResume": {
    "MaxPerBoot": 10,
    "WarmupSeconds": 15,
    "GapSeconds": 5
  }
}
```

Поля `TurnAutoResumeOptions` (`Models/TurnAutoResumeOptions.cs`) — три
перечисленных; **`Enabled` нет**. Включение идёт ТОЛЬКО через
пер-владельческий флаг `turn-auto-resume`; конфиг не задаёт
включённость, потому что флаг per-owner, а конфиг общий. Для
штатного выключения сервиса — снять флаг; `MaxPerBoot = 0` —
«потолок буквально 0, цикл не отправит ничего», что **фактически**
выключает сервис без удаления DI-регистрации (для on-call удобно).

- **`MaxPerBoot = 10`** — потолок за один старт. Обоснование —
  сценарий «N одновременных `ResumeAfterRestartAsync` на старте сервера
  плюс уже тёплый путь соседних CLI» создаёт всплеск нагрузки, который
  наслаивается на общий cgroup `ccs-agents.slice` (см.
  [docs/architecture/sandbox.md](sandbox.md) — лимиты slice, OOM-инцидент
  21.09 как раз пришёл от сборок **внутри** хода под тем же cgroup).
  Правило расчёта машинно-специфичных значений — там же; конкретное
  число 10 — консервативная отправная точка и подбирается замерами.
- **`WarmupSeconds = 15`** — пауза после `ApplicationStarted`, чтобы
  `SessionManager` и серверные сторожа чатов закрепились. Уменьшение —
  отдельная правка с замером.
- **`GapSeconds = 5`** — пауза между сессиями при последовательной
  отправке. Смягчает одновременный разогрев нескольких CLI.

## Сценарии отказа и реакция

1. **Ход валит сервер сам.** OOM посреди хода или рекурсивный падеж.
   Сторож `RestartResumeAttempts >= 1` отбивает повтор на втором старте
   (`Eligibility` → `already_attempted`); чат остаётся в `Orphaned` без
   автоотправки, пользователь видит баннер и жмёт «Продолжить» руками.
2. **Хвост с `deploy_start` / `deploy_rollback`.** Последний `tool_use`
   без result, и он — `deploy_start`/`deploy_rollback`.
   `Eligibility.Reason = deploy_loop`, баннер остаётся.
3. **Битый транскрипт не-deploy.** Последний `tool_use` без result —
   что-то ещё (не deploy). Это попадает уже не в `Eligibility`, а в
   `ResumeAfterRestartAsync` через `IsTailIntactForResume`
   (`SessionManager.cs:7016` — `IsTailIntact` из `TranscriptProbe`).
   `Reason = "Хвост транскрипта повреждён"`, баннер остаётся. Проверка
   делается ТОЛЬКО при `TranscriptOnServer`: для локального проекта
   (`ProjectCapabilities.TranscriptOnServer` → false) обе проверки
   пропускаются (`LastTurnWasDeployOrRollback` → false,
   `IsTailIntactForResume` → true), Eligibility не блокирует.
4. **Офлайн-устройство локального проекта.** Через
   `ProjectCapabilities.IsDeviceBound(project)` и
   `IProjectDeviceGate.Check(project).IsReady` (`IsDeviceOnline` в шве).
   `Eligibility.Reason = device_offline_v1_excluded`, баннер остаётся;
   в очередь на устройство не встаём.
5. **Resume упал по сети/модели.** Фолбэк-цепочка ADR-007 → `error`
   через `OnMessageAsync` (`SessionManager.cs:9828`). Счётчик
   `RestartResumeAttempts` на этом пути **не сбрасывается**: сброс
   только на `ResultMessage`. Сессия остаётся заблокированной для
   авто-возобновления до ручного «Продолжить» (или явного обнуления
   счётчика). Это безопаснее для crash-loop, но иллюстрирует риск
   «ручного повтора»: после одной неудачной попытки автоматика больше
   не пробует, и человек обязан сам нажать «Продолжить».

## Отвергнутые варианты

- **`bootId` и `data/server-state.json`** — вторая точка правды поверх
  `Status` и снимка. Конфликты записи при concurrent-стартах двух
  реплик (когда будем масштабировать), ничего не даёт на одну реплику.
  Статуса `Working` в `LoadSessions` достаточно.
- **`deploy_loop` как Eligibility-проверка именно на `deploy_start`/
  `deploy_rollback`, а не «вообще любой неотвеченный `tool_use` без
  разбора».** Отвергнут более широкий вариант «любой `tool_use` без
  result → отказ» — в общем случае это может быть легитимный
  `Bash`/`Write` с целым хвостом, и блокировать такие возобновления
  нельзя. Узкая Eligibility-проверка `deploy_loop` ловит точечно
  (`TranscriptProbe.LastToolUseIsDeployOrRollback`); битый
  нетранскрипт ловится отдельной `IsTailIntactForResume` в
  `ResumeAfterRestartAsync` (`Reason = "Хвост транскрипта повреждён"`).
- **Отдельный `ServerMessage` для авто-возобновления.** Переиспользуем
  существующий `work_loop_stopped` с `reason="server_restart"`. Канал
  стабильный, фронт получил отдельную ветку в `case` (F1) — но сама
  схема событий старая, новых правил на `client.ts` не появилось.
- **`systemDirective`-доставка.** Обходит `DeviceWaitGate` и ручной
  триггер пользователя в локальных проектах. Не используем: первое
  нарушает «человек разрешает запуск», второе — «устройство должно
  быть онлайн», и обход этих гейтов именно для авто-возобновления
  неуместен.
- **Возобновление `Waiting`.** Маппинг исходного `Working` в
  `LoadSessions` отсекает `Waiting` структурно (см. §«Источник истины»).
  В `Eligibility` отдельного правила нет и быть не должно — мы не
  выбираем за человека, чат ждавший ввода — это не «прерванный
  рестартом».
- **Потолок обязателен; отвергнут вариант «вообще без потолка».** На инциденте 21.09 (`sandbox.md`)
  сборки внутри хода под `ccs-agents.slice` создали пик, который оомд
  отработал через `ManagedOOMMemoryPressure`; N одновременных resume на
  старте плюс уже тёплый путь соседей могут наложиться на ту же
  cgroup. Потолок обязателен; `MaxPerBoot = 0` — технический
  аварийный выключатель без удаления DI.
- **Сброс `RestartResumeAttempts` по таймеру, а не на `ResultMessage`.**
  Маскирует crash-loop, разрешает повторный авто-запуск того же
  валящегося хода через час. Не используем.

## Последствия (что НЕ решено)

- **Неидемпотентные действия при авто-возобновлении.** Пользователь
  обязан понимать через карточку, что ход мог выполнить то, чего он
  не видел (`Bash` с `rm -rf`, `git push` и т. п. могли отработать до
  того, как сервер упал). Текст карточки должен быть коротким и
  недвусмысленным.
- **Поведение зависит от `appsettings.Local.json`** (потолок
  `MaxPerBoot`). При `MaxPerBoot = 10` и `GapSeconds = 5` полная
  серия автоотправок занимает максимум ≈ `(MaxPerBoot − 1) × GapSeconds +
  WarmupSeconds` ≈ 60 с; остальные сверх потолка остаются ждать
  следующего захода пользователя и ручного «Продолжить». Это
  сознательно (OOM-инцидент через cgroup), но требует документирования
  в on-call чек-листе.
- **Стоимость токенов.** Автозапуск LLM без явного клика. Флаг
  `turn-auto-resume` обязателен, потому что иначе платный LLM стартует
  без явного сигнала «да, я оплачу этот прогон».
- **Сторож `MaxPerBoot` — на инстанс, не на чат.** Сверх `MaxPerBoot`
  (или при выключенном флаге) автоотправки нет: `TakeRestartResumeCandidates`
  отдаёт и очищает список, на следующем старте те же сессии уже `Orphaned`
  и кандидатами не становятся. Для них остаётся только ручное «Продолжить»
  в чате. Это компромисс: снимать со счётчика мы не можем, потому что
  иначе неконтролируемо накопим CLI-процессы.
- **Счётчик `RestartResumeAttempts` сбрасывается только на
  `ResultMessage`.** После `ErrorMessage` или выхода без result
  следующая попытка авто-возобновления уже заблокирована. Это
  сознательный trade-off: безопаснее для crash-loop, но требует
  ручного «Продолжить» у пользователя после любого отказа (см.
  §«Сценарии отказа», п. 5).
- **Не покрывает восстановление `deploy_start`** как и описано выше:
  если пользователь поймал «хвост упал посреди выкатки», он жмёт
  «Продолжить» руками и руками же решает, что делать с полу-выкатанным
  состоянием.
- **`ShutdownInterruptedAt` удалено решением A1 (b).** Поле диагностики
  не несло: при штатной остановке оно гасится при первом старте без записи
  в лог, а при OOM/`kill -9` не пишется вовсе; зато ради него
  `KillAllProcesses` получил лишний `SaveSessions()` под `_saveLock` — тот
  самый класс зависаний остановки, с которым уже боролись.

## Связанные документы

- [ADR-007](ADR-007-model-preset-chains.md) §4 — фолбэк-цепочка для
  «resume упал по сети/модели».
- [ADR-010](ADR-010-deploy-from-chat.md) — выкатка, основной источник
  рестартов.
- [ADR-014](ADR-014-internal-subsystems.md) — правило зависимостей
  между вертикалями и обоснование шва `ITurnAutoResumeHost` в Core.
- [ADR-016](ADR-016-local-projects.md) — локальные проекты, шов
  `IProjectDeviceGate`, `ProjectCapabilities.IsDeviceBound` /
  `ProjectCapabilities.TranscriptOnServer`, запрет инлайн-проверок
  `if (project.IsLocal)`.
- [ADR-023](ADR-023-turn-context.md) — это НЕ тот `ChatContext`, что в
  карточке auto-resume; в нашем случае «контекст» — это аккумулятор
  истории, явно `TurnAccumulator`.
- [docs/architecture/sandbox.md](sandbox.md) — cgroup
  `ccs-agents.slice` и OOM-инцидент 21.09 как обоснование потолка.
- [docs/architecture/features.md](features.md) — раздел про
  фоновые сторожа и автоматику после рестарта.
