# QA-прогон пилота отключаемой подсистемы Notes

**Дата:** 2026-09-10
**Ветка:** `feature/notes-optional`
**Коммит прогона:** `7383cd8f` (`fix(briefing): отдавать 503 вместо 500 при выключенной подсистеме Notes`)
**Стенд:** дев-стенд из worktree `C:\Sources\ClaudeCodeServer\.claude\worktrees\notes-optional-real`,
`ASPNETCORE_ENVIRONMENT=Development`, `http://localhost:5015`, данные `C:/ClaudeData/dev`
**Тумблер:** `Subsystems:Notes:Enabled` в `backend/ClaudeHomeServer/appsettings.Local.json` worktree
**Пользователь:** `admin` (роль `admin`, userId `2a5f8815-ab91-418c-a1cd-abeedac5063e`)

Порт стенда — `5015`, а не `5000`: на `:5000` в момент прогона висел чужой дев-стенд
(`wt-blocker-honest`), на `:80` — боевой инстанс. Фронт SPA ходит относительными путями,
на проверки смена порта не влияет.

Контекст: предыдущий прогон шёл на `b5212bb0` и упирался в блокер Д1 (`/api/notes/*` → 500).
Этот прогон — на финальном состоянии ветки, после пяти закрывающих коммитов: `0b8c0b9b`
(ApplicationPart в композиции), `a10137fd` (вкрапления фронта), `a7d08f9f` (админский экран),
`1def4e92` (платный LLM до гейта), `7383cd8f` (503 на брифинге).

## Подготовка (обязательные шаги выполнены)

| Шаг | Результат |
|---|---|
| `cd backend; dotnet build` | ✅ `Сборка успешно завершена. Предупреждений: 0, Ошибок: 0` (12,55 с) |
| `cd frontend; npm run build` | ✅ пересобран, `dist/index.html` от 10.09 10:35:47, чанк `assets/index-DOXMGguf.js` от 10:35:46 |
| Стенд раздаёт фронт из worktree | ✅ во всех трёх запусках лог: `Фронтенд раздаётся из C:\Sources\ClaudeCodeServer\.claude\worktrees\notes-optional-real\frontend\dist` |
| Окружение Development, свой порт | ✅ `Hosting environment: Development`, `Now listening on: http://localhost:5015` |
| Сброс SW и `caches` в браузере перед ON и перед OFF | ✅ `caches.delete` по всем ключам + `serviceWorker.unregister` по всем регистрациям |
| Смена тумблера через `appsettings.Local.json` и рестарт | ✅ ON: `subsystems` 20 ключей с `notes`; OFF: 19 ключей без `notes` |

## Итог одной строкой

**Блокер прошлого прогона снят: 500-х на `/api/notes/*` больше нет** — гейт выключает
вертикаль структурно, `NotesController` исчезает из MVC, лог сервера в режиме OFF чист
(ноль записей `UnhandledExceptionHandler` / `NotesService` / `Unable to resolve`).
Брифинг отдаёт 503 с человеческим текстом, «Итог сессии» отказывает 502 **до** обращения
к модели (запись в тратах не появилась), консоль браузера в OFF чистая, кнопки
«Сохранить в заметку» в ленте нет. Цикл ON → OFF → ON данные не тронул: 147 `.md`,
список файлов и суммарный размер побайтово совпали с baseline.

**Два дефекта открыты** (оба — на пунктах, которые задача требовала подтвердить):

* **Д-A** — при выключенной подсистеме `/api/notes/*` отдают **200 `text/html`**
  (SPA-фолбэк), а не **404**, как требует постановка.
* **Д-B** — админский экран «Подсистемы» **не открывается никогда**, ни в ON, ни в OFF:
  падает в `ErrorBoundary` («Что-то пошло не так») из-за рассинхрона контракта
  `/api/admin/subsystems`.

---

## Режим «включено» (`Subsystems:Notes:Enabled = true`)

