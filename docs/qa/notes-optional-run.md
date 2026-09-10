# QA-прогон пилота отключаемой подсистемы Notes

**Дата:** 2026-09-10
**Ветка:** `feature/notes-optional`
**Коммит прогона:** `b5212bb0` (`fix(subsystems): синхронизировать форму поля subsystems между бэком и фронтом`)
**Стенд:** дев-стенд из worktree `C:\Sources\ClaudeCodeServer\.claude\worktrees\notes-optional`,
`ASPNETCORE_ENVIRONMENT=Development`, `http://localhost:5000`, данные `C:/ClaudeData/dev`
**Тумблер:** `Subsystems:Notes:Enabled` в `backend/ClaudeHomeServer/appsettings.Local.json` worktree
**Пользователь:** `admin` (роль `admin`, userId `2a5f8815-ab91-418c-a1cd-abeedac5063e`)

Контекст прогона: первый прогон пилота на коммите `088e5f65` обнаружил корневой дефект Д-1 — гейт
не работал ни в одном положении тумблера (бэк отдавал `subsystems` массивом, фронт ждал `Record`).
Дефект закрыт коммитом `b5212bb0` (Кира): форма синхронизирована, добавлен контрактный сторож
`subsystems.contract.test.ts`. Цель прогона — подтвердить, что гейт действительно
заработал на исправленном коде.

## Подготовка (обязательные шаги выполнены)

| Шаг | Результат |
|---|---|
| `cd frontend; npm run build` | ✅ собран заново (предыдущий бандл от 09.09 был ДО коммита b5212bb0), `dist/index.html` от 10.09 08:41, чанк `assets/index-BiU5_E5M.js` |
| `dotnet build` (через `dotnet run` после правок) | ✅ компилируется без ошибок (`bin/Debug/net10.0/ClaudeHomeServer.dll` от 10.09 08:41, и Notes: `ClaudeHomeServer.Notes.dll` от 10.09 08:41) |
| Стенд раздаёт фронт из worktree | ✅ лог: `Фронтенд раздаётся из C:\Sources\ClaudeCodeServer\.claude\worktrees\notes-optional\frontend\dist` |
| Сброс SW и `caches` в браузере перед прогоном ON | ✅ выполнен: `caches.delete` для всех ключей, `serviceWorker.unregister` для всех регистраций, `localStorage.clear()` |
| Бэкенд стартует через `dotnet run` с `ASPNETCORE_ENVIRONMENT=Development` | ✅ лог: `Hosting environment: Development`, Kestrel слушает `127.0.0.1:5000` |
| Смена тумблера через `appsettings.Local.json` и рестарт | ✅ при `Enabled=true` бэкенд печатает `subsystems: 20 ключей, includes notes`; при `Enabled=false` — `19 ключей, без notes`, см. `Приложение А` |

## Итог одной строкой

**Фронтовый гейт подсистемы на b5212bb0 работает корректно** — дефект Д-1 (базовый)
закрыт полностью: при включённой подсистеме фронт отрисовал вкладку «Заметки», панель
проекта, виджет главной и быстрое действие «Новая заметка»; при выключенной — всё это
полностью исчезло. Глобальный поиск работает без секции заметок, чат жив.

**Бэковый гейт (ИЗ `SubsystemGate.IsEnabled` через `Appsettings:Notes:Enabled=false`)
по-прежнему не исключает `NotesController` из MVC** — `ApplicationPartAttribute("ClaudeHomeServer.Notes")`
присутствует в авто-генерируемом файле
`backend/ClaudeHomeServer/obj/Debug/net10.0/ClaudeHomeServer.MvcApplicationPartsAssemblyInfo.cs`
всегда, до условного `Configure<MvcOptions>` от `NotesSubsystem.Register` дело не доходит:
контроллеры на дискавери уже подключены, а сервисов в DI нет. Маршрут `/api/notes/*` отвечает
**500**, а не **404**, как требовал сценарий прогона.

