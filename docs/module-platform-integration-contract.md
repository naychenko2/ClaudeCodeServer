# Контракт взаимодействия «ядро AI Home ↔ внешний модуль» v1.0

> Передан командой «Личные финансы» вместе с ТЗ `docs/module-platform-core-requirements.md`. Источник правды — git проекта «Личные финансы» (`integration-contract.md`); изменения только через совместное согласование (semver, §8).

- Статус: **APPROVED** (консенсус, одобрен Андреем 2026-07-22)
- Первый потребитель: модуль `family-budget`. Контракт универсален — под будущие модули, язык-агностичен (HTTP + манифест + Module Federation).
- Заморозка: backend-якоря (§2–§6, §8) зафиксированы; фронт-якоря (§7) — после зелёного спайка R5a.

## 1. Топология

```
                        Браузер (PWA-оболочка ядра)
                         │  cc_token (HMAC JWT ядра)
                         ▼
  ┌───────────────────────────────────────────────────────────┐
  │  ЯДРО AI Home — единственная публичная точка входа          │
  │  [SPA]  [/api/* ядра]  [Module Gateway (YARP)]              │
  │   MF-loader ──────────────┐        │ /api/modules/{id}/**   │
  │   MCP node-прокси ────────┤        │ + инъекция identity    │
  └───────────────────────────┴────────┼───────────────────────┘
                                        ▼ (модуль виден ТОЛЬКО ядру)
                        ┌───────────────────────────────┐
                        │  СЕРВИС МОДУЛЯ (отдельный      │
                        │  процесс, своя БД, свой деплой)│
                        └───────────────────────────────┘
```

Весь трафик к модулю — через gateway ядра (`/api/modules/{id}/**`): и браузер, и MCP. Сервис модуля наружу не публикуется. Подключение модуля = запись в реестре + перезапуск ядра (hot-plug вне scope v1).

## 2. Манифест модуля `module.json`

Читается ядром на старте из каталога `modules/` или списка в конфиге.

```jsonc
{
  "schemaVersion": "1.0",           // мажор.минор контракта
  "id": "family-budget",            // slug: сквозной ключ (маршрут, MF-remote, MCP)
  "version": "0.1.0",               // semver модуля; бьётся в ?v= remoteEntry
  "displayName": "Бюджет",
  "description": "Семейный бюджет",
  "backend": {
    "baseUrl": "http://127.0.0.1:5100",  // внутренний адрес сервиса
    "healthPath": "/health",
    "routePrefix": "/api/modules/family-budget"
  },
  "frontend": {                     // опционально
    "remoteEntry": "/api/modules/family-budget/ui/remoteEntry.js",
    "exposedModule": "./Tab",
    "tab": { "label": "Бюджет", "icon": "wallet", "order": 45 }
  },
  "mcp": [                          // опционально
    { "key": "budget", "command": "node", "args": ["mcp/budget-server/index.js"] }
  ],
  "scopes": ["budget.read", "budget.write"],  // возможности модуля, НЕ per-user права
  "auth": { "audience": "aihome-module:family-budget" }
}
```

Обязательные: `schemaVersion, id, version, displayName, backend.*`. Поле `auth.identity` в v1 отсутствует — JWKS-валидация мандатна.

## 3. Backend-интеграция (gateway)

Ядро генерирует из реестра на модуль: маршрут `module-{id}` (`Match.Path = {routePrefix}/{**}`, `Transform PathRemovePrefix`) + кластер на `baseUrl` с active health-check на `healthPath` (образец в ядре — маршрут forgejo). Провайдер YARP — **добавочный** к существующему `LoadFromConfig`, не замена.

Требования к сервису модуля: `200 OK` на `GET {healthPath}` без аутентификации; работа только под своим `routePrefix`; identity — исключительно из модульного токена (§5); внутренняя доменная авторизация (например, членство семьи) — ответственность модуля.

