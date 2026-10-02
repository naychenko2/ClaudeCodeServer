# Спайк: браузерная рука для локальных проектов (2026-09-28)

Задача трекера `e836f9ca`. Сейчас руки (ADR-016 §7, `backend/HandsBridge`) видят Chrome только
через UIA, а гейт `HandsPolicy.ForbiddenArgumentFragments` запрещает `remote-debugging` и
`load-extension` в `app`. Спайк сравнивает два пути: изолированный профиль Chrome на проект
под управлением агента и настоящий Chrome владельца. Код в прод не вносился.

**Где что мерено.** Все цифры ниже сняты на Linux-машине разработки: Chrome 153.0.8010.52,
headless, Node 22.22, .NET 10.0.112. Размеры архивов посчитаны по артефактам win-x64 (кросс-publish
и загрузка с nodejs.org), поэтому они точные. Время старта на Windows будет другим (Defender,
сканирование `node.exe` при первом запуске, headed-окно), так что времена здесь ориентировочные.
Пункты с пометкой **[Windows]** требуют живой машины, их прогоняет владелец (чек-лист в конце).

## Итог коротко

- **Рекомендация: изолированный профиль, свой CDP-клиент на C# внутри `HandsBridge`.** Архив
  не растёт (десятки КБ кода против +38–39 МБ у обоих Playwright-вариантов), Node на устройстве
  не нужен, гейт остаётся один (`HandsPolicy`), процесс новый не появляется, маркер тот же. Цена —
  писать инструменты самим: оценочно 1,5–3 тыс. строк. «Голый» CDP по pipe даёт первую страницу
  с AX-деревом за 0,23 с против 0,6–0,7 с у Playwright.
- **Если нужен готовый набор инструментов сразу**, запасной вариант — `Microsoft.Playwright` .NET
  внутри того же моста. Набор инструментов тоже пишем свои, поверх готового API
  (`AriaSnapshotMode.Ai` даёт снимок со ссылками). +38,4 МБ к архиву (внутри `node.exe`), гейт
  один. `@playwright/mcp` по размеру такой же (+39,0 МБ), но это отдельный процесс со своими 25
  инструментами без нашего гейта, среди них `browser_run_code_unsafe` («RCE-equivalent» по
  собственному описанию).
- **Встроенный режим Chrome у Claude Code CLI (`--chrome`) нам недоступен по построению.** CLI на
  устройстве авторизуется `ANTHROPIC_AUTH_TOKEN` шлюза, а интеграция требует claude.ai-подписку и
  OAuth-токен со scope шире `user:inference`. Трафик расширения к тому же идёт через облачный мост
  Anthropic `bridge.claudeusercontent.com` мимо нашего egress.
- **Режим расширения Playwright MCP** технически совместим с нашими учётками (токен расширения
  локальный, claude.ai не нужен), умеет ограничиваться группой вкладок. Но он снова требует Node
  на устройстве и отдельный процесс без нашего гейта, а модели отдаёт залогиненные сессии
  владельца. Вернуться к нему стоит, только если владелец осознанно захочет «работать в моём
  браузере».

## 1. Изолированный профиль: три реализации

Общая схема для всех трёх: установленный Chrome, `--user-data-dir` = постоянный профиль проекта
в данных агента (не в папке проекта: там git), управление по `--remote-debugging-pipe`,
порта нет. Запуск только агентом в Job хода по маркеру (как `DeviceExecPlaceholders.Hands`).

### Таблица