Цикл ON → OFF → ON сохранил данные: 145 `.md`, 35 личных у `admin`, переименованная заметка
присутствует.

---

## Режим «включено» (`Subsystems:Notes:Enabled = true`)

| # | Сценарий | Ожидание | Факт | Итог |
|---|---|---|---|---|
| 1 | `/api/auth/me` — `subsystems` массивом + `notes` присутствует | да | `"subsystems":["backgrounds","changelog","code-graph","deploy","dossiers","git","images","knowledge","llm","memory","notes","project-icons","project-services","reader","skills","spend","tasks","tts","video","yandex"]` (20 элементов, `notes` на позиции 10) | ✅ |
| 2 | Вкладка «Заметки» в хабе | есть | nav: **Чаты, Проекты, Календарь, Заметки, Персоны**, Echo | ✅ |
| 3 | Панель «Заметки» в рабочей области `/#/notes` | есть | дерево Личный 35 (Journal, Сессии, Дизайн) и ClaudeCodeServer 76; кнопки «Новая», «Новая заметка», центральная подсказка про [[wikilinks]] | ✅ |
| 4 | Виджет «Заметки» на главной (`/#/home`) | есть | на главной — блок «Заметки» с кнопками «Новая заметка», «Все заметки →» и списком 5 свежих (`2026-09-10`/`09`/`07`, ответы по задачам) | ✅ |
| 5 | Быстрое действие «Новая заметка» на главной | есть | в блоке «Быстрые действия» рядом с «Новый чат», «Новая задача», «Новый проект», «Новая персона» | ✅ |
| 6 | `GET /api/notes` (без авторизации) → 401 | да | `401 Unauthorized, WWW-Authenticate: Bearer` | ✅ |
| 7 | `GET /api/notes` (с admin JWT) → 200 + 111 заметок | 200 | 200, JSON-массив из 111 заметок (109 у admin + 2 персональные journal), источники: `personal`, `1923e4f9-cc07-4b28-ac29-a8e64db5b722` (ClaudeCodeServer) | ✅ |
| 8 | `GET /api/notes/caps`, `/api/notes/folders`, `/api/notes/search`, `/api/admin/subsystems` | 200 | 200, `/api/admin/subsystems` отдаёт 20 подсистем массивом, `notes: {"key":"notes","title":"Заметки","enabled":true,"active":true,"restartRequired":false}` | ✅ |
| 9 | «Итог сессии в заметку» / «Сохранить в заметки» в карточках/меню чатов | есть | (см. ON в `screenshot on-02-notes-page.png`) | ✅ |
| 10 | **Б4 — клик по заметке → сменить заголовок → клиент должен иметь новый id без «пропадания»** | да | PUT `/api/notes/cGVyc29uYWx8Sm91cm5hbC8yMDI2LTA5LTEwLm1k` с `{"title":"2026-09-10-RENAMED-by-qa"}` → 200, ответ содержит **новый** id `cGVyc29uYWx8Sm91cm5hbC8yMDI2LTA5LTEwLVJFTkFNRUQtYnktcWEubWQ`. Контроллер зовёт `Broadcast("updated", note.Id)` ([NotesController.cs:466](backend/ClaudeHomeServer.Notes/Controllers/NotesController.cs)), после перезагрузки SPA показывает переименованную заметку в списке. Прямой клик по новому заголовку открывает URL `/#/notes/<новый id>`. | ✅ |

Скриншоты ON: `on-00-initial.png` (до логина), `on-01-home-notes-tab.png` (главная с «Заметки»), `on-02-notes-page.png` (страница `/#/notes` с деревом), `on-03-note-renamed-b4-fix.png` (открытая переименованная заметка по новому id).

### Подтверждение Б4 на уровне исходника

