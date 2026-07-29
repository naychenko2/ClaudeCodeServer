# Реализованные фичи (детали)

> Подробные описания реализованных фич, вынесенные из [CLAUDE.md](../CLAUDE.md).
> Краткий список — там же, раздел «Реализовано». Читать при работе над конкретной фичей.

## Базовые возможности

- Auth: реальная аутентификация по API-ключу — `[Authorize]` на всех API + хабе.
  Ключ из `Auth:ApiKey` (env/config) или автоген в `data/auth-key.txt` (печатается в консоль).
  Клиент: `Authorization: Bearer` (REST), `?access_token=` (WS); 401 → авто-логаут.
  Удалённый доступ (Tailscale + HTTPS): [remote-access.md](remote-access.md)
- Проекты: CRUD, редактирование, выход
- Сессии: создание с именем/режимом/моделью, редактирование названия и модели (шапка чата + список), статусы (starting/active/waiting/finished/error)
- Чат: Composer (вложения, режим ⚡/📋/❓, голосовой ввод, стоп, «Claude печатает…»)
- Сообщения: text, thinking, tool_use (spinner), permission_request, file_changed, result, error+retry
- Empty states: нет сессий, пустой чат с подсказками, пустая папка, нет результатов поиска
- Файловый менеджер: дерево, поиск, просмотр/редактирование, diff/revert, бинарные, изображения, loading
- Несохранённые изменения: диалог при закрытии файла

## Виджеты в чате

Штатная фича, без флага: модель показывает интерактивные HTML-виджеты (дашборды, графики,
калькуляторы) через `mcp__widgets__widget_show` — [mcp/widgets-server/index.js](../mcp/widgets-server/index.js)
(без зависимостей, в API не ходит: валидирует input, лимит html 64 КБ). Фронт рендерит
`input.html` в sandbox-iframe ([WidgetView.tsx](../frontend/src/components/chat/WidgetView.tsx) +
чистое ядро [lib/widgetHtml.ts](../frontend/src/lib/widgetHtml.ts)): `sandbox="allow-scripts
allow-forms allow-modals"` (без same-origin/popups), строгая CSP-мета в обёртке (default-src
'none' — никакой сети), тема через CSS-переменные `--cc-*` (смена темы = ремаунт iframe),
авто-высота postMessage `cc-widget-height` (фильтр по `e.source`, кламп 120–800/560-мобила),
защитный cap рендера 256 КБ. Карточка виджета «пришпилена» в ленте (`isWidgetEntry` в ChatPanel) —
не прячется в свёртку «N действий», как медиа fal.ai; виджет переживает F5 (input в history.json).
Подсказка модели — widgetsHint в ClaudeSession (только при подключённом сервере); сервер
регистрируется в BuildTurnMcpConfig с `alwaysLoad` и работает у всех CLI-провайдеров.
Дискаверабилити: действие `chat.widget` в AI-палитре (контекстные промпты по разделам) +
rule-based подсказка в календаре при 5+ активных задачах. Для docker-песочницы сервер
копируется в образ (`/app/mcp/widgets-server`) — при обновлении нужна пересборка sandbox-образа.

## Артефакты сессии

За фич-флагом `session-artifacts`: панель справа от чата с вкладками — план (ExitPlanMode),
задачи (Todo/Task), агенты (субагенты Task/Agent + workflow-группы с раскрытием деталей:
промпт, лента вызовов, результат), изменённые/упомянутые файлы, ссылки. Всё derived из ленты
чата ([useSessionArtifacts.ts](../frontend/src/hooks/useSessionArtifacts.ts)), без участия бэкенда.

## Панель «Доки»

Документация проекта (`README.md` + `docs/**`) как связный корпус, а не список файлов:
дерево документов, превью с оглавлением, поиск по заголовкам и телу, переходы по
относительным ссылкам между документами, обратные ссылки («кто сюда ссылается») и передача
в чат. Панель живёт в правой рельсе рядом с «Файлами»
([DocsPanel.tsx](../frontend/src/pages/workspace/DocsPanel.tsx)).