| | `@playwright/mcp` + Node | `Microsoft.Playwright` .NET | Свой CDP-клиент на C# |
|---|---|---|---|
| Версия в замере | 0.0.82 (playwright-core 1.64.0-alpha) | 1.63.0 | прототип на 40 строк (`cdp_pipe.mjs`) |
| Прирост распакованного каталога win-x64 | **+112,3 МБ**: `node.exe` v24.21.0 93,6 МБ + `node_modules` 18,7 МБ | **+108,3 МБ**: `.playwright/node/win32_x64` 93,6 МБ + `.playwright/package` 13,5 МБ + dll | **≈0** (код внутри `HandsBridge.dll`) |
| Прирост zip (`-9`) | **+39,0 МБ** (34,8 + 4,2) | **+38,4 МБ** | **≈0** |
| Относительно архива агента (54,3 МБ zip / 124,9 МБ распак., замер этого же дня) | +72 % | +71 % | ≈0 % |
| Node на устройстве | нужен (свой `node.exe` в каталоге агента) | нужен, но внутри пакета (`node.exe` драйвера) | не нужен |
| Холодный старт, spawn → ответ `initialize` / `tools/list` | 343–369 / 350–376 мс | — (библиотека) | — |
| Холодный старт до первой страницы | +286–566 мс на первый `browser_navigate` (запуск Chrome), итого ≈0,65–0,93 с от spawn | драйвер 307–347 мс, Chrome +250 мс, страница 567–628 мс; полное время процесса с рантаймом 0,72 с | первый ответ CDP 172–185 мс, страница + AX-дерево 225–238 мс |
| Следующая навигация | 33–36 мс | — | — |
| Инструменты из коробки | **25** по умолчанию, **45** с `--caps vision,pdf,devtools` | 0 MCP-инструментов; API: навигация, локаторы, `AriaSnapshotAsync(Mode = Ai)` со ссылками, скриншоты | 0; пишем всё сами |
| Pipe без порта | **да**, проверено вживую: `--remote-debugging-pipe`, слушающих портов 0 | **да**, проверено вживую: тот же драйвер, портов 0 | **да** на Linux (fd 3/4); на Windows — `--remote-debugging-io-pipes=<h1>,<h2>` **[Windows]** |
| Процессы в дереве хода | CLI → `node` (MCP) → Chrome | CLI → `HandsBridge` → `node` (драйвер) → Chrome | CLI → `HandsBridge` → Chrome |
| `KillTree` Job хода | совместим: всё потомки CLI **[Windows]** | совместим **[Windows]** | совместим **[Windows]** |
| Гейт | свой у процесса, `HandsPolicy` его не видит | `HandsPolicy`: инструменты наши | `HandsPolicy`: инструменты наши |

### Что показали замеры и код

- **Pipe у Playwright — поведение по умолчанию.** Для Chromium `defaultArgs` в `playwright-core`
  добавляет `--user-data-dir=…` и `--remote-debugging-pipe`, порт (`--remote-debugging-port=0`)
  появляется только в ветках Selenium Hub и BiDi. Во время работы `ss -ltnp` не показал ни одного
  слушающего порта у Chrome, `node` и .NET-процесса.
- **Chrome сам умирает, когда закрывается pipe.** После `kill -9` драйвера все 9 процессов Chrome
  пропали за ≤0,2 с (Linux). Это страховка поверх `KillTree`, а не замена ему. На Windows
  повторить **[Windows]**.
- **Chrome 136+ не принимает `--remote-debugging-pipe`/`--remote-debugging-port` с профилем по
  умолчанию** (`RemoteDebuggingServer::NotStartedReason::kDisabledByDefaultUserDataDir` в
  `chrome/browser/devtools/remote_debugging_server.cc`; блог Chrome 2025-03-17). Значит, CDP к
  настоящему профилю владельца закрыт браузером, и изолированный профиль — единственный путь по
  флагам.
- **Pipe на Windows у своего клиента.** Флаг `--remote-debugging-pipe` читает CRT-дескрипторы 3/4.
  Node передаёт их через `STARTUPINFO.lpReserved2`, в .NET `Process` этого нет. Для Windows в
  Chromium есть отдельный ключ `--remote-debugging-io-pipes` («comma separated list of two pipe
  handles serialized as unsigned integers», `content/public/common/content_switches.cc`, main).
  Путь для C#: `AnonymousPipeServerStream(..., HandleInheritability.Inheritable)`, значения
  хэндлов в этот ключ, `Process.Start` наследует хэндлы. С какой версии стабильного Chrome ключ
  работает, не проверено **[Windows]**.
