# Spend Analytics v2 — API-контракт бэкенда

Бэкенд фичи «Аналитика использования токенов v2» (спека
`.omc/specs/deep-interview-token-spend-analytics-v2.md`, прототип
`docs/design/mockups/spend-analytics-v2-prototype.html`). Метрика — токены; стоимость $
собирается в данных про запас, но API на экраны её не отдаёт.

## Общее

- Все эндпоинты под `[Authorize]`. База: `/api/spend`.
- **Роли**: не-админ видит только своё — `scope=all`, фильтр `user` с чужим id и разрез
  `user` возвращают **403**. Админ с `scope=all` видит всех (цифры и названия; содержимое
  сообщений не существует в хранилище by design).
- **Период**: `from`/`to` в `yyyy-MM-dd` (UTC-дни), дефолт — последние 30 дней.
- **Фильтры** (все опциональны, значения — id): `user`, `project`, `chat` (id сессии),
  `task`, `persona`, `provider`, `model`, `source`. Пустая строка `""` — узел «без значения»
  (вне проектов / фоновые вызовы без чата / без персоны / модель по умолчанию).
- **Источники** (`source`): `chat-turn` (ходы чатов/задач), `one-shot` (фоновые вызовы),
  `fal` (генерации fal.ai — токенов нет, счётчик `generations`), `free` (бесплатные модели:
  ollama, openrouter-direct, модели `*:free`; токены есть, стоимость 0).
- **Гибридная глубина**: детальные записи ходов — последние `Spend:DetailDays` дней
  (конфиг, дефолт 30), старше — только дневные агрегаты. Границу отдают `detailDays` +
  `windowStart` в обзоре и флаги `aggregated` (день) / `hasDetail` (узел).
- Токены везде объектом: `{ input, output, cacheRead, cacheCreation, total }`.

## GET /api/spend/overview

Параметры: `from`, `to`, `scope=mine|all`, фильтры.

```jsonc
{
  "from": "2026-06-26", "to": "2026-07-25",
  "detailDays": 30, "windowStart": "2026-06-26",   // первый день детального окна
  "allUsers": false,                                // режим admin-scope=all
  "totals": { "input": 1, "output": 2, "cacheRead": 3, "cacheCreation": 4, "total": 10 },
  "turns": 123, "falGenerations": 12,
  "byDay": [                                        // полный ряд дней from..to, с нулями
    { "date": "2026-07-25", "aggregated": false,    // true — день уже свёрнут (за окном)
      "total": 4567,
      "bySource": { "chat-turn": 4000, "one-shot": 500, "free": 67 }, // токены по источникам
      "falGenerations": 2 }
  ],
  "cards": {                                        // топ-8 строк на разрез, по total убыв.
    "users":    [ /* только при allUsers */ ],
    "projects": [ { "key": "id|\"\"", "name": "…|null", "meta": null,
                    "tokens": { }, "turns": 5, "falGenerations": 0 } ],
    "models":   [ ], "chats": [ /* meta: "chat"|"task" */ ], "personas": [ ],
    "sources":  [ ], "providers": [ ]
  },
  "topTurns": [ /* до 10 самых дорогих ходов окна, SpendTurnDto (см. turns) */ ]
}
```

`name: null` — сущность удалена (фронт показывает «удалено»); `key: ""` — узлы
«Вне проектов» / «Фоновые вызовы» / «Без персоны» / «Модель по умолчанию» (имена уже
подставлены в `name`). `users` c `key: ""` — «Система» (фоновые вызовы без владельца).

## GET /api/spend/pivot

Узлы ОДНОГО уровня pivot-дерева. Параметры: `groupBy` (обязателен, один из
`user|project|chat|persona|provider|model|source`), `from`, `to`, `scope`, фильтры.

Раскрытие узла = следующий вызов: `groupBy` — следующий уровень цепочки пользователя,
плюс фильтр по значению раскрытого узла (например раскрыли проект «AI Home» на уровне
`chat` → `groupBy=chat&project=<id>`). Порядок уровней целиком на фронте — бэкенду
цепочка не нужна. Терминальный уровень «ход» — отдельный вызов `/turns` с теми же фильтрами.