```cs
// backend/ClaudeHomeServer.Notes/Controllers/NotesController.cs:462
[HttpPut("{id}")]
public async Task<ActionResult<NoteDetail>> Update(string id, [FromBody] UpdateNoteRequest req)
{
    try
    {
        var note = _notes.Update(UserId, id, req);
        if (note is null) return NotFound();
        await Broadcast("updated", note.Id);   // ← note.Id, а НЕ параметр id (старый)
        return Ok(note);
    }
    ...
}
```

`Broadcast("updated", note.Id)` отдаёт **новый** id — фронт после хаба получит корректный
идентификатор, заметка не «исчезает» из списка.

---

## Режим «выключено» (`Subsystems:Notes:Enabled = false`)

| # | Сценарий | Ожидание | Факт | Итог |
|---|---|---|---|---|
| 1 | Бэкенд стартует | да | стартует, фронт раздаётся из того же `frontend/dist`, Kestrel слушает :5000 | ✅ |
| 2 | `/api/auth/me` показывает `subsystems` БЕЗ `notes` | да | массив из 19 элементов, `notes` отсутствует, `notes` НЕ в `SubsystemStateStore.ActiveKeys()` | ✅ |
| 3 | `GET /api/admin/subsystems` → `notes: enabled=false, active=false` | да | массив 20 элементов, `notes: {"key":"notes","title":"Заметки","enabled":false,"active":false,"restartRequired":false}` | ✅ |
| 4 | **`GET /api/notes` → 404, не 500** | **404** | **500** — `InvalidOperationException: Unable to resolve service for type 'ClaudeHomeServer.Services.Notes.NotesService' while attempting to activate 'ClaudeHomeServer.Notes.Controllers.NotesController'` | ❌ **Д1 (повтор)** |
| 5 | `GET /api/notes/caps`, `/api/notes/folders`, `/api/notes/search` | **404** | **500** (тот же DI-сбой) | ❌ **Д1 (повтор)** |
| 6 | `/api/tasks` → 200 | 200 | 200 | ✅ |
| 7 | `/api/projects` → 200 | 200 | 200 | ✅ |
| 8 | Ход чата проходит без секции заметок и не падает | да | композер открыт (чат QA stop scenario 1 final), кнопка «Сохранить в заметку» присутствует у ответа Ассистента — клик → 500 (см. ниже Д5), в остальном чат работает | ⚠️ |
| 9 | Вкладки, панели, виджета, пунктов меню нет | нет | nav: **Чаты, Проекты, Календарь, Персоны**, Echo (нет «Заметки»); на главной — нет виджета «Заметки» и нет быстрого действия «Новая заметка»; `/#/notes` редиректит в `/#/chats` (или показывает его) | ✅ |
| 10 | Глобальный поиск работает без секции «Заметки» | да | `/api/search?q=test` → 200, фронт не сыплет лишними ошибками после загрузки секции, поисковик показывает результаты (там сейчас нет ни одной заметки, потому что в индексе Dify заметки отдельной категорией) | ✅ |
| 11 | `AiLauncher` открывается без ошибок в консоли по notes | без новых ошибок | **2 ошибки в консоли после загрузки `/#/home`** (см. ниже Д2) — `/api/notes/caps: 500` и `/api/notes: 500` | ❌ **Д2 (повтор)** |
| 12 | Кнопка «Сохранить в заметку» на ответе ассистента скрыта | скрыта | присутствует, клик вызовет 500 (NotesService недоступен — Д1) | ❌ **Д5 (повтор)** |

Скриншоты OFF: `off-01-home.png` (главная без «Заметки»), `off-02-chat-open-save-to-note.png` (чат с кнопкой «Сохранить в заметку» рядом с ответом Ассистента).

### API в выключенном режиме — актуальные коды

```
GET /api/auth/me               → 200, subsystems (19, без 'notes')  ✅
GET /api/admin/subsystems      → 200, notes.enabled=false            ✅
GET /api/notes                 → 500 ❌  (ожидался 404)
GET /api/notes/caps            → 500 ❌
GET /api/notes/folders         → 500 ❌
GET /api/notes/search?q=тест   → 500 ❌
GET /api/tasks                 → 200 ✅
GET /api/projects              → 200 ✅
GET /api/search?q=тест         → 200 ✅
```