- **Что отдаёт модели `@playwright/mcp`.** Текст: ARIA-снимок в YAML со ссылками (`button
  "Hello" [ref=e2]`). Картинка: только по `browser_take_screenshot` (отключается
  `--image-responses omit`). Побочный эффект: снимки и скриншоты сервер **пишет файлами в
  `.playwright-mcp/` рабочего каталога**, то есть в корень проекта, если не задать `--output-dir`.
- **Опасные инструменты `@playwright/mcp` включены по умолчанию.** `browser_run_code_unsafe`:
  «executes arbitrary JavaScript in the Playwright server process and is RCE-equivalent».
  `browser_evaluate` исполняет JS на странице, `browser_file_upload` читает файлы в пределах
  корней. Фильтра отдельных инструментов у сервера нет, только группы `--caps`. Убрать можно
  только `--disallowedTools` у CLI, то есть вторым местом ограничений на сервере.
- **Свой клиент: минимальный цикл проверен.** `Browser.getVersion` → `Target.createTarget` →
  `Target.attachToTarget(flatten)` → `Page.navigate` → `Accessibility.getFullAXTree`. Рамка
  сообщений — JSON с разделителем `\0`. Для рабочего набора (снимок со ссылками, клик,
  ввод, ожидание загрузки, вкладки, скриншот) нужно ещё 1,5–3 тыс. строк. Это **оценка**, не
  замер: для сравнения, у моста сейчас 29,7 тыс. строк C#.
- **`Microsoft.Playwright` не избавляет от Node.** В publish win-x64 лежит тот же `node.exe`
  (93 580 104 байт, совпадает с v24.21.0 с nodejs.org), просто внутри пакета. Выигрыш перед
  `@playwright/mcp` не в размере, а в том, что инструменты наши и идут через `HandsPolicy`.

### `KillTree` и Job хода — риски, которые надо проверить на Windows

