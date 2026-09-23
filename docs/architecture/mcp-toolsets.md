# Продуктовые MCP-тулсеты (MCP-over-HTTP)

> Общий механизм транспорта — [mcp-servers.md](mcp-servers.md) и [ADR-012](../adr/ADR-012-mcp-over-http-transport.md)
> (переезд со stdio на HTTP внутри Kestrel), выжимка — в [CLAUDE.md](../../CLAUDE.md).
> Здесь — справочник по каждому тулсету: состав инструментов, гейты, источник контекста.
> Читать перед правкой тулсета и перед добавлением нового.

Продуктовый MCP-сервер — это C#-класс **тулсет**, реализующий `IMcpToolset`
([IMcpToolset.cs](../../backend/ClaudeHomeServer.Core/Services/Mcp/Http/IMcpToolset.cs)).
Все тулсеты обслуживает **один** контроллер
([McpTransportController](../../backend/ClaudeHomeServer/Controllers/McpTransportController.cs)):
маршрут `POST /mcp/{name}[/{хвост}]`, JSON-RPC 2.0 в теле, `Authorization` — сервисный JWT
владельца. Имя `{name}` — ключ сервера в конфиге хода (константы
[McpEndpoints.cs](../../backend/ClaudeHomeServer.Core/Services/McpEndpoints.cs));
дубль имени двух тулсетов — ошибка старта
([McpToolsetRegistry](../../backend/ClaudeHomeServer/Services/Mcp/Http/McpToolsetRegistry.cs)).

Тулсетов два сорта (контракт `IMcpToolset`):

- **статический** (`IMcpStaticToolset`) — фиксированный состав, не зависящий ни от чего
  (`widgets`);
- **параметризованный** (`IMcpParameterizedToolset`) — состав и/или данные зависят от
  **хвоста маршрута** (`POST /mcp/{name}/{хвост}`). Хвост — наш код: его кладёт конфиг
  хода, модель его не видит, поэтому из него берут СЕССИЮ-вызывателя (tasks, notes,
  personas, wsp, codegraph, notifications, dify, higgsfield, watch, websearch) или
  персону/проект (memory). Вызов без хвоста на параметризованный сервер — 404
  контроллера; чужой/невалидный хвост — пустой состав (fail-closed).

Источники прав и контекста у каждого вызова:

- **владелец** — только из claim `sub` сервисного JWT заголовка `Authorization`
  (`McpToolCallContext.OwnerId`); тело, URL и заголовки на владельца не влияют;
- **сессия-вызыватель** — хвост маршрута (а у stdio-ветки — env `*_SESSION_ID`),
  резолвится через `SessionManager.GetOwned` (принадлежит владельцу токена — иначе
  fail-closed);
- **`X-Caller-Session-Id`** — тот же id сессии в заголовке; на нём держатся фильтр
  `[DenyOnDelegatedTurn]` и белый список `McpToolWhitelist`.

В `backend/ClaudeHomeServer/Services/Mcp/Http/` 17 файлов: 11 тулсетов (9 файлов + 2
файла `.Schemas` — partial-классы `PersonasToolset`/`WorkspaceToolset`)
плюс 4 файла каркаса: [McpToolsetRegistry.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/McpToolsetRegistry.cs)
(реестр имя → тулсет), [McpHttpTransport.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/McpHttpTransport.cs)
(гейт схемы адреса + откат на stdio),
[McpToolWhitelist.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/McpToolWhitelist.cs)
(белый список инструментов провайдера) и
[HiggsfieldSnapshotWarmer.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/HiggsfieldSnapshotWarmer.cs)
(фоновый прогрев снимка higgsfield). Тулсет `notes` живёт в вертикали
`ClaudeHomeServer.Notes` (регистрация — `NotesSubsystem.Register`).

---

## Сводная таблица

| Сервер (имя в конфиге) | Файл | Инструментов | Гейтится чем |
|---|---|---|---|
| `widgets` | [WidgetsToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/WidgetsToolset.cs) | 1 | только владелец (`sub`); статический, без хвоста |
| `memory` (включая все `pmem_<handle>`) | [MemoryToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/MemoryToolset.cs) | 17 | хвост `{personaId}/{projectId}` + владение персоны/проекта; запись команды — персона самого проекта; `dossier_*` — флаг владельца `change-dossiers-recall` |
| `tasks` | [TasksToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/TasksToolset.cs) | 13 | `tool:tasks` (`TasksMcpEnabled`); зона `AllowedProjectIds` (сессия + кросс-привязки); `tasks_run_executor` — `DelegatedTurnGate` с квотой и возвратом |
| `notes` | [NotesToolset.cs](../../backend/ClaudeHomeServer.Notes/Services/Mcp/NotesToolset.cs) (вертикаль Notes) | 19 | подсистема `Subsystems:Notes:Enabled`; `tool:notes`; модуль 12 инструментов — `tool:notes-annotations` (состав + вызов) |
| `personas` | [PersonasToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/PersonasToolset.cs) | 20 | `tool:personas` (с исключением групповых чатов); ядро+привязки+`knowledge_search` всегда; manage/automation/mentions — по живым привязкам (2-я проверка на вызове); `set_default`/`persona_ask` — `DelegatedTurnGate` |
| `wsp` | [WorkspaceToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/WorkspaceToolset.cs) | 41 | 10 секций по `BuildWorkspacePlan` (ключи/флаги/роли; ни одной секции — сервер не монтируется); `ProjectDenied` (владение+зона, R/W); `SafeJoin`; `DelegatedTurnGate` на 5; self-send/self-delete; белый список провайдера |
| `codegraph` | [CodeGraphToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/CodeGraphToolset.cs) | 3 | `tool:codegraph` (состав + вызов); проект = владельца токена; дерево — живой резолв сессии |
| `notifications` | [NotificationsToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/NotificationsToolset.cs) | 4 | `NotificationsEnabled` (`tool:notifications`/модуль по роли) — состав + вызов |
| `dify` | [DifyToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/DifyToolset.cs) | 12 | настроенная секция `Dify`; релевантность датасета (своя/публичная); `document_id` — белый список формы; search-only (4) при собственной базе проекта; namespace `Dify:Namespace` |
| `higgsfield` | [HiggsfieldToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/HiggsfieldToolset.cs) | 10 (белый список из 88+) | OAuth-подключение инстанса; белый список на вызов; хвост + `GetOwned` |
| `watch` | [WatchToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/WatchToolset.cs) | 3 | хвост + `GetOwned`; per-owner стор; **без** `DelegatedTurnGate` (решение ADR-013) |
| `websearch` | [WebSearchToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/WebSearchToolset.cs) | 2 | `Perplexity:ApiKey` (пустой — сервер не объявляется); квота чтения, общая с панелью «Чтение»; SSRF-рубежи ридера |

