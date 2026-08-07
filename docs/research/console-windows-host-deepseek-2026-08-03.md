# Окна консоли на хосте: замер на живом ходе через DeepSeek (прод-инстанс)

Продолжение замера от 2026-08-03 (`docs/research/console-windows-host-2026-08-03.md`).
Цель — нагрузить ход так, чтобы модель реально поработала инструментами, и поймать
окно от любого ребёнка `claude.exe`.

## Условия замера

- **Хост:** Windows 11 Pro (10.0.26200), ClaudeHomeServer.exe PID 37040, активен.
- **Прод-инстанс:** `C:\ClaudeServer\prod\ClaudeHomeServer.exe` (80/443).
- **Модель:** `deepseek-chat` (провайдер `deepseek`, env `ANTHROPIC_BASE_URL` →
  сторонний эндпоинт). Weekly limit Claude не мешает.
- **Пользователь:** andrey (admin), проект ClaudeCodeServer
  (RootPath = `C:\Sources\ClaudeCodeServer`).
- **Сессии:** две новые — `1c326442-04c4-4453-9c27-b92a730d782e` (10 шагов),
  `f68f962e-4f08-4737-a34c-5bf32a96cab2` (7 коротких шагов).
- **Пробник:** фоновый PowerShell-Job, `EnumWindows` (P/Invoke) каждые 100 мс
  по фильтру `cmd|conhost|powershell|bash|git|node|claude|rg|ripgrep|findstr|
  tasklist|wt|windowsterminal` + классы `ConsoleWindowClass,
  CASCADIA_HOSTING_WINDOW_CLASS, HwndWrapper, TApplication`. Файлы:
  - `C:\Users\naych\AppData\Local\Temp\console-window-probe-2026-08-03\windows.jsonl`
  - `C:\Users\naych\AppData\Local\Temp\console-window-probe-2026-08-03\procs.jsonl`
  - `C:\Users\naych\AppData\Local\Temp\console-window-probe-2026-08-03\errors.log`
- **Длительность:** ~7 минут (пробник остановлен после завершения замера).

## Дерево процессов моего хода (PID 33480, deepseek-chat)

PPID 37040 (ClaudeHomeServer.exe) → 33480 (claude.exe, CmdLen=13714) →:
- 16 × `node.exe` — MCP-серверы (CmdLen 48-91)
- 2 × `cmd.exe` — shell-обёртки для bash-инструмента
- 1 × `conhost.exe`
- 1 × `pythonw.exe` — не наш процесс, лежит на пути MCP-сервера
- ещё `node.exe` — совместный с предыдущим ходом 39300

Параллельно в системе:
- **33400** (`MultiInstanceforClaude_*.exe`, PPID 34700) — **Claude Desktop из
  Microsoft Store**, HWND=134366, Title="Claude". Это НЕ наш процесс —
  пользовательский Claude Desktop.
- **42788** (`Claude.exe` в `WindowsApps\Claude_1.24012.9.0_x64_…`) — другой
  Claude Desktop, HWND=0.

## Окна, обнаруженные пробником

За 7 минут — **4 уникальных** окна, все НЕ от нашего хода:

| HWND | PID | Процесс | Класс | Заголовок |
|---|---|---|---|---|
| 1508368 | 28036 | chrome.exe | Chrome_WidgetWin_1 | Home AI |
| 1117824 | 28036 | chrome.exe | Chrome_WidgetWin_1 | Usage - Google Chrome |
| 134366 | 33400 | claude.exe | Chrome_WidgetWin_1 | Claude |
| 132514 | 13848 | Onyx.exe | Chrome_WidgetWin_1 | Onyx |

Первые два — браузер. 134366 — пользовательский Claude Desktop (см. выше).
Onyx — десктопный клиент, к нашему стеку не относится.

**MainWindowHandle у дочерних `node.exe` / `cmd.exe` / `conhost.exe` /
`powershell.exe` от хода 33480 — везде 0.** Ни одного нового видимого окна.

## Итог

**Подтверждается вывод первого замера: на нашей стороне проблема не
воспроизводится.** Даже на полном живом ходе через DeepSeek (Bash-стресс,
7-10 команд, 22 ребёнка у `claude.exe`) — окон от дочерних процессов нет.

**Гипотеза 1. «Claude Desktop» путают с CCS.** Постоянное окно с заголовком
"Claude" на хосте — это **Claude Desktop из Microsoft Store** (PID 33400,
пакет `MultiInstanceforClaude`). У него всегда HWND=134366, и он живёт
независимо от того, что происходит в CCS. Если пользователь жалуется на
«окно Claude, мигнувшее при ходе», это либо:
- Claude Desktop, открытый **до** хода (живёт постоянно),
- вкладка `claude.ai` в Chrome (HWND=134366, другое окно, тоже постоянно).

**Гипотеза 2. Не воспроизводимый сценарий.** Сценарий пользователя — что-то,
чего мой замер не покрыл: фоновые операции (теги, сводки, чек-листы),
бэкапы, задачи с другими Claude-подписками, периодические уведомления,
panel-сервисы, persona-asks. Эти ветки я не запускал.

**Что не получится поймать средствами замера:**
- Окна от **`claude.exe` в интерактивном режиме** (без `--print`): его
  поведение отличается, консоль может быть привязана к терминалу.
- Окна от **самого Claude Desktop** — это отдельный exe, к ClaudeHomeServer
  отношения не имеет.
- Окна от **MCP-серверов, которые мы не подключаем** в обычной поставке.

## Что нужно от владельца, чтобы продвинуться

Поскольку мои замеры показывают 0 окон, нужна конкретика с его стороны:

1. **Скриншот или Process Hacker-снимок** момента, когда окно мигнуло.
   PID, имя процесса, заголовок.
2. **Какое действие запустило** — обычный чат, задача-исполнитель, фоновая
   операция (теги/сводки/чек-листы), бэкап, панель «Сервисы», автопилот,
   persona-ask, что-то ещё.
3. **Где именно экран** — центр, угол, поверх IDE, поверх чата CCS.
4. **Видно ли заголовок** — `cmd`, `conhost`, `bash`, `Claude`, `Editor`,
   `Connecting…`, что-то ещё.
5. **Свежесть проблемы** — было всегда или появилось после последних
   релизов. Помогает понять, связано ли с хуками (они сняты 2026-08-03).

После этого либо:
- повторю замер на конкретном сценарии, который покажет владелец,
- либо будет ясна ветка кода (конкретный MCP-сервер, конкретный запуск)
  и фикс пойдёт точечно, без вмешательства в `LocalProcessRunner`.

## Файлы замера

- `C:\Users\naych\AppData\Local\Temp\console-window-probe-2026-08-03\` —
  скрипт пробника, `windows.jsonl`, `procs.jsonl`, `errors.log`.
- Сам протокол — этот файл.