| # | Сценарий | Ожидание | Факт | Итог |
|---|---|---|---|---|
| 1 | `/api/auth/me` — `subsystems` массивом с `notes` | да | 200, 20 элементов, `notes` на позиции 10: `backgrounds,changelog,code-graph,deploy,dossiers,git,images,knowledge,llm,memory,notes,project-icons,project-services,reader,skills,spend,tasks,tts,video,yandex` | ✅ |
| 2 | `GET /api/admin/subsystems` → notes включена | да | 200, 20 записей, `notes: {"key":"notes","title":"Заметки","description":"Личный vault, заметки проектов, связи [[…]] и граф.","enabled":true,"active":true,"restartRequired":false}` | ✅ |
| 3 | `GET /api/notes` (с admin JWT) | 200 | **200**, 111 заметок | ✅ |
| 4 | `GET /api/notes/caps` / `folders` / `sources` / `templates` / `graph` | 200 | **200** во всех пяти | ✅ |
| 5 | `GET /api/notes` без авторизации | 401 | **401** | ✅ |
| 6 | `/api/tasks`, `/api/projects`, `/api/search` | 200 | **200 / 200 / 200** | ✅ |
| 7 | Вкладка «Заметки» в хабе | есть | nav: **Чаты, Проекты, Календарь, Заметки, Персоны**, Echo | ✅ |
| 8 | Панель «Заметки» на `/#/notes` | есть | дерево Личный 35 (Journal 31, Сессии 2, Дизайн 1) + ClaudeCodeServer, кнопки «Новая» / «Заметка» | ✅ |
| 9 | Виджет «Заметки» и быстрое действие «Новая заметка» на главной | есть | блок «Заметки» со списком свежих; «Новая заметка» в блоке быстрых действий | ✅ |
| 10 | **Б4 — переименование заметки отдаёт НОВЫЙ id** | да | `PUT /api/notes/cGVyc29uYWx8Sm91cm5hbC8yMDI2LTA5LTEwLVJFTkFNRUQtYnktcWEubWQ` с `{"title":"2026-09-10-RENAMED-run2"}` → **200**, в ответе новый id `cGVyc29uYWx8Sm91cm5hbC8yMDI2LTA5LTEwLVJFTkFNRUQtcnVuMi5tZA`; `GET` по новому id → 200; на диске файл `2026-09-10-RENAMED-run2.md` | ✅ |
| 11 | `POST /api/briefing/today` (контроль к 503) | не 503 | **200**, собрана дневниковая заметка `2026-09-10`, 20,8 с | ✅ |
| 12 | `POST /api/sessions/{id}/summary` (контроль к «без траты») | 200 + запись в тратах | **200**, заметка «Итог_ QA stop scenario 1 final · 2026-09-10»; записей `session-summary` в `spend/turns-2026-09-10.jsonl`: **0 → 1** | ✅ |
| 13 | Админский экран «Подсистемы» открывается | открывается | **`ErrorBoundary`: «Что-то пошло не так»** | ❌ **Д-B** |

Скриншоты ON: `on-01-home.png`, `on-02-notes-page.png`, `on-03-subsystems-screen.png` (падение Д-B),
`on-04-home-after-cycle.png` (главная после возврата ON в конце цикла).

---

## Режим «выключено» (`Subsystems:Notes:Enabled = false`)

| # | Сценарий | Ожидание | Факт | Итог |
|---|---|---|---|---|
| 1 | Бэкенд стартует | да | стартует, фронт из того же `dist`, слушает :5015 | ✅ |
| 2 | `/api/auth/me` без `notes` | да | 200, **19** элементов, `notes` отсутствует | ✅ |
| 3 | `GET /api/admin/subsystems` → notes выключена | да | 200, `notes: {"enabled":false,"active":false,"restartRequired":false}` | ✅ |
| 4 | **`GET /api/notes` → 404, не 500** | **404** | **200 `text/html`, 146 506 байт** — SPA-фолбэк (`index.html`). 500-ки нет, маршрут из MVC исчез | ❌ **Д-A** |
| 5 | `GET /api/notes/caps` / `folders` / `sources` / `templates` / `graph` | **404** | **200 `text/html`** во всех (тот же фолбэк) | ❌ **Д-A** |
| 6 | В логе сервера нет исключений по Notes | нет | **ноль** совпадений по `NotesService`, `Unable to resolve`, `Необработанное исключение`, `UnhandledExceptionHandler` (129 строк лога) | ✅ |
| 7 | `POST /api/briefing/today` → 503 | **503** | **503**, тело `{"error":"Утренний бриф недоступен: подсистема «Заметки» отключена","reason":"notes_disabled"}`, в логе исключения нет | ✅ |
| 8 | `POST /api/sessions/{id}/summary` → отказ без траты | отказ без записи в тратах | **502**, тело `{"error":"Подсистема Notes отключена — сохранение итога недоступно"}`; записей `session-summary`: **1 → 1**, всего строк файла трат **41 → 41** | ✅ |
| 9 | `/api/tasks`, `/api/projects`, `/api/search` | 200 | **200 / 200 / 200** | ✅ |
| 10 | Вкладки, виджета, быстрого действия нет | нет | nav: **Чаты, Проекты, Календарь, Персоны**, Echo; в быстрых действиях только «Новый чат», «Новая задача», «Новый проект», «Новая персона»; виджета «Заметки» на главной нет | ✅ |
| 11 | **Консоль браузера чистая, `AiLauncher` не зовёт `notes.caps()`** | без ошибок | **0 errors** за всю сессию OFF (12 warnings — это preload-предупреждения Vite про `createLucideIcon`/`activity`/`apple`, к Notes отношения не имеют). Запросов к `/api/notes*` — **ноль**, в том числе после открытия `AiLauncher` | ✅ |
| 12 | **Кнопки «Сохранить в заметку» в ленте нет** | скрыта | поиск по открытому чату — `No matches found for "Сохранить в заметку"`; чат работает, композер жив | ✅ |
| 13 | Админский экран «Подсистемы» открывается | открывается | **`ErrorBoundary`: «Что-то пошло не так»** (то же, что в ON) | ❌ **Д-B** |

