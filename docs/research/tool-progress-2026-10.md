# Прогресс долгих операций в чате — разведка (2026-10-02)

Вопрос: можно ли показать в чате прогресс операций агента (Bash, генерации, сборки, тесты,
загрузки). Сейчас прогресс есть только у Workflow (`WorkflowProgressMessage`, его питает
`WorkflowWatcher` по файлам транскрипта, а не CLI). Код не писали.

## 1. Что отдаёт claude CLI (проверено на живом 2.1.287)

Прогон `--print --output-format stream-json --input-format stream-json
--include-partial-messages --verbose`, каждая строка помечена временем прихода.

**Bash на 16 секунд с выводом каждые 2 с** (`for i in 1..8; echo; sleep 2`):

```
21.60s assistant   tool_use Bash {"command":"for i in 1 2 3 …"}
26.92s system      {"subtype":"task_started","task_id":"b2mnab6yu","tool_use_id":"toolu_01Q4…",
                    "description":"Run loop with 8 steps…","is_backgrounded":false,"task_type":"local_bash"}
           … 16 секунд тишины: ни строки вывода, ни elapsed …
43.37s system      {"subtype":"task_notification","task_id":"b2mnab6yu","status":"completed","output_file":""}
43.82s user        tool_result "step 1\nstep 2\n…\nstep 8"
```

- Вывод Bash по ходу выполнения **не приходит** — только целиком в `tool_result`.
- Событий `tool_progress` / elapsed для Bash нет. Есть только пара `task_started` (теперь и
  для foreground-Bash, `is_backgrounded:false`) и `task_notification` — то есть точное время
  старта и конца, без промежуточного.

**Сабагент (Task)** — единственный, у кого есть промежуточное событие:

```
{"type":"system","subtype":"task_progress","task_id":"af1ee0d973d0567d1",
 "tool_use_id":"toolu_01VsCAWteTMQdmaaK59twFAC","description":"Running Sleep 4 seconds and echo a",
 "subagent_type":"general-purpose","usage":{"total_tokens":35532,"tool_uses":1,"duration_ms":4522},
 "last_tool_name":"Bash"}
```

Приходит на каждый вызов инструмента внутри сабагента. Процента нет, но есть «что сейчас
делает», число вызовов и длительность. Мы его сейчас молча выбрасываем
(`ClaudeSession.cs:5151` разбирает только `task_started`/`task_notification`).

**MCP-инструмент, шлющий `notifications/progress`** (свой stdio-сервер, 6 уведомлений за 6 с):
CLI передаёт в `tools/call` `_meta = {"claudecode/toolUseId":"toolu_01Ur…","progressToken":2}`,
но сами уведомления в stream-json **не пробрасывает** — между `tool_use` (26.7 с) и
`tool_result` (33.7 с) пусто. Зато `claudecode/toolUseId` в `_meta` — готовый ключ: наш
MCP-сервер сам знает, к какой карточке инструмента относится вызов, и может докладывать
прогресс в чат мимо CLI. Проверено на stdio; для нашего MCP-over-HTTP то же `_meta` должно
приезжать в теле `tools/call`, но это надо подтвердить первым шагом реализации.

## 2. Источники: у кого есть процент

Реального процента (шагов сэмплера, байтов, тестов) **нет ни у одного источника**. Есть
стадия, позиция в очереди и оценка по ETA.

| Источник | Что знает | Процент |
|---|---|---|
| local-media (ComfyUI) | статус, позиция в очереди, elapsed, ETA из таблиц замеров; HTTP-опрос `/history` + `/queue` (`LocalMediaService.cs:580`) | оценка: `progress_estimate` = elapsed/ETA, потолок 95 (`LocalMediaToolset.cs:186`). Настоящие шаги есть только в WebSocket ComfyUI, который мы не слушаем |
| Редактор картинок (fal queue, Higgsfield, local) | стадия Queued(pos)/Running/Downloading, ETA; уже летит на фронт событием `image_edit_progress` | оценка по ETA, та же формула (`useJobStatus.ts:113`) |
| fal: аватары/фоны (`FalImageService`) | ничего — синхронный `fal.run` | нет |
| fal MCP (`mcp.fal.ai` submit_job) | чужой сервер; агент сам опрашивает `check_job` | нет (только статус очереди в ответе агенту) |
| glif | working / completed / failed | нет |
| dotnet build/test | вывод не разбираем; CLI его по ходу и не отдаёт | нет |
| Загрузки, бэкапы | `IProgress`/подсчёта байтов нет | нет |
| Сабагент (Task) | `task_progress`: последний инструмент, tool_uses, duration_ms | нет, но есть «живой» этап |
| Выкатка (`DeployProgressCard`) | таймер | имитация: min(92, elapsed/ожидаемое) |

## 3. Варианты

**(а) Неопределённая полоса + таймер на карточке инструмента.** Сейчас у карточки
(`ToolUseView.tsx:225`) только спиннер при `result === undefined`, времени старта у
`ChatItem` нет. Добавить `startedAt` и тикающий «идёт 0:42» с неопределённой полосой после
порога (например 5 с). Чисто на фронте `startedAt` = момент прихода `tool_use`, но он не
переживёт переподключение. Лучше бэк пробрасывает `task_started` (у него точный старт
Bash/агента) в событие с `toolUseId` и временем — тогда таймер честный и после reload.
Объём: только фронт ~0,5 дня; с бэком и сохранением в историю ~1 день.

