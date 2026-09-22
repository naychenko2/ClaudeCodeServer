# ADR-013: Шина событий хода (TurnEventBus)

**Статус:** Принято (этап 0 — каркас и список событий v1; этап 1 — переезд существующих
sink'ов и паспорта хода на шину; этап 2 — новая функциональность поверх шины).
**Дата:** 2026-09-01
**Принимающие решение:** Андрей (владелец продукта), Александр (архитектор)
**Реализация:** Денис (бэкенд)
**Связанные артефакты:**
[TurnEventBus.cs](../../backend/ClaudeHomeServer/Services/Turn/TurnEventBus.cs),
[TurnEvents.cs](../../backend/ClaudeHomeServer/Services/Turn/TurnEvents.cs),
[TurnEventBusTests.cs](../../backend/ClaudeHomeServer.Tests/Services/TurnEventBusTests.cs),
[TurnEventsMapContractTests.cs](../../backend/ClaudeHomeServer.Tests/Services/TurnEventsMapContractTests.cs)
(новый — тест-генератор карты)

## Контекст

К этапу 0 продукт уже держит три «точечные шины» — каждая заточена под одного потребителя,
общая у них только сигнатура метода на контексте хода:

- `SubagentRunSink` (`ClaudeSession.SubagentRunSink`): сабагент-холдер вызывает sink из
  `SubagentStreamWatcher.Finalize`, который пишет паспорт прогона в `SubagentRunLog`
  и взводит `TruncatedSubagent`/`TruncatedBgNote`. Потребитель ровно один —
  `SessionManager.SubagentRunSinkFor`. Поле контекста — статичное, без per-owner изоляции.
- `PromptSnapshotSink`/`PromptSnapshotToolsSink` (`ClaudeSession`): снимок промпта хода в
  `PromptSnapshotStore` — первичная запись (Draft) и дозапись состава инструментов из
  `system/init`. Потребитель один, поле контекста то же.
- `TurnRunLog`: запись паспорта хода из `finally` блока цикла попыток
  `FallbackLlmSessionAdapter`. Один источник записи, единственный путь фиксации.

Что болит: на каждое новое наблюдение за ходом приходится заводить поле контекста и парный
sink; per-owner изоляции на этих точках нет, она обеспечивается тем, что sink смотрит на
`IHubContext` или на сессионный scope, а у хода вне HTTP (фоновый ход, ход-исполнитель
задачи) ни того, ни другого нет — и добавление нового sink'а снова будет требовать
частного решения.

Цель этапа 0 — единая шина с явным контрактом вида события, общим контекстом хода
(`TurnContext`) и владельцем, который берётся не из «текущего пользователя», а из самого
события. Подписчиков на этом этапе нет: поведение продукта не меняется, решаются только
контракт и список событий v1.

## Решение

### Контракт

```csharp
public sealed record TurnContext(
    string SessionId, string? OwnerId, int TurnSeq, int AgentDepth, string? ProjectId = null);

public interface ITurnEvent { TurnContext Turn { get; } }
public interface ITurnNotification : ITurnEvent { }       // fire-and-forget
public interface ITurnFilter : ITurnEvent { }              // waterfall (Filter-цепочка)

public delegate Task TurnFilterHandler<in T>(T e, Func<Task> next) where T : ITurnFilter;

public interface ITurnEventBus
{
    void OnNotification<T>(Func<T, Task> handler, string? name = null) where T : ITurnNotification;
    void OnFilter<T>(int order, TurnFilterHandler<T> handler, string? name = null) where T : ITurnFilter;
    Task PublishAsync<T>(T e) where T : ITurnNotification;
    Task<T> ApplyAsync<T>(T e) where T : ITurnFilter;
}
```

- **`TurnContext`** — общая часть каждого события. `OwnerId` берётся отсюда, а не из
  «текущего пользователя»: подписчик может исполняться вне HTTP-запроса (фоновый ход,
  ход-исполнитель задачи), и единственный достоверный источник per-owner изоляции — само
  событие. `TurnSeq` — номер хода в сессии, `AgentDepth` — глубина делегирования
  (агентные ходы), `ProjectId` — null у чата вне проекта.
- **Вид события — контракт отказа, не оформление.** `ITurnNotification` и `ITurnFilter`
  НЕ наследуют друг другу; это защищено тестом `Контракт_Notification_и_Filter_раздельный_по_типу_события`
  в `TurnEventBusTests.cs`. Решение «Notification или Filter» принимает владелец события
  исходя из того, что хуже: проглотить правку промпта/модели или упасть.
- **`ITurnNotification`** — факт свершился (fire-and-forget). Подписчик стоит в стороне от
  пути хода: порядка между подписчиками нет, исключение гасится и логируется, ход не
  падает. Это нормальная семантика наблюдения, и у `subagent/completed`, `turn/completed`
  она единственно правильная.
- **`ITurnFilter`** — водопад (waterfall). Подписчик стоит В ПУТИ хода: правит событие и
  передаёт управление дальше вызовом `next()`. Порядок явный (`order`, меньше = раньше;
  равные `order` идут в порядке подписки — стабилизируется монотонным счётчиком
  подписки). Исключение подписчика — честный сбой хода: молча пропустить правку промпта
  хуже, чем упасть. Это нормальная семантика для `prompt/assembling`.
- **Не позвал `next()` — `InvalidOperationException` с понятным сообщением.** Многократный
  вызов `next()` — тоже ошибка. Тихого зависания нет: фильтр, который «забыл» позвать
  `next()`, роняет ход диагностируемой строкой, а не оставляет правку промпта на полдороге.
- **Один экземпляр шины на инстанс.** Подписки живут дольше сессии; изоляция владельцев —
  через `TurnContext` события, а не через отдельные шины. Это та же модель, что в
  продуктовых MCP-серверах (ADR-012): один реестр, разные владельцы по контексту вызова.

### Карта событий v1

Четыре события. В исходной постановке этапа 0 их было семь (вместе с `subagent/completed`,
добавленным под переезд `SubagentRunSink`); три события (turn/started, turn/attempt-started
и tool/result) сняты как неиспользованные — см. «Что осталось за бортом» ниже и решение в
[`docs/research/session-core-split-2026-09.md`](research/session-core-split-2026-09.md),
раздел «Ответы на открытые вопросы и план шага 1».

| Событие | Вид | Producer | Consumer этапа 1 | Зачем |
|---|---|---|---|---|
| `prompt/assembling` | Filter | `ClaudeSession` (перед склейкой системного промпта) | `PromptSectionContributors` (recall-notes, persona-recall, team-memory и т.п.) | Водопад секций промпта: подписчик читает `TurnText` и `PromptSessionContext`, добавляет свою `PromptSection` или правит чужие. Параллельный выход `ManifestItems` — «использовано сейчас» для F3, отдельным `RecallManifestMessage` едет во фронт |
| `prompt/assembled` | Notification | `ClaudeSession` (промпт склеен и уходит в процесс) | `PromptSnapshotStore` (этап 1: переезд `PromptSnapshotSink` + `PromptSnapshotToolsSink` с полей контекста на шину) | Снимок промпта. `Phase` (`Draft`/`Tools`) различает первичную запись и дозапись состава инструментов из `system/init` |
| `subagent/completed` | Notification | `SubagentStreamWatcher.Finalize` | `SessionManager.SubagentRunSinkFor` (этап 1: переезд `SubagentRunSink`) | Паспорт сабагента. **Отдельное событие, а не часть `turn/completed`.** Иначе side-effects «обрыва посреди хода» (`TruncatedSubagent`/`TruncatedBgNote`, по которым политика добиваний отличает обрывок от итога) ехали бы к самому концу хода, и директивы добиваний теряли бы своевременность |
| `turn/completed` | Notification | `FallbackLlmSessionAdapter` (`finally` цикла попыток) | `TurnRunLog` (этап 1: переезд записи паспорта хода с `finally` на подписчика); `Services/Team/TeamTurnCompletionService` (этап 4: боевой подписчик конца хода штаба — замещает прямой вызов `HandleTeamTurnEndAsync` из `OnMessageAsync`; план в `LastTeamTurnEnds` по ключу `TurnSeq`. Шаг 1в завёл подписчик в `SessionManager`, шаг 2г-3и волной Ж перенёс тело в вертикаль — в ядре осталась owning-обёртка `HandleTeamTurnCompletedShim`, а доступ к `LastTeamTurnEnds` идёт через шов `ITeamRunState`) | Паспорт хода. `Outcome` (success/failed/egress_down/**local_down**/**window_1m_unavailable**/interrupted/cancelled/crashed — восемь исходов, сверено с `turnOutcome` в `FallbackLlmSessionAdapter` 2026-09-22), `ErrorClass` (`TurnErrorClassifier`) у неуспешного исхода, `Passport` (полный `TurnRunPassport`) |

Строки `Outcome` и `ErrorClass` — не `enum`'ы: шина не должна зависеть от внутренних
типов слоя LLM, чтобы миграция каталога исходов/классов не трогала подписчиков.
`CompactOutcome` (`Passport.CompactOutcome`) даёт потребителям, которым достаточно исхода,
не зависеть от типа `TurnRunPassport` целиком.

Слой персоны едет **отдельной секцией** `Key="persona-layer"` и клеится `Combine` через
`PersonaSeparator` после тела (`CLAUDE.md`, раздел «Голосовой режим чата»); контрибьюторы
`prompt/assembling` его НЕ дублируют.

### UI — только через существующую дорогу

**`TurnEventBus` НЕ заводит вторую дорогу к клиенту.** В UI события не ходят. Всё, что
видит фронт, идёт через существующий `ServerMessage`/`OnMessage`. Это правило записано
в шапке `TurnEvents.cs` комментарием и продублировано здесь как инвариант этапа.

Причины:

- **`ServerMessage` — единственный источник правды для фронта.** Добавлять параллельный
  канал, доставляемый «через шину», значит завести второй `tools/list` для UI-клиента
  и второй протокол согласования форматов. Это и инвариант MCP-серверов из CLAUDE.md
  («состав `tools/list` не зависит от хода») ломает, и простоту стрима размывает.
- **Шина — внутренний инструмент сервера.** Подписчики могут исполняться вне HTTP
  (фоновый ход, ход-исполнитель задачи), и доставлять их события в UI-клиент в общем
  случае некорректно — у клиента нет `OwnerId`/`SessionId` того хода.
- **Ни одно из четырёх событий шины не имеет UI-представления в v1.** Если в этапе 2 такое
  представление появится — оно придёт как НОВЫЙ тип `ServerMessage`, а не как проброс шины
  наружу.

Если в этапе 2 понадобится UI-уведомление о факте хода (например, признак «ход поднят
человеком или агентом» для штаба) — заводится точечный подписчик шины на НОВОЕ событие
с полями, которые нужны настоящему потребителю, а не как проброс старого объявления.

### Не трогаем в v1

Два места в `ClaudeSession` сознательно остаются прямой точкой и НЕ переводятся на шину
в этапах 0–2:

#### `ClaudeSession.BuildTurnMcpConfig`

Состав MCP-конфига хода входит в сигнатуру запуска CLI — это контракт, который
выполняется **до** старта процесса. Любая зависимость `tools/list` от свойств хода
перезапускает процесс со всеми MCP-серверами; см. CLAUDE.md, раздел «MCP-серверы
продукта», инвариант «состав `tools/list` не зависит от хода». Шина здесь означала бы
неявную зависимость «что подписчик сделает с `prompt/assembling`» от того, какой набор
серверов CLI увидит на старте → «Stream closed» / «No such tool available» у половины
тулсетов.

Перенос `BuildTurnMcpConfig` на шину — отдельная задача под отдельный ADR, не этапы 0–2
этого. До того водопад `prompt/assembling` трогает состав секций, а не состав серверов.

#### `ClaudeSession.DecidePermissionAsync`

Путь безопасности: granting/denial `tool_use` по `sdk_control_request` от CLI. Цикл —
CLI спрашивает → бэкенд отвечает `control_response` в stdin → CLI продолжает ход.
Подписчик шины, роняющий ход или висящий дольше разрешённого, ломает этот цикл на
самом деликатном стыке: пользователь уже видит permission-диалог, и таймаут ответа от
бэкенда означает потерю хода, а не сообщение об ошибке (CLI не умеет «откатить»
permission-фазу). Текущий код держит эту точку прямой по той же причине.

Решение о выносе `DecidePermissionAsync` на шину принимаем после этапа 2 — когда увидим
паттерны остальных потребителей и сможем спроектировать изоляцию permission-цикла
(вероятно, отдельный вид события `ITurnPermissionRequest`, синхронный и с дедлайном).
В v1 она остаётся прямой точкой в `ClaudeSession`.

### Тест-генератор карты

Карта в этом доке **собирается из кода**: единственный источник правды для списка
событий — `const string Event` в типах `Services.Turn`, реализующих `ITurnNotification`
или `ITurnFilter`. Расхождение между ADR и кодом — красный тест.

`TurnEventsMapContractTests.cs` (новый) делает две вещи:

1. **Самопроверка шины.** По рефлексии ассембли находит все неабстрактные публичные
   типы в `Services.Turn`, наследующие `ITurnNotification` или `ITurnFilter`; для
   каждого проверяет наличие `const string Event` и уникальность значения. Новый тип
   события без `Event`-константы ловится сразу.
2. **Кросс-проверка с ADR.** Читает `docs/adr/ADR-013-turn-event-bus.md` как строку,
   регуляркой вытаскивает все идентификаторы событий (`turn/...`, `prompt/...`,
   `tool/...`, `subagent/...` внутри backticks), сравнивает с множеством `Event`
   из кода. Несовпадение — `Assert.Equal` с перечислением того, чего не хватает и
   что лишнее.

Это и есть «карта в доке собирается из кода»: единственный способ добавить новое событие
— изменить и тип в `Services/Turn/`, и таблицу в ADR одним коммитом; иначе CI красный.
Заодно тест служит сторожем: если в `Services.Turn` завели новый тип события, но не
дописали строку в таблицу ADR — задача считается не сданной, пока таблица не подтянута.

Образец стиля — `LocalActionRoutingTests.Catalog_AllKeysUnique` (рефлексия по
`LocalActionCatalog.All` и проверка уникальности ключей) и `featureFlags.contract.test.ts`
на фронте (кросс-тест «C#-каталог ↔ TS-константы», читает C#-файл напрямую по
регулярке). Тест карты событий идёт по тому же принципу, но в обратную сторону —
читает ADR, а не C#-каталог.

## Что осталось за бортом (этап 2)

Три события (turn/started, turn/attempt-started и tool/result) сняты как неиспользованные
(этап 4 подготовки к выносу штаба; решение —
[`docs/research/session-core-split-2026-09.md`](research/session-core-split-2026-09.md),
раздел «Ответы на открытые вопросы и план шага 1»): вне `TurnEvents.cs` имён нет, ни
издателя, ни подписчика; обоснование каждого в исходной постановке устарело фактически
(счётчика трат в `LlmSessionAdapterFactory`, ради которого заводилось turn/attempt-started,
больше нет; tool/result легло бы в горячий путь `ClaudeSession.ProcessLineAsync` без
потребителя; нагрузка turn/started в виде (Turn, Text) не покрывает реальную потребность
штаба — не хватает auto/systemDirective). Если в этапе 2 такое представление понадобится —
заводится точечный подписчик шины на НОВОЕ событие с фактической нагрузкой, а не возврат
старого объявления.

Вынос `DecidePermissionAsync` на шину — отдельный вопрос (см. §«Не трогаем в v1»).
Состав подписчиков `prompt/assembling` и `prompt/assembled` — по результатам этапа 1
(переезд sink'ов и `PromptSnapshotStore`).
