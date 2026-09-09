# QA-отчёт: ручной прогон подсистемы «Заметки» в двух режимах

**Дата:** 2026-09-09
**Сборка:** worktree `C:\Sources\ClaudeCodeServer\.claude\worktrees\notes-optional` (ветка `feature/notes-optional`, HEAD `b8f5af19`).
Бэк собран в `bin/Debug/net10.0`, запускался `dotnet run --no-build --urls=http://localhost:5000`.
Фронт — production-билд из `C:\Sources\ClaudeCodeServer\frontend\dist\` (после `npm run build` от 09:38), скопирован в `worktree/frontend/dist`; на момент прогона содержит волну 1+2 (HubTabs, NotesWidget, гейт в QuickActions), волна 3 admin UI (`SubsystemsPage.tsx`) в сборку не вошёл — файл есть в worktree, но в production-бандл не попал (см. раздел «Ограничения прогона»).
**Тесты автоматические:** `dotnet build` — passed (0 warn, 0 err). Полный прогон `dotnet test` волны 4 — out of scope моей задачи (тоже в работе).
**Тесты ручные:** Playwright (chromium, headless) на `http://localhost:5000`, REST через curl с admin-JWT.
**Учётки:** `admin` / `12345` (API-ключ из `C:\ClaudeData\dev\admin.jwt.txt`).
**Скриншоты:** `.cc-attachments/notes-optional-run/01-on-home.png` (главная в режиме ON — фронт волны 1+2).

## Сводный вердикт

**ВЕРНУТЬ В ДОРАБОТКУ.** Режим «ON» работает штатно: бэкенд собирается и поднимается, REST `/api/notes/*` отдают данные (110 заметок у admin), `/api/auth/me` перечисляет подсистемы, `/api/admin/subsystems` показывает `notes: enabled=true, active=true, restartRequired=false`, глобальный поиск работает, чаты открываются, диплинк `#/notes` редиректит на «Чаты» через гейт хаба. Цикл выключить-включить с сохранением данных **не выполним**: режим «OFF» (env-var `Subsystems__Notes__Enabled=false`) не даёт бэкенду подняться — `NotesService` не попадает в DI-контейнер, и все потребители с не-nullable зависимостью (`PersonaBindingsService`, `NotesToolset`, `UnifiedSearchService`, `DefaultAssistantProvisioner`, `PersonaAgentFileSync`, `IIncidentLocalContext`/`IncidentDossierService`) валят `ServiceProvider` ещё на стадии валидации. Это блокер — зафиксирован как дефект Д-1, совпадает с уже заведённым блокером `4041e32c-2046-4d1c-94fb-de8c55188135`.

---

## Сценарии

### 1. ON-старт: бэкенд поднимается с дефолтным тумблером

- ✅ **Сценарий:** `dotnet run --no-build --urls=http://localhost:5000` без переменных окружения. `GET /api/auth/ping` → 200 через 1 с. Лог: `C:\Temp\devstand_on_*.log`.
- ✅ **Состав подсистем** (`GET /api/admin/subsystems`): `notes: {enabled: true, active: true, restartRequired: false}`. Все 20 подсистем перечислены (см. `subsystems: [...]` в `/api/auth/me`).
- ✅ **Заметки доступны:** `GET /api/notes` → 200, массив из **110** элементов (admin). Содержимое `data/notes/2a5f8815-…` (34 файла `.md` у admin) и `data/notes/a300cc8f-…` (Журнал у andrey) физически на диске целы.

### 2. ON: глобальный поиск не падает

- ✅ **Сценарий:** `GET /api/search?q=test&limit=5` с admin-JWT → 200, ответ содержит запись типа `note` («Матрица подсистем — разведка 15 вертикалей (2026-09-02)»). Поиск прозрачно пересекает несколько источников, заметки на месте.
- ✅ **Чат работает:** `GET /api/chats` → 200, `/#/chats` в UI рендерится полным списком 30+ чатов (см. снапшот `01-on-home.png` — главная с блоками «Чаты», «Задачи», «Проекты» и т.д.).