Скриншоты OFF: `off-01-home.png` (главная без «Заметок»), `off-02-ai-launcher.png`
(AiLauncher открыт, ноль запросов к notes), `off-03-chat-no-save-to-note.png` (чат без кнопки),
`off-04-subsystems-crash.png` (падение Д-B).

### API в выключенном режиме — фактические коды

```
GET  /api/auth/me                  → 200, subsystems 19, без 'notes'      ✅
GET  /api/admin/subsystems         → 200, notes.enabled=false             ✅
GET  /api/notes                    → 200 text/html ❌ (ожидался 404)
GET  /api/notes/caps               → 200 text/html ❌
GET  /api/notes/folders            → 200 text/html ❌
GET  /api/notes/sources            → 200 text/html ❌
GET  /api/notes/templates          → 200 text/html ❌
GET  /api/notes/graph              → 200 text/html ❌
POST /api/briefing/today           → 503 {"reason":"notes_disabled"}      ✅
POST /api/sessions/{id}/summary    → 502, трат не прибавилось             ✅
GET  /api/tasks                    → 200                                  ✅
GET  /api/projects                 → 200                                  ✅
GET  /api/search?q=тест            → 200                                  ✅
```

---

## Дефекты

### Д-A — `/api/notes/*` при выключенной подсистеме отдают 200 `text/html`, а не 404

**Затронуто:** все маршруты `NotesController`.

**Что изменилось против прошлого прогона:** 500-ки больше нет — `0b8c0b9b` работает.
`ApplicationPart` вертикали снимается в композиции, `NotesController` в MVC не попадает,
DI-исключения в логе отсутствуют. То есть корневая причина прошлого блокера вылечена.

**Что осталось:** несопоставленный путь ловит SPA-фолбэк
[`Program.cs:1596`](../../backend/ClaudeHomeServer/Program.cs) —
`app.MapFallbackToFile("index.html", …)` без исключения для `/api`. Единственный вырезанный
префикс — `/_api` (`Program.cs:1595`, ради Office/SharePoint).

**Проверка природы дефекта.** Заведомо несуществующий `/api/nonexistent-xyz` тоже
отдаёт `200 text/html`, а существующий контроллер с несуществующим id
(`/api/tasks/zzz-none`) — честный `404`. Значит это не регрессия ветки, а общее свойство
приложения: **любой** несопоставленный `/api/*` маршрут возвращает SPA-страницу. Проявилось
оно здесь потому, что выключение вертикали — первый штатный сценарий, когда маршрут
`/api/*` перестаёт существовать.

**Почему это не косметика.** Для XHR/fetch-клиента 200 хуже 404: `response.ok === true`,
`Content-Type: text/html`, и `JSON.parse` падает синтаксической ошибкой вместо понятного
«такого раздела нет». Требование постановки «`/api/notes/*` → 404, не 500» по букве
не выполнено.

**Лечение (одна строка, вне моего владения):** ограничить фолбэк — либо тем же приёмом,
что уже применён к `/_api` (`app.Map("/api", … 404)` ПОСЛЕ маршрутизации контроллеров),
либо предикатной формой `MapFallbackToFile` с исключением префикса `/api`. Правка задевает
все API-маршруты приложения, поэтому её место — отдельная задача с ревью, а не хвост пилота.