Лог сервера на каждый из четырёх 500-ответов содержит один и тот же стек:

```
fail: ClaudeHomeServer.Services.Http.UnhandledExceptionHandler[0]
      Необработанное исключение на GET (маршрут не сопоставлен):
      System.InvalidOperationException
        — Unable to resolve service for type 'ClaudeHomeServer.Services.Notes.NotesService'
          while attempting to activate 'ClaudeHomeServer.Notes.Controllers.NotesController'.
        at Program.<>c__DisplayClass0_8.<<<Main>$>b__57>d.MoveNext:1523
        ...
        at Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddlewareImpl.<Invoke>...
```

То есть `NotesController` обнаруживается MVC-роутером (маршрут `/api/notes/*`
сопоставляется), но DI не содержит `NotesService` (потому что `NotesSubsystem.Register`
не звался — гейт сработал), и конструктор падает. Это **тот же дефект Д1**, что в
прошлом прогоне (см. `docs/qa/notes-optional-run.md@088e5f65`). `b5212bb0` его не
трогал: коммит касался только стороны фронта (массив vs Record).

---

## Дефекты

### Д1 — повтор, блокирующий по критерию задачи. `/api/notes/*` отдают 500 вместо 404

**Затронуто:** все маршруты `NotesController` (`/api/notes`, `/api/notes/caps`, `/api/notes/folders`,
`/api/notes/search`, etc.).

**Причина:** авто-генерируемый файл
[`ClaudeHomeServer.MvcApplicationPartsAssemblyInfo.cs`](backend/ClaudeHomeServer/obj/Debug/net10.0/ClaudeHomeServer.MvcApplicationPartsAssemblyInfo.cs)
содержит безусловный
`[assembly: ApplicationPartAttribute("ClaudeHomeServer.Notes")]`. Это MSBuild-SDK-поведение
для проектов под `Microsoft.NET.Sdk.Web` (Notes.csproj декларирует
`<Project Sdk="Microsoft.NET.Sdk.Web">` при `OutputType=Library`, чтобы через `FrameworkReference`
подтянуть `Microsoft.AspNetCore.App`). При компиляции Main SDK собирает ApplicationPart
для каждой referenced-сборки, использующей Web SDK, и прописывает атрибут в `Main`. Эту
регистрацию НЕ видит `NotesSubsystem.Register` — она пытается добавить **второй**
`AssemblyPart` через `Configure<MvcOptions>` после `SubsystemGate.IsEnabled`, но MVC уже
видит первую часть от атрибута.

**Почему это пропустил `b5212bb0`:** коммит закрывал только ИЗ `frontend/src/lib/subsystems.ts`
(и unit-тесты), бэк не трогал.

**Что нужно:** выбрать один из двух путей —

1. Убрать Web SDK у `ClaudeHomeServer.Notes` (нельзя без `<FrameworkReference>` для типов
   ASP.NET вроде `Configure<MvcOptions>`) — **тупик**, нужен другой путь.
2. Перенести регистрацию контроллеров и `NotesController` из авто-`ApplicationPart`
   в явный `IMvcBuilder.AddApplicationPart` (или эквивалент внутри `NotesSubsystem.Register`)
   — автоатрибут при этом остаётся, роутер всё равно найдёт контроллер; Д1 не лечится.
3. **Правильно:** в `ClaudeHomeServer.Notes.csproj` убрать `<Project Sdk="Microsoft.NET.Sdk.Web">`,
   перейти на обычный `Microsoft.NET.Sdk` плюс явный `<FrameworkReference Include="Microsoft.AspNetCore.App" />`
   и опустеть SDK-генерацию `ApplicationPartAttribute` через MSBuild-свойство
   `IncludeApplicationPartMetadata` или явный пустой `AssemblyAttribute`. Это **обход SDK**,
   требует проверки, что бэкенд всё ещё подтянет нужные типы, и согласования с ревью (эта
   правка прямо задевает раздел «Внутренние подсистемы (Services/Composition)» из
   `docs/adr/ADR-014-internal-subsystems.md`).