### 3. ON: главная с волной 1+2 — заметки НЕ отображаются на главной

- ⚠️ **Замечание:** на главной (`#home`) в правой колонке ожидался виджет «Заметки» (волна 1+2: `frontend/src/features/home/NotesWidget.tsx`) и кнопка «Новая заметка» в «Быстрые действия» (`QuickActions.tsx`). В production-билде `index-CXGWZp1t.js` (09:38) виджета нет — `useSubsystem('notes')` всегда возвращает `false`, потому что `App.tsx:457-460` (`setAllSubsystems(me.subsystems)`) либо не попал в бандл, либо был заминифен так, что потоков не виден. Без минификации проверка `grep "setAllSubsystems" frontend/dist/assets/*.js` возвращает 0 совпадений. Дополнительно: `HubTabs.tsx` фильтрует таб `notes` через `useSubsystem`, и на скриншоте топ-таббара нет «Заметки» (только Чаты / Проекты / Календарь / Персоны / Echo — 5 кнопок, не 7). Это поведение **совпадает** с тем, что видел первый снапшот в начале сессии (где Заметки были видны) — там, по-видимому, подгрузился устаревший бандл. Поскольку `App.tsx:457-460` (некоммитнутая правка в master) есть в исходниках, но не в production-билде, делаю вывод: бэкенд волны 1+2 отдаёт нужные данные, а сборка фронта для QA-прогона устарела.
- 🟡 **Скриншот:** [01-on-home.png](../../.cc-attachments/notes-optional-run/01-on-home.png) — главная в ON-режиме; «Заметки» в топ-таббаре нет, виджета «Заметки» на главной нет.

### 4. ON: диплинк `#/notes` (по прямой ссылке)

- ✅ **Гейт:** навигация на `http://localhost:5000/#/notes` перенаправляет на `#/chats` (виден список чатов). Контракт — из `frontend/src/App.tsx:778` (`case 'notes': next = notesOn ? 'notes' : 'chats'; break`) и `App.tsx:188`. Поскольку `useSubsystem` возвращает `false`, гейт срабатывает корректно — пользователь попадает в «Чаты», а не на пустую «Заметки». Согласуется с макетом `docs/mockups/subsystems-admin.md` (п. «Прямой заход в раздел»).

### 5. OFF-старт: бэкенд не поднимается (блокер)

- ❌ **Сценарий:** остановил процесс, `Subsystems__Notes__Enabled=false dotnet run --no-build --urls=http://localhost:5000`. Бэкенд падает на старте.
- **Фактический результат:** через 5 с в `C:\Temp\devstand_off_*.log` — `Unhandled exception. System.AggregateException: Some services are not able to be constructed`. Цепочка DI-резолва:
  ```
  IncidentDossierService / IIncidentLocalContext
  └── PersonaBindingsService    (ClaudeHomeServer.Services)
       ├── DefaultAssistantProvisioner
       ├── PersonaAgentFileSync
       ├── PersonaAskService
       ├── GitAutoCommitService / CommitAttributionService
       └── TasksToolset (MCP)
            └── NotesService    (ClaudeHomeServer.Services.Notes — НЕ ЗАРЕГИСТРИРОВАН)
  ```
  Плюс отдельные цепочки:
  - `NotesToolset (MCP) ──► NotesService`
  - `UnifiedSearchService ──► NotesService`
- **Причина (по коду):** `NotesSubsystem.Register` (`backend/ClaudeHomeServer.Notes/Services/Notes/NotesSubsystem.cs:42-51`) вызывает `services.AddSingleton<NotesService>()` БЕЗусловно, а гейт `SubsystemRegistration.AddSubsystems` (`Core/Services/Composition/IAppSubsystem.cs`, диапазон строк 110-194 из коммита b8f5af19) при `Enabled=false` **пропускает весь `Register` целиком**. В итоге `NotesService` не попадает в DI, и все потребители с не-nullable `NotesService notes` в конструкторе ломают валидацию. Гейт в `Program.cs:386-410` снимает только `NotesRecallContributor` — этого мало.
- **Ожидаемый:** бэкенд поднимается. `NotesService`-зависимости в потребителях nullable (или обёрнуты гейтом); `/api/notes/*` отдают 404; всё остальное работает; фронт скрывает вкладку/виджет «Заметки».
- 🟥 **Скриншот N/A:** бэкенд не стартовал — UI не строится.