### Д-B — админский экран «Подсистемы» падает в `ErrorBoundary` в обоих режимах

**Как воспроизвести:** меню пользователя → «Подсистемы». Вместо модалки — полноэкранное
«Что-то пошло не так». Воспроизводится и при `Enabled=true`, и при `Enabled=false`.

**Консоль:**

```
TypeError: Cannot read properties of undefined (reading 'filter')
[ErrorBoundary] перехвачена ошибка рендера: TypeError: Cannot read properties of undefined (reading 'filter')
```

**Причина — рассинхрон контракта, ровно той же природы, что закрытый Д-1 прошлого прогона,
но в другом месте.** Бэк отдаёт **голый массив**:

```cs
// backend/ClaudeHomeServer/Controllers/SubsystemsController.cs:23
public ActionResult<IReadOnlyList<SubsystemInfo>> List() => Ok(subsystems.Snapshot(config));
```

Фронт ждёт объект-обёртку и деструктурирует его:

```ts
// frontend/src/api/subsystems.ts:36
export interface SubsystemsListResponse { subsystems: Subsystem[] }
// frontend/src/pages/SubsystemsPage.tsx:68
subsystemsApi.get().then(({ subsystems }) => { setSubsystems(subsystems); setLoadState('ok'); })
```

У массива поля `subsystems` нет → `setSubsystems(undefined)` → `loadState = 'ok'` →
строка 79 `subsystems.filter(...)` бросает `TypeError`.

**Почему не поймали.** Контрактный сторож `frontend/src/lib/__tests__/subsystems.contract.test.ts`,
заведённый под прошлый Д-1, покрывает **только** поле `subsystems` в `/api/auth/me`
(`AuthController.cs` + `types/index.ts`). Маршрут `/api/admin/subsystems`, добавленный
коммитом `a7d08f9f`, не покрыт ничем — ни vitest, ни ручной проверкой: сообщение коммита
утверждает «контракт клиента приведён к реальному контракту», но обёртка `{ subsystems }`
осталась.

**Побочный эффект:** падение ловит глобальный `ErrorBoundary`, поэтому уносит **весь**
экран приложения, а не одну модалку; hash-навигация (`#/home`) состояние не сбрасывает —
нужна полная перезагрузка страницы.

**Лечение (правка на строку, вне моего владения):** привести одну из сторон к другой.
Дешевле фронт — `get: () => request<Subsystem[]>('/admin/subsystems')` и
`.then(list => setSubsystems(list))`; заодно расширить `subsystems.contract.test.ts`
второй линией на `SubsystemsController.cs` ↔ `api/subsystems.ts`, иначе сторож снова
не заметит рассинхрон.

### Наблюдение (не дефект)

В `AiLauncher` при выключенной подсистеме остаются подписи, упоминающие заметки:
«Единый поиск — *по заметкам и задачам сразу*», «Обзор за меня — *приоритеты на сегодня
по задачам и заметкам*». Запросов к notes эти пункты не делают (проверено по сети),
на работу не влияют — но текст обещает раздел, которого в этом инстансе нет.

---

## Цикл ON → OFF → ON

Baseline снимался в ON **после** мутаций этого прогона (переименование Б4, бриф, итог сессии),
чтобы сравнивать цикл, а не последствия проверок.

| Замер | Всего `.md` | У `admin` | Суммарный размер, байт | Список файлов |
|---|---|---|---|---|
| Baseline (ON, перед выключением) | 147 | 37 | 127 830 | — |
| После режима OFF | 147 | 37 | 127 830 | идентичен baseline (`Compare-Object` — пусто) |
| После возврата в ON | 147 | 37 | 127 830 | идентичен baseline (`Compare-Object` — пусто) |

После возврата в ON: `/api/auth/me` снова 20 подсистем с `notes`, `GET /api/notes` → **200**,
113 заметок, переименованная `2026-09-10-RENAMED-run2` на месте, вкладка «Заметки» и быстрое
действие «Новая заметка» вернулись (`on-04-home-after-cycle.png`).

**Данные целы.** Разница 111 → 113 заметок между началом и концом прогона — это две
заметки, созданные самими проверками ON (дневниковый бриф `2026-09-10` и «Итог_ QA stop
scenario 1 final · 2026-09-10»), а не следствие выключения.