Чем отличается от соседей: «Файлы» — дерево репозитория для работы с кодом, «Заметки» —
личный vault вне репы, «Знания» — семантический поиск через Dify.

Устройство:

- **Индекс** собирает бэкенд ([DocsIndexService](../backend/ClaudeHomeServer/Services/Docs/DocsIndexService.cs)):
  заголовки со слагами, ссылки (doc / repo / external) и обратные ссылки — разворот
  исходящих, отдельного хранилища нет. Кеш ключуется корнем папки и живёт до изменения
  **отпечатка области** (путь + время правки + размер каждого файла): по «максимальному
  mtime и количеству» замена одного файла другим в ту же секунду прошла бы незамеченной.
- **Гейт области**: `GET docs/doc` отдаёт только то, что попало в индекс. Без него эндпоинт
  стал бы вторым универсальным файл-ридером поверх `files/content`.
- **Якоря**: слаг считается от текста заголовка, очищенного от markdown-разметки, одинаково
  на сервере и на клиенте ([docsLinks.ts](../frontend/src/lib/docsLinks.ts)) — это контракт,
  по которому панель находит раздел. Якорь из ссылки декодируется (кириллица в href приезжает
  процент-энкодингом).
- **Оглавление** снимается с реального DOM после рендера ([useHeadings.ts](../frontend/src/hooks/useHeadings.ts)),
  тем же хуком пользуется панель «План» — список TOC и цель скролла физически один узел.
- **В чат** двумя способами: документ целиком уходит **путём-вложением** (Claude прочитает
  файл сам, контекст не раздувается), раздел из оглавления — **цитатой исходного markdown**
  в композер.
- Панель подписана на `filesChanged`: правки `docs/` прямо из чата обновляют дерево, превью
  и обратные ссылки без перезагрузки.

Вне объёма первой версии: переходы по ссылкам в центральной области (там документ
рендерит `DocCommentedMarkdown`), картинки с относительными путями в превью, индикатор
устаревшей документации.

## Продуктовая история «Что нового»

Основной функционал, кнопка в шапке: AI-сводка изменений **по всем проектам сразу** — что
нового и чем полезно пользователю (не код, не diff).
[ChangelogService](../backend/ClaudeHomeServer/Services/ChangelogService.cs) собирает git-коммиты
из репы продукта — путь в `Changelog:SourceRepoPath` (машинно-специфичный, в
`appsettings.Local.json`; без него раздел показывает «не настроено»), имя —
`Changelog:SourceProjectName` (дефолт = имя папки). Дальше группирует по дням и суммирует
каждый день **одним вызовом** через общий
[OneShotClaudeRunner](../backend/ClaudeHomeServer/Services/Llm/OneShotClaudeRunner.cs) (модель
`Changelog:Model`, дефолт haiku; лениво, продуктовый промпт — польза, а не техника; таймаут
`Changelog:TimeoutMs`, дефолт 480с). Промпт просит не более 12 пунктов на день (агрессивная
группировка) и короткий `scoreReason`. Области выравниваются между днями подсказкой частых
`area` из кеша (`KnownAreas`) + канонизацией `NormalizeAreas` (схлопывает «Чат»/«чат»/«ЧАТ»).
Fallback без LLM (`FallbackItems`, при недоступном claude) кладёт `area` по типу коммита
(feature→«Новое», fix→«Исправления», improvement→«Улучшения», иначе «Прочее») и честный
`scoreReason` «сводку собрать не вышло». **Дробить день на параллельные чанки пробовали —
отказались**: старт CLI (~15с) платится за каждый вызов, а чанки не видят друг друга и дробят
смысл (замер на 59 коммитах: один вызов — 141с/13 пунктов, три чанка — 182с/29 пунктов).
Каждый пункт: `type` (feature/improvement/fix/other), `area` (раздел продукта — Claude
определяет сам), `emoji`, `title`, `benefit`, `authors`, `projects`. Результат кешируется на
уровне продукта в `data/changelog/product.json` (ключ дня = хеш sha-набора всех проектов —
сводка одна для всех и перегенерируется только при новых коммитах дня). Алиасы авторов —
`Changelog:AuthorAliases` (email → имя). Эндпоинты — глобальный
[HistoryController](../backend/ClaudeHomeServer/Controllers/HistoryController.cs) (`api/history/*`).
Фронт: [ProductHistory.tsx](../frontend/src/components/ProductHistory.tsx) — полноэкранная лента
по дням (Сегодня/Вчера/дата). Внутри дня пункты **сгруппированы по области** (`area`), и режим
показа адаптивный (`LIST_MODE_MAX = 12`): мало пунктов — все области идут **секциями списком**
(заголовок `CategoryHeader` + свой таймлайн), много — **вкладки-подчёркивания** `AreaTabs`
с таймлайном активной области. Таймлайн (`GroupTimeline`) — маркеры-кружочки единым
accent-цветом. Иконки авторов — эмодзи-роли (`AUTHOR_EMOJI`: известным авторам сопоставлены
фиксированные эмодзи, новым — из пула детерминированно по имени). Фильтр по исполнителю (чипы,
авторы по алфавиту; режим считается от отфильтрованных пунктов). Навигация по дням —
**календарь** (`DayCalendar`): дни с изменениями кликабельны, сводка генерится лениво только
для выбранного дня. Кнопка «Что нового» в [HubHeader](../frontend/src/components/HubHeader.tsx)
видна во всех разделах (событие `open-product-history` → overlay в
[App.tsx](../frontend/src/App.tsx)), бейдж считает новые коммиты с последнего захода
(timestamp в `localStorage`).