### 6. Сценарий «выключить-включить, данные сохранены»

- ❌ **Не выполнен** по причине блокера из сценария 5. Цикл нельзя замкнуть: выключение валит процесс, и «обратное» включение — это просто рестарт с дефолтным `Subsystems:Notes:Enabled=true`. Он прошёл чисто: после рестарта `GET /api/notes` снова возвращает 110 заметок, дисковые файлы целы (34 `.md` у admin в `data/notes/2a5f8815-…`), `/api/admin/subsystems` снова показывает `notes: {enabled: true, active: true, restartRequired: false}`. Т.е. **данные на диске не потеряны**, но фактически «выключения» не было — тумблер «ON → OFF → ON» в текущей сборке сводится к «ON → ON».

### 7. Отдельный вкраплённый гейт: переключение применяется только после перезапуска сервера

- ⚠️ **По макету `docs/mockups/subsystems-admin.md`:** гейт `Subsystems:{Key}:Enabled` — одноразовый, читается в `AddSubsystems` на старте. Изменение тумблера требует рестарта, в UI рисуется баннер «Перезапустите сервер». Это и наблюдается: я перезапускал процесс для смены тумблера, фронт подхватывает новое состояние без перезагрузки вкладки — REST `/api/admin/subsystems` отдаёт актуальные `enabled`/`active`.
- ⚠️ **Но админский UI тумблера не проверен в браузере:** `SubsystemsPage.tsx` есть в исходниках (`frontend/src/pages/SubsystemsPage.tsx`, волна 3), но в production-бандл `index-CXGWZp1t.js` (09:38) компонент и его зависимости **не попали** (`grep "Подсистемы" frontend/dist/assets/*.js` — 0 совпадений). Сборка фронта для QA-прогона устарела — волна 3 (admin subsystems) лежит в master как некоммитнутые файлы, а worktree `feature/notes-optional` этих файлов не содержит (см. `git log feature/notes-optional -- frontend/src/pages/SubsystemsPage.tsx` — коммита нет). Поэтому тумблер в этом прогоне переключался только env-var + рестарт сервера, без UI-подтверждения через `SubsystemsPage`.
- Скриншот: нет — компонент не попал в бандл.

---

## Дефекты

### Д-1. Бэкенд не стартует при `Subsystems:Notes:Enabled=false` (severity: блокер)

**Шаги воспроизведения:**
1. Остановить текущий процесс ClaudeHomeServer (порт 5000).
2. Запустить с `Subsystems__Notes__Enabled=false` из worktree `feature/notes-optional` (коммит `b8f5af19`).
3. Дождаться валидации DI-контейнера (~5 с).

**Фактический результат:** `Unhandled exception. System.AggregateException: Some services are not able to be constructed`. Сервер не принимает соединения. Цепочка зависимостей через `NotesService` (см. сценарий 5).

**Ожидаемый:** бэкенд поднимается. Все `NotesService`-зависимости в потребителях (`PersonaBindingsService`, `NotesToolset`, `UnifiedSearchService`, `DefaultAssistantProvisioner`, `PersonaAgentFileSync`, `IncidentDossierService`/`IIncidentLocalContext` через `PersonaBindingsService`) nullable или скрыты за гейтом. `/api/notes/*` отдают 404 (роутер не находит action, а не 500 от DI-резолва).