**(б) Реальный процент точечно.** Реального процента ни у кого нет, поэтому это «стадия +
оценка» там, где она уже считается. Канал: общее событие `tool_progress(toolUseId, stage,
percent?, etaSeconds?)`; ключ `toolUseId` берём из `_meta.claudecode/toolUseId` вызова MCP.
Первые источники: `local_jobs_wait` (позиция в очереди → «генерация ~60%»), генерации
редактора (событие уже есть, надо привязать к карточке инструмента), `task_progress`
сабагентов («Running: Bash», 3 вызова, 0:12). Объём: канал + local-media + сабагенты ~2–3 дня;
настоящий процент ComfyUI через его WebSocket (шаги сэмплера) — ещё ~1–2 дня. Bash, dotnet,
загрузки этим путём не покрываются вовсе.

**(в) MCP-инструмент `progress_report`.** С инвариантом совместим: статический инструмент
в существующем тулсете, состав `tools/list` от хода не зависит. Но для поставленной цели не
годится: модель стоит, пока ждёт свой инструмент, поэтому доложить этап ВО ВРЕМЯ долгого
Bash/генерации она не может — только между шагами. А «этапы между шагами» уже видны: план
через TaskCreate/TaskUpdate рисуется карточкой. Плюс токены и лишний ход на каждый доклад.
Объём ~1 день, ценность низкая.

## Рекомендация

Делать **(а) с бэковым `startedAt` из `task_started`** — покрывает все инструменты сразу,
включая Bash, dotnet и загрузки, и это единственное, что для них вообще возможно. Следом
дешёвый кусок **(б)**: маппинг `task_progress` сабагентов и прогресс `local_jobs_wait` по
`toolUseId` из `_meta`. Вариант **(в)** не делать.

## Этап 2 — что сделано (2026-10-02)

- Событие `tool_progress` (`ToolProgressMessage`, привязка к `toolUseId`): стадия, подпись,
  оценка процента (потолок 95), место в очереди, ETA; у сабагента — последний инструмент,
  число вызовов, длительность. В историю не пишется — живое.
- Сабагенты: `system/task_progress` больше не выбрасывается (`ClaudeSession.ParseTaskProgress`).
- `_meta.claudecode/toolUseId` по нашему MCP-over-HTTP **подтверждён живым CLI**: тело
  `tools/call` несёт `"_meta":{"claudecode/toolUseId":"toolu_…","progressToken":2}`. Контроллер
  кладёт id в `McpToolCallContext.ToolUseId` (свойство вызова — на `tools/list` не влияет).
- local-media: `local_jobs_wait` на каждом опросе шлёт прогресс в чат через
  `ISessionBroadcaster.ToSession`, мимо CLI. Несколько задач сводятся в одну строку.
- Редактор картинок не тронут сознательно: `image_generate` возвращается сразу, карточка
  инструмента закрывается за доли секунды, а живой прогресс задачи уже рисует своя карточка
  запуска (`ImageLaunchCard` по `image_edit_progress`). Дублировать его на закрытой карточке
  незачем.
- Не покрыто: фоновые сабагенты (`run_in_background`) — их карточка закрыта квитанцией
  запуска, прогресс ложится только на идущую; карточка консультации персоны показывает свою
  «Активность» вместо строки прогресса.

Попутно: `HandleTaskStarted` кладёт в `PendingBg` и foreground-Bash (CLI теперь шлёт
`task_started` с `is_backgrounded:false`). Сейчас это безвредно — `task_notification` снимает
запись до `tool_result`, — но учитывать при правках учёта фоновых задач.

## Этап 3 — настоящие шаги ComfyUI (2026-10-02)

- `ComfyProgressListener` (фоновая служба подсистемы images) слушает WebSocket ComfyUI
  `/ws?clientId=ccs-local-media` — тот же `client_id`, с которым `ComfyClient` ставит граф:
  события `progress` ComfyUI шлёт только сокету этого клиента. Подключается, лишь пока есть
  незавершённые наши задачи и включён `LocalMedia:Enabled`; задачи кончились — сокет закрыт.
  Двоичные кадры (превью latent2rgb) читаются и выбрасываются. Граф по-прежнему только из
  `ComfyWorkflows`: слушатель ничего не отправляет.
- `ComfyProgressTracker` держит шаги только НАШИХ незавершённых прогонов (`store.IsActivePrompt`):
  `progress` → «шаг N из M», смена ноды с прогрессом → «этап K» (у видео семплеров несколько),
  `executing` с `node = null` и `execution_*` снимают запись. Связь `prompt_id ↔ задача` — поле
  `LocalMediaJob.PromptId`, `задача ↔ карточка` — `toolUseId` вызова `local_jobs_wait` (этап 2).
- В `tool_progress` — `Exact = true`, процент по шагам (потолок 99) и `Label` «шаг N из M»;
  фронт рисует его сплошной заливкой без «≈». `Exact` — только если шаги известны у ВСЕХ идущих
  задач, иначе прежняя оценка пунктиром.
- Откат тихий: ComfyUI выключен, сокет оборвался или закрыт — запись шагов сбрасывается, лог
  только Debug, повтор с паузой 1→30 с; опрос `local_jobs_wait` от сокета не зависит и остаётся
  на оценке по ETA.
- Ограничение: два наших инстанса (дев и бой) на одном ComfyUI делят `clientId`, и сокет второго
  вытесняет первый — у вытесненного прогресс откатывается на оценку.
- **Живьём не проверено** (ComfyUI на машине разработки не поднят): только тесты на записанной
  последовательности событий и фейковом WS-сервере (`ComfyProgressTests`).
