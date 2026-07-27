# Интеграция с мессенджерами (Max / Telegram)

> Подробная документация. Выжимка — в [CLAUDE.md](../CLAUDE.md), раздел «Интеграция с
> мессенджерами». Читать перед правками в `Services/Messenger/` и связанных с ними
> webhook-контроллерах.

Дока фиксирует результаты ресёрча Max Bot API (июль 2026), чтобы любая сессия могла
опереться на готовые факты, а не повторять поиск. Если устарело — первоисточник
[dev.max.ru/docs-api](https://dev.max.ru/docs-api); свериться с ним дешевле, чем
переоткрывать.

## Почему вообще мессенджер

CCS — веб-платформа для глубокого диалога с Claude: код, diff'ы, артефакты, файлы,
permissions, plan mode. Мессенджер **фундаментально не подходит** как основной канал —
нет рендеринга markdown-блоков кода, diff'ов, iframe-виджетов, длинных контекстов.
Поэтому интеграция имеет смысл **только для узкого набора кейсов**, где мессенджер
закрывает то, чего веб-интерфейс не может (удалённый доступ без вкладки).

**Сценарий, оправдывающий интеграцию:** CCS крутится на сервере, пользователь не за
компьютером, нужно знать о завершении задач или реагировать на permission-запросы.

Если сценарий «открыл вкладку, поработал, закрыл» — интеграция **не нужна**, это
оверинжиниринг.

## Max Messenger — краткая справка

| Параметр | Значение |
|---|---|
| Владелец | VK Company |
| Запущен | 2024 |
| География | **Только РФ** (юрлица, ИП, самозанятые) |
| Документация | [dev.max.ru](https://dev.max.ru/docs) / [dev.max.ru/docs-api](https://dev.max.ru/docs-api) |
| Экосистема | Интеграция с VK ID, mini-apps, MAX Bridge (lib для mini-app↔client) |

**Когда выбирать Max, а не Telegram:** Telegram заблокирован в РФ без VPN, Max —
российский, работает из любой точки. Для аудитории CCS (русскоязычная) Max = дефолтный
мессенджер для уведомлений. Если аудитория международная — Telegram (или оба).

## Max Bot API — технические характеристики

| Параметр | Значение |
|---|---|
| Тип API | HTTPS REST |
| Base URL | `https://platform-api2.max.ru` |
| HTTP-методы | GET, POST, PUT, PATCH, DELETE |
| Auth | Bot token, заголовок `Authorization: <token>` (через query param **больше не поддерживается**) |
| Webhook | ✅ **Обязателен для прода** (подписка `POST /subscriptions`) |
| Long polling | `GET /updates` — **только для dev**, ограниченная скорость и хранение |
| Rate limit | **30 rps** на `platform-api2.max.ru`; превышение → HTTP 429 |
| Сертификаты | **HTTPS + trusted CA обязательно** (с 25 мая 2026); self-signed не поддерживается |

### Поддерживаемые сообщения

- **Текст** с Markdown (`**bold**`, `[link](url)`, `` `code` ``) и HTML (`<b>`, `<i>`, `<a>`).
- **Media:** `image` (JPG/PNG/GIF/…, до 50MB или 7680×7680), `video` (MP4/MOV/…, до 250MB),
  `audio` (MP3/WAV/M4A, до 256MB / 60мин), `file` (TXT/DOC/PDF/…, до 4GB).
- **Inline keyboards:** до **210 кнопок**, 30 рядов (макс 7 кнопок в ряд; 3 для спецтипов).
- **Типы кнопок:** `callback`, `link` (до 2048 символов), `request_contact`,
  `request_geo_location`, `open_app` (mini-app), `message`, `clipboard`.
- **Deep links:** payload до 128 символов (`max.ru/bot?start=...`).
- **Mini-apps:** HTML/CSS/JS внутри мессенджера, общение через MAX Bridge.

### Типы updates (для webhook)

`message_created`, `message_edited`, `message_removed`, `message_callback` (нажатие кнопки),
`bot_started` (с payload deep link), `bot_added`/`bot_removed`, `user_added`/`user_removed`,
`chat_title_changed`.

### SDK

- **Официальные:** TypeScript (`@maxhub/max-bot-api`,
  [max-messenger/max-bot-api-client-ts](https://github.com/max-messenger/max-bot-api-client-ts)),
  Golang ([max-messenger/max-bot-api-client-go](https://github.com/max-messenger/max-bot-api-client-go)).
- **Неофициальные:** Python
  ([max-messenger/max-botapi-python](https://github.com/max-messenger/max-botapi-python)),
  TypeScript-фреймворк ([sergey12313/max-bot-ts](https://github.com/sergey12313/max-bot-ts)).
- **C#/.NET SDK НЕТ** — писать свой HTTP-клиент (HttpClient wrapper поверх REST).

### Сравнение с Telegram Bot API

| Аспект | Max | Telegram |
|---|---|---|
| API style | REST | REST |
| Webhook | Обязателен для прода | Опционален (есть polling) |
| Rate limit | 30 rps (явный) | Не задокументирован |
| SDK C#/.NET | ❌ | ✅ `Telegram.Bot` |
| Inline keyboards | ✅ до 210 | ✅ до 100 |
| Inline mode (запросы в любом чате) | ❌ | ✅ |
| Payments | ❌ | ✅ |
| Mini-apps | ✅ | ✅ (Web Apps) |
| География | РФ только | Глобально |
| Модерация ботов | Обязательная | Нет |
| Зрелость экосистемы | Ранняя (2024) | Зрелая (2013) |

## Use cases — резолюции

| # | Кейс | Польза | Усилия | Резолюция |
|---|---|---|---|---|
| 1 | **Permission push с inline-кнопками** (Approve/Deny) — Claude просит разрешение, бот шлёт сообщение с кнопками, callback идёт на webhook → `SessionHub.RespondPermission` | 🔥 Высокая, уникальна для мессенджера | 🟡 Средние | ✅ **Делать в V1** (после MVP) |
| 2 | **Уведомления о завершении долгих задач** (markdown: cost, tokens, diff, ссылка на чат) | 🟢 Высокая для async-работы | 🟢 Низкие | ✅ **MVP** |
| 3 | **Quick commands** (`/status`, `/interrupt <id>`, `/new chat`) | 🟡 Средняя, дублирует веб | 🟡 Средние | 🟡 **V2** |
| 4 | **Forwarding из `notifications-server`** (задачи, напоминания) | 🟢 Средняя, расширяет существующую фичу | 🟢 Низкие | ✅ **MVP** |
| 5 | **Mini-app dashboard** (список сессий, статусы, cost за день) | 🔥 Высокая в перспективе | 🔴 Высокие | ⏸️ **Отдельный epic** |
| 6 | **Status queries** («шо там с билдом?» → сводка) | 🟡 Средняя | 🟡 Средние | 🟡 **Спорно**, дублирует #3 |
| 7 | **Пересылка файлов из Max → проект** (скриншот → Claude) | 🟡 Низкая, UX стрёмный | 🟡 Средние | ❌ **Не делать** |
| 8 | **Полноценный чат с Claude через Max** | 🔴 Низкая, убивает UX CCS | 🔴 Высокие | ❌ **Не делать** |
| 9 | **Голосовой ввод/вывод через Max** | 🔴 Низкая, дублирует существующий стек | 🔴 Высокие | ❌ **Не делать** |
| 10 | **Deep links как ярлыки** (`?start=interrupt_<id>`) | 🟡 Низкая, нишевое | 🟢 Низкие | 🟡 **Опционально** к #3 |

**MVP = #2 + #4.** V1 = + #1 (Killer Feature). V2+ = #3, #10, потом #5.

## Персоны и проекты в мессенджере — варианты использования

`Session.ProjectId` и `Session.PersonaId` — готовые оси для routing в Max. Mapping
`external_chat_id → { ownerId, projectId?, personaId?, sessionId? }` позволяет привязывать
конкретный чат/группу в Max к контексту CCS без изменений существующей модели. Ниже —
разбор реалистичных связок (июль 2026, теоретические, не реализованы).

### A. Личный ассистент-персона ✅ (самый сильный кейс)

Личка с ботом в Max → CCS-сессия с `PersonaId = X` (глобальная персона, `Zone = global`).
Сообщения из Max идут в эту сессию, ответы персоны возвращаются в Max.

- **Плюсы:** «персона в кармане» — София-аналитик, Полина-секретарь доступны откуда
  угодно. Память персоны (`persona-memory.json` + Dify-семантика) переживает паузы
  (часы/дни между вопросами). `PersonaPromptBuilder` + recall памяти **уже работают в CCS** —
  переиспользуются без изменений.
- **Минусы:** мессенджер не отрендерит артефакты/diff/виджеты — только текст. Теряется
  богатый UX веб-версии, но для консультативных вопрос-ответ это норм.
- **Резолюция:** ✅ **Делать** — топовый кейс, оправдывает интеграцию сам по себе. Ложится
  на `Zone = global` без проектного контекста.

### B. Уведомления от имени персон 🟢 (UX-улучшение)

`notifications-server` + `MessengerIntegrationService` форматируют уведомления через
persona-стиль: вместо безликого `«Task #42 completed»` — `Polina: Я завершила задачу
«Refactor AuthMiddleware»`. Аватарка/имя персоны в Max (насколько позволяет API).

- **Плюсы:** дёшево, переиспользует существующий `PersonaPromptBuilder`. UX становится
  «живым» — общение с разными людьми, а не безликий бот.
- **Минусы:** пользователь должен понимать, кто есть кто в пантеоне. Не всегда уместно
  (для технических алертов лучше neuterальный тон).
- **Резолюция:** ✅ **Делать** — как опция, с fallback на plain-text для технических
  уведомлений.

### C. Проект-специфичный чат 🟢

Создал проект в CCS → бот создал Max-группу `«ccs: backend-refactor»`. Сообщения в эту
группу → летят в сессию с `ProjectId = X`. Knowledge base проекта (Dify RAG) под рукой
через `TASKS_PROJECT_ID` в env, `notifications` проекта идут в этот же чат.

- **Плюсы:** чёткое разделение контекстов — один проект = один чат. История чата в Max =
  хронология работы. Git-статусы, completion alerts — всё в одном месте.
- **Минусы:** при большом количестве проектов плодятся чаты. Управление (создать/удалить
  синхронно) требует webhook'ов в обе стороны.
- **Резолюция:** ✅ **Делать** — хорошо для solo с несколькими проектами. Можно совмещать
  с персоной проекта (`PersonaId = Y`, `Zone = project`).

### D. Групповой чат = команда + персона 🟡 (спорно)

Max-группа «Project X», добавлены коллеги + бот-персона. Любой пишет → бот отвечает от
лица персоны в контексте проекта.

- **Плюсы:** team-collaboration над проектом через мессенджер; все участники видят ответы.
- **Минусы:** ACL. В CCS права per-owner (токен владельца), а в Max-группе пишут все.
  Варианты: только owner слёт команды, остальным read-only; либо whitelist
  `Max-user-id → CCS-user-id`. CCS сейчас **не multi-user в полном смысле** — каждый юзер
  видит только свои проекты, командный чат ломает модель.
- **Резолюция:** 🟡 **Отложить** — требует проработки multi-user ACL. Возможно после
  появления второго активного пользователя CCS.

### E. Пантеон в одном чате 🔴 (не делать)

Один Max-чат, разные персоны отвечают каждый в своей роли: юзер «оцени риски» → Mark
(ревьюер) + Sofia (аналитик) отвечают каскадом.

- **Плюсы:** эффект «совета экспертов» в одном чате, красиво для стратегических вопросов.
- **Минусы:** дорого по токенам (много LLM-вызовов), шумно (каскад сообщений confusing),
  routing сложный (кто когда отвечает). В CCS это уже есть через subagents — они остаются
  внутри одного хода Claude, агрегация в один чат не даёт выгоды.
- **Резолюция:** ❌ **Не делать** — subagents лучше остаются внутри одного хода Claude, а
  не как cascade в мессенджере.

### Архитектурно — как это ложится

```
Max chat ──webhook──▶ MessengerWebhookController
                          │
                          ▼
                  RoutingService.Lookup(maxChatId)
                          │
                          ▼
                  { ownerId, projectId?, personaId?, sessionId? }
                          │
                          ▼
                  SessionManager.SendMessageAsync(
                      sessionId, text,
                      projectId=..., personaId=...)
```

Mapping в `data/messenger-mappings.json`:

```json
{
  "max:<personal_chat_id>": {
    "ownerId": "grigory",
    "projectId": null,
    "personaId": "sofia",
    "sessionId": "abc123"
  },
  "max:<project_group_id>": {
    "ownerId": "grigory",
    "projectId": "backend-refactor",
    "personaId": "polina",
    "sessionId": "def456"
  }
}
```

**Команды управления** (текстовые или через inline-кнопки):

- `/persona sofia` — переключить персону в текущем чате.
- `/project backend-refactor` — привязать чат к проекту.
- `/new` — начать новую сессию (старая остаётся в history).
- `/clear` — отвязать от проекта/персоны, вернуться к default.

### Что переиспользуется из существующего кода

- **`PersonaPromptBuilder`** — собирает системный промпт персоны каждый ход (переживает
  рестарт, работает с `--resume`).
- **`persona-memory.json` + Dify-семантика** — долгая память персоны (auto-recall).
- **`Persona.Access` / `PersonaTools`** — ограничение, что персона может делать через Max
  (например, запретить file writes — в мессенджере они бесполезны).
- **`Session.PersonaId` + `Session.ProjectId`** — готовые оси, ничего менять не надо.
- **`Persona.Zone`** (global / project) — определяет scope чата: глобальная персона →
  личка в Max, проектная → её проектный чат.

### Что нужно новое

- Mapping `external_chat_id → { ownerId, projectId?, personaId?, sessionId? }` в
  `data/messenger-mappings.json`.
- Команды управления (`/persona X`, `/project Y`) с парсингом или inline-кнопками.
- Auth: `Max-user-id → CCS-ownerId` (проверка, что владелец чата имеет доступ к
  проекту/персоне — `PersonaAccessPolicy`).
- Для кейса B: расширение `notifications-server` каналом Max с persona-aware
  форматированием.

### Что НЕ делать

- ❌ **Полноценная работа с кодом через персону в Max** — мессенджер не отрендерит diff,
  артефакты, файлы. Персона может **обсуждать** код, но не **показывать**.
- ❌ **Все инструменты персоны через Max** — `Persona.Tools` может гейтить tasks/notes/web,
  но в Max имеет смысл только простые действия (вопрос-ответ, статус).
- ❌ **Групповые чаты с несколькими CCS-юзерами** — ломает модель per-owner, требует
  переделки ACL (см. кейс D).

## Архитектура интеграции с CCS

```
Max / Telegram ──webhook──▶ MessengerWebhookController (новый, POST /api/webhooks/max)
                                │
                                │ валидация сигнатуры (Max: Bearer/HMAC; TG: secret)
                                ▼
                            SessionManager.SendMessageAsync(sessionId, text)
                                │
                                ▼
                            ClaudeSession → CLI
                                │
                            ServerMessage events (ResultMessage, PermissionRequestMessage…)
                                │
                                ▼
                            MessengerIntegrationService (новый IHostedService)
                                │ подписка на OnSessionMessage
                                ▼
                            IMessengerClient.SendMessageAsync(externalChatId, formatted)
                                │
                                ▼
                            Max API / Telegram API ──▶ пользователь в мессенджере
```

### Что переиспользуется из существующего кода

- **`PersonaAutomationService`** — **идеальный референс**: `IHostedService`, подписка на
  `OnUserMessage`/`OnSessionMessage`, вызов `SendMessageAsync`. Почти готовый шаблон для
  `MessengerIntegrationService`. Читать перед реализацией.
- **`JwtService.GetServiceToken(ownerId)`** — бот как сервисный юзер, паттерн уже отлажен
  на MCP-серверах.
- **`IHubContext<SessionHub>`** — бродкаст событий (для дублирования в веб и мессенджер).
- **`notifications-server`** — уже умеет слать уведомления в систему; расширить каналом Max.
- **`ServerMessage` protocol** — типизированные события для фильтрации (`ResultMessage`
  для завершения хода, `PermissionRequestMessage` для permission-pushed).

### Что нужно новое

1. **`IMessengerClient`** — generic-интерфейс (`SendMessageAsync`, `SendButtonsAsync`,
   `RegisterWebhookAsync`). Реализации: `MaxMessengerClient`, `TelegramMessengerClient`.
2. **`MessengerWebhookController`** — приём POST от Max/Telegram, валидация сигнатуры,
   routing в `MessengerIntegrationService`.
3. **`MessengerIntegrationService`** (`IHostedService`) — подписка на события CCS,
   фильтрация, форматирование, отправка через `IMessengerClient`.
4. **Mapping `external_chat_id ↔ session_id`** — `data/messenger-mappings.json`
   (не в `sessions.json`, чтобы не плодить связи; в бэкапах хранить осторожно — чат-айди
   это PII-подобная информация).
5. **Bot-пользователь** в `data/users.json` — бот использует его JWT для вызовов CCS API.
6. **Конфигурация** — секция `Messenger` в `appsettings.Local.json`:
   ```json
   "Messenger": {
     "Max": { "BotToken": "...", "ApiBaseUrl": "https://platform-api2.max.ru" },
     "Telegram": { "BotToken": "...", "ApiBaseUrl": "https://api.telegram.org" }
   }
   ```

### Message flow: permission push (пример)

```
1. ClaudeSession ловит permission_request от CLI
2. SessionManager → BroadcastAsync(PermissionRequestMessage) → все SignalR-клиенты
3. MessengerIntegrationService ловит PermissionRequestMessage
4. Для каждого подписанного external_chat_id:
   - IMessengerClient.SendButtonsAsync(chatId, "Claude wants: rm -rf …",
       buttons=[Approve ✅, Deny ❌])
5. Пользователь жмёт кнопку в Max → Max шлёт message_callback на webhook
6. MessengerWebhookController → routing по callback_data ("approve:<requestId>")
7. SessionManager.RespondPermission(sessionId, requestId, approve=true)
8. Claude продолжает
```

## Архитектурное решение: один бот или несколько

**Контекст:** в перспективе у CCS два источника сообщений в мессенджер — собственные
уведомления CCS и алерты телеметрии (см. [observability.md](observability.md),
раздел «Future Epics — Alerting»).

### Вариант A — два разных бота

`CCS Alerts bot` + `Telemetry Monitor bot`. Чистое разделение доменов, независимые rate
limits, можно разным людям давать доступ, но две регистрации/модерации и переключение
между чатами.

### Вариант B — один бот, разные чаты/топики ✅ (рекомендация)

Личка → интерактив (commands, permission pushes). Группа `CCS Alerts` → завершения задач.
Группа `Infra` → телеметрия. Пользователь сам решает, куда подписываться.

**Почему B:**
- Для solo-использования удобнее один бот в списке.
- Push-уведомления естественным образом разделяются по чатам.
- Rate limit 30 rps для solo с запасом.
- Меньше регистрационного overhead (одна модерация бота).

**Условие:** общая .NET-библиотека Max API клиента (один `MaxApiClient`, используется и
CCS, и сервисом телеметрии).

```
                    ┌──────────────────────┐
                    │   Max Bot (1 шт)     │
                    │   platform-api2.max.ru│
                    └──────────┬───────────┘
                               │
                    ┌──────────▼───────────┐
                    │  Webhook Receiver    │  ← ASP.NET контроллер
                    │  (/api/webhooks/max) │
                    └──────────┬───────────┘
                               │
                ┌──────────────┴───────────────┐
                │                              │
    ┌───────────▼──────────┐      ┌────────────▼───────────┐
    │ MaxMessengerClient   │      │ MaxMessengerClient     │ ← та же .dll
    │  (для CCS)           │      │  (для Telemetry)       │
    └───────────┬──────────┘      └────────────┬───────────┘
                │                              │
    ┌───────────▼──────────┐      ┌────────────▼───────────┐
    │ MessengerIntegration │      │ Telemetry Alerter      │
    │ Service (CCS)        │      │ Service                │
    └──────────────────────┘      └────────────────────────┘
```

### Когда переходить на вариант A (два бота)

- Появился **второй пользователь** с разными правами на CCS и телеметрию.
- Телеметрия стала **шумной** (десятки алертов в час) и забивает CCS-уведомления.
- У телеметрии **другой owner** (например, monitoring рабочего сервиса).

Переход бесшовный: `MaxMessengerClient` остаётся, меняется только токен и chat_id.

## Специфика Max — грабли и ограничения

- **Нет официального .NET SDK.** Писать свой `HttpClient`-wrapper. Не страшно — API REST,
  схема простая, можно опираться на TS/Go SDK как референс.
- **Модерация ботов обязательна.** Учётка на MAX for Partners (нужно ИП/юрлицо/самозанятый),
  верификация профиля, модерация перед публикацией. Учитывать в lead time первого деплоя.
- **HTTPS + trusted CA.** Self-signed не работает с мая 2026. Для dev-деплоя за nginx /
  Caddy нужен валидный серт (Let's Encrypt или российский Минцифры). Смотреть
  [remote-access.md](remote-access.md) для существующей схемы терминирования TLS.
- **Только РФ.** Международным юзерам бесполезен — делать fallback на Telegram.
- **30 rps rate limit.** Для solo-сценариев хватит, но при массовом деплое (много юзеров,
  много чатов) — продумать батчинг или несколько ботов.
- **Long polling не для прода.** Webhook обязателен, публичный endpoint нужен. Если CCS
  за NAT без white IP — другие опции (cloudflare tunnel, ngrok-like, smee.io для dev).

## Статус и план

**Текущий статус (июль 2026):** ресёрч завершён, интеграция **не реализована**. Решение
о реализации отложено до появления:
- потребности в удалённых permissions (кейс #1);
- или второго пользователя CCS (кейс продуктовой необходимости);
- или желания делать mini-app (кейс #5);
- или желания иметь персонального ассистента в мессенджере (кейс A — личка с глобальной
  персоной, см. раздел «Персоны и проекты» выше).

Архитектурно закладывать можно уже сейчас — generic-`IMessengerClient` и stub-реализация
для теста, потом под Max/Telegram/VK — разные адаптеры.

**Если решено реализовывать — шаги:**

1. Зарегистрировать бота на [MAX for Partners](https://dev.max.ru), получить токен.
2. Создать `IMessengerClient` + `MaxMessengerClient` (HttpClient wrapper).
3. `MessengerWebhookController` + валидация сигнатуры.
4. `MessengerIntegrationService` (`IHostedService`) — подписка на `OnSessionMessage`,
   фильтрация `ResultMessage`, отправка уведомлений о завершении (кейс #2).
5. Расширить `notifications-server` каналом Max (кейс #4).
6. Permission push с inline-кнопками (кейс #1) — V1.
7. Quick commands (кейс #3) — V2.

## Cross-links

- [CLAUDE.md](../CLAUDE.md) — общая архитектура, SignalR hub, auth, REST API.
- [observability.md](observability.md) — телеметрия CCS; в «Future Epics — Alerting»
  упомянуты Telegram-алерты. При реализации интеграции с Max — использовать общую
  .NET-библиотеку клиента и одного бота (см. раздел выше).
- [mcp-servers.md](mcp-servers.md) — паттерн внешних интеграций (HTTP + service token).
- [personas.md](personas.md) — персоны, `PersonaPromptBuilder`, память, зоны, доступы;
  используются в расширенных кейсах A/B/C (см. раздел выше).
- [features.md](features.md) — `notifications-server` и продуктовые уведомления.
- [remote-access.md](remote-access.md) — существующая схема публичного доступа и TLS.

## Источники (проверено июль 2026)

- [dev.max.ru](https://dev.max.ru) — основной портал разработчиков.
- [dev.max.ru/docs](https://dev.max.ru/docs) — основная документация.
- [dev.max.ru/docs-api](https://dev.max.ru/docs-api) — Bot API reference.
- [dev.max.ru/docs/chatbots/bots-coding/prepare](https://dev.max.ru/docs/chatbots/bots-coding/prepare) — гайд по созданию бота.
- [dev.max.ru/docs-api/changelog-api](https://dev.max.ru/docs-api/changelog-api) — changelog API.
- [GitHub: max-messenger](https://github.com/max-messenger) — официальные SDK (TS, Go).