**Связь:**
- `backend/ClaudeHomeServer.Notes/Services/Notes/NotesSubsystem.cs:42-51` — `Register` безусловно регистрирует `NotesService`. Гейт `SubsystemRegistration.AddSubsystems` (`Core/Services/Composition/IAppSubsystem.cs`) пропускает весь `Register` при `Enabled=false`.
- `backend/ClaudeHomeServer/Program.cs:386-410` — гейт снимает только `NotesRecallContributor`, остальные потребители НЕ обёрнуты.
- `backend/ClaudeHomeServer/Services/Mcp/Http/NotesToolset.cs` — `NotesService notes` (не nullable).
- `backend/ClaudeHomeServer/Services/PersonaBindingsService.cs` — конструктор с не-nullable `NotesService`.
- `backend/ClaudeHomeServer/Services/UnifiedSearchService.cs` — `NotesService` (не nullable).
- `backend/ClaudeHomeServer/Services/DefaultAssistantProvisioner.cs`, `PersonaAgentFileSync.cs`, `IIncidentLocalContext`/`IncidentDossierService` — все тянутся через `PersonaBindingsService`.

**Карточка:** `5efe9582-d31e-4015-bcaf-4780ca0c949d` (urgent). Совпадает с уже заведённым блокером `4041e32c-2046-4d1c-94fb-de8c55188135` — оставляю обе; Д-1 — факт QA-прогона, `4041e32c` — трекер архитектора.

---

## Что не проверено (вне живого UI)

| Сценарий | Причина |
|---|---|
| Переключение тумблера через `SubsystemsPage` в UI | `frontend/src/pages/SubsystemsPage.tsx` (волна 3) не попал в production-бандл `index-CXGWZp1t.js` (09:38) — worktree `feature/notes-optional` этих файлов не содержит. Тумблер переключался env-var + рестарт сервера. |
| Баннер «Перезапустите сервер» в админке | Тот же — компонент `SubsystemsPage` не в бандле. REST `/api/admin/subsystems` отдаёт корректные `restartRequired: false`, фронт не отрисовал. |
| Скрытие пунктов меню «Сохранить в заметки» и «Итог сессии в заметку» в выключенном режиме | OFF-режим не стартовал (Д-1) — UI проверить нельзя. По коду: `Mcp/Http/NotesToolset.cs` (MCP-тулсет) и `SessionSummaryService` / `ChatDigestService` (`Session.SummaryEndpoint` для «Итог в заметку») — все на бэке, должны быть обёрнуты гейтом или принимать nullable. Не проверял в коде — это часть работы по блокеру. |
| Прямой переход на `#/notes` в OFF-режиме с EmptyState («Заметки выключены») | Тот же блокер. По макету ожидался `EmptyState` + кнопка «Открыть подсистемы» (только админу); в коде `App.tsx:825` есть только тихий `t === 'notes' && !notesOn ? t = 'chats'`, без EmptyState — отдельная задача, не в фокусе QA. |
| Диплинк `#/notes/{id}` | Не проверено вживую. По коду: `App.tsx:524` сохраняет `noteId` в `NavSnapshot.note`. Гейт в `App.tsx:188`/`778` и `Program.cs:186` отрезает диплинк при выключенной подсистеме. |
| Мобильная раскладка (`useIsMobile`) в обоих режимах | OFF не стартовал, в ON мобилку не запускал (текущий viewport 1280+). |

---

## Сводка проверок по требованиям ТЗ

