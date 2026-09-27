# HandsBridge — источник

Форк [sbroenne/mcp-windows](https://github.com/sbroenne/mcp-windows), тег `v1.3.24`
(коммит `b90485c3d1a228fc4f5341bc2bdfc7be7672c6d0`), каталог `src/Sbroenne.WindowsMcp`.
Лицензия MIT — `LICENSE` рядом (копия оригинала), в публикацию едет как `HandsBridge.LICENSE.txt`.

Назначение — руки локального проекта (ADR-016, раздел «Руки»; план
`docs/research/hands-plan-2026-09.md`, Ш2). Каждый инструмент до любого действия зовёт гейт
`HandsPolicy` из проекта `HandsBridge.Policy` (чистый `net10.0`, тесты — `HandsBridge.Policy.Tests`
на Linux). Запуск моста: `HandsBridge.exe --apps-file <путь к hands-apps.json>`; без аргумента
белый список пуст и гейт закрыт.

## Что удалено из upstream

Классы инструментов вне набора `app`, `ui_snapshot`, `ui_find`, `ui_click`, `ui_type`, `ui_read`,
`window_management`, `screenshot_control` — удалены из сборки, а не выключены флагом:

- `Tools/KeyboardControlTool.cs`, `Tools/MouseControlTool.cs`;
- `Automation/Tools/{UIBatch,UIFile,UIOpenFile,UIReadTable,UISelect,UIWait}Tool.cs`;
- `Clipboard/Tools`, `Macros/Tools`, `Processes/Tools`.

Сервисы этих подсистем (`KeyboardInputService`, `MouseInputService`, `ClipboardService`,
`MacroService`, `ProcessService`) оставлены: их создаёт `WindowsToolsBase`, а автоматизация
пользуется вводом изнутри. Модели до них дотянуться нечем — сторож
`HandsBridgeSourceGuardTests.Bridge_exposes_exactly_the_hands_tools`.

## Правленые файлы upstream

Правки сводятся к вызову гейта и удалению опасных веток. Своё (не upstream) — `Hands/*`.

| Файл | Правка |
|---|---|
| `Tools/AppTool.cs` | гейт `CheckLaunch`; запуск нормализованного пути из белого списка, `UseShellExecute=false`, процесс — во вложенный Job (не вышло — гасим); удалены поиск окна по заголовку для «заглушек» и «любое окно процесса с тем же именем»; окно ищется только среди своих; описание для модели |
| `Automation/Tools/UIClickTool.cs`, `UITypeTool.cs`, `UIFindTool.cs`, `UISnapshotTool.cs`, `UIReadTool.cs` | гейт `CheckUi` (своё окно + своё окно у каждого `elementId`/`parentElementId`/`nearElement`); у `ui_snapshot` убран фолбэк на окно переднего плана; у `ui_read` удалён OCR-фолбэк, копировавший прямоугольник окна с экрана |
| `Tools/WindowManagementTool.cs` | гейт `CheckWindowAction`; `list`/`find` — только свои окна; `get_foreground` — только если своё; `wait_for` (поиск по заголовку по всему столу) — отказ |
| `Tools/ScreenshotControlTool.cs` | гейт `CheckScreenshot`: только `target='window'` своего окна, только `inline` |
| `Capture/ScreenshotService.cs` | удалён фолбэк снимка окна через копию области экрана (снимал и чужие окна поверх своего) |
| `Input/KeyboardInputService.cs` | `CheckKeys`/`CheckKeyDown` по виртуальному коду: Win, Alt+Tab, Alt+Esc, Ctrl+Esc, Ctrl+Shift+Esc, Ctrl+Alt+… не уходят никогда |
| `Program.cs` | аргумент `--apps-file`, `HandsGate.Configure` до старта хоста |
| `GlobalUsings.cs` | пространства имён гейта |
| `HandsBridge.csproj` | ссылка на `HandsBridge.Policy`, комментарий |

## Синхронизация с upstream

1. Выгрузить новый тег upstream во временный каталог, взять `src/Sbroenne.WindowsMcp`.
2. Снять diff upstream между `v1.3.24` и новым тегом: `git diff v1.3.24 <тег> -- src/Sbroenne.WindowsMcp`.
3. Выкинуть из него файлы удалённых классов (список выше) и применить остальное к этому каталогу
   (`git apply --3way`). Конфликты ожидаемы только в файлах из таблицы правок.
4. Разобрать новое в upstream на предмет обхода гейта: новый инструмент (`[McpServerTool]`) — либо
   удалить, либо завести гейт и строку в `HandsTools`; новые параметры с hwnd или `elementId`;
   новые фолбэки на окно переднего плана, поиск по заголовку, копию экрана, `UseShellExecute`.
5. Обновить тег и sha в шапке этого файла и `<Version>` в `HandsBridge.csproj`.
6. `dotnet build backend/ClaudeHomeServer.slnx -warnaserror` и `dotnet test` проекта
   `HandsBridge.Policy.Tests` — сторожа по исходникам краснеют, если гейт потерялся при слиянии.
