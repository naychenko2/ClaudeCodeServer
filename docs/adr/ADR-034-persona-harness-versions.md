# ADR-034: обвязка персоны под модель — версии, закрепление за чатом, журнал сигналов, офлайн-стенд

- **Статус:** предложено 2026-10-20 (Александр, разрез эпика 19678f0d). Ревизия 2026-11-18 по коду master `75c7ab1dd` (задача 878f7b57): изменено решение 2, уточнены решения 1, 3 и 4, якоря обновлены (раздел «Ревизия 2026-11-18» в конце). Решений Андрея пока нет, открытые вопросы — в заметке ревизии «Разрез: контур настройки обвязки персон (ревизия, 2026-11-18)».
- **Вход:** заметки «Целевое видение: контур настройки обвязки персон (черновик)» (Артём) и «Harness² и автонастройка обвязки агентов — исследование (20.10.2026)». Полный разрез с этапами Ф0–Ф5 — заметка «Разрез: контур настройки обвязки персон (Александр, 2026-10-20)».
- **Связано:** [ADR-007](ADR-007-model-preset-chains.md) §9.1 (модель замораживается при создании чата), [ADR-012](ADR-012-specialty-settings-single-layer.md) (один глобальный слой настроек, запись только админом), [ADR-013](ADR-013-turn-event-bus.md) (шина событий хода), [ADR-014](ADR-014-internal-subsystems.md) (подсистемы, отключаемость), [ADR-032](ADR-032-executor-experience-and-memory-versions.md) (журнал изменений вместо перезаписи, пилот с контролем по хешу).

## Контекст

Якоря — по master на 2026-11-18.

- **Дисциплина промпта зашита в код.** `PersonaPromptBuilder.DisciplineFor` выбирает секции `switch` по ключу провайдера (`backend/ClaudeHomeServer/Services/PersonaPromptBuilder.cs:213-220`). Ключ — имя провайдера в секции `LlmProviders` (`backend/ClaudeHomeServer.Llm/LlmProviderRegistry.cs:14,317`). Ключи `minimax` и `local-qwen` (`backend/ClaudeHomeServer/appsettings.json:313,457`) попадают в ветку по умолчанию `[Brevity, NeverRules]` (`PersonaPromptBuilder.cs:219`); туда же падают `kimi`, `alibabacloud`, `ghostcli` и `openrouter`. Модель персоны задаётся тремя ячейками уровней, а не одним полем: целиком на M3.1 Flash сидят обе Софьи, на `local-qwen` — Денис, Кира и Кузьма. Ещё пять персон держат одну из этих моделей в слабой ячейке (`personas_list` на 18.11).
- **Обвязка собирается из трёх осей:**
  - слой персоны (контракт, дисциплина) — `PersonaPromptBuilder.BuildCore` (`:38`);
  - секции специальности с наложенным слоем персоны — `backend/ClaudeHomeServer.Turn/PromptSectionsContributor.cs:54-63` через `PromptSectionOverlay.Apply` (`backend/ClaudeHomeServer.Core/Services/Llm/PromptSectionOverlay.cs:17`);
  - `## ПРАВИЛА` и `## ЭТАП` в тексте хода исполнителя (`backend/ClaudeHomeServer/Services/TaskExecutionService.cs:874-879`, слепок этапа `backend/ClaudeHomeServer.Core/Models/WorkflowTemplate.cs:196-217`).

  Первые две оси читаются заново каждый ход.
- **У обвязки нет версий.** `PersonaManager.Save` переписывает весь `data/personas.json` (`backend/ClaudeHomeServer/Services/PersonaManager.cs:1687-1691`). В `Persona` нет поля ревизии (`backend/ClaudeHomeServer.Core/Models/Persona.cs:85-217`). Есть только хеш набора секций специальности `SectionsVersion` (`PersonaManager.cs:158-166`) — метка «админ менял секции», а не версия. У `specialty-settings.json` есть только версия формата (`backend/ClaudeHomeServer.Llm/SpecialtySettingsStore.cs:129`). Шаблон процесса хранит номер версии без истории (`WorkflowTemplate.cs:95-96`).
- **Ход нельзя связать с обвязкой.** В паспорте хода нет ни персоны, ни версии промпта (`backend/ClaudeHomeServer.Core/Services/Llm/TurnRunPassport.cs:39-57,70-147`). Хеша системного промпта нет нигде.
- **Повторять ходы нечем.**
  - `OneShotClaudeRunner` не принимает системный промпт и MCP (`backend/ClaudeHomeServer.Llm/OneShotClaudeRunner.cs:199,339-343`).
  - `TaskExecutionService` создаёт настоящие сессии в трекере (`TaskExecutionService.cs:394-396`) и собирает промпт из шаблона (`:452`).

## Решения

### 1. Обвязка = база из `personas.json` + вариант «персона × модель» в отдельном сторе

- **База остаётся в персоне.** Её единственный писатель — `PersonaManager`. Второго места правды для контракта не появляется.
- **Вариант** — дельта поверх базы с ключом модели. Ключ разрешается сверху вниз:
  1. алиас модели из `Session.Model` без суффикса `[1m]` (метка весов — полем ревизии: под одним алиасом веса менялись, см. «Ревизия 2026-11-18»);
  2. ключ провайдера (как сейчас в `switch`);
  3. база.
- **Хранение:** `data/persona-harness/{personaId}.json`, один файл на персону.
  - Ревизии в файле append-only: `{ rev, scope: "base"|<ключ модели>, parentRev, delta, author: "human"|"lab", source, approvedTaskId, createdAt, note }`.
  - В том же файле лежат указатели `active[<ключ>]` и `canary[<ключ>] = { rev, share }`.
  - Каждую правку базы через `PersonaManager.Update` дополнительно пишем ревизией `scope: "base"` со снимком контракта. Так откат базы становится возможным без переноса правды.
- **Схема дельты v1** (ревизия 2026-11-18):
  - `contract` — по слотам: заменить или дописать, для `MustDo` и `MustNot` — добавить или убрать пункт;
  - `sections` — `List<PromptSectionOverride>` поверх слоя секций персоны (`Persona.PromptSections`, `backend/ClaudeHomeServer.Core/Models/Persona.cs:112`), новый `SectionSource.Variant`. Текст — с лимитом `SpecialtyPromptPresets.SectionTextLimit` (`backend/ClaudeHomeServer.Core/Services/SpecialtyPromptPresets.cs:21`);
  - `discipline` — `List<PromptSectionOverride>`, только `Enabled` (решение 2);
  - `bindings` — добавить или убрать привязку, у скиллов обязательно условие «когда» (`SpecialtyPromptPresets.cs:27`);
  - `effort`.

  Хуков на уровне персоны в продукте нет, поэтому в v1 их нет и в дельте. Параметры сэмплинга тоже не входят: `presence_penalty` сейчас глобальная настройка прокси (`tools/thinking-strip-proxy/proxy.py:49-53`), а персональный канал до прокси — отдельное решение.
- **Удаление персоны** уносит её файл. Бэкап получает каталог по умолчанию, но на шаге реализации нужно сверить `BackupPaths` (`backend/ClaudeHomeServer/Services/Backup/BackupPaths.cs:5`) и `BackupValidation` (`backend/ClaudeHomeServer/Services/Backup/BackupValidation.cs:67`).
- **Отклонено:** держать ревизии внутри `personas.json`. Файл переписывается целиком на каждую правку и целиком живёт в памяти, а бэкап валидирует его как список `Persona`.

### 2. Дисциплина под модель — данные с дефолтом, равным нынешнему `switch`

- **Тексты секций остаются константами кода.** Каталог id — `Brevity`, `LeastChange`, `Verification`, `SelfCheck`, `TurnIntent`, `FiveFailures`, `OutcomeFirst`, `NeverRules`, `AntiSlop`. Новые тексты (например «минимальная правка» для Ф0б) добавляются в каталог константами, **выключенными по умолчанию**.
- **Состав по ключу модели** — базовые состояния `EffectivePromptSection` (`SectionSource.Code`) из таблицы-дефолта в коде, байт в байт равной нынешнему `switch`. Их перекрывает глобальный слой `specialty-settings.json`: новый раздел `ModelDiscipline[<ключ>] = List<PromptSectionOverride>`, запись только админом по ADR-012. Поверх этого слоя действует вариант персоны (решение 1).
- **Семантика наследования — та же, что у секций специальности.** Попараметрное наложение одной функцией `PromptSectionOverlay.Apply` (`backend/ClaudeHomeServer.Core/Services/Llm/PromptSectionOverlay.cs:17`): задан — перекрывает, не задан — не затирает. Своего текста у дисциплины нет: `Text` в переопределении дисциплины валидатор отвергает. В таблице осей наследования (`docs/architecture/personas.md`, «Четыре оси наследования») появляется пятая строка с этой же семантикой и этой же точкой.
  - *Ревизия 2026-11-18:* исходная формулировка «побеждает первый заданный список целиком» отвергнута. Она заводила пятую семантику наследования, а правило проекта это запрещает (`docs/architecture/personas.md:400-416`, по итогам разреза «Процессы фичи» 2).
- **Ключ `subagent`** (`PersonaPromptBuilder.cs:36,218`) остаётся провайдер-нейтральным и не переопределяется: модель сабагента на этапе генерации неизвестна.

### 3. Версия обвязки закрепляется за чатом при создании, как модель

- Ревизию варианта, действующую на момент создания, сессия запоминает в `Session.HarnessPin = { key, rev }`. Для чата исполнителя задачи это момент создания сессии в `TaskExecutionService`.
- **Слой персоны** по-прежнему пересобирается каждый ход, но из закреплённой ревизии. Новая утверждённая версия действует только на новые чаты. Так не ломаются prefix cache и неизменность системного блока.
- **Канарейка.** Если у ключа задано `canary`, ревизия выбирается при закреплении по `hash(sessionId) < share`. Это схема пилота ADR-032 (контроль по хешу id).
- **Аварийный откат** — единственный случай, когда закрепление меняется у живых чатов. Он стоит одной сброшенной полосы кеша и выполняется только явным действием админа.
- **Отклонено:** канарейка через `ModelAssignmentResolver`. Он резолвит модель места (`backend/ClaudeHomeServer.Llm/ModelAssignmentResolver.cs:40,52,76`), долей трафика у него нет. Второй смысл в нём — путь ко второй точке правды.

### 4. Журнал сигналов — проекция офлайн-инструмента, а не новый стор

Сервер дописывает только то, чего без него не восстановить:

- **в `TurnRunPassport`** — поля `PersonaId`, `HarnessKey`, `HarnessRev`. Паспорт уже пишется в `data/logs/turn-runs-yyyyMMdd.jsonl` (`backend/ClaudeHomeServer.Llm/TurnRunLog.cs:25,97`) единственным подписчиком `TurnCompleted` (`backend/ClaudeHomeServer/Services/Sessions/TurnPassports.cs:24`, событие `backend/ClaudeHomeServer.Core/Services/Turn/TurnEvents.cs:114`);
- **постоянный журнал событий прокси** `data/logs/llm-proxy-events-yyyyMMdd.jsonl`. Сейчас:
  - `loop_guard` доходит до бэкенда, но оседает только карточкой в истории чата (`backend/ClaudeHomeServer/Controllers/LlmProxyEventsController.cs:54,90`);
  - обрыв по мусору не отправляется вовсе — только счётчик в памяти и дамп (`tools/thinking-strip-proxy/proxy.py:540,2930-2932`).

  Прокси начинает слать `junk_abort`; контроллер, кроме карточки, дописывает строку в журнал. Отправка остаётся fail-open (`proxy.py:1560,1602`).

Всё остальное инструмент стенда собирает join'ом по `sessionId` и `taskId`:

- расход и токены — из `SpendRecord`, где уже есть `PersonaId`, `TaskId` и `Model` (`backend/ClaudeHomeServer.Core/Models/SpendRecord.cs:65-66,86`);
- вердикт, rework и дефекты — из `TaskItem` (`backend/ClaudeHomeServer.Core/Models/TaskItem.cs:81,167-175,237,252`);
- объём диффа — из коммитов с трейлером `CCS-Task`.

Сборка и тесты в журнал с прода **не идут**: машиночитаемого источника нет, а парсить `resultMarkdown` ненадёжно. Объективная проверка делается при повторе (решение 5).

**Срок жизни сырья короткий:**
- паспорта — `Logging:File:RetainDays`, по умолчанию 14 дней (`TurnRunLog.cs:69-70`);
- снимки промпта — 50 на чат (`backend/ClaudeHomeServer/Services/PromptSnapshotStore.cs:22`).

Поэтому сбор случаев копирует сырьё в свой архив сразу.

### 5. Стенд повтора — отдельный офлайн-инструмент, промпт рендерит сервер

- **Инструмент:** `tools/harness-lab/` на python3 без сторонних библиотек, как `tools/thinking-strip-proxy/bench`. Он гоняет `claude` CLI с флагами `ClaudeSession`, в изолированном клоне, со своими `$TMPDIR` и `HOME` и с заглушкой MCP.
  - Заглушка записывает вызовы инструментов с побочными эффектами (`tasks_*`, `notes_*`, `memory_*`, `wsp_*`, `chats_*`) и возвращает заготовленные ответы.
  - Клон содержит только стартовый коммит: урок фазы 0 — эталон находили через `git log --all`.
  - Тяжёлые прогоны запускаются под потолком cgroup `ccs-agents.slice` (инвариант OOM из CLAUDE.md) и через прокси, как на бою. Qwen гоняем только ночью.
- **Слой персоны стенд не собирает сам.** Админская ручка подсистемы `POST /api/persona-harness/render { personaId, model, delta }` возвращает текст через тот же `PersonaPromptBuilder.BuildCore` (`PersonaPromptBuilder.cs:38`) плюс секции специальности (`PromptSectionsContributor.EffectiveSectionsFor`). Сборка промпта остаётся одна.
- **Отклонено:**
  - раннер на `TaskExecutionService`: побочные эффекты в боевом трекере, нет явного промпта и модели;
  - раннер на `OneShotClaudeRunner`: нет системного промпта, MCP и папки;
  - обобщать `bench`: это законченный замер сжатия, его судья абсолютный, 0–5 против эталона (`tools/thinking-strip-proxy/bench/run_bench.py:47-61`). Берём приёмы (кеш прогонов, `compare.py`, `calibrate_judge.py`), общий модуль выносим, когда появится второй потребитель.

### 6. Новая версия становится активной только по кнопке человека

- Цикл улучшения создаёт задачу трекера `role=review`, `assignee=me` с диффом ревизий и отчётом «правка → прогноз → эффект на отложенном наборе».
- Активация и канарейка — админская REST-ручка с `[DenyOnDelegatedTurn]`. **MCP-инструмента нет по построению**, как у запуска обучения LoRA в ADR-026.
- **Автоматика умеет только понижать:** автооткат ставит `share = 0` и возвращает предыдущую активную ревизию. Поднять версию сама она не может никогда.

### 7. Подсистема и флаги

- **Вертикаль `ClaudeHomeServer.PersonaHarness`** (ключ `persona-harness`): стор ревизий, ручки render, activate и rollback, закрепление, монитор канарейки.
- **Швы в Core** — `IPersonaHarnessSource` (вариант по `(personaId, pin)`) и `IHarnessPinner`. Оба потребляются через `?.`: у выключенной подсистемы слой персоны собирается ровно как сейчас, ручки отвечают честным 503 (образец — `backend/ClaudeHomeServer/Controllers/TasksController.cs:545`).
- **Решение 2** (дисциплина в данных) живёт в спине. Флага у него нет: дефолт поведения тождествен нынешнему, и это доказывает тест.
- **Флаг `persona-harness`** (новая строка в `FeatureFlagCatalog.All`, `backend/ClaudeHomeServer.Core/Models/FeatureFlag.cs`; на 18.11 флага нет) открывает UI истории, диффа и отката в карточке персоны.
- **Канарейка** включается конфигом `PersonaHarness:Canary:Enabled`, по умолчанию false.

## Последствия

- **Решение 2 можно делать сразу.** Оно не зависит от остальной фичи и меняет поведение только после явной записи админа.
- **Поля паспорта и журнал прокси нужно начать писать как можно раньше.** Сырьё живёт 14 дней, и банку случаев нужна история.
- **Онлайн-адаптации живых ходов нет и не будет.** Уроки цикла не попадают в recall живых ходов и в обвязку прода — только через утверждённую ревизию.
- **Цена:** одна новая вертикаль, два шва, три поля паспорта, один журнальный файл и раздел в `specialty-settings.json` с инкрементом `FormatVersion`. Новых точек сборки промпта нет.

## Ревизия 2026-11-18

Сверка с master `75c7ab1dd` (задача 878f7b57). Полная таблица «вывод → верно / изменено / отвергнуто кодом» — в заметке «Разрез: контур настройки обвязки персон (ревизия, 2026-11-18)». Здесь только то, что меняет решения.

- **Код ADR не начат.** Grep по `HarnessPin`, `PersonaHarness`, `persona-harness`, `ModelDiscipline`, `harness-lab` пуст.
- **Решение 1, ключ.** Ключ варианта берётся из `Session.Model`, как у дисциплины сегодня (`backend/ClaudeHomeServer.Turn/PersonaLayerContributor.cs:121`). «Модель персоны» не годится: у персоны три ячейки уровней и пресеты-цепочки (`backend/ClaudeHomeServer.Llm/ModelAssignmentResolver.cs:40-77`).
  - Порядок разрешения: алиас без `[1m]` → провайдер → база.
  - Точный id весов не различим: под алиасом `qwen3.8-27b` 23.10 сменились веса. Поэтому метка весов пишется в ревизию (открытый вопрос).
  - Подмена фолбэком посреди чата ключ не меняет.
- **Решение 1, состав.** Дельта правит ещё и слой секций персоны (`sections`), а не заводит свои секции дисциплины. `LightContext` (`Persona.cs:206`) в v1 не входит: он меняет сигнатуру запуска CLI.
- **Решение 2** изменено: семантика наложения — `PromptSectionOverlay.Apply`, а не «первый список целиком».
- **Решение 3, два режима.** Прецедент закрепления — слепок этапа `TaskItem.Stage` (`backend/ClaudeHomeServer.Core/Models/TaskItem.cs:208`, запись `backend/ClaudeHomeServer.Tasks/TaskManager.cs:648-689`). Закрепляется только вариант стенда. Правки базы и секций специальности человеком остаются живыми: они читаются каждый ход (`PromptSectionsContributor.cs:20-24`). Это два режима, и их надо назвать явно.
- **Решение 4.**
  - В паспорт добавляются `SectionsVersion` (функция уже есть: `backend/ClaudeHomeServer/Services/PersonaManager.cs:158-166`) и такой же хеш контракта `ContractVersion`. Без них эффект ревизии не отличить от правки человеком.
  - Паспорт по-прежнему пишет один подписчик (`backend/ClaudeHomeServer/Services/Sessions/TurnPassports.cs:24`), хотя у события `TurnCompleted` подписчиков стало несколько.
  - Исход задачи частично есть готовым:
    - машиночитаемый предгейт на SHA (`TaskItem.cs:252,347-355`);
    - причины возврата (`backend/ClaudeHomeServer.Core/Models/TaskFeatureConstants.cs:66-92`);
    - долгоживущий журнал ходов фичи (`backend/ClaudeHomeServer.Tasks/Journal/FeatureJournalStore.cs:10,217`, `backend/ClaudeHomeServer.Tasks/Journal/FeatureTurnJournalSubscriber.cs:113-129`).

    Тезис «машиночитаемого источника сборки и тестов нет» отвергнут кодом.
- **Решение 5.**
  - Ручка render отдаёт обе оси системного промпта: слой персоны и `PromptSectionsContributor.EffectiveSectionsFor`.
  - Кодовый случай копирует слепок этапа задачи: `## ЭТАП` входит в постановку.
  - Детские чаты (`backend/ClaudeHomeServer.Core/Models/User.cs:105`) в банк случаев не попадают никогда, а детскую секцию слоя (`PersonaPromptBuilder.cs:65-66,123-124`) вариант не трогает.
- **Альтернатива для Ф0б.** Правило «минимальной правки» можно положить в `Prompt` этапа `step` встроенного шаблона (`backend/ClaudeHomeServer.Core/Models/WorkflowTemplate.cs:67-68`), без кода. Цена: правило действует на все модели и только в конвейере фичи. Рекомендуется как первый замер до решения 2.
- **ADR-032 не реализован** (статус «предложено», кода нет). Ссылки на него — ссылки на формат журнала, а не на переиспользуемый код.
- **Связанный дефект** `d27d4473`: `persona_ask` собирает дисциплину по `persona.Model` (`backend/ClaudeHomeServer/Services/PersonaAskService.cs:26`), а исполняется на модели резолвера (`:63-64`). Чинить до решения 2: иначе тест «байт в байт» закрепит ошибку.