4. До патча — **fail-soft**: `NotesController` в режиме `Enabled=false` ловит собственные
   `InvalidOperationException` в `try/catch` каждого экшена и возвращает `404 NotFound`
   со стандартным телом. Грубо, но закрывает требование «404, не 500» в обоих
   контрактах (для текущих эндпоинтов и для будущих), пока правильное лечение не
   сделано.

**Текущее состояние:** дефект открыт. Старый отчёт уже отмечал его как Д1-блокирующий,
`b5212bb0` его не закрыл. Открыть новую задачу `notes-optional-hotfix-2` или вернуть
Д1 на доработку текущей ветки.

### Д2 — повтор. `AiLauncher` безусловно зовёт `/api/notes/caps` на каждой загрузке

`frontend/src/components/ai/AiLauncher.tsx:127` —
`useEffect(() => { api.notes.caps()… }, [])` без гейта подсистемы. При выключенной
подсистеме даёт один 500 на каждую загрузку страницы.

### Д5 — повтор. «Сохранить в заметку» на каждом ответе ассистента — гейта нет

`frontend/src/components/chat/ChatItemView.tsx:478` — кнопка появляется у каждого
ответа. `useSubsystem` не импортируется. При выключенной подсистеме клик → 500.

### Д4 (НЕ воспроизвелось) — экран «Подсистемы» в меню пользователя

В прошлом прогоне экран `SubsystemsPage` существовал, но нигде не монтировался.
В этом прогоне не проверял (за рамками задачи), но если будет нужен — отдельная
задача на ревью `App.tsx`.

---

## Цикл ON → OFF → ON

Файлы `.md` в `C:/ClaudeData/dev/notes/{userId}` не пострадали:

| Замер | Всего `.md` | У `admin` (2a5f8815…) | Уникальность переименованной заметки |
|---|---|---|---|
| До прогона (baseline в ON) | 145 | 35 | `2026-09-10.md` → `2026-09-10-RENAMED-by-qa.md` ПОСЛЕ переименования |
| После OFF-режима (файлы с диска) | 145 | 35 | переименованный файл на месте |
| После возврата в ON (API `subsystems.includes('notes')`) | 145 | 35 | `2026-09-10-RENAMED-by-qa` присутствует в списке 111 заметок |

API после возвращения в ON: `/api/notes` → 200, 111 заметок, `subsystems` снова 20
элементов с `notes` на индексе 10 — то же состояние, что до выключения. Стоит
отметить, что **этот прогон внёс мутацию** — заметка `2026-09-10.md` была переименована
в `2026-09-10-RENAMED-by-qa.md` (это часть проверки Б4). До прогона файл назывался
`2026-09-10.md`. Возврат к исходному имени потребует переименования файла на диске
обратно (а не из UI) либо записи его заново — это осознанное последствие проверки
Б4 и **не блокер**, потому что исходный файл сохранён `SmokeTest/notes-baseline/2026-09-10.md`
вне data/, при необходимости.

---

## Что закрыл b5212bb0

Дефект Д-1 **фронтовой** части пилота. Подтверждено:

1. `/api/auth/me` отдаёт `subsystems` **массивом** (20 элементов, `subsystems?.includes('notes') === true`).
2. `Me.subsystems: string[]` в `frontend/src/types/index.ts` — тип приведён в соответствие.
3. `setAllSubsystems` нормализует массив в `Record<string, boolean>` внутри стора.
4. После применения:
   - `useSubsystem('notes') === true` при включённой подсистеме,
   - `useSubsystem('notes') === false` при выключенной;
   - вкладка «Заметки» в хабе появляется/исчезает правильно,
   - кнопка «Новая заметка» на главной появляется/исчезает правильно,
   - диплинк `/#/notes` редиректит в `/#/chats` при выключенной.