Сейчас Job хода (`WindowsJobProcess`) ставит только `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, без
`BREAKAWAY_OK`, поэтому потомки из него не выходят. Для Chrome это значит:

1. **Песочница Chrome создаёт свои Job'ы для рендереров.** Вложенные Job поддерживаются с
   Windows 8, и мост уже так кладёт программы `app` во вложенный Job. Надо проверить, что Chrome
   в Job хода стартует без ошибок песочницы и что `TerminateJobObject` гасит все процессы,
   включая `crashpad_handler` **[Windows]**.
2. **Синглтон профиля.** Если с этим `--user-data-dir` уже работает Chrome (остался от прошлого
   хода или его запустила модель через `app` без запрещённых флагов), новый `chrome.exe`
   передаст URL живому процессу и выйдет, pipe умрёт. Одни руки на машину (`HandsMachineLock`)
   закрывают гонку двух ходов, а случай «профиль занят чужим процессом» агенту надо
   распознавать и отказывать с причиной **[Windows]**.
3. **Аварийное закрытие профиля.** После `KillTree` Chrome на следующем старте покажет
   «Chrome завершил работу некорректно». Для профиля под управлением CDP это лечится флагами
   запуска (`--hide-crash-restore-bubble` или правкой `Preferences`) **[Windows]**.

## 2. Настоящий Chrome владельца

### Встроенный режим Chrome у Claude Code CLI (`--chrome`)

| Вопрос | Ответ | Источник |
|---|---|---|
| Как устроено | Расширение «Claude in Chrome» ≥1.0.36 + native messaging host `com.anthropic.claude_code_browser_extension` (на Windows ключ `HKCU\Software\Google\Chrome\NativeMessagingHosts\`) + MCP-сервер `claude-in-chrome` внутри CLI; связь с расширением — WebSocket через `bridge.claudeusercontent.com` | code.claude.com/docs/en/chrome, …/network-config |
| Работает без claude.ai-логина | **Нет.** «A direct Anthropic plan (Pro, Max, Team, or Enterprise)… requires signing in with `/login`. If you authenticate with an API key or a long-lived token from `claude setup-token`, Claude Code keeps Chrome integration off, even when you pass `--chrome`» | docs/chrome |
| Со сторонними провайдерами через шлюз | **Нет.** В бинаре CLI 2.1.283: «Claude in Chrome requires a claude.ai subscription», «Disabled: OAuth token has no scope accepted by /api/oauth/validate (needs user:profile, user:office, or user:ccr_inference; env-var and setup-token sessions default to user:inference only)». У нас CLI на устройстве получает `ANTHROPIC_BASE_URL` на сайдкар и `ANTHROPIC_AUTH_TOKEN` = заглушку (`CliEnvironment.Build`) | `strings` бинаря 2.1.283, код агента |
| Что уходит модели | Инструменты (имена из бинаря 2.1.283): `read_page` (дерево доступности), `get_page_text`, `find`, `computer` (скриншоты и ввод), `javascript_tool`, `read_console_messages`, `read_network_requests`, `form_input`, `file_upload`, `upload_image`, `gif_creator`, `tabs_context_mcp`, `tabs_create_mcp`, `navigate`, `resize_window`, `shortcuts_*`, `browser_batch`. То есть и DOM-текст, и скриншоты, и результаты JS | бинарь CLI |
| Ограничение вкладками | Вкладки, которые открыл Claude, собираются в группу сессии; права по сайтам задаются в настройках расширения | docs/chrome |

Вывод: без claude.ai-подписки пользователя на устройстве режим не включается. Если бы
пользователь и залогинился в CLI агента, трафик расширения пошёл бы через облако Anthropic мимо
нашего egress. Как в этом случае CLI выбирает между `/login` и `ANTHROPIC_AUTH_TOKEN` для
запросов к модели, не проверено **[Windows + claude.ai-подписка]**. Смешивать две авторизации в
одном профиле CLI агента в любом случае плохо.

### Режим расширения Playwright MCP (`--extension`)

Устройство по коду `playwright-core` (`cdpRelay.ts`, в бандле 1.64.0-alpha):

- MCP-сервер поднимает WebSocket-реле на **`localhost`, случайном порту**, путь со случайным
  UUID (`/extension/{uuid}`, `/cdp/{uuid}`). Второй CDP-клиент отвергается.
- Потом сервер запускает `chrome.exe chrome-extension://mmlmfjhmonkocbjadbfplnigmagldckm/connect.html?mcpRelayUrl=…&token=…`
  (`detached: true`). Chrome уже запущен, поэтому URL уходит живому браузеру, и **браузер
  владельца остаётся вне Job хода**. `KillTree` гасит только `node`, реле закрывается,
  расширение отцепляет отладчик. Открытые моделью вкладки остаются в браузере.
- Команды к вкладкам идут через `chrome.debugger.attach/sendCommand` — полноценный CDP к
  вкладке, включая JS и cookies страницы.

| Вопрос | Ответ |
|---|---|
| Без claude.ai-логина | **Да.** Нужен только `PLAYWRIGHT_MCP_EXTENSION_TOKEN`, токен профиля Chrome из UI расширения. Без него каждое подключение подтверждается вручную |
| Со сторонними провайдерами через шлюз | **Да.** MCP-сервер к LLM не ходит, провайдер модели значения не имеет **[Windows: живой прогон]** |
| Что уходит модели | То же, что у изолированного `@playwright/mcp`: ARIA-снимок текстом, скриншоты по запросу, JS, консоль, сеть. Но **из залогиненных сессий владельца** |
| Ограничение вкладками | **Да.** При первом подключении владелец выбирает вкладку. У каждого клиента своя группа вкладок, клиент видит только вкладки своей группы, состав группы меняется перетаскиванием (README расширения) |

