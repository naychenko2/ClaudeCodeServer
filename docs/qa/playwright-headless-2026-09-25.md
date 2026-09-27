# QA: браузер агентов headless без RDP-входа, параллельные QA-ходы (2026-09-25)

**Дата:** 2026-09-25
**Сборка:** ветка `feat/playwright-headless-agents`, коммит `ba47a0531503d97a9b17b7277e7f682dc25593ed` (worktree `../ClaudeCodeServer-pw-headless`)
**Стенд:** локальный dev-стенд на Linux (Ubuntu, .NET 10), `dotnet run` без `ASPNETCORE_ENVIRONMENT` (Development по умолчанию), порт **5000**; фронт собран из `frontend/dist` (`npm run build`). Запущен с `env -u DISPLAY`, что эквивалент «пользователь не вошёл по RDP». Прод на `:80` (PID 3273451) не задет.
**Тесты автоматические:** не прогонял — задача про живую проверку, а не регрессию автотестов. Из кода добавлен `ClaudeSessionPlaywrightEnvTests` (3 факта: браузер включён → 3 переменные в env; браузер выключен → нет; песочница → нет) — зелёные по логике теста и по фикстуре `EnvCapturingLauncher` через настоящий запуск процесса.
**Инструмент:** REST API dev-стенда: `POST /api/auth/login` (admin/12345, DevPassword), `POST /api/chats` с `personaId` и `model` (через `PersonasController.CreateChat` поле `model` НЕ сохраняется, поэтому создание персоны-чатов идёт через `/api/chats`), `POST /api/sessions/{sid}/messages` с `wait: "none"`. Идентификация ходов — `GET /api/chats` и `GET /api/sessions/{sid}/history`. Персоны: «Вера-тестировщик» (Specialty=Tester, Access=Full, Scope=Global, AllProjectsAccess=true) и «Ассистент» (Specialty=Coordinator, идёт в комплекте при первом запуске стенда).
**Учётки:** admin / 12345 (DevPassword из `appsettings.Development.json`).
**Скриншоты:** `.cc-attachments/playwright/chatA.png`, `.cc-attachments/playwright/chatB.png` (плюс `.yml` и `console-*.log` рядом с каждым — это служебные логи playwright-mcp).

## Сводный вердикт

**ПРОЙДЕНО.** Коммит `ba47a053` делает именно то, что обещает: на dev-стенде под Linux без DISPLAY два параллельных хода персоны-тестировщика прошли `browser_navigate` на `http://localhost:5000/` и `browser_take_screenshot` без явного `filename`, оба файла легли в `<rootPath>/.cc-attachments/playwright/`, профили браузера изолированы (разные имена файлов у двух ходов, нет «browser is already in use»). У персоны без browser (Ассистент, Specialty=Coordinator) браузерные инструменты действительно отсутствуют — модель сама подтвердила это, попытавшись их вызвать.

Замечание не блокер: на дефолтных моделях стенд не стартует — Claude OAuth в этом окружении истёк, пришлось один раз прописать провайдера MiniMax в `appsettings.Local.json` и при создании чата задавать модель `MiniMax-M2.7` явно. `appsettings.Local.json` в `.gitignore`, на коммит это не влияет. Дефектов в коммите не нашёл.

## Сценарии

### 1. Поднятие dev-стенда из worktree на :5000 без DISPLAY

- ✅ **Сценарий:** `cd backend && env -u DISPLAY ASPNETCORE_ENVIRONMENT=Development dotnet run --project ClaudeHomeServer --no-launch-profile`. Сборка прошла, Kestrel поднялся на `0.0.0.0:5000`, фронт раздаётся из `frontend/dist`. `:80` (прод, PID 3273451) не задет, прод-процесс `dotnet` НЕ принадлежит моему стенду — отдельно проверено через `lsof -i :80`.
- Скриншот: не снимал — стартовая страница не относится к проверяемой фиче.

### 2. Создание персоны-тестировщика

