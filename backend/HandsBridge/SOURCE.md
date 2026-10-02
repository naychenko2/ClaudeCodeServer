# HandsBridge — источник

Форк [sbroenne/mcp-windows](https://github.com/sbroenne/mcp-windows), тег `v1.3.24`
(коммит `b90485c3d1a228fc4f5341bc2bdfc7be7672c6d0`), каталог `src/Sbroenne.WindowsMcp`.
Лицензия MIT — `LICENSE` рядом (копия оригинала), в публикацию едет как `HandsBridge.LICENSE.txt`.

Назначение — руки локального проекта (ADR-016, раздел «Руки»; план
`docs/research/hands-plan-2026-09.md`, Ш2). Каждый инструмент до любого действия зовёт гейт
`HandsPolicy` из проекта `HandsBridge.Policy` (чистый `net10.0`, тесты — `HandsBridge.Policy.Tests`
на Linux). Запуск моста: `HandsBridge.exe [--exclude-tools screenshot_control]` — узел подставляет
агент (Ш3). Белого списка программ нет (решение владельца 2б, 2026-09-27): запрещены только
интерпретаторы и терминалы `HandsForbiddenApps`. Границы «только свои окна» нет (вторая волна
решений владельца 2026-09-27, ADR-016 §7): любое окно и снимок экрана разрешены, `--turn-job`
снят; в окна `HandsForbiddenApps` руки не вводят.

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

Мост едет в каталоге версии агента и делит его рантайм (2026-09-27), поэтому из сборки убраны
WinForms и проекция Windows SDK — они добавляли к архиву агента 21 МБ:

- `Capture/LegacyOcrService.cs`, `Models/OcrResult.cs` — OCR (Windows.Media.Ocr); параметр
  `language` у `ui_read`;
- подсветка элемента (`HighlightElementAsync`, `HideHighlightAsync`, форма `HighlightForm` в
  `Automation/UIAutomationService.Actions.cs`) — ни один инструмент её не звал.

## Правленые файлы upstream

Правки сводятся к вызову гейта и удалению опасных веток. Своё (не upstream) — `Hands/*`.
Лог моста — `Hands/HandsLog.cs`: `AiHomeAgent\logs\hands.log` рядом с логами агента (путь
переопределяет `--log`): запуск, отказы гейта, коды ошибок `CreateProcess` и
`AssignProcessToJobObject`.

| Файл | Правка |
|---|---|
| `Tools/AppTool.cs` | гейт `CheckLaunch`; запуск нормализованного полного пути (кроме `HandsForbiddenApps`), `UseShellExecute=false`, процесс — в свой вложенный Job на каждый запуск (не вышло — гасим, отказ с кодом ошибки Windows; процесс уже вышел — отказ «заглушка передала запуск»); запуск и коды ошибок — в `hands.log`; удалены поиск окна по заголовку для «заглушек» и «любое окно процесса с тем же именем» — окно ищется среди окон запущенного PID; описание для модели |
| `Automation/Tools/UIClickTool.cs`, `UITypeTool.cs`, `UIFindTool.cs`, `UISnapshotTool.cs`, `UIReadTool.cs` | гейт `CheckUi`: hwnd обязателен; у `ui_click`/`ui_type` — ни окно, ни окно любого `elementId`/`parentElementId` не из `HandsForbiddenApps`; у `ui_snapshot` убран фолбэк на окно переднего плана; у `ui_read` удалён OCR-фолбэк, копировавший прямоугольник окна с экрана, а пустой текст — отказ `no_text_found` с подсказкой снять окно |
| `Tools/WindowManagementTool.cs` | гейт `CheckWindowAction` (только действия из схемы); `list`/`find`/`get_foreground`/`wait_for` — без фильтра |
| `Tools/ScreenshotControlTool.cs` | гейт `CheckScreenshot`: любая цель, только `inline`, без `outputPath` |
| `Capture/ScreenshotService.cs` | удалён фолбэк снимка окна через копию области экрана (снимал и чужие окна поверх своего) |
| `Input/KeyboardInputService.cs` | `CheckKeys`/`CheckKeyDown` по виртуальному коду: Win, Alt+Tab, Alt+Esc, Ctrl+Esc, Ctrl+Shift+Esc, Ctrl+Alt+… не уходят никогда |
| `Window/WindowEnumerator.cs`, `Capture/MonitorService.cs` | `Screen.AllScreens` → `Native/DisplayMonitors.cs` (EnumDisplayMonitors + MONITORINFOEX) |
| `Capture/ScreenshotService.cs` (снимок всех мониторов) | `SystemInformation.VirtualScreen` → `ScreenBounds.GetVirtual()` |
| `Prompts/*`, `Resources/SystemResources.cs`, `Models/UIAutomationErrorType.cs` | упоминания OCR-фолбэка |
| `Program.cs` | `HandsGate.Configure` до старта хоста (профиль браузера и `--activity-event`) |
| все инструменты, кроме `ui_snapshot`/`ui_find`/`ui_read` | `HandsGate.Acted(...)` сразу после отказа гейта: первое действие хода поднимает событие хода, и агент зажигает плашку (сторож `Tool_reports_its_action_after_the_gate`) |
| `GlobalUsings.cs` | пространства имён гейта; `System.Drawing` вместо неявного от WinForms |
| `HandsBridge.csproj` | ссылка на `HandsBridge.Policy`; TFM `net10.0-windows` без версии SDK, без `UseWindowsForms`, `System.Drawing.Common` пакетом, сателлиты только `en` |

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