### 3.1 Лимиты data-plane (per-route для `module-*`)

| Параметр | Значение | Примечание |
|---|---|---|
| Max request body | **100 МБ** → иначе `413` | фото чеков 5–15 МБ, PDF/Excel-выписки, запас ×3 |
| Proxy activity timeout | **300 с** (бездействия, не суммарный) | чтение байтов сбрасывает счётчик |
| Streaming/SSE | обязателен, gateway не буферизует ответы | модуль шлёт keep-alive `: ping` ≤30 с |
| Response body | без лимита (стриминг) | |
| WebSocket | вне scope v1 | |

Рекомендация модулям: операции >60 с оформлять асинхронно (`202` + polling).

### 3.2 Ошибки gateway (зарезервированные формы)

- Модуль unhealthy/недоступен → `503` + `Retry-After: 15` + `{"error":"module_unavailable","moduleId":"...","retryAfterSeconds":15}`
- Таймаут → `504` + `{"error":"module_timeout","moduleId":"..."}`

Коды `module_unavailable|module_timeout` эмитит только gateway; модуль их использовать не вправе.

## 4. Env-контракт запуска сервиса модуля

```
AIHOME_CORE_BASEURL   # напр. http://host.docker.internal:5000
AIHOME_JWKS_URL       # default: {AIHOME_CORE_BASEURL}/.well-known/aihome-modules/jwks.json
AIHOME_MODULE_ID      # свой id — для сборки ожидаемого aud
```

## 5. Аутентификация

Ядро — единственный эмитент identity. Раздача HMAC-секрета ядра модулям запрещена.

### 5.1 Модульный токен (RS256), схема заморожена в мажоре 1

Header: `alg=RS256` (константа), `typ=JWT`, `kid` (`^[a-z0-9-]{8,64}$`).

| Claim | Тип/формат | Значение |
|---|---|---|
| `iss` | константа | `"ClaudeHomeServer"` |
| `aud` | константа-шаблон | `"aihome-module:{moduleId}"`, точное сравнение |
| `sub` | строка ≤64, непрозрачная | userId (сейчас GUID lowercase); модуль НЕ парсит |
| `name` | UTF-8 ≤128 | отображаемое имя пользователя |
| `scope` | scopes через пробел (RFC 8693) | `^[a-z][a-z0-9._-]{1,63}$`; подмножество манифеста |
| `chan` | enum | `"gateway"` \| `"mcp"` — канал выпуска |
| `iat`,`nbf`,`exp` | NumericDate | `nbf=iat`; clock skew при валидации 60 с |
| `jti` | GUID, опционален | ядро эмитит всегда |

Неизвестные claims модуль игнорирует (forward-compat). Добавление claim = минор schemaVersion; изменение существующего = мажор.

TTL по каналу: `chan=gateway` — **5 мин**; `chan=mcp` — **60 мин**. Режим отказа по контракту: агентский ход длиннее 60 мин получает `401` на инструментах модуля — корректное поведение, лечится новой сессией.

### 5.2 Gateway-инъекция

На каждый запрос `/api/modules/**`: ядро валидирует `cc_token`; **на входе срезает** клиентский `Authorization` и все `X-AIHome-*`; вставляет свежий модульный токен. Без валидного `cc_token` → `401` на границе ядра. Браузер модульный токен никогда не держит.

### 5.3 JWKS и ротация

- JWKS: `/.well-known/aihome-modules/jwks.json`.
- Модуль кэширует JWKS 10 мин; неизвестный `kid` → немедленный рефреш (rate limit 1/30 с, иначе `401`).
- Ротация: **новый ключ публикуется в JWKS ДО первой подписи им**; старый остаётся в JWKS ≥24 ч после последней подписи.
- JWKS недоступен: работа с кэша + grace 10 мин, затем `503` (fail-closed).

### 5.4 Авторизация в модуле