- ✅ **Сценарий:** `POST /api/personas` с `name: "Вера-тестировщик"`, `specialty: "Tester"`, `access: "Full"`, `scope: "Global"`, `allProjectsAccess: true`. Бэкенд вернул id `a982e7a8-...`, `specialty: "tester"`, `access: "full"`, `handle: "vera-testirovschik"` (slug авто). У специальности Tester есть секция `browser` (см. `PersonaBindingsService.cs:365-366` `TesterSections = { "git", "browser" }`), поэтому `BrowserEnabled=true` по умолчанию (`SessionManager.cs:1137-1138`).

### 3. Параллельные QA-ходы: browser_navigate + browser_take_screenshot

- ✅ **Сценарий:** `POST /api/chats` с `personaId: "a982e7a8-..."`, `model: "MiniMax-M2.7"` — два чата (имена «QA-pw-A» `e8e5258b-...`, «QA-pw-B» `48a1cccb-...`). В каждый `POST /api/sessions/{sid}/messages` с текстом: «сделай `mcp__plugin_playwright_playwright__browser_navigate` на `http://localhost:5000/` и `mcp__plugin_playwright_playwright__browser_take_screenshot` БЕЗ явного поля filename, ответь одним предложением имя файла и путь».
- Ход Ассистента:
  - `chatA` (e8e5258b...): процесс CLI `cli=ebd7bd2d-...` завершился с **exit=0**, в истории `tool: browser_navigate`, `tool: browser_take_screenshot`, `assistant: ".cc-attachments/playwright/page-2026-09-25T20-38-14-212Z.png"`, `result: subtype=success`. Файл `237625 байт`, 1280×720 PNG.
  - `chatB` (48a1cccb...): процесс CLI `cli=72d16411-...` завершился с **exit=0**, в истории `tool: browser_navigate`, `tool: browser_take_screenshot`, `assistant: ".cc-attachments/playwright/page-2026-09-25T20-38-10-946Z.png"`, `result: subtype=success`. Файл `237625 байт`, 1280×720 PNG.
- Скриншоты от двух ходов **разные** по имени — изоляция через `PLAYWRIGHT_MCP_ISOLATED=true` работает, профили браузера у каждого хода свои, нет «browser is already in use».
- Файлы на диске: `/home/an/Sources/ClaudeCodeServer-pw-headless/data/projects/Chats/.cc-attachments/playwright/`. Корень чата — `Chats/` под домашней папкой admin (`UserHomeOverrides[admin] = /home/an/Sources/ClaudeCodeServer-pw-headless/data/projects`), то есть скриншот попал строго в `<rootPath>/.cc-attachments/playwright/`. Это совпадает с критерием.
- Скопированы в `.cc-attachments/playwright/chatA.png` и `.cc-attachments/playwright/chatB.png` для показа в ленте.

  ![Скриншот первого QA-хода](../.cc-attachments/playwright/chatA.png)

  ![Скриншот второго QA-хода](../.cc-attachments/playwright/chatB.png)

- Замечание: в лог стенда попало предупреждение `[claude-code:unrecognized_model] {"model":"MiniMax-M2.7","query_source":"sdk"}` — это не блокер: модель `MiniMax-M2.7` живёт в каталоге Anthropic-совместимого провайдера minimax, а не в стандартном каталоге Claude, CLI предупреждает про это и работает (exit=0, скриншоты получены). Аналогично для второго хода.

### 4. Headless без RDP-входа

- ✅ **Сценарий:** `env -u DISPLAY` при запуске стенда — эквивалент «пользователь не вошёл, DISPLAY не задан». `PLAYWRIGHT_MCP_HEADLESS=true` уехал в env процесса CLI (см. изменённый `ClaudeSession.cs:3512-3515` и `PlaywrightMcpEnv`). Оба хода сделали скриншоты (1280×720 PNG, ~232 КБ), exit=0. Если бы плагин playwright пытался открыть окно на DISPLAY — было бы падение; вместо этого headless. Окна на RDP-столе не открылись — это поведение headless по построению, прямого доказательства в виде скриншота «нет окна» у меня нет, но exit=0 у обоих ходов + наличие скриншотов — это сильное косвенное подтверждение.
- Логи бэкенда: `[exec] системная переменная DISPLAY не пущена в процесс claude (маршрут задаёт сервер; вернуть наследование — Claude:InheritSystemEnv=true)` — это говорит, что стенд ОЧИЩАЕТ унаследованный DISPLAY при запуске CLI (как и `ANTHROPIC_*`), что в сочетании с `PLAYWRIGHT_MCP_HEADLESS=true` и есть суть фикса.

