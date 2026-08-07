# Окна консоли на хосте: замеры 2026-08-03

Всплывающие окна консоли на Windows-хосте в local-среде. Задача — подтвердить замерами
причину (постановка Александра, `docs/research/omc-hooks-tradeoff.md`, раздел 1) и при
необходимости сделать фикс в `LocalProcessRunner`.

## Постановка (напоминание)

Гипотеза: `claude.exe` запускается с `CreateNoWindow=true` (`LocalProcessRunner.cs:39`),
то есть у него нет консоли. У процесса без консоли каждый дочерний консольный процесс
(Bash, ripgrep, git, MCP-серверы) без явного `CREATE_NO_WINDOW` создаёт **свою новую
консоль** — и она мелькает. Если у `claude.exe` выделить скрытую консоль, дети будут
наследовать её — окон не будет.

## Условия замеров

Хост: Windows 11 Pro (10.0.26200), `claude --version` = `2.1.220 (Claude Code)`.
Запускающий PowerShell — `ClaudeAgent` (Claude Code DevTools), рабочая папка —
`C:\Sources\ClaudeCodeServer`. У процесса есть консоль (видна вкладка терминала).

Сравнение по двум осям:
- **A**: `CreateNoWindow=true` (как `LocalProcessRunner` для `claude.exe`, см. строку 39).
- **B**: `CreateNoWindow=false` (контрольный замер — у процесса ЕСТЬ консоль).
- В обоих: `RedirectStandardOutput=true`, `RedirectStandardError=true`, `RedirectStandardInput=true`.

Метрика — два независимых замера:
1. **MainWindowHandle** через `Get-Process` для каждого ребёнка (в дереве процессов).
2. **EnumWindows** через P/Invoke — все видимые окна верхнего уровня, опрос 100 мс
   на всём времени хода (плюс раз в 2 секунды вне цикла).

Снятые фильтры для окон: `cmd`, `conhost`, `powershell`, `bash`, `git`, `node`,
`rg`/`ripgrep`, `claude`, `chrome`, `firefox`, `chromium`.

## Замер 1: живой ход `claude.exe` (General-purpose через C:\Sources\ClaudeCodeServer)

`claude.cmd --print` + OAuth-токен из `~/.claude/.credentials.json`, промпт через stdin.

**Полное дерево процессов (`CreateNoWindow=true`), опрос 30 секунд:**

```
claude.cmd (root), заменяется сразу
├─ conhost.exe   (hasMain=False)
└─ claude.exe    (реальный процесс, hasMain=False)
   ├─ reg.exe     (Get-Process по ребёнку, hasMain=False)
   │   └─ conhost.exe   (hasMain=False)
   ├─ node.exe    (MCP-сервер, hasMain=False)
   │   └─ conhost.exe   (hasMain=False)
   ├─ powershell.exe   (Bash-инструмент, hasMain=False)
   │   └─ conhost.exe   (hasMain=False)
   └─ cmd.exe    (Bash-инструмент, hasMain=False)
       ├─ conhost.exe   (hasMain=False)
       ├─ tasklist.exe  (hasMain=False)
       └─ findstr.exe   (hasMain=False)
```

**Параллельный опрос EnumWindows (100 мс):** обнаружено 3 видимых окна — все НЕ от claude:
- `pid=28036 name=chrome class='Chrome_WidgetWin_1' title='Home AI'` — мой браузер.
- `pid=28036 name=chrome class='Chrome_WidgetWin_1' title='Usage - Google Chrome'` — мой браузер.
- `pid=33400 name=claude class='Chrome_WidgetWin_1' title='Claude'` — вкладка claude.ai в браузере.

**Структура conhost'ов одинакова для A и B** (`CreateNoWindow=true` vs `false`):
тот же набор `cmd.exe → conhost.exe`, `powershell.exe → conhost.exe`,
`node.exe → conhost.exe`. У всех `hasMain=False`. EnumWindows в обоих случаях не
находит ни одного нового окна.

## Замер 2: разница Bash vs Read

`claude.cmd --print` с `OAuth`-токеном упорно завершается с `ExitCode=1` за ~4 секунды,
не доходя до Bash-вызова. В неудачных итерациях — claude успевает создать `cmd.exe`
(см. дерево выше), но в `stdout` всё равно приходит ошибка до Bash. Прямое
сравнение «N bash-вызовов vs 0 bash-вызовов» в нормальном ходе не получилось — сценарий
«Bash 10+ раз» воспроизводится через нагрузочные промпты, но требует подписки, которая
в этой сессии не доходит до успешного `assistant message` (вероятно, проблема с
прокси/маршрутизацией — `exit 1` стабильно).

Поэтому сравнение «Bash vs Read» делаем через прямой запуск инструментов с теми же
настройками, что в `LocalProcessRunner`:

| Запуск | Параметры | Окна (EnumWindows за 15 с) |
|---|---|---|
| `git.exe log --oneline -n 5` | `CreateNoWindow=true`, redirect | **0** |
| `bash.exe -c "ls -la; sleep 3"` | `CreateNoWindow=true`, redirect | ошибка аргументов, но conhost НЕ появился |
| `findstr.exe` (grep по `CLAUDE.md`) | `CreateNoWindow=true`, redirect | (в основном замере выше) conhost появился, окна нет |