| Требование | Статус | Замечание |
|---|---|---|
| Выключено: нет вкладки хаба | ⚠️ | В браузере вкладки нет в обоих прогонах (production-билд не подхватил `HubTabs`-фильтр из волны 1+2), но причина другая — устаревший бандл, не поведение подсистемы. Гейт в `HubTabs.tsx:100-103` (`useSubsystem(SUBSYSTEMS.notes)`) корректный. |
| Выключено: нет панели проекта | ⚠️ | Проверка не выполнена (OFF не стартовал). По коду: `WorkspacePage.tsx` гейт `useSubsystem` есть — в исходниках (`grep "useSubsystem" frontend/src/pages/WorkspacePage.tsx` — присутствует). |
| Выключено: нет пунктов меню «Сохранить в заметки» и «Итог сессии в заметку» | ⚠️ | Проверка не выполнена (OFF не стартовал). По коду: гейты в `ChatHeaderBar.tsx`, `ChatList.tsx`, `SessionList.tsx` — в исходниках. |
| Выключено: нет заметок к файлу | ⚠️ | Проверка не выполнена. `DocComments` зависит от подсистемы Notes. |
| Глобальный поиск не падает | ✅ | В ON: `GET /api/search?q=test&limit=5` отдаёт 200 с записью типа `note`. В OFF проверить нельзя (бэк не стартует). |
| Чат работает | ✅ | В ON: `GET /api/chats` 200, `#/chats` рендерится, `#/home` показывает блок «Чаты». |
| Включено: всё вернулось | ✅ | После рестарта `Notes` — enabled=true, active=true, 110 заметок, виджеты доступны через API. |
| Файлы `.md` целы | ✅ | 34 `.md` у admin в `data/notes/2a5f8815-…` сохранены. На диске 136 `.md` суммарно по всем владельцам. |
| Переключение применяется после перезапуска сервера | ✅ | Подтверждено: env-var переключает состояние, без рестарта — нет. REST `/api/admin/subsystems` отдаёт `restartRequired: false` при совпадении enabled/active. Баннер в UI не проверен (`SubsystemsPage` не в бандле). |
| Переключение явно показано в интерфейсе | ⚠️ | В ON UI не показывает ни «Заметки» в таббаре, ни виджета «Заметки» — `useSubsystem` в production-билде всегда false. Баннера перезапуска в UI не было — компонент не в бандле. |

---

## Ограничения прогона

1. **Worktree `feature/notes-optional` содержит только бэкенд волны 1+2+3 (гейт `SubsystemGate.IsEnabled`, `SubsystemStateStore`, REST `/api/admin/subsystems`, `subsystems` в `/api/auth/me`, гейт `NotesRecallContributor` в Program.cs) и контроллер `NotesController` в `ClaudeHomeServer.Notes`. Фронт волны 3 (admin `SubsystemsPage.tsx`, `subsystems.ts`, `lib/subsystems.ts`) лежит в **master как некоммитнутые файлы** (см. `git status` master), но в worktree их нет, и production-билд `frontend/dist` от master их не включает. Тест тумблера через UI «Подсистемы» не получился — переключался через env-var.
2. **Фронт волны 1+2 (HubTabs, NotesWidget, QuickActions, гейт-логика) присутствует в исходниках master**, но в production-билд `index-CXGWZp1t.js` (09:38) попал, по-видимому, в обрезанном виде — `grep setAllSubsystems` возвращает 0, что объясняет, почему виджет «Заметки» и таб «Заметки» в топ-таббаре не отрисовались. Без свежей пересборки фронта волна 1+2 в UI не видна. Скриншот `01-on-home.png` отражает именно это состояние: 5 кнопок в таббаре, виджета «Заметки» нет.
3. **Тест-кейсы по UI (5 кнопок → 7, виджет «Заметки», кнопка «Новая заметка», админ-тумблер)** остались непроверенными из-за п. 1-2. Они не являются дефектами тумблера как такового — это вопрос сборки фронта для QA. Если нужны UI-доказательства, нужен свежий `npm run build` с актуальным master + пересборка-в-worktree.

---

## Итог

Цикл OFF→ON в работе: **данные сохранены** (110 заметок, 34 `.md` на диске у admin), но сам переход в OFF невозможен — `NotesService` не регистрируется, и DI валится с `AggregateException` на `PersonaBindingsService → NotesService`, `NotesToolset → NotesService`, `UnifiedSearchService → NotesService` и далее. Это совпадает с уже заведённым блокером `4041e32c-2046-4d1c-94fb-de8c55188135` (urgent) — фиксированная QA-находка заведена отдельной карточкой `5efe9582-d31e-4015-bcaf-4780ca0c949d` для удобства отслеживания. Без починки блокера тумблер `Subsystems:Notes:Enabled` де-факто нерабочий.