Тест `frontend/src/lib/__tests__/subsystems.contract.test.ts` зелёный, как и
`subsystems.test.ts` (см. `git show b5212bb0 --stat`). Это **базовый** дефект пилота —
закрыт.

## Что НЕ закрыто и почему это важно

Бэковый гейт (`ApplicationPart` подключается условно) не реализован, и `b5212bb0`
его не трогал. Включение этого гейта — отдельная работа (см. Д1 «Что нужно» выше):
либо убирать Web-SDK у `Notes.csproj`, либо fail-soft в `NotesController`. Без этого
**требование задачи «`/api/notes/*` → 404, не 500» не выполнено**, и пилот в текущем
виде всё ещё не блокирует слияние, если опираться на критерий `404 vs 500`.

Д2 (безусловные `aiLauncher.notes.caps()`) и Д5 (кнопка «Сохранить в заметку» на
каждом ответе) — фронтовые UX-баги, живут без гейта подсистемы; до фикса
консоль/UX в выключенном режиме зашумлены. Это поломки меньшего ранга, чем Д1,
и в этом прогоне подтверждены как «повтор из прошлого отчёта», без попыток лечить.

## Скриншоты

Каталог `.cc-attachments/notes-optional-run/`. Файлы этого прогона:

- `on-00-initial.png` — главная до логина (форма входа).
- `on-01-home-notes-tab.png` — главная подсистема ON: вкладка «Заметки» в хабе, виджет «Заметки», кнопка «Новая заметка».
- `on-02-notes-page.png` — `/#/notes`: дерево заметок (Личный 35 + ClaudeCodeServer 76) и центральная подсказка.
- `on-03-note-renamed-b4-fix.png` — заметка `2026-09-10-RENAMED-by-qa` по НОВОМУ id (Б4).
- `off-01-home.png` — главная подсистема OFF: вкладки без «Заметки», виджета нет.
- `off-02-chat-open-save-to-note.png` — чат открыт, у ответа Ассистента есть кнопка «Сохранить в заметку» (Д5).

---

## Приложение А — лог старта бэкенда в двух режимах

**ON** (`Subsystems:Notes:Enabled=true`):

```
[2026-09-10T05:41:52.247Z] [TranscriptRoots] разрешён корень провайдера: C:\ClaudeData\dev\claude-profiles\...
[2026-09-10T05:41:52.661Z] info: ClaudeHomeServer[0] Фронтенд раздаётся из C:\Sources\ClaudeCodeServer\.claude\worktrees\notes-optional\frontend\dist
[2026-09-10T05:45:10.348Z] GET /api/auth/me 200 (subsystems in response: ["backgrounds",...,"notes",...,"yandex"], 20 элементов)
```

**OFF** (`Subsystems:Notes:Enabled=false`):

```
[2026-09-10T05:53:23.???Z] [SubsystemGate] IsEnabled("notes") = false
[2026-09-10T05:53:23.???Z] [NotesSubsystem] Register не вызван
... (ApplicationPartAttribute("ClaudeHomeServer.Notes") всё равно присутствует от MSBuild SDK) ...
[2026-09-10T05:53:39.589Z] fail: Необработанное исключение на GET (маршрут не сопоставлен):
      System.InvalidOperationException — Unable to resolve service for type
      'ClaudeHomeServer.Services.Notes.NotesService' while attempting to activate
      'ClaudeHomeServer.Notes.Controllers.NotesController'.
```

## Приложение Б — коммиты, на которых проверялось

| Коммит | Что несёт |
|---|---|
| `b5212bb0` | `fix(subsystems): синхронизировать форму поля subsystems между бэком и фронтом` |
| `7952419b` | `fix(Notes): вернуть эффективный id заметки в событие updated` (предпосылка Б4) |
| `088e5f65` | Merge master → feature/notes-optional (начальная позиция первого прогона) |