## Плагин oh-my-claudecode

Плагин [oh-my-claudecode](https://github.com/Yeachan-Heo/oh-my-claudecode) (MIT): ставится
автоматически на старте контейнера (entrypoint: `claude plugin marketplace add` +
`install oh-my-claudecode@omc`, идемпотентно, best-effort). Скиллы плагинов
(`SkillsService.GetPluginSkills` из `~/.claude/plugins/installed_plugins.json`) видны в панели
навыков (секция «Плагины») и попапе «/» композера; вызов с namespace —
`/oh-my-claudecode:autopilot`; описания переводятся на русский фоном
([PluginSkillLocalizer](../backend/ClaudeHomeServer/Services/PluginSkillLocalizer.cs), кеш
`data/skill-translations.json`). Каталог `plugins` синкается в профили CLI-провайдеров (без
`.git`). **Роутинг персон**: при `/oh-my-claudecode:*` в ход дописывается таблица замен
([OmcPersonaRouting](../backend/ClaudeHomeServer/Services/Prompts/OmcPersonaRouting.cs)) —
советнические типы (analyst/critic/planner/architect…) замещаются персонами по
`PersonaSpecialty` (+фолбэк по названию роли), исполнительские (executor/qa-tester/git-master…)
— только персонами с опт-ином `Persona.SubagentExecutor` (тумблер «Исполнитель в сабагентах»,
только при Access=Full: сабагент получает Write/Edit/Bash и рамку исполнителя,
`PersonaConsultantToolset.IsExecutor`). Team-режим (tmux) и npm-CLI `omc` не поддерживаются;
`/oh-my-claudecode:setup` не запускать. Подробности — [docker.md](docker.md).

## Механики OmO в чатах (флаг `work-loop`)

Тексты — переводы oh-my-openagent ([omo-adoption.md](omo-adoption.md)); рантайм-константы —
[Services/Prompts/OmoPrompts*.cs](../backend/ClaudeHomeServer/Services/Prompts/OmoPrompts.cs)
(Categories генерируются скриптом docs/omo/gen-omo-prompts.ps1 из переводов).

- Своя вставка «магического слова ultrawork» УДАЛЕНА: слова `ultrawork`/`ulw` ловит
  keyword-detector плагина oh-my-claudecode (см. BuildCliTurnText).
- **Цикл «до готово»** (`work-loop`, по мотивам ralph/ulw-loop): тумблер в композере →
  `PUT /api/chats/{id}/loop` → `Session.WorkLoop` {promise=«ГОТОВО», iteration, maxIterations
  (конфиг `Loop:MaxIterations`, дефолт 20), phase working|verifying}. Пока цикл активен, к ходу
  дописывается протокол «выведи `<promise>ГОТОВО</promise>` когда всё сделано»; на `exited`
  штатного хода `ContinueWorkLoopAsync`: маркер не найден → автопродолжение (continuation-сообщение
  видно в ленте как обычное), найден → фаза verifying (один верификационный ход со свидетельствами;
  без рабочего протокола), после — стоп; стоп также по лимиту, ошибке хода и Interrupt (снимается
  синхронно до exited). Событие `work_loop` (active/iteration/max/phase) → бейдж «Цикл: итерация N/M»
  в композере. Текст хода агрегируется в `SessionEntry.LoopTurnText` (поиск маркера).
- Справочник категорий делегирования (`OmoPrompts.DelegationCategories`) — секция «ДЕЛЕГИРОВАНИЕ»
  в промпте персоны-исполнителя задач (TaskExecutionService).

## Задачи v3

Напоминания, регулярные, Claude-исполнитель; исполнение гейтится флагом `personas` для персон:

- Напоминания: `TaskItem.ReminderMinutes` (офсет от срока), `TaskSchedulerService`
  (BackgroundService, тик 30 с) шлёт `NotificationMessage` в группу user_* (тост
  [NotificationToasts.tsx](../frontend/src/components/NotificationToasts.tsx)) + web push.
  Сроки локальные: `User.TimeZone` (IANA, фронт шлёт при старте), конверсия в UTC —
  [TaskDueCalculator.cs](../backend/ClaudeHomeServer/Services/TaskDueCalculator.cs), без времени — 09:00
- Web push: VAPID-ключи автогенерация в `data/vapid-keys.json`, подписки в
  `data/push-subscriptions.json` (несколько устройств per-user, авточистка 404/410). SW — свой
  `frontend/src/sw.ts` (vite-plugin-pwa `injectManifest`, отдельный tsconfig.sw.json),
  обработчики push/notificationclick с hash-диплинками
- Регулярные задачи: `TaskRecurrence` + `SeriesId`; при переводе экземпляра в done
  PUT /api/tasks/{id} спавнит следующий
  ([TaskRecurrenceCalculator.cs](../backend/ClaudeHomeServer/Services/TaskRecurrenceCalculator.cs) —
  отсчёт от срока, не от завершения)
- Claude-исполнитель: [TaskExecutionService.cs](../backend/ClaudeHomeServer/Services/TaskExecutionService.cs) —
  сессия acceptEdits в проекте задачи (личная — чат вне проекта), промпт с правилами ведения
  статуса через MCP tasks_*; наблюдение через событие `SessionManager.OnSessionMessage`
  (result → отметка + уведомление, permission → «ждёт ответа»); триггеры: кнопка «Выполнить
  с Claude» и автозапуск планировщиком в момент срока (окно 24 ч)
- Исполнитель = персона: `TaskItem.PersonaId` — задача выполняется «от лица» персоны (модель
  приоритетнее `Tasks:ExecutorModel`, 6-секционный контракт, уведомления её лицом). Единый
  пикер «Исполнитель» (Я/Claude/персона) в форме и диалоге; вкладка «Задачи» персоны и
  назначение через REST/MCP — см. [personas.md](personas.md). За флагом `personas`
  (отдельного `task-claude-exec` нет)

## Бэкапы и восстановление

Резервные копии каталога `data` (чаты с историей, персоны с памятью, задачи, заметки,
лог событий SQLite). Код — [Services/Backup/](../backend/ClaudeHomeServer/Services/Backup/).

**Настройка — только руками**, секция `Backup` в `appsettings.Local.json`
(`Enabled`, `Path`, `IntervalHours`, `SecretsPath`; образец с пояснениями —
`appsettings.Local.example.json`). Через UI не правится намеренно: пути машинно-специфичны,
а настройка внутри `data` откатывалась бы вместе с данными при восстановлении — включая
путь к папке собственных архивов.

**Что попадает в архив.** Всё из `data`, кроме: секретов (отдельный архив, см. ниже),
`logs/`, `sandbox-tmp/`, `server-pids.txt` (восстановленный реестр PID заставил бы следующий
старт убить по протухшим номерам всё, что зовётся claude/node), самих папок архивов,
временных и `.corrupt-*` файлов. Из профилей CLI (`claude-profiles/`, `sandbox-profiles/`)
берётся **только подпапка `projects/`** — транскрипты для `--resume`, которые больше
неоткуда восстановить; `.credentials.json` с OAuth-токенами и синканные `plugins` осознанно
за бортом, потому что основной архив уезжает в облако. `project-events.db` снимается
online-backup API SQLite (`BackupDatabase`), а не копированием файла: копия без WAL-хвоста
протухшая. Секреты (`jwt-secret.txt`, `vapid-keys.json`, `module-keys.json`,
`appsettings.Local.json`, `appsettings.{Env}.json`) — **отдельным локальным архивом**
в `SecretsPath` (дефолт `{папка exe}/backups-secrets`, вне `data`, чтобы пережить restore).

**Как снимается.** Расписание интервальное, не календарное: проверка раз в час, снимок —
если с последнего успеха прошло больше `IntervalHours` (машина Windows может спать ночью, и
cron молча пропускал бы окно). Плюс ручной снимок (кнопка виджета, меню трея,
`exe --backup`) и автоматический в `deploy80.ps1` перед выкаткой. Архив пишется сразу в
целевую папку как `.part` и переименовывается **на месте** — иначе `File.Move` между томами
(облачная папка обычно на другом диске) даёт copy+delete, и синхронизатор подхватывает
недописанный файл. Рядом кладётся sidecar-манифест: по нему виджет и трей показывают состав
без вскрытия архива. Инициаторов трое (таймер, трей, деплой), поэтому снимок под именованным
мьютексом; ротация — 7 дневных + 4 недельных + 3 месячных, и **только по своим архивам**
(чужой `instanceId` и нечитаемый sidecar не трогаются: в общей облачной папке лежат
архивы разных инстансов, а OneDrive держит файлы плейсхолдерами).

**Восстановление — вне веб-интерфейса**: `ClaudeHomeServer.exe --restore <архив>
[--secrets <архив>] [--force]` либо пункт меню трея. Причина простая: бэкап нужен ровно
тогда, когда приложение не работает, и механизм внутри сервера в этот момент недоступен.
Четыре гейта, все до того, как каталог сдвинут с места:

1. **сервер не запущен** — признак `Global\ccs-instance-{sha1(dataDir)}`, который сервер
   держит весь uptime ([InstanceLock.cs](../backend/ClaudeHomeServer/Services/Backup/InstanceLock.cs));
   живой сервер продолжил бы писать в перемещённый каталог;
2. **тот же инстанс** — `instanceId` из `data/instance-id.txt`. Три случая: файла нет
   (чистая машина после смерти диска) → id **усыновляется** из архива; совпал → ок;
   не совпал → отказ, обходится `--force`. Разведение случаев принципиально: без него
   штатный disaster-recovery требовал бы того же флага, что и опасное кросс-инстансное
   восстановление;
3. **версия формата** — `manifest.schemaVersion <= BackupSchema.Version`; архив новее кода
   роняет десериализацию, а `JsonFileStore` на этом молча отдаёт пустой стор;
4. **целостность** — sha256 всех файлов по манифесту + **строгое** чтение сторов
   ([BackupValidation.cs](../backend/ClaudeHomeServer/Services/Backup/BackupValidation.cs))
   теми же `JsonSerializerOptions`, что у самих сторов. Отдельно от штатной загрузки именно
   потому, что та прощает ошибки: битый файл переименовывается в `.corrupt-*.bak`, стор
   стартует пустым, и «восстановилось» означало бы «всё зелёное, а персон нет».

Дальше: гасится контейнер песочницы (`data/sandbox-*` смонтированы внутрь и держат каталог),
снимается страховочный снапшот, `SqliteConnection.ClearAllPools()` (пул держит `.db` и после
закрытия соединений — каталог иначе не переименовать), `data` → `data.old-{ts}`, распакованное
→ `data`; **любая ошибка распаковки откатывает** `data.old` обратно. Секреты и история
архивов переносятся из `data.old` в новый каталог: их нет в архиве, а каталог заменён целиком —
без переноса обычный откат втихую разлогинивал бы всех (новый jwt-secret) и убивал push-подписки.
Ключ `--secrets` поверх этого восстанавливает архивные.

**Post-restore**: маркер `.post-restore` обрабатывается на старте до подъёма сторов
([PostRestoreHook.cs](../backend/ClaudeHomeServer/Services/Backup/PostRestoreHook.cs)) —
обнуляются карты документов `workspace-knowledge.json`, чтобы штатный `BootstrapDocsAsync`
пересобрал Dify-слой с натуры. Сторы заметок и памяти персон не трогаются: у них дифф-синк
по хешам, он сойдётся сам. Ограничение v1: restore рассчитан на **тот же Dify** —
документы, созданные в датасетах после снятия снапшота, останутся сиротами.

**Инспекционная копия**: `exe --inspect <архив> [--port 5599]` поднимает временный инстанс
на `127.0.0.1` из архива — посмотреть и перенести данные руками. Копия обезврежена, иначе она
бы **жила**: планировщик выполнял бы просроченные задачи Claude-исполнителем (реальные деньги
и правки файлов), синхронизация знаний «поправляла» бы боевые датасеты Dify под отставшее
состояние, push уходили бы дублями. Поэтому в ней: снимаются регистрации **всех** своих
hosted-сервисов (разом, по сборке — перечень пришлось бы дописывать при каждом новом сервисе),
не запускаются `ProcessRegistry` (его pid-файл лежит рядом с exe и принадлежит боевому серверу),
`PersonaAgentFileSync` (пишет `.claude/agents/*.md` в реальные папки проектов), стартовые
миграции и прогрев моделей; принудительно пустые `Dify:ApiKey` и `Sandbox:ProjectsRoot`;
read-only middleware отбивает все не-GET запросы (стоит до аутентификации и до WebDAV, иначе
`PUT/MKCOL/MOVE` ушли бы на живой диск мимо гейта). Переопределения кладутся **последним**
источником конфигурации — `appsettings.Local.json` грузится поверх командной строки и иначе
вернул бы боевые пути; окружение дочернего процесса — `Inspection`, потому что пустое
означало бы `Production` с его `Kestrel:Endpoints` на `0.0.0.0:80/443`.

**Чего бэкап не покрывает** (осознанно): транскрипты CLI подписки (`~/.claude/projects/**`,
вне `data` — при обычном откате на той же машине они на месте и `--resume` работает, после
смерти диска лента чатов есть, а продолжить разговор нельзя), файлы проектов (это git-репы),
вложения чатов, датасеты Dify (переиндексируются). Снапшот не транзакционен между файлами.

**Виджет** «Бэкап» на главной ([BackupWidget.tsx](../frontend/src/features/home/BackupWidget.tsx),
только `role=admin`): статус, последние 3 снимка со сводкой состава и кнопка ручного снимка
(скрыта, пока бэкап не включён в конфиге). Данные — из `data/backup-state.json`;
в папку архивов виджет не ходит намеренно: на спящем облаке перечисление файлов подвесило бы
дашборд. API — `GET /api/admin/backup`, `POST /api/admin/backup/run` (оба admin-only).