**Внесённая мутация:** заметка `2026-09-10-RENAMED-by-qa.md` (наследие прошлого прогона)
переименована в `2026-09-10-RENAMED-run2.md` — это и есть проверка Б4. Плюс две новые
заметки от проверок брифа и итога сессии.

---

## Что подтверждено по каждому закрывающему коммиту

| Коммит | Заявка | Подтверждение прогоном |
|---|---|---|
| `0b8c0b9b` | 500 → 404 на `/api/notes/*` | 500 **устранён** (лог чист, контроллер вне MVC); до 404 не доходит — перехватывает SPA-фолбэк, см. **Д-A** |
| `a10137fd` | вкрапления фронта под гейтом | ✅ консоль OFF без ошибок, ноль запросов `/api/notes*`, кнопки «Сохранить в заметку» нет |
| `a7d08f9f` | админский экран подключён | экран **достижим** из меню (пункт «Подсистемы» под `isAdmin` есть), но **не открывается** — см. **Д-B** |
| `1def4e92` | отказ до платного LLM | ✅ 502 без прироста записей `session-summary` в тратах |
| `7383cd8f` | 503 вместо 500 на брифинге | ✅ 503 + `reason: notes_disabled` + текст про отключённые заметки, в логе исключения нет |

## Скриншоты

Каталог `.cc-attachments/notes-optional-run/` (в git не попадает — путь в
`.git/info/exclude`). Файлы этого прогона:

- `on-01-home.png` — главная, подсистема ON: вкладка «Заметки», виджет, быстрое действие.
- `on-02-notes-page.png` — `/#/notes`: дерево заметок, переименованная `2026-09-10-RENAMED-run2`.
- `on-03-subsystems-screen.png` — Д-B в режиме ON: «Что-то пошло не так».
- `on-04-home-after-cycle.png` — главная после возврата ON в конце цикла.
- `off-01-home.png` — главная, подсистема OFF: вкладки без «Заметок», виджета и быстрого действия нет.
- `off-02-ai-launcher.png` — `AiLauncher` открыт в OFF без ошибок и без запросов к notes.
- `off-03-chat-no-save-to-note.png` — чат в OFF: кнопки «Сохранить в заметку» под ответом нет.
- `off-04-subsystems-crash.png` — Д-B в режиме OFF.

---

## Приложение А — как воспроизвести

```powershell
# конфиг стенда: backend/ClaudeHomeServer/appsettings.Local.json
#   "Urls": "http://localhost:5015",
#   "DataPath": "C:/ClaudeData/dev/projects.json",
#   "Subsystems": { "Notes": { "Enabled": true } }   # или false

cd backend; dotnet build
cd ..\frontend; npm run build
cd ..\backend\ClaudeHomeServer
$env:ASPNETCORE_ENVIRONMENT = "Development"; dotnet run --no-build --project .

# логин и проба
$t = (Invoke-RestMethod http://localhost:5015/api/auth/login -Method Post `
      -Body (@{username='admin';password='12345'}|ConvertTo-Json) -ContentType 'application/json').token
Invoke-WebRequest http://localhost:5015/api/notes -Headers @{Authorization="Bearer $t"} -UseBasicParsing |
  Select-Object StatusCode, @{n='ct';e={$_.Headers['Content-Type']}}
```

Смена режима — правка `Subsystems:Notes:Enabled` и **перезапуск** процесса: гейт читается
один раз на старте (`restartRequired` в снимке подсистем именно про это).

## Приложение Б — коммиты, на которых проверялось

| Коммит | Что несёт |
|---|---|
| `7383cd8f` | `fix(briefing): отдавать 503 вместо 500 при выключенной подсистеме Notes` (HEAD прогона) |
| `1def4e92` | `fix(SessionSummary): отказывать до платного LLM, если подсистема Notes выключена` |
| `a7d08f9f` | `feat(subsystems): подключить админский экран и свести контракт с бэком` |
| `a10137fd` | `fix(notes): закрыть гейтом вкрапления Notes (чат-кнопка, AiLauncher, прогрев)` |
| `0b8c0b9b` | `fix(subsystems): отключать ApplicationPart вертикали в композиции при закрытом гейте` |
| `b5212bb0` | база прошлого прогона (`fix(subsystems): синхронизировать форму поля subsystems`) |