```jsonc
{ "nodes": [
  { "key": "opus", "name": "opus", "meta": null,
    "tokens": { }, "turns": 42, "falGenerations": 0,
    "hasDetail": true }   // false — все данные узла из дневных агрегатов (🔒: ходы недоступны)
] }
```

## GET /api/spend/turns

Листья-ходы среза (существуют только в детальном окне). Параметры: период + фильтры +
`limit` (дефолт 50, максимум 500), `offset`, `sort=tokens|time` (дефолт tokens).

```jsonc
{
  "total": 234,            // всего ходов среза (для пагинации)
  "windowClamped": true,   // в периоде были свёрнутые дни — их ходы недоступны
  "items": [ {             // SpendTurnDto
    "id": "…", "timestamp": "2026-07-25T09:15:00Z",
    "ownerId": "u1", "userName": "andrey",
    "sessionId": "…", "chatName": "…", "projectId": "…", "projectName": "…",
    "taskId": null, "taskTitle": null, "personaId": null, "personaName": null,
    "provider": "claude", "model": "opus", "source": "chat-turn",
    "label": null,          // подпись one-shot действия (ключ каталога) или endpoint fal
    "tokens": { }, "generations": 0, "durationMs": 42000,
    "own": true             // ход текущего пользователя → фронт показывает «Открыть чат»
  } ]
}
```

## GET /api/spend/turns/{id}

Паспорт хода. Свой ход — владельцу, чужой — только админу (404 в остальных случаях).

```jsonc
{
  "turn": { /* SpendTurnDto */ },
  "neighbors": [             // все ходы той же сессии в окне (для спарклайна), по времени
    { "id": "…", "timestamp": "…", "total": 4567 }
  ]
}
```

## GET /api/spend/widget

Виджет «Домой», всегда текущий пользователь.

```jsonc
{
  "today": { /* tokens */ }, "week": { /* tokens за 7 дней */ },
  "todayTurns": 5, "weekTurns": 40, "weekFalGenerations": 3,
  "byDay": [ /* 7 дней, формат byDay обзора */ ]
}
```

## GET /api/spend/sessions/{sessionId}/badge

Бейдж чата (обновлять по `result`-событию хода). Владелец сессии или админ, иначе 403.

```jsonc
{
  "sessionId": "…",
  "total": { /* tokens за всю жизнь чата: детали + агрегаты */ },
  "turns": 87,
  "lastTurn": { /* SpendTurnDto последнего хода в окне | null */ }
}
```

## Сбор данных (для понимания семантики)

- **Ходы чатов**: запись на `result` каждого хода (SessionManager). Модель — фактическая
  доминирующая из `modelUsage` result'а (субагенты могли считать другой), фолбэк — модель
  сессии. Провайдер: подписки `sub-*` нормализуются в `claude`.
- **One-shot**: OneShotClaudeRunner теперь всегда просит `--output-format json` — usage
  есть у каждого фонового вызова; `label` = ключ действия каталога (через CheapTextRunner).
  Владелец — `ownerId` вызова, без него — «Система» (`ownerId: ""`).
- **fal.ai**: запись в момент резолва стоимости billing-events (счётчик `generations=1`,
  model/label = endpoint). Токенов нет.
- **Бесплатные**: Ollama (`prompt_eval_count`/`eval_count`) и OpenRouter-direct (`usage`
  ответа) пишут токены с cost 0; модели `*:free` через CLI также классифицируются `free`.
- **Backfill**: разовый импорт при первом старте из `data/sessions/*/history.json`
  (маркер `data/spend/backfill.done`). У сохранённых result'ов нет отметки времени —
  ходы сессии распределяются равномерно между `CreatedAt` и `UpdatedAt` сессии
  (по дням это правдоподобно; точная минута старых ходов не гарантируется).
- **Хранилище**: `data/spend/turns-YYYY-MM-DD.jsonl` (детали) + `data/spend/daily.json`
  (дневные агрегаты по полному составному ключу разрезов). Rollup — фоновый сервис
  (при старте и раз в час). День живёт либо в деталях, либо в агрегатах — двойного счёта нет.
