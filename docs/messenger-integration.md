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
- или желания делать mini-app (кейс #5).

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
- [features.md](features.md) — `notifications-server` и продуктовые уведомления.
- [remote-access.md](remote-access.md) — существующая схема публичного доступа и TLS.

## Источники (проверено июль 2026)

- [dev.max.ru](https://dev.max.ru) — основной портал разработчиков.
- [dev.max.ru/docs](https://dev.max.ru/docs) — основная документация.
- [dev.max.ru/docs-api](https://dev.max.ru/docs-api) — Bot API reference.
- [dev.max.ru/docs/chatbots/bots-coding/prepare](https://dev.max.ru/docs/chatbots/bots-coding/prepare) — гайд по созданию бота.
- [dev.max.ru/docs-api/changelog-api](https://dev.max.ru/docs-api/changelog-api) — changelog API.
- [GitHub: max-messenger](https://github.com/max-messenger) — официальные SDK (TS, Go).