Третий путь к настоящему Chrome, для полноты: `chrome://inspect/#remote-debugging` → «Allow
remote debugging for this browser instance» (Chrome 144+) плюс `--cdp-endpoint=chrome`. Каждое
подключение подтверждается диалогом. Здесь CDP ко всему профилю по localhost-порту, без
ограничения вкладками; в сравнение не включён.

## 3. Инварианты ADR-016

| Инвариант | Изолир. профиль, свой CDP / .NET Playwright в мосте | Изолир. профиль, `@playwright/mcp` | Playwright `--extension` | Claude `--chrome` |
|---|---|---|---|---|
| Учётки сервера не едут на клиент | держит | держит | держит (токен расширения местный, агент ставит его сам: `env` из серверного конфига `SanitizeMcpConfig` выбрасывает) | **ломает модель авторизации**: нужна claude.ai-учётка пользователя в CLI агента рядом с токеном шлюза; трафик расширения идёт через `bridge.claudeusercontent.com` мимо egress |
| Серверных stdio-узлов нет (`SanitizeMcpConfig`) | держит: браузер — поле маркера рук или второй маркер, команду подставляет агент | держит, если узел `browser` подставляет агент по новому маркеру; `SanitizeMcpConfig` надо расширить | то же, что слева | не MCP-конфиг, а флаг CLI и native host, который ставит сам CLI в реестр **пользователя** — вне нашего контроля |
| Один гейт | **держит**: инструменты в `HandsBridge`, проверки в `HandsPolicy` | **ломает**: второй процесс со своими инструментами, `browser_run_code_unsafe` в наборе; ограничивать пришлось бы `--disallowedTools` на сервере (второе место), stdio-прокси уже отвергнут в ADR-016 §7 | **ломает** так же | **ломает**: права по сайтам живут в расширении |
| `ProjectCapabilities` — единственная точка правды | держит: `ProjectFeatures.Browser` + `BrowserRefusal` рядом с `HandsRefusal`, агент объявляет `DeviceCapabilities.Browser` (найден Chrome) | держит так же | держит, но возможность зависит от состояния расширения у владельца, его знает только агент | не выражается: доступность зависит от claude.ai-подписки, её сервер не знает |

Замечание по гейту `ForbiddenArgumentFragments`: запускать Chrome с `--remote-debugging-pipe`
будет агент (или мост внутренним вызовом), а не модель через `app`, поэтому запрет трогать не
нужно. Модели по-прежнему нельзя превратить свой Chrome в управляемый. Но через `app` модель
может запустить `chrome.exe --user-data-dir=<профиль проекта>` без запрещённых фрагментов и
занять профиль (см. «Синглтон профиля»). При реализации гейт должен отказывать в `app` с
`user-data-dir` профилей агента.

## Рекомендация

1. **Изолированный профиль, свой CDP-клиент в `HandsBridge`.** Не растит архив, не тащит Node,
   держит все четыре инварианта, сохраняет один гейт и одну схему подключения (маркер → агент →
   мост в Job хода). Минимальный набор инструментов: `browser_navigate`, `browser_snapshot`
   (AX-дерево со ссылками), `browser_click`, `browser_type`, `browser_tabs`,
   `browser_screenshot` (по флагу `vision`, как `screenshot_control`), `browser_wait`. Без
   исполнения произвольного JS: мы защищаемся от ошибок модели, а не от злоумышленника (решение
   1в), так что проще не давать модели инструмент, который она легко употребит не по делу.
2. **Запасной — `Microsoft.Playwright` в том же мосте**, если оценка своего клиента окажется
   неподъёмной. +38,4 МБ к архиву, остальные свойства те же.