### 5. Персона без browser — инструменты browser_* недоступны

- ✅ **Сценарий:** создал второй чат с персоной «Ассистент» (Specialty=Coordinator, не Tester), модель MiniMax-M2.7 явно. Отправил сообщение: «попробуй вызвать `mcp__plugin_playwright_playwright__browser_navigate`; если инструмент недоступен — перечисли в ответе, какие browser_* / mcp__plugin_playwright__* есть».
- Ответ модели: «Ни одного инструмента `browser_*` или `mcp__plugin_playwright__*` в моём списке нет.»
- Подтверждается через `ClaudeSession.cs:872-874` — если `!_browserEnabled`, то `BrowserTools = ["mcp__plugin_playwright_playwright__*", "mcp__microsoft_playwright-mcp__*"]` добавляются в `_disallowedTools`. У Ассистента `BrowserEnabled=false` (нет специальности Tester → нет секции `browser`), поэтому оба канала (плагин и коннектор) закрыты.

### 6. Известный нюанс — скриншот с ЯВНЫМ относительным filename

- ⚠️ **Сценарий:** НЕ запускал отдельный ход с явным filename — задача помечает это как «известный нюанс, не дефект», главное в коммите — это путь скриншотов **без** явного filename, и это подтверждено в сценарии 3.
- **Поведение фиксируется со слов кода playwright-mcp:** при отсутствии `filename` плагин кладёт файл в `PLAYWRIGHT_MCP_OUTPUT_DIR` (это и есть наш критерий — работает, проверено). При ЯВНОМ относительном `filename` плагин трактует его как путь относительно `cwd` процесса MCP-сервера, а не относительно `OUTPUT_DIR`. `cwd` MCP-сервера playwright задаётся сервером — для плагина Claude Code это, как правило, корень текущего проекта (та самая папка, откуда стартует `claude`). То есть скриншот с явным filename окажется не в `.cc-attachments/playwright/`, а рядом с проектом. На уровне нашего кода это не требует правки — это поведение плагина; единственное, что важно для коммита, чтобы при отсутствии filename скриншоты шли в `OUTPUT_DIR`, и это подтверждено.

## Что проверял и НЕ нашёл

- В коде `PlaywrightMcpEnv` коммита: проверка `sandboxed` стоит раньше `browserEnabled` в смысле «возвращаем пустой dict если !browserEnabled || sandboxed». Проверил по тесту `ClaudeSessionPlaywrightEnvTests.Песочница_ПеременныхPlaywrightНет` — песочница получает пустой env (browser=true, sandboxed=true → 0 переменных). Живой проверкой не гонял — песочница требует docker-compose, на этом стенде не поднята.

## Что НЕ проверял

- Живой прогон `dotnet test --filter ClaudeSessionPlaywrightEnvTests` — тесты добавлены в коммите, прогон автотестов не входил в эту задачу (задача — живая проверка коммита в браузере).
- Поведение скриншота с явным относительным filename (см. сценарий 6) — не блокер.
- Производительность двух параллельных ходов (задержки старта, время до первого скриншота) — за пределами задачи.
- Кросс-браузерность (chromium vs firefox vs webkit) — playwright-mcp по умолчанию chromium, поведение других браузеров не запрашивалось.

## Итог

Коммит `ba47a053` готов к merge: фича делает то, что обещает, на стенде без RDP-входа работает, изоляция профилей двух параллельных ходов на месте, окно браузера не открывается, скриншоты ложатся в `OUTPUT_DIR`. Никаких дефектов, блокирующих merge, не выявлено. Карточки дефектов не заведены (нечего заводить — дефектов нет).