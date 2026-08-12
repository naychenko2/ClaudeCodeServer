# scripts/ops — разовые операционные скрипты

Скрипты для точечной починки данных инстанса. Каждый — с сухим прогоном по умолчанию,
собственным бэкапом и записью в этом файле: что чинилось, когда и почему.

---

## fix-local-action-route-prefix.ps1

**Что чинит.** Маршрут места применения модели, сохранённый голым именем модели прямого
адаптера — `auto:smart` вместо `direct:auto:smart`. Без префикса значение попадает в форму
«id модели провайдера» ([ADR-009](../../docs/adr/ADR-009-local-action-route-format.md) §1,
форма 8) и уезжает в claude CLI вместо прямого HTTP-адаптера. Место при этом выглядит
настроенным, а фактически каждый вызов падает по таймауту и работает страховкой цепочки.

**Когда обнаружено.** 2026-08-12, прод (`C:\ClaudeData\prod`). Найдено 5 записей:

| Место | Было | Стало |
|---|---|---|
| `chat-retitle` | `auto:fast` | `direct:auto:fast` |
| `team-memory-compress` | `auto:fast` | `direct:auto:fast` |
| `persona-memory-autolearn` | `auto:smart` | `direct:auto:smart` |
| `team-memory-autolearn` | `auto:smart` | `direct:auto:smart` |
| `dossier-summary` | `auto:smart` | `direct:auto:smart` |

Модель и поставщик не меняются — правится только форма записи маршрута.

**Свидетельство дефекта** (`C:\ClaudeServer\prod\logs\server.log`):

```
09:00:07  cheap-runner: действие persona-memory-autolearn — модель auto:smart
          недоступна, иду дальше по цепочке
          LlmTimeoutException at OneShotClaudeRunner.RunCliAsync
```

против места с корректным префиксом (`chat-title` = `direct:auto:fast`):

```
09:26:42  cheap-runner: действие chat-title — прямой вызов auto:fast ...
```

**Критерий «кривизны» берётся из конфига, не хардкодом:** чинится только значение, чья
модель объявлена источником прямого адаптера (`CheapHttpSources:{key}:Models` или
`OpenRouter:DirectModels`) — то есть у которого есть канонический вид `direct:{id}` в
каталоге `/api/models`.

### Чего скрипт НЕ трогает (и почему это правильно)

- `fusion` (18 мест) и `MiniMax-M3` (в шагах пресетов) — модели **CLI-провайдеров**
  (freellmapi, minimax). Для них голое имя каноническое (ADR-009 §1, форма 8). Они тоже
  валятся в лог по таймауту, но это **не формат маршрута**, а медленный CLI-транспорт —
  отдельная задача (для `fusion` — добавить её в `CheapHttpSources:freellmapi:Models`).
- Приписать `direct:` модели CLI-провайдера — **сломать рабочее значение**: `ResolveSource`
  не найдёт такой id ни в одном источнике и уедет в первый настроенный (freellmapi) с чужим
  именем модели.

---

### Порядок применения на проде

Прод (`C:\ClaudeServer\prod\ClaudeHomeServer.exe`) запущен трей-супервизором
`ClaudeHomeServer.Tray.exe`, который **авто-перезапускает сервер через 3 с после падения**.
Поэтому останавливать через `Stop-Process` бесполезно — только через меню трея.

Рестарт не graceful (`Kill` по дереву процессов) — оборвёт идущие чаты, ходы задач и
фоновые действия. Выбирать момент, когда на проде не идёт активная работа.

1. **Сухой прогон** — убедиться, что список правок тот же (скрипт ничего не пишет):

   ```powershell
   cd C:\Sources\ClaudeCodeServer\scripts\ops
   .\fix-local-action-route-prefix.ps1 -DataPath C:\ClaudeData\prod -AppSettingsDir C:\ClaudeServer\prod
   ```

2. **Остановить сервер:** правый клик по иконке в трее → **«Выход (остановить сервер)»**.

3. **Применить правку** (скрипт сам снимет бэкап перед записью):

   ```powershell
   .\fix-local-action-route-prefix.ps1 -DataPath C:\ClaudeData\prod -AppSettingsDir C:\ClaudeServer\prod -Apply
   ```

4. **Запустить сервер:** снова запустить `C:\ClaudeServer\prod\ClaudeHomeServer.Tray.exe`
   (он поднимет бэкенд сам).

> Более короткий вариант без остановки: прогнать шаг 3 на живом сервере и сразу сделать
> в трее **«Перезапустить сервер»**. Работает потому, что стор читает `local-actions.json`
> только при старте. Риск — узкое окно: если в эти секунды кто-то сохранит маршрут в
> разделе «Поставщики моделей», сервер перезапишет файл своим снимком из памяти и правка
> молча потеряется. При остановленном сервере такого окна нет.

**Почему обязателен рестарт.** `LocalActionOverridesStore` читает файл только при старте и
переписывает его целиком из снимка в памяти при любом сохранении из UI. Правка файла без
рестарта не применяется и может быть затёрта.

---

### Бэкап

- **Снят до правок, вручную:**
  `C:\ClaudeData\prod\backups\ops\local-action-route-prefix-20260812-112121\`
  — `local-actions.json` (SHA256 `AF7BFE869D5DC73F0238AD9139158EBE3B5B158E930EC9DCF98C6BD0F8FCCCF0`)
  и `specialty-settings.json` (для контекста, скрипт его не трогает).
- **Скрипт при `-Apply` делает свой бэкап** в
  `<DataPath>\backups\ops\local-action-route-prefix-<timestamp>\`: исходный
  `local-actions.json` + `changes.json` со списком применённых замен. Путь печатается в вывод.

### Откат

1. Остановить сервер через трей («Выход (остановить сервер)»).
2. Вернуть файл из бэкапа:

   ```powershell
   Copy-Item 'C:\ClaudeData\prod\backups\ops\local-action-route-prefix-20260812-112121\local-actions.json' `
             'C:\ClaudeData\prod\local-actions.json' -Force
   ```

3. Запустить `ClaudeHomeServer.Tray.exe`.

### Проверка результата

1. **Повторный сухой прогон** — тем же критерием, которым искали. Должен сказать, что
   чинить нечего:

   ```powershell
   .\fix-local-action-route-prefix.ps1 -DataPath C:\ClaudeData\prod -AppSettingsDir C:\ClaudeServer\prod
   # ожидаемо: «Записей с маршрутом без префикса не найдено — чинить нечего.»
   ```

2. **Глазами по файлу** — голых `auto:*` не осталось, только префиксные:

   ```powershell
   # пусто = хорошо (ищем ":\"auto:" без direct:)
   Select-String -Path C:\ClaudeData\prod\local-actions.json -Pattern '"[^"]+":"auto:[^"]+"'
   # для сравнения — префиксные на месте
   Select-String -Path C:\ClaudeData\prod\local-actions.json -Pattern 'direct:auto:' -AllMatches
   ```

3. **По логу — что сценарий реально отрабатывает.** После рестарта дождаться вызова
   починенного места (`persona-memory-autolearn` дёргается раз в несколько минут) и
   убедиться, что вместо «недоступна» идёт прямой вызов:

   ```powershell
   Select-String -Path C:\ClaudeServer\prod\logs\server.log `
                 -Pattern 'persona-memory-autolearn|team-memory-autolearn' | Select-Object -Last 5
   # было:  «модель auto:smart недоступна, иду дальше по цепочке»
   # стало: «прямой вызов auto:smart …» либо тишина (шаг отработал без warning'а)
   ```