Итого: 12 серверов, 145 инструментов.

---

## Что стоит знать при добавлении инструмента

Правила, собранные из комментариев тулсетов, ADR-012 и сторожей (не выдумано — каждая
точка имеет опору в коде/доках):

**Подключение (ADR-012: «новый сервер — описание схем и одна регистрация в DI, а не копия
контроллера»):**

1. **Одна регистрация** `AddSingleton<IMcpToolset, XToolset>()` (в `Program.cs` либо в
   `Register` своей вертикали — как `NotesToolset`). Контроллер один на всех
   (`McpTransportController`), менять его не нужно.
2. **Дубль имени = ошибка СТАРТА** (`McpToolsetRegistry`): два тулсета на одном URL молча
   перекрыли бы друг друга, и инструмент пропал бы у модели без единого сообщения.
3. **Имя — константой** (`ServerName = McpEndpoints.<Name>`) — единственная точка правды
   для URL конфига хода; литералы не дублируются. Не бери имена, зарезервированные CLI
   (`workspace` молча отбрасывается — поэтому `wsp`).
4. **Имена инструментов**: не плодить однокоренные имена с пересекающейся семантикой
   (правило именования — на них модель путается; `watch` — не «monitor» именно потому,
   что Monitor уже есть в tools/list).

**Состав:**

5. **Инвариант: `tools/list` не зависит от свойств ХОДА** — он входит в сигнатуру запуска
   CLI (как у stdio), и «мерцание» перезапускает процесс со ВСЕМИ серверами. Зависеть
   может от свойств сессии/владельца (персона, привязки, флаги) — они стабильны на
   жизнь адаптера. Сторож — `McpToolsetStabilityTests`.
6. **Формула состава и отпечаток (`shapes`) — ОДНА** (урок волны 2: `personas` отдавал
   инструмент, а shape говорил «нет» — заведение второй персоны впустую перезапускало
   процесс).
7. **Хвост** (параметризованный сервер): один сегмент, 1–128 символов,
   `[A-Za-z0-9-_]` (та же форма, что у resumeSessionId-белого списка); строится и
   проверяется ОДНОЙ формулой (`RouteTail` + `TryParseRoute`), иначе форма разъедется.
   Вызов без хвоста на параметризованный сервер — 404 контроллера.

**Изоляция и гейты:**

8. **Владелец — только из `sub`** сервисного JWT; тело, URL и заголовки на него не влияют.
   Хвост виден модели — право на него (сессия/персона/проект) проверяется на **КАЖДЫЙ**
   вызов и tools/list, чужое = fail-closed (отказ текстом + пустой состав).
9. **Гейт проверяется ДВАЖДЫ — в составе и на вызове** (defense-in-depth, урок приёмки
   волны 2): гейт только в составе пропускал ПЛАТНЫЙ вызов, когда привязка была выключена
   mid-life (состав мог ещё «помнить» предыдущий).
10. **Анти-рекурсия — общим `DelegatedTurnGate`** (та же точка, что `[DenyOnDelegatedTurn]`
    на REST): MVC-фильтр к `McpTransportController` не применяется вовсе, так что гейт
    вызывается вручную; `failOpenWhenUnknown: false` — fail-closed (у REST-ветки
    отсутствие заголовка осознанно fail-open, у MCP — наш конфиг хода всегда кладёт
    заголовок).
11. **Код ответа — текстом** (`isError`), не HTTP-кодом: у инструмента нет HTTP-кода,
    модель обязана прочитать причину; необработанные исключения наружу не выпускать —
    диспетчер обернёт их в протокольный `-32603`, а 5xx на `initialize` снимает весь
    набор инструментов до конца жизни процесса CLI.

**Конфиг хода и доставка:**

12. **Сервисный JWT — фабрикой на каждый ход** (`TokenFactory`), не захваченной строкой:
    захваченный токен у чата старше `ServiceTokenLifetime` истекал (7 суток), и
    инструменты пропадали молча.
13. **`X-Caller-Session-Id` кладётся в `headers` конфига хода** (id чата постоянен, в
    сигнатуру не входит) — на нём держатся `[DenyOnDelegatedTurn]` (при отсутствии —
    **fail-open**!) и журнал `McpCallLog`.