`aud` → `sub` → `scope` → собственная доменная проверка модуля. «Валидный токен» ≠ «доступ». `scopes` — возможности модуля, не per-user права.

## 6. MCP-серверы модуля

Ядро на ходу сессии добавляет каждый `mcp[]` из манифеста в mcp-config **данными** (аддитивно, встроенные серверы не трогаются):

```
servers[mcp.key] = { command, args: [resolve(moduleDir, ...)],
  env: { MODULE_API_URL:  "{ядро}/api/modules/{id}",   // через gateway!
         MODULE_API_TOKEN: <модульный токен chan=mcp, TTL 60 мин>,
         MODULE_ID: "{id}" } }
```

## 7. Frontend (Module Federation) — якоря фиксируются после спайка R5a

- Remote name = `manifest.id`; exposes `./Tab` → `React.ComponentType<AIHomeModuleContext>`.
- `remoteEntry` и чанки — под `{routePrefix}/ui/**`: **публичная статика без аутентификации**, `Cache-Control: public, max-age=31536000, immutable`, версия `?v={module.version}`. В `ui/` — только статика, ни байта данных.
- Оболочка: generic module-screen (`#/module/{id}`), вкладки из реестра, `ModuleHost` монтирует remote с контекстом:

```ts
interface AIHomeModuleContext {
  user: { id: string; name: string };
  apiBase: string;                  // "/api/modules/{id}"
  getToken: () => string | null;    // cc_token для Authorization на apiBase
  theme: { mode: 'light'|'dark' };
  navigate: (hash: string) => void;
  onTitleChange?: (t: string) => void;
  schemaVersion: string;
}
```

### 7.1 Platform Runtime (привязан к мажору schemaVersion)

```
schemaVersion 1.x ⇒
  react / react-dom : shared singleton, requiredVersion ^19.2.0, strictVersion
  JS target         : ES2022, native ESM
  Браузеры          : последние 2 мажора Chrome/Edge/Safari
  Тема              : только CSS-переменные ядра (lib/theme.css, data-theme)
```

Patch зависимостей — свободно; минор react = минор schemaVersion с анонсом; мажор react = мажор schemaVersion.

## 8. Версионирование

`schemaVersion` = мажор.минор. Несовместимый мажор → модуль не поднимается (лог + пропуск). Минор выше → работа на общих полях + предупреждение. Якоря мажора 1: путь `/api/modules/{id}`, JWT-схема §5.1, JWKS-URL и aud-схема, MF-expose `./Tab`, `AIHomeModuleContext`, Platform Runtime §7.1.

## 9. Conformance: эталонный модуль `module-echo`

Предоставляет команда модуля. Состав: backend (`GET /health`; `GET /echo/whoami` → `{sub,name,scope,chan}`; `POST /echo/upload` → размер; `GET /echo/stream` → SSE 1 соб/с, 60 с); frontend (`remoteEntry` c `./Tab`); MCP tool `echo_whoami`.

| CT | Проверка |
|---|---|
| CT-1 | health через gateway → 200 |
| CT-2 | инъекция токена: все claims §5.1 корректны |
| CT-3 | ротация: неизвестный kid → рефреш JWKS → запрос проходит |
| CT-4 | недостающий scope → 403 |
| CT-5 | upload 100 МБ проходит; 101 МБ → 413 |
| CT-6 | SSE 60 с без обрыва и буферизации |
| CT-7 | модуль погашен → форма 503 из §3.2 |
| CT-8 | `./Tab` монтируется в prod-оболочке; React-инстанс один |
| CT-9 | MCP tool отвечает в ходе сессии |
| CT-10 | identity-заголовки клиента срезаны на входе |
| CT-11 | негативные auth-кейсы: протухший `exp` / чужой `aud` / битая подпись → 401 |

**Приёмка:** команда ядра сдаёт свои работы прогоном `module-echo` (все CT зелёные); команда модуля сдаёт модуль тем же чеклистом.