3. **`@playwright/mcp` в изолированном профиле — не брать.** По размеру он равен варианту 2, но
   даёт второй гейт и RCE-инструмент по умолчанию.
4. **Настоящий Chrome: `--chrome` исключён** (claude.ai-подписка, облачный мост).
   Playwright `--extension` — отдельное решение владельца, если «работай в моём браузере с моими
   логинами» станет нужным сценарием. Тогда он идёт тем же маркером, но со вторым гейтом, и это
   надо принять осознанно, как снятие границы окон во второй волне.

## Чек-лист для прогона владельцем на устройстве [Windows]

1. Chrome из Job хода: запустить `chrome.exe --user-data-dir=%LOCALAPPDATA%\…\probe
   --remote-debugging-pipe about:blank` потомком процесса в Job с `KILL_ON_JOB_CLOSE` (проще
   всего через `Microsoft.Playwright`-пробник из этого спайка). Проверить: песочница не падает,
   `TerminateJobObject` гасит все `chrome.exe` и `crashpad_handler.exe`.
2. Смерть драйвера без Job: убить `node`/пробник через «Диспетчер задач» и посмотреть, выходит ли
   Chrome сам (на Linux — ≤0,2 с).
3. `--remote-debugging-io-pipes=<h1>,<h2>` в стабильном Chrome: две анонимные трубы из C#,
   ответ на `Browser.getVersion`.
4. Холодный старт headed на Windows: `@playwright/mcp` (spawn → первая страница),
   .NET-пробник, «голый» CDP. На Linux: 0,65–0,93 / 0,72 / 0,23 с.
5. Профиль занят: запустить Chrome с профилем проекта руками, потом ход — нужен отказ с
   причиной, а не молчаливый обрыв.
6. Playwright `--extension` с токеном и сторонним провайдером через шлюз: подключается ли, видит
   ли только свою группу вкладок.

## Как запустить пробник

Пункты чек-листа 1, 2, 3, 5 и 4 (только «голый» CDP) проверяет один консольный exe на C#
(нумерация в таблице ниже своя, как в выводе пробника):
[`browser-hands-probe/`](browser-hands-probe/) (.NET 10, без Node и без Playwright, в решение
не входит). CDP идёт по двум `AnonymousPipeServerStream` с `HandleInheritability.Inheritable`,
хэндлы передаются ключом `--remote-debugging-pipe --remote-debugging-io-pipes=<вход>,<выход>`.
Порядок подтверждён по исходнику Chromium: первый хэндл Chrome читает, во второй пишет
(`AdoptPipes` в `content/browser/devtools/devtools_agent_host_impl.cc`). Chrome запускается
через `CreateProcess` приостановленным, при необходимости кладётся в Job с
`KILL_ON_JOB_CLOSE` и только потом возобновляется, чтобы ни один потомок не успел выйти из Job.

**Запуск.** Скопировать `BrowserHandsProbe.exe` на Windows-машину с установленным Chrome и
запустить двойным кликом. Около минуты окна Chrome будут открываться и закрываться сами. В конце
консоль ждёт Enter, отчёт сохраняется рядом с exe в `BrowserHandsProbe-report-<дата>.txt`
(если туда писать нельзя, то в `%TEMP%`). В файле есть приложения: хвост `chrome_debug.log` при
ошибке и текст `chrome://sandbox`.

- Где взять exe: в worktree спайка, `docs/research/browser-hands-probe/dist/BrowserHandsProbe.exe`
  (11,3 МБ, single-file, self-contained, trimmed). В git не вносится (`.gitignore` папки).
- Собрать самому (нужен .NET 10 SDK, на Windows или кросс-сборкой с Linux):
  `dotnet publish docs/research/browser-hands-probe -c Release -r win-x64 --self-contained -o dist`.
- Ключи: `--chrome "<путь>\chrome.exe"` (или переменная `CHROME_PATH`), если Chrome стоит не в
  стандартном месте. Без ключа Chrome ищется в `App Paths` реестра (HKCU, HKLM), `%ProgramFiles%`,
  `%ProgramFiles(x86)%`, `%LOCALAPPDATA%`. Ещё есть `--headless` и `--no-pause`.