14. **`alwaysLoad: true` обязателен** (проверено живым прогоном): без него первый вызов
    в ходе падает «No such tool available» (claude-code#19282).
15. **NO_PROXY** — обязательное условие, не удобство: CLI ходит в бэкенд сам, и
    непокрытый loopback-адрес уезжает в прокси (503, инструмент пропадает молча);
    `ClaudeSession` ставит оверрайд на каждый ход по фактическим адресам всех
    http-серверов хода (детерминированно — входит в сигнатуру).

**Семантика и паритет:**

16. **Прямые вызовы в сервисы через DI**, не HTTP-петля через собственный Kestrel; общая
    с REST оркестрация **выносится в сервис, а не дублируется**
    (`PersonasCrudService`, `SessionMessagingService`, `KnowledgeBaseCatalogService`,
    `TeamMemoryService.WriteDeniedFor`/`LengthViolation`) — дублирование = гарантированный
    рассинхрон веток.
17. **Формат ответов — как у stdio-ветки**: camelCase JSON, кириллица без `\u`
    (`UnsafeRelaxedJsonEscaping`), enum'ы строками.
18. **Паритет со stdio-веткой отката**: `mcp/*-server/index.js` заморожены (шапки-
    предупреждения), каждый тулсет держит `*ParityTests` (состав, required-наборы,
    формулировки) — правка обязана ехать **парой**; рубильник `Mcp:HttpTransport=false`
    возвращает всё на stdio прежним env. У `watch`/`websearch` stdio-ветки нет —
    выключенный рубильник лишает тулсет (осознанное упрощение).
19. **Маршрут вне `/api`** (`/mcp/{name}`): три проверенных следствия — `AllowedHosts`
    должен содержать loopback-формы (в чужом `appsettings.Local.json` может не быть —
    400 «на пустом месте»), опечатка в имени даёт 404 catch-all (не SPA-`index.html`
    со 200), инспекционная копия режет не-GET 403-м (живые инструменты в копии = правки
    боевых данных).

<!-- Разделы: -->

### widgets — виджеты чата

[WidgetsToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/WidgetsToolset.cs) — первый
переехавший сервер (фаза 1 ADR-012). Статический тулсет: не ходит ни в какой API, только
валидирует input (HTML-виджет рендерит фронт из аргументов вызова), поэтому ход ради него
процесс node не поднимает.

| Инструмент | Назначение |
|---|---|
| `widget_show` | Показать пользователю интерактивный self-contained HTML-виджет в ленте чата (inline CSS/JS, внешние ресурсы блокированы песочницей); лимит 64 КБ на HTML, 120 символов на title |

**Гейты:** никаких по роли/флагам/хвосту — самый простой сервер. Единственная проверка —
владелец из `sub` (общий для всех, `[Authorize]` + `McpTransportController`).

**Контекст вызова:** хвост маршрута и `X-Caller-Session-Id` не использует; из контекста —
только `OwnerId` (и тот не трогает: данные не пер-владельческие).

**Подводные камни:** `MaxHtml`/`MaxTitle`/`MinHeight`/`MaxHeight` — `internal`-константы
сторожа парности с замороженным `mcp/widgets-server/index.js` (`WidgetsToolsetParityTests`):
обе ветки живы (рубильник `Mcp:HttpTransport`), правка лимита обязана ехать парой. Лимит
размера не спасает уже улетевший в историю input — фронт держит собственный cap на рендер.

### watch — серверные сторожа чатов

[WatchToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/WatchToolset.cs) (ADR-013,
по форме — волна 2 ADR-012). Параметризованный тулсет: модель декларирует «дожидайся
условия и разбуди этот чат», а сам цикл опроса живёт в `WatchdogService` и переживает ходы,
рестарты и смерть процесса CLI.

| Инструмент | Назначение |
|---|---|
| `watch_start` | Поставить сторожа: сервер раз в интервал выполняет `poll_command` в рабочем каталоге проекта чата (exit 0 = условие выполнено); при выполнении или истчении потолка жизни чат-постановщик получает сообщение-будильник |
| `watch_list` | Сторожа этого чата: статус, последний опрос, флаг `undelivered` (терминальный сторож, чьё сообщение не дошло) |
| `watch_cancel` | Снять сторожа по id; идущий прямо сейчас опрос прерывается немедленно |

**Гейты:** хвост — ровно один сегмент (id сессии), форма строгая: 1–128 символов,
`[A-Za-z0-9-_]`; сессия резолвится через `SessionManager.GetOwned` (принадлежит
**владельцу токена**, не просто существует) — на КАЖДЫЙ вызов и tools/list, fail-closed.
Данные per-owner: `store.Create(context.OwnerId, …)`, `store.Cancel(id, context.OwnerId, …)`.
**`DelegatedTurnGate` сознательно НЕ стоит** (решение плана ADR-013): гейт в образце
(`tasks`) стоит только на инструмент, ЗАПУСКАЮЩИЙ ход (`tasks_run_executor`), а `watch_start`
ход не запускает — гейт отрезал бы полезный кейс «агент-исполнитель сторожит своё условие».

**Контекст вызова:** `POST /mcp/watch/{sessionId}` — хвост = сессия-вызыватель (от неё
владелец, `WorkingDirectory` опроса, будимый чат); `sub` — владелец данных. Сторож будит
ТОЛЬКО чат-постановщика (будильник чужому чату — «не входим» плана ADR-013).

**Подводные камни:** (1) stdio-ветки отката **нет** — node-сервера никогда не существовало,
при `Mcp:HttpTransport=false` тулсет недоступен (осознанное упрощение); (2) имя «watch», а не
«monitor» — сознательное: правило именования запрещает однокоренные имена с пересекающейся
семантикой, Monitor уже есть в том же tools/list; (3) `undelivered` вычисляется в `Brief`,
не хранится — «терминальный статус и `DeliveredAt == null`».

### websearch — веб-поиск и чтение страниц

[WebSearchToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/WebSearchToolset.cs) (ADR-012).
Заведён ради локальной модели: под `--bare` CLI не отдаёт ни `WebSearch`, ни `WebFetch`
(флаг `--tools` умеет только сужать), а среди продуктовых MCP веб-поиска не было.
Инструментов сознательно **два, а не три**: у локальной модели окно маленькое, и каждая схема
в tools/list стоит токенов (deep research по дорогому эндпоинту Perplexity — отдельное решение).

| Инструмент | Назначение |
|---|---|
| `web_search` | Поиск в интернете: ответ по существу (Sonar) + источники с 1-based `index` под маркерами «[2]»; опциональный `recency` (day/week/month/year) |
| `web_read` | Чтение страницы по URL в markdown; потолок 20 000 символов на страницу, PDF не разбирается |

**Гейты:** (1) **фич-флаг ключа**: пустой `Perplexity:ApiKey` = сервер ходу не объявляется
вовсе (SessionManager не строит контекст), а если запрос всё же дошёл — состав пустой и
вызов отвечает честным текстом (ключ мог исчезнуть из конфига между сборкой и вызовом —
reloadOnChange); (2) хвост `{sessionId}` + `GetOwned` — как у watch, fail-closed на каждый
вызов; (3) **квота чтения — общая с панелью «Чтение»** (`ReaderQuotaService`, ADR-005 §5):
у одного владельца ходы CLI и панель делят одни и те же 2 одновременных чтения и 30 в
минуту; (4) SSRF-защита — рубежи `ReaderService` (SsrfGuard на каждом хопе редиректа,
белый список схем/портов) — `web_read` не переписывает чтение заново.

**Контекст вызова:** `POST /mcp/websearch/{sessionId}`; данных, зависящих от чата, у поиска
нет — сессия нужна ради fail-closed и разрезов траты (проект/задача/персона), которые
иначе пришлось бы брать из подставляемого заголовка. `sub` = владелец квоты и записи
`SpendRecord` (источник `perplexity`; деньги считаются, только если хозяин проставил цены).

**Подводные камни:** (1) классы отказов `web_read` разведены **сознательно** (дефект
2026-09-07): «сайт ответил и отверг» (с HTTP-кодом) / «соединиться не удалось» / «таймаут» /
«страница не разбрана» — по одинаковому тексту модель не отличала «сайт защищается» от
«сеть лежит»; исключение — `local-address` и `dns-failed` схлопнуты в ОДИН текст, иначе
модель получала бы оракул внутренней сети (ADR-005 §6); (2) stdio-ветки отката **нет**
(как у watch); (3) markdown обрезка — 20 000 символов, значение не вынесено в конфиг (это
свойство потребителя, а не инстанса).

### notifications — уведомления владельца

[NotificationsToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/NotificationsToolset.cs)
(ADR-012, волна 3). Раньше — `mcp/notifications-server` (тонкий JSON-RPC-фасад в наш же
бэкенд); теперь вызовы идут напрямую в `NotificationService`/`NotificationStore`.

| Инструмент | Назначение |
|---|---|
| `notifications_create` | Создать уведомление (kind: reminder/claude/info/success/meeting, type, url-диплинк, projectId/sessionId/taskId/source/tag); push у reminder/claude/success — та же формула, что в REST-контроллере |
| `notifications_list` | Список с фильтрами (kind, unreadOnly) и пагинацией (limit 1–100, offset) |
| `notifications_mark_read` | Отметить прочитанным по id или все сразу (`all=true`) |
| `notifications_delete` | Удалить уведомление по id |

**Гейты:** (1) хвост `{sessionId}` + `GetOwned` (fail-closed, каждый вызов); (2)
**`NotificationsEnabled`** (`PersonaBindingsService`, привязка `tool:notifications` / модуль
автоматизации по роли) — проверяется **дважды**: в составе (`ToolsFor`) и на вызове.
Урок приёмки волны 2: гейт только в составе пропускал ПЛАТНЫЙ вызов, когда привязка была
выключена mid-life.

**Контекст вызова:** `POST /mcp/notifications/{sessionId}`; хвост — сессия-вызыватель,
из неё **персона чата** (аналог env `NOTIFICATIONS_SELF_PERSONA_ID`): лицо на создаваемом
уведомлении подставляется из `session.PersonaId`, если модель не дала `personaId` явно;
резолв живой, на момент вызова — смена спикера подхватывается без пересоздания адаптера.
`sub` = владелец уведомлений (per-owner).

**Подводные камни:** (1) `index.js` заморожен, паритет состава держит
`CodeGraphNotificationsToolsetParityTests` (схема инструментов — порт node-файла, источник
контракта — в этом C#); (2) состав постоянный (4) и от хода не зависит — инвариант
`IMcpToolset`.

### codegraph — граф кода проекта

[CodeGraphToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/CodeGraphToolset.cs)
(ADR-012, волна 3). Раньше — фасад `mcp/codegraph-server` к `CodeGraphController`; теперь
вызовы идут напрямую в `CodeGraphQueryService` (индекс: degree + смежность, кэш по сигнатуре
снимка).

| Инструмент | Назначение |
|---|---|
| `codegraph_find` | Найти тип (класс/интерфейс/структуру/enum) по имени или части FQN: файл со строкой, вид типа, степень связности — «где объявлен X» точнее Grep |
| `codegraph_neighbors` | Связи типа: кто зависит (in) и от чего зависит (out), тип связи (Calls/Implements/References) и уверенность (Extracted/Inferred); «узел не найден» — полезный ответ с похожими кандидатами |
| `codegraph_hubs` | Топ типов по связности («god-узлы»): с чего начинать разбор и что ломается больнее всего при правке |

**Гейты:** цепочка на каждый вызов: хвост `{sessionId}` → `GetOwned` (сессия — владельца
токена) → проект той же цепочки (`project.OwnerId`) → живой гейт **`tool:codegraph`**
(`PersonaBindingsService.ServerToolEnabled`) — **дважды**: в составе и на вызове (урок
приёмки волны 2).

**Контекст вызова:** `POST /mcp/codegraph/{sessionId}`; **рабочее дерево НЕ везётся в
маршруте** (решение волны 3 ADR-012): дерево = `WorktreePath` сессии (отдельное worktree
имеет СВОЙ граф, ADR-003; watcher его файлов поднимается лениво, как в
`CodeGraphController.ResolveRoot`) `??` корень проекта, резолвится живьём на каждый вызов —
подключение/отключение worktree посреди сессии подхватывается без пересоздания адаптера, а
сигнатура запуска от дерева не зависит. `sub` = владелец проекта и графа.

**Подводные камни:** (1) чат **вне проекта** — не отказ, а честный текст «граф недоступен»
(как stdio-ветка; `projectless`-флаг в `TryResolve`); (2) «графа нет» — фоновый
`StartRebuildIfIdle` + текст «повтори через 1–2 минуты»; (3) **у стареющего графа** пустой
результат рендерится отдельно («поиск шёл по СТАРОМУ снимку»), иначе модель принимает пустоту
за истину; (4) состав — 3 чтения, постоянный; `index.js` заморожен, паритет —
`CodeGraphNotificationsToolsetParityTests`.

### higgsfield — прокси к Higgsfield MCP (генерация медиа)

[HiggsfieldToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/HiggsfieldToolset.cs) —
единственный **прокси-тулсет**: ходит в `mcp.higgsfield.ai/mcp` (Streamable HTTP, SSE)
Bearer-токеном инстанса (`HiggsfieldOAuthService`). Состав ходового tools/list — **кэш
снимка**: Higgsfield отдаёт 88+ инструментов, в конфиг едут только **10 по белому списку**
(`generate_image`, `generate_video`, `generate_audio`, `job_status`, `jobs_wait`,
`show_generation_by_ids`, `models_explore`, `media_import_url`, `media_upload`,
`media_upload_widget`); остальные (подписки, деплой, sandbox, TikTok) — шум и риск.

**Гейты:** (1) **инстансное подключение** — пустой `EnsureFresh()` (нет OAuth-токена)
состав пустой и сервер ходу не объявляется вовсе; на вызове — отказ текстом «не подключено
на этом инстансе»; (2) **белый список** — на каждый `tools/call` (инструмент вне списка —
отказ, даже если модель его «видела»); (3) хвост `{sessionId}` + `GetOwned` — fail-closed,
как у всех.

**Контекст вызова:** `POST /mcp/higgsfield/{sessionId}`; `sub` = владелец (изоляция чата),
но **данные и ключ — инстансные** (токен Higgsfield, секреты под `ServiceOwnerId` —
`HiggsfieldOAuthService`, ADR-001). Тело запроса — JSON-RPC, проброшенный в апстрим.

**Подводные камни (ревью Глеба, зафиксировано в коде):** (1) **`Mcp-Session-Id` сейчас не
отвечается апстримом** — если Higgsfield перейдёт на Streamable HTTP с сессиями, прокси
должен упасть ПЕРВЫМ, а не молча ломаться о проде (тест pitfall в `HiggsfieldToolsetTests`);
(2) SSE-запросы требуют `Accept` с **обоими** типами (`application/json` +
`text/event-stream`), иначе 406 Not Acceptable (Higgsfield именно это и делал); (3) кэш
состава: TTL 30 мин, negative-cache 1.5 мин на провал (пять чатов при лежащем апстриме не
долбят сеть каждый ход), снимок старше **1 суток** — состав пустой, иначе модель кормится
**фантомными инструментами** (апстрим переименовал `generate_image` — 10 фантомов, каждый
вызов горит ошибкой); (4) снимок `data/higgsfield-tools.json` (без секретов, в исключениях
облачного архива) + фоновый warmer `HiggsfieldSnapshotWarmer` — холодный старт с диска,
иначе первый ход после рестарта — лотерея на рваном канале; (5) таймаут tools/list — 40 с
+ один повтор (замер: 0.9 / 17.8 / 19.9 / >40 с на 6 попыток; 15 с не хватало, тулсет
пропадал молча); (6) статус handshake (Failed/Connected) пишется в `McpStatusStore`
(`RecordProbe`), иначе UI держит сервер connected с нулём инструментов.

### personas — персоны владельца

[PersonasToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/PersonasToolset.cs) +
[PersonasToolset.Schemas.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/PersonasToolset.Schemas.cs)
(ADR-012, волна 2). Тяжёлая оркестрация (создание/правка/удаление, дефолт, привязки, аватар,
состав команды) — в `PersonasCrudService`, общем с REST-контроллером (дублировать её значило бы
гарантированный рассинхрон веток).

**Состав (20 инструментов) — f(сессия), группы:**

| Группа | Гейт | Инструменты |
|---|---|---|
| Ядро | сервер включён | `personas_list` (scope: context/project/global/all), `personas_get`, `personas_set_default` (ВСЕГДА в составе; вызов разрешён только онбординг-сессии) |
| Привязки (чтение) | сервер включён | `personas_bindings_list`, `personas_suggest_bindings`, `personas_mcp_list` |
| База знаний | сервер включён | `knowledge_search` (Dify-датасеты, доступные владельцу; операторы фильтров — белый список) |
| manage: голова | модуль manage | `personas_create`, `personas_update` |
| manage: привязки | модуль manage | `personas_bindings_set`, `personas_mcp_grant` (выдача/отзыв MCP-сервера персоне) |
| automation | привязка `tool:personas-automation` | `personas_automation_list` / `create` / `update` / `delete` / `test` (правила проактивности) |
| manage: хвост | модуль manage | `personas_delete` (необратимо: долгая память тоже), `personas_generate_avatar` (AI/fal.ai, ~10–30 с), `personas_ai_team` (черновики команды, персон НЕ создаёт) |
| mentions | `MentionsToolsEnabled` | `persona_ask` (вопрос другой персоне — платный one-shot ход) |

**Гейты:** (1) `PersonasEnabled` (привязка `tool:personas`, исключение для групповых чатов) —
на КАЖДЫЙ вызов и в составе; (2) **модуль manage** = проектный онбординг (форсирует) ИЛИ
`SectionEnabled(personas-manage)`; (3) **mentions** — единая формула
`SessionManager.MentionsToolsEnabled` (её же читает отпечаток сигнатуры запуска — две формулы
расходились при единственной персоне владельца: блокер приёмки волны 2.1); (4) все три
модульных гейта — **defense-in-depth на вызове**: выключенный модуль не отрабатывает, даже
если состав отдал инструмент (без проверки `persona_ask` при выключенном `tool:consultants`
запускал ПЛАТНЫЙ one-shot другой персоны); (5) `personas_set_default` и `persona_ask`
проходят **`DelegatedTurnGate`** (тот же, что `[DenyOnDelegatedTurn]` у их REST-пар —
MVC-фильтр к `McpTransportController` не применяется), `failOpenWhenUnknown: false`.

**Контекст вызова:** `POST /mcp/personas/{sessionId}`; хвост = эквивалент env
`PERSONAS_SESSION_ID/PROJECT_ID/SELF_PERSONA_ID/EXTRA_*` stdio-ветки: по сессии живьём
резолвятся проект чата, персона-вызыватель и **кросс-проектные привязки** (персона видит
персон/проекты из своих `PERSONAS_EXTRA_*` — привязки типа `mcp:` в том числе). `sub` =
владелец.

**Подводные камни:** (1) **персона не может менять собственные** привязки/доступы — «попроси
пользователя»; (2) конкретная модель не задаётся через MCP — только уровнями
(`modelTier`/`tierStrong/Medium/Weak`); (3) пустые значения (`""`, `[]`) ОЧИЩАЮТ поля, а не
«не меняют» (tier-ячейки: `null` = не менять, `""` = сброс — поэтому `ContainsKey`, а не
`OptionalArg`); (4) персона из чата **всегда** генерирует аватар (best-effort,
`AutoAvatar: true`); (5) `index.js` заморожен, паритет — `PersonasToolsetParityTests`.

### memory — память персоны, команды, паспорта изменений

[MemoryToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/MemoryToolset.cs) (ADR-012,
волна 1). **Один тулсет обслуживает ВСЕ ключи конфига хода**: `memory` (персона чата) и каждый
`pmem_<handle>` (файловые сабагенты-консультанты) — различаются только хвостом
`POST /mcp/memory/{personaId}/{projectId}` (`-` = параметра нет). Имена серверов в конфиге и
инструментов НЕ меняются: frontmatter-файлы персон ссылаются на сервер по ключу
(`mcpServers: [pmem_<handle>]`).

**Инструменты (17) — по хвосту:**

| Группа | Инструменты |
|---|---|
| Персона (есть `personaId`) | `memory_remember`, `memory_recall` (фокус + скоринг + команда), `memory_search`, `memory_list`, `memory_forget`, `memory_rethink` (переписать запись вместо дубля), `memory_to_note` (в заметку vault), `memory_from_note` (закрепить заметку в памяти), `memory_get_focus`, `memory_clear_focus` |
| Проект (есть `projectId`) | `team_memory_remember`, `team_memory_search`, `team_memory_list` (пагинация, тексты усечены до 200 символов; `id`/`full` — целиком), `team_memory_forget`, `team_memory_update` |
| Проект + флаг | `dossier_lookup`, `dossier_get` (паспорта изменений: «зачем, что решили, что отвергли, грабли») |

**Гейты:** (1) **изоляция хвоста на КАЖДЫЙ вызов**: владелец — только из `sub`, персона из
хвоста обязана принадлежать владельцу (`PersonaManager.Get(personaId, ownerId)`), проект — его
(`OwnerId`); чужое/несуществующее — отказ текстом, а не пустая память (хвост виден модели —
доверять ему нельзя); (2) **группы по наличию сущности**: персона `null` → personal-инструменты
отказывают (chat team-only), проект `null` → team-отказ; (3) **запись команды** —
`TeamMemoryService.WriteDeniedFor`: пишет либо «свой» ход без персоны, либо персона **самого**
проекта (чужая персона читает); (4) **`dossier_*`** — фич-флаг владельца
`change-dossiers-recall` (решается по владельцу — стабильно в рамках сессии, инвариант
состава).

**Контекст вызова:** хвост = персона + проект чата (конфиг хода — наш код, тело контролирует
модель). Токен — **фабрикой на каждый ход** (`TokenFactory`), не захваченной строкой:
захваченный JWT у чата старше 7 суток истекал, и инструменты пропадали молча.

**Подводные камни:** (1) **известное ограничение (ADR-012, «не лечим»)**: `shapes["memory"]`
не включает хвост маршрута — смена персоны-спикера в групповом чате не пробивает
**доживающий** процесс CLI, и он до конца доживания ходит по прежнему URL (прежние
персона/проект); изоляцию это не рвёт — право на хвост проверяется на каждый вызов; (2)
`team_memory_remember` — **точный `Add`** (как REST UI и stdio-ветка), а не
`AddAsync` с семантическим дедупом: дедуп перезаписал бы чужую близкую запись и вернул её
модели под видом новой; (3) усечение командной памяти (200 символов) — **только на выдаче
модели**, стор приходит целиком (на нём UI «Командного центра»); (4) `index.js` заморожен,
паритет — `MemoryToolsetParityTests` (сверяет с живым node-сервером по env-осям
persona/project/dossier).

### dify — базы знаний Dify

[DifyToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/DifyToolset.cs) (ADR-012,
волна 4 — закрытие фазы 2). Раньше — единственный сервер с внешней зависимостью (TypeScript,
`mcp-dify`, **объявлялся не нашим кодом**, а внешним базовым конфигом `McpConfigPath`); волна 4
перенесла объявление в код: источник правды ключа и адреса — секция `Dify` appsettings (общая
с `KnowledgeService`/заметками/памятью персон), 274 строки TS-клиента схлопнулись в существующий
C#-`KnowledgeService`.

**Инструменты (12; режим search-only — первые 4):**

| Инструмент | Назначение |
|---|---|
| `search_knowledge` | RAG-поиск (top_k 1–20, search_method, score_threshold) — доступный всегда |
| `list_datasets` | Базы, релевантные пользователю (не весь workspace) — search-only |
| `list_documents` | Документы датасета (страница/лимит/keyword) — search-only |
| `list_segments` | Сегменты документа (keyword/status) — search-only |
| `create_dataset` | Личная — префикс `{username}:kb:`; публичная — без префикса (двоеточие недопустимо), явная проверка коллизии; лимит имени Dify 40 символов |
| `delete_dataset` | Только deletable: привязанные (заметки/проект/память) не удаляются, публичные — только админу |
| `create_document_by_text` / `create_document_by_file` | Индексация документа текстом/файлом (base64); `indexing_technique`, `process_rule_mode` — валидируются |
| `update_document_by_text` / `update_document_by_file` | Переиндексация существующего |
| `delete_document` | Удалить документ |
| `add_segments` | Добавить сегменты в документ (массив) |

**Гейты:** (1) **настроенность** `Dify:ApiUrl`/`Dify:ApiKey` — пустая секция = сервер ходу не
объявляется (и ходит по записи внешнего конфига, если она есть — откат); (2) хвост `{sessionId}`
+ `GetOwned`; (3) **релевантность датасета пользователю** — `KnowledgeBaseCatalogService.
ResolveReadableAsync` (своя/публичная — да, чужая помеченная — нет) на ЯВНЫЙ `dataset_id` и
по-умолчанию: **у stdio-сервера этого гейта НЕ БЫЛО** (TS-клиент ходил ключом инстанса и видел
ВСЕ датасеты workspace) — перенос ужесточил доступ; (4) **`document_id` — белый список формы
ДО HTTP** (`IsValidDifyId`): dot-segment-пейлоад («../../{uuid}/…») раньше резолвился HttpClient'ом
в чужой датасет под общим ключом workspace (блокер приёмки волны 4.1); (5) **search-only** —
у проекта чата есть своя база → только 4 read-инструмента (эквивалент `DIFY_SEARCH_ONLY=true`),
проверено и в составе, и на вызове (defense-in-depth); (6) `Dify:Namespace` (контуры Dev/Prod на
одном Dify) — внутри `KnowledgeService`.

**Контекст вызова:** `POST /mcp/dify/{sessionId}`; по хвосту живьём резолвятся: username
(из `UserStore`, не из JWT — сервисный JWT может не нести claim Name; нужен для
классификации/префиксов), проект чата и **дефолтный датасет** (`WorkspaceKnowledgeStore` по
`EffectiveRoot` рабочей папки сессии — той же формуле, что инжекция `DIFY_DEFAULT_DATASET_ID`
в stdio-ветку и `shapes["dify"]`: датасет может появиться в середине жизни чата, и s-бит
сигнатуры корректно пробивает доживание). Чат вне проекта — НЕ отказ, а полный состав.
**Ключ Dify не покидает бэкенд**: ни в env, ни в конфиг хода, ни в заголовках — тексты ошибок
собираются из статуса/тела ответа (ключ в них не возвращается).

**Подводные камни:** (1) `mcp-dify/dist/index.js` — stdio-ветка отката (поднимается тем же
ключом из секции; если dist не собран, ход едет записью внешнего конфига); (2) `WordCount`
у индексируемого документа — `null` (не «0 слов»): JsonOpts null не прячет; (3) паритет —
`DifyToolsetParityTests` (сверяет с ЖИВЫМ `mcp-dify/dist/index.js` полный состав (12),
search-only (4) и required-наборы посимвольно).

### notes — заметки владельца

[NotesToolset.cs](../../backend/ClaudeHomeServer.Notes/Services/Mcp/NotesToolset.cs) — **в
вертикали `ClaudeHomeServer.Notes`** (динамический модуль: регистрация тулсета живёт в
`NotesSubsystem.Register`, не в `Program.cs`). ADR-012, волна 2; как tasks — тонкий
JSON-RPC-фасад, повёрнутый напрямую к `NotesService`/`NotesKnowledgeService`/`NotesAiService`/
`NoteTaskSyncService`.

**Инструменты (19) — две группы:**

| Группа | Гейт | Инструменты |
|---|---|---|
| Ядро (7) | сервер включён | `notes_list`, `notes_search`, `notes_read`, `notes_create` (source = проект чата или `personal`), `notes_update`, `notes_move`, `notes_semantic_search` (Dify-индекс, topK 1–20) |
| Комментарии и редкие операции (12) | привязка `tool:notes-annotations` | `notes_suggest_title`, `notes_backlinks`, `notes_graph`, `notes_delete`, `notes_annotate`, `notes_annotations`, `notes_reply`, `notes_thread`, `notes_set_status`, `notes_daily`, `notes_resolve`, `notes_promote_task` |

**Гейты:** (1) **подсистема** `Subsystems:Notes:Enabled` — один рубильник на все четыре
сервиса: либо все `null` (тулсет не зарегистрирован вовсе), либо все есть; (2) **`tool:notes`**
(`EffectiveToolEnabled`, право на сервер ВООБЩЕ) — на КАЖДЫЙ вызов и в составе; (3) **модуль
`notes-annotations`** (`SectionEnabled`) — **defense-in-depth**: и в составе, и на вызове
(модель не должна видеть разницы между «нет в списке» и «отказ»); (4) хвост `{sessionId}` +
`GetOwned`.

**Контекст вызова:** `POST /mcp/notes/{sessionId}` (эквивалент env `NOTES_SESSION_ID` /
`NOTES_PROJECT_ID`): по сессии — проект чата (источник по умолчанию для создания) и персона
(живая, на каждый вызов); созданная из чата заметка **помнит свой источник**
(`SourceSessionId = session.Id`, как `NOTES_SESSION_ID` stdio-ветки). `sub` = владелец
заметок (per-owner).

**Подводные камни:** (1) любая **мутация** — отложенная синхронизация семантического индекса
+ событие ленты (панель «Заметки» обновляется и при MCP-записи); (2) `notes_update`: пустая
строка = ОЧИСТИТЬ содержимое, `null` = не менять — `ContainsKey`, а не `OptionalArg`
(блокер приёмки волны 2.1: OptionalArg глотал очистку молча); (3) `notes_annotate`: офсеты —
**хинт**, сервис сам находит единственное дословное вхождение (verify-before-write;
неуникально — честная ошибка без порчи файла); (4) `notes_daily`: дописывание эндпоинтом не
поддержано — тулсет делает сам (читает текущий текст, PUT-ит склейку, как stdio);
(5) `notes_promote_task`: чекбокс можно задать текстом — резолв по спислу задач заметки,
несколько совпадений — отказ с перечнем строк; (6) `index.js` заморожен, паритет —
`NotesToolsetParityTests`.

### tasks — задачи владельца

[TasksToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/TasksToolset.cs) (ADR-012,
волна 2). Был JSON-RPC-фасадом к нашему же REST; вызовы — напрямую в TaskManager /
TaskExecutionService / TaskAiService.

**Инструменты (13) — состав ФИКСИРОВАННЫЙ**, включая `tasks_run_executor` (инвариант
стабильности; `inProject` лишь переключает описания: проект чата или личные дела):

`tasks_list_projects`, `tasks_list`, `tasks_search`, `tasks_get`, `tasks_create`,
`tasks_update`, `tasks_board_columns`, `tasks_complete`, `tasks_delete`,
`tasks_add_subtask`, `tasks_toggle_subtask`, `tasks_find_duplicate`, `tasks_run_executor`
(запуск Claude-исполнителя — отдельная сессия, фоновая).

**Гейты:** (1) хвост `{sessionId}` + `GetOwned` (каждый вызов; чужая = отказ текстом, а не
пустой список); (2) **`TasksMcpEnabled`** (привязка `tool:tasks`; `null`-персона = человек —
формула та же, что `SessionManager.BuildTasksContext`); (3) **зона `AllowedProjectIds`** =
проект сессии + **кросс-проектные привязки персоны** (`BuildExternalTaskScopes` — живой
эквивалент env `TASKS_EXTRA_*`): `ReadDenied`/`WriteDenied`/`TaskDenied` — доступ к задаче
проверяется на каждом инструменте, запись в readonly-проект — отдельный гейт; (4)
**`tasks_run_executor`** — **`DelegatedTurnGate`** (тот же, что `[DenyOnDelegatedTurn]` на
REST-паре; MVC-фильтр к `McpTransportController` не применяется) с
`allowInTeamImplement: true` и квотой team-implement **с возвратом** (Refund) при любом
неудачном исходе — отказ модели («задача не найдена») или исключение.

**Контекст вызова:** `POST /mcp/tasks/{sessionId}` (эквивалент env `TASKS_SESSION_ID`/
`*_PROJECT_ID`/`*_SELF_*`): проект чата, персона-постановщик и её привязки считаются по
САМОЙ сессии на каждый вызов — смена спикера/привязок подхватывается без пересоздания
адаптера. `sub` = владелец задач.

**Подводные камни:** (1) `[DenyOnDelegatedTurn]` — **MVC-атрибут, к тулсету не применяется
вовсе** (тулсет зовёт сервисы через DI, минуя конвейер) — поэтому гейт вызывается вручную
общим `DelegatedTurnGate` (один код — тексты отказов, порядок квот, refund-семантика не
расходятся с REST); (2) `tasks_update`: пустая строка = **очистить** поле, `null` = не менять
(`OptionalArg` глотал очистку — блокер приёмки волны 2.1); (3) `tasks_complete` дефекта без
вердикта = 400 (EnsureVerificationOnClose); сервер сам подставляет `VerifiedAt`/`PersonaId`
из сессии (`null`-персона = проверка человеком); `outcome: closedWithoutCheck` — только
`kind=defect`; (4) обратная запись «задача ↔ заметка» (`INoteTaskSync`) — `null` при
выключенной подсистеме Notes = молчаливый пропуск; (5) `tasks_list_projects` без
схемы-аргументов — отдаёт проекты, **доступные в этом ходе** (текущий + кросс-проектные
привязки), по которым модель выбирает `projectId`.

### wsp — рабочее пространство (файлы, git, проекты, чаты, деплой)

[WorkspaceToolset.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/WorkspaceToolset.cs) +
[WorkspaceToolset.Schemas.cs](../../backend/ClaudeHomeServer/Services/Mcp/Http/WorkspaceToolset.Schemas.cs)
(ADR-012, волна 3; самый крупный тулсет). Имя сервера — **`wsp`, а не `workspace`**:
`workspace` зарезервировано в claude CLI (CLI молча отбрасывает сервер с таким именем).

**Инструменты (41) — 10 секций** (состав = `SessionManager.BuildWorkspacePlan`,
**одна формула** и для tools/list, и для `shapes`/отпечатка — разошедшиеся формулы были
блокером приёмки волны 2):

| Секция | Гейт монтажа | Инструменты |
|---|---|---|
| `projects` | tool-ключ (дефолт по роли) | `projects_list`, `projects_get`, `projects_create`, `projects_update`, `tags_apply`, `tags_remove` |
| `files` | tool-ключ | `files_tree`, `files_read`, `files_document_read`, `files_document_summary`, `files_document_extract`, `files_to_markdown`, `files_write`, `files_search`, `files_mkdir`, `files_rename` |
| `knowledge` | tool-ключ | `knowledge_search`, `knowledge_status`, `knowledge_index` |
| `search` | **всегда** (при смонтированном сервере) | `search_unified` |
| `git` | секция `files` + пресет по роли (чтение истории) | `git_status`, `git_diff`, `git_log` |
| `git_write` | секция `files` + **явный** ключ `git` | `git_commit`, `git_stage` |
| `knowledge_bases` | секция `knowledge` + ключ `kb` | `kb_list`, `kb_get`, `kb_search`, `kb_add_document` |
| `chats` | явный tool-ключ **ИЛИ** неявный opt-in (ProjectPersonas-привязки к чужому проекту) | `chats_list`, `chats_history`, `chats_create`, `chats_send`, `chats_report_up`, `chats_update`, `chats_archive` |
| `destructive` | флаг `workspace-destructive` + персона ≠ ReadOnly + tool-ключ `destructive` | `files_delete`, `chats_delete` |
| `deploy` | контур `Deploy` настроен + персона ≠ ReadOnly + владелец — admin | `deploy_start`, `deploy_status`, `deploy_rollback` |

Ни одной секции — **сервер не монтируется** (план `null`, fail-closed).

**Гейты:** (1) хвост `{sessionId}` + `GetOwned`; (2) **`ProjectDenied`** — `projectId`
**аргумент инструмента** (его контролирует модель!), поэтому: принадлежит владельцу токена
**И** входит в зону сессии (`AllowedProjectIds`: `null` = все проекты владельца; при
привязках = fileScopes ∪ chatScopes + проект самой сессии) — **одна и та же проверка для
чтения и записи** (урок волны 2: у tasks запись смотрела только readonly-подмножество);
(3) **все пути — через `FileService`** (`SafeJoin`/`SafeJoinPublic`, traversal = внятный
отказ); (4) **`DelegatedTurnGate`** (fail-closed, `failOpenWhenUnknown: false`) на
`chats_send`, `chats_report_up`, `chats_archive`, `files_delete`, `chats_delete`;
**self-send / self-delete запрещены** (чат не пишет сам в себя); (5) **белый список
провайдера** (`McpToolWhitelist.KeepMcpTools` по `X-Caller-Session-Id`) — фильтрует tools/list
и отбивает вызов; **честная граница**: защита от ОШИБКИ модели, а не от намеренного обхода
(у исполнителя есть Bash и конфиг хода — `curl` мимо заголовка доедет; до ~30 КБ).

**Контекст вызова:** `POST /mcp/wsp/{sessionId}` (аналог env `WORKSPACE_*`): сессия →
проект чата, персона, привязки (живьём, на каждый вызов); `sub` = владелец.

**Подводные камни:** (1) **формулировки-предохранители — часть защиты**: «БЕЗВОЗВРАТНО…
ТОЛЬКО по явной просьбе» у `files_delete`/`chats_delete`, «никогда по своей инициативе» у
`deploy_*` — дословное совпадение со stdio-веткой держит тест парности; (2)
`projects_create` **не принимает `rootPath`** (модель сдвигала границу SafeJoin; создание
только в стандартном каталоге владельца, а параметр оставлен для паритета составов);
создание в **суженной** зоне запрещено; (3) `git_status` детектит чужой коммит по сдвигу
HEAD → `CommitAttributionService` (правки мимо продукта), `git_commit` дописывает трейлер
`CCS-Session`/`CCS-Task` (ADR-004); (4) `tags_apply`/labels личных задач — в **суженной**
зоне; broadcast `task_updated` (потерян был бы при переходе на прямые вызовы — урок волны
3.1); дефект → Done = Deny, а не 500; (5) `chats_send`: busy/queued/queue_full — **не сбой**,
подсказка «не ретраить»; (6) `index.js` заморожен, паритет — `WorkspaceToolsetParityTests`
(8 осей секций + «инструмент → секция» + формулировки).

<!-- Заполнять по одному: прочитал файл → записал раздел. -->