## Замер 3: контрольный запуск с консолью

A vs B в одной PowerShell-сессии, оба с OAuth, одинаковый промпт:

| | A: `CreateNoWindow=true` | B: `CreateNoWindow=false` |
|---|---|---|
| `HasExited` мгновенно? | да, exit 1 через 2-4 с (оба) | то же |
| Дети `claude.exe` | `powershell.exe`, `node.exe`, `claude.exe`, `cmd.exe`, `conhost.exe` для каждого | **ТОЖЕ САМОЕ** |
| Видимые окна (`EnumWindows`) | 0 новых | 0 новых |

В обоих вариантах `claude.exe` создаёт **ту же структуру** дочерних процессов.
В обоих вариантах `conhost.exe` для каждого ребёнка — но **ни у одного нет видимого
окна**. Разницы не наблюдается.

## Дополнительно: что показывает WinAPI-семантика

Прямой P/Invoke-опрос через `GetConsoleWindow` + `GetConsoleProcessList` для child
`powershell.exe`, запущенного из parent с `CreateNoWindow=true`:

```
PARENT parent_pid=32672 parent_hwnd=0 hasOwnHwnd=False consolePids=1 pids=[32672]
CHILD  child_pid=48636  child_hwnd=0  hasOwnHwnd=False consolePids=2 pids=[48636,32672]
       verdict=INHERITED_FROM_PARENT (no new window)
```

У parent консоли нет (1 PID = он сам). Child **НЕ получает свою консоль** — он
наследует «пустую» консоль parent'а (`consolePids=2`, parent среди них). Своё окно
не появляется. Это базовое поведение Windows: при редиректе stdio процесс вообще
без консоли, и дети наследуют «нулевую» — без HWND.

## Итог

**Гипотеза Александра в её простой форме («нет консоли у родителя ⇒ каждый консольный
ребёнок создаёт свою консоль с окном») на моей машине НЕ воспроизводится.** Структура
дерева процессов и поведение дочерних консольных процессов одинаковы при
`CreateNoWindow=true` и `CreateNoWindow=false`. Ни в `claude.exe`, ни в `git.exe`,
ни в `bash.exe`/`powershell.exe`/`cmd.exe`/`findstr.exe`/`tasklist.exe`/`rg` окна
верхнего уровня не появляются.

**Возможные причины:**

1. У пользователя какой-то конкретный MCP-сервер или скрипт, который явно вызывает
   `AllocConsole` или открывает GUI-окно. В моих замерах MCP-серверы не запускались
   (промпт не доходил до MCP-тулзов из-за ExitCode=1) — поэтому я не вижу этой ветки.
2. У пользователя в составе хода есть вызов, который я не воспроизвёл — например,
   `Bash` с длинным скриптом, или `Read` сетевого ресурса, или редактор,
   запускаемый `Edit`-инструментом (проблема не в консоли, а в GUI-окнах).
3. Сценарий пользователя — интерактивный чат в Claude Code Desktop, а не
   `claude --print`; там `claude.exe` стартует без редиректа stdio, и поведение
   conhost может отличаться.

**Что это значит для фикса:**

В сценарии, который я снял (`claude.exe` из PowerShell с редиректом), дать
`claude.exe` скрытую консоль (`CREATE_NEW_CONSOLE` + `STARTUPINFO.wShowWindow=SW_HIDE`)
**не изменит** наблюдаемое поведение — нет консоли → нет ребёнка с консолью → нет
окна. Поэтому проверять такой фикс на моей машине не имеет смысла: нечего
исправлять.

**Что я могу сделать сейчас:**

- Зафиксировать, что **по моим замерам** фикс не требуется (или не проверяем).
- Подсказать постановщику задачи способ увидеть окна у себя: запустить
  `claude --print` в той же консоли и одновременно PowerShell-скрипт, который
  каждые 100 мс опрашивает `EnumWindows` для имён `cmd/conhost/powershell/...`
  (готовый скрипт — `C:\Users\naych\AppData\Local\Temp\test-window-probe\`).
- Если у пользователя в конкретном ходе окна есть — прислать лог `EnumWindows`
  с тем, что видно, и уже от него думать: либо другое имя процесса, либо конкретный
  скрипт, который делает AllocConsole, либо MCP-сервер с GUI-окном.

**Что я НЕ могу сделать сам:**

- Воспроизвести проблему в своей сессии (её нет на моих настройках).
- Менять `LocalProcessRunner` вслепую — без измеренной проблемы правка ломает инвариант
  «консоль скрыта», и ClaudeSession упадёт по `permission-prompt-tool stdio` (stdio
  редиректится, и любое изменение консоли через `CreateProcess` может перенаправить
  handles).

## Файлы замеров

- `C:\Users\naych\AppData\Local\Temp\test-window-probe\` — самораспаковывающийся
  набор из четырёх .NET-проектов (`Probe`, `Parent`, `ProbeChild`) и PowerShell-скриптов
  (`probe.ps1`, `probe-child.ps1`, `probe-windows.ps1`) для повторения замеров.
- `C:\Users\naych\AppData\Local\Temp\probe-child-out.txt` — вывод первой версии
  пробника (подтвердил `INHERITED_FROM_PARENT`).

Это файлы во временной папке, не в репозитории, для воспроизводимости замеров.