- Профили создаются в `%TEMP%\hands-browser-probe-<id>\`, после прогона каталог удаляется. Свой
  Chrome владельца пробник не трогает, но в строках «во всей системе» считает и его процессы.

**Что проверяется и что значит PASS.**

| Пункт | Что делает пробник | PASS |
|---|---|---|
| 1. `io-pipes` | Без Job: `Browser.getVersion` → `Target.createTarget` → `attachToTarget(flatten)` → `Page.navigate` (data:-страница с кнопкой) → `Page.loadEventFired` → `Accessibility.getFullAXTree` | в AX-дереве есть кнопка «Hello». При FAIL печатаются версия файла `chrome.exe`, шаг, точная ошибка, код выхода Chrome и строки лога про DevTools/pipe |
| 2a. Песочница в Job | То же в Job с `KILL_ON_JOB_CLOSE`, дерево процессов с типами (`--type=` из командной строки) | страница получена, есть `renderer`, браузер жив, в логе нет ERROR/FATAL со словом sandbox |
| 2b. `TerminateJobObject` | Снимок дерева (все `chrome.exe`, включая `crashpad-handler`), `TerminateJobObject`, опрос каждые 10 мс | все процессы дерева вышли и счётчик живых в Job равен 0 за ≤10 с, время в мс |
| 3. Закрытие труб | Без Job: закрываем свой пишущий конец | браузер и всё дерево вышли сами за ≤20 с; время до EOF, выхода браузера и всего дерева |
| 4. Профиль занят | Проверка занятости до запуска (`lockfile` открывается с `FileShare.None` + скрытое окно `Chrome_MessageWindow` с заголовком = путь профиля), первый Chrome, снова проверка, второй Chrome с тем же профилем | занятость видна до запуска второго: «свободен → занят → свободен после гашения». Поведение второго записывается: ответ, EOF или таймаут, код выхода, ушёл ли его URL в первый процесс |
| 5. Холодный старт | Три запуска со свежим профилем, в Job, с окном | три удачных замера: первый ответ CDP и страница + AX-дерево |

**Самопроверка на Linux** (тот же код CDP-клиента, трубы — fd 3/4 через FIFO, Job и
`lockfile` только на Windows, поэтому 2 = SKIP): Chrome 153.0.8010.52 headless. 1 — PASS,
первый ответ 176 мс, страница + AX-дерево 215 мс. 3 — PASS, после закрытия нашего конца
браузер вышел за 40 мс, все 16 процессов за 51 мс. 4 — PASS: второй запуск закрыл трубу
(EOF) и вышел с кодом 21 через 76 мс (`RESULT_CODE_NORMAL_EXIT_PROCESS_NOTIFIED`: командная
строка передана первому процессу); занятость видна до запуска по `SingletonLock`.
5 — PASS, 209/211/213 мс. Проверка мутацией: без нулевого байта в конце сообщения все пункты
краснеют по таймауту, и после провала не остаётся ни одного процесса Chrome. Цифры для Windows
headed будут другими, этот прогон подтверждает только логику пробника.

## Воспроизведение

Пробники лежали в `/tmp/spike` на машине разработки (в репозиторий не вносились):
`pwmcp/probe.mjs` — stdio-клиент MCP (initialize, tools/list, два `browser_navigate`);
`pwnet/Program.cs` — `Playwright.CreateAsync` → `LaunchPersistentContextAsync(Channel =
"chrome")` → `GotoAsync` → `AriaSnapshotAsync`; `cdp_pipe.mjs` — CDP по fd 3/4 без
библиотек. Архив агента — `dotnet publish ClaudeHomeServer.DeviceAgent -c Release -r win-x64
--self-contained`, zip `-9`.
