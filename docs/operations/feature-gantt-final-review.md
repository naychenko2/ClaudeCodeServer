# Чек-лист финального ревью фичи «Гант на „Хронологии“» (GC-FIN)

- **Фича:** 50a3a0c3 «Гант на „Хронологии“: вариант C „общая ось“», worktree `feature-gantt-c`.
- **Для кого:** Александр (шаг GC-FIN) — финальное ревью собранной фичи.
- **Что делает:** финальное ревью смотрит весь диапазон `git diff master...feature-gantt-c` сразу. Этот документ — не «на что смотреть вообще», а адреса проверок: инвариант → где смотреть → чем проверять → что блокер.
- **Составлен по:** HEAD `5c97e64a6` ветки `feature-gantt-c` (26.10.2026), плюс незакоммиченные правки `featureListFold.ts` / `featureListFold.test.ts` (GC-F4R2 в работе у Киры). Номера строк — для этого HEAD; при сдвиге искать по имени символа.
- **Источники:** план-заметка «План Ганта на „Хронологии“ — карточки шагов GC-*» (разделы GC-FIN, «Схема зависимостей»), приёмка GC-QAP (`docs/operations/feature-gantt-acceptance.md`), сверка доков GC-DOCP, ревью GC-R-B / GC-R-F12 и их дельты.

## 0. Как пользоваться

1. Идти по пунктам §2 в порядке — они идут от дешёвого чтения к дорогой проверке.
2. Пункт = инвариант. Если инвариант держится — отметить «держится» и переходить дальше; если нет — сразу фиксировать находку с severity (см. §5).
3. Пункты со знаком **⛔** — инварианты, которые на момент HEAD ещё НЕ выполнены (шаг в работе). Проверять их надо после того, как шаг закрыт; в текущем виде они не дефект фичи, а незакрытая работа.
4. Полный прогон тестов и сборка — на гейте Марка, не здесь. Этот чек-лист — про то, что гейтом не проверяется: чтением, точечными прогонами и сравнением.

## 1. Границы проверки: что финальное ревью вообще охватывает

Диапазон `git diff master...feature-gantt-c` (35 файлов, +5605/−1976) делится так:

| Слой | Файлы | Что в нём живёт |
|---|---|---|
| Бэк — проектор | `FeatureProjector.cs`, `FeatureRun.cs`, `FeatureStageIntervals.cs` (нов.), `StageIntervals.cs` (нов.), `FeatureTimeline.cs` | интервалы этапов, хвосты после мержа, `SlimRun` |
| Бэк — тесты | `FeatureStageIntervalsTests.cs` (нов.), `FeatureSlimRunContractTests.cs` (нов.), `FeaturePostMergeTailsTests.cs` (нов.), `FeatureTimelineJournalTests.cs` | сторожа контракта |
| Фронт — модель | `FeatureLaneModel.ts` (нов.), `featureListFold.ts` (нов.), `types/featureStats.ts`, `types/featureRun.ts` | единственный источник геометрии и счётчиков |
| Фронт — компоненты | `FeatureStageLane.tsx`, `FeatureLaneAxis.tsx`, `FeatureStepDetails.tsx`, `FeatureTimelineBits.tsx`, `FeatureTimelineTab.tsx`, `FeatureProdCards.ts`, `FeatureStatsTab.tsx` | рендер |
| Фронт — удаление | `FeatureGanttModel.ts` + тест (удалены) | старая модель Ганта |
| Доки | `docs/operations/feature-gantt-acceptance.md` (нов.) | приёмка GC-QA |

**Не входит в диапазон** (проверять отдельно, §2.4): `docs/features/features-overview.md`, `docs/mockups/feature-gantt-7.md` — они идентичны master, потому что GC-DOC **не выполнен**.

## 2. Инварианты — проверки с адресом

### 2.1 «Фронт не пересчитывает» (critical)

**Что проверяем:** интервалы, «стоит», «ждёт», хвосты и план приходят с бэка. Во фронте нет своей арифметики конвейера и своих порогов: геометрия (доли, клампинг, сортировка) — да, состояние конвейера — нет.

**Где:**
- `frontend/src/components/feature/FeatureLaneModel.ts:206-303` — `buildStageLanes`: единственный вход. `:221-231` — «стоит» ТОЛЬКО из `run.attention.kind === 'stopped'`; `:237-239` — «ждёт» ТОЛЬКО из `timeline.current.kind === 'waiting'` + `taskId`; `:259-269` — состояние интервала `done|live|wait|stalled`; `:285-293` — `plan` из `run.stepProgress` (prod/review) или `executors[].status` (прочие).
- `frontend/src/components/feature/FeatureLaneModel.ts:7-10, 221-224` — комментарий-фиксация: «порог 30 мин — макетный, фронт его не считает».
- `frontend/src/components/feature/FeatureStageLane.tsx:62-67` — дубль `spanMinutes` ради `aria-label` (только чтение, без счёта).
- `frontend/src/components/feature/FeatureTimelineBits.tsx:131-136` — `minutesSince` с округлением **вверх** (`ceil`); `frontend/src/components/feature/FeatureStatsBits.ts:90` — `minutesSince` с округлением **по правилам** (`round`). Комментарий в `FeatureStatsBits.ts:87-89` прямо пишет, что расхождение «round против ceil» уже разводило числа на минуту и потому был заведён общий помощник (W5-R-F3b) — но дубль в `FeatureTimelineBits` остался и используется в `executorStatus` (`:149, :153, :157` — минуты у исполнителя). Итог: две арифметики в одном продукте после «устранения» расхождения; на разных подписях (исполнитель в ленте — ceil; герой, строка анализа, пауза между интервалами — round) числа разойдутся на минуту. Не «стоит», но проверить, действительно ли дубль нужен.
- Бэк, откуда всё приходит: `backend/ClaudeHomeServer.Tasks/Projector/FeatureStageIntervals.cs:43-130` (интервалы из журнала, живой ход от `max(ClaudeStartedAt, конец последнего закрытого хода)`, `GlueGap = 2 мин` на `:20`), `FeatureProjector.cs:86-93` (хвосты), `:141-181` (`waitTask`/`stoppedTask`/треvога).

**Чем:**
```
grep -rn "30 \* 60\|AddMinutes(-30)\|Minutes(30)" frontend/src/components/feature/   # ожидаем 0
grep -n "attention\|stopped\|current" frontend/src/components/feature/FeatureLaneModel.ts
```
Прогон: `vitest files=[frontend/src/components/feature/FeatureLaneModel.test.ts]` (зелёный на HEAD), бэк — `run_tests target=backend/ClaudeHomeServer.Tasks.Tests/ClaudeHomeServer.Tasks.Tests.csproj filter=FullyQualifiedName~FeatureStageIntervalsTests`.

**Блокер (major):** фронт выводит состояние конвейера из своих вычислений (порог, счётчик минут «стоит»); расхождение «дорожка» ↔ «строка этапа» ↔ «карточка шага».
**Приемлемо:** геометрия в фронте (`axisFrac`, обрезка по окну, `pauseAfterMinutes` — пауза между интервалами, её считает только фронт, в бэке её нет); расхождение округлений `minutesSince` между двумя файлами — minor, если числа не попадают в одну подпись.

### 2.2 SlimRun не потолстел, кроме `PostMergeTails` (critical)

**Что проверяем:** `SlimRun` облегчает ответ агрегата. Добавиться может ровно одно — `PostMergeTails`. `acceptance`/`evidence`/`planSteps` обязаны дойти (их читает фронт), тексты (`Description`/`ResultMarkdown`/`Verification.Notes`/`Repro`/`LinkedFiles`/`Subtasks`/`Labels`) — обнуляются намеренно, итог грузится лениво.

**Где:**
- `backend/ClaudeHomeServer.Tasks/Projector/FeatureProjector.cs:837-871` — `SlimRun`: единственная новая строка в диффе — `PostMergeTails = run.PostMergeTails` (`:871`).
- `backend/ClaudeHomeServer.Tasks/Projector/FeatureProjector.cs:813-829` — `Slim`: что обнуляется.
- `backend/ClaudeHomeServer.Core/Models/TaskListItem.cs:27-45` — `TaskListItem.From` копирует записываемые свойства `TaskItem` рефлексией; поэтому `Acceptance`/`Evidence`/`PlanSteps` доезжают, а `ResultMarkdown = null`.
- Сторож: `backend/ClaudeHomeServer.Tasks.Tests/FeatureSlimRunContractTests.cs` — 5 тестов: `SlimRun_Acceptance_СовпадаетС_Исходным` (`:72`), `SlimRun_Evidence_СовпадаетС_Исходным` (`:83`), `SlimRun_PlanSteps_СовпадаютС_Исходными` (`:96`), `SlimRun_Acceptance_Эпика_НеСрезано` (`:110`), `SlimRun_ResultMarkdown_Null_ИтогГрузитсяЛениво` (`:125`).

**Чем:**
```
git diff master...feature-gantt-c -- backend/ClaudeHomeServer.Tasks/Projector/FeatureProjector.cs | grep -E "^[+-].*=" | grep -iv "//"
run_tests target=backend/ClaudeHomeServer.Tasks.Tests/ClaudeHomeServer.Tasks.Tests.csproj filter=FullyQualifiedName~FeatureSlimRunContractTests
run_tests target=backend/ClaudeHomeServer.Tasks.Tests/ClaudeHomeServer.Tasks.Tests.csproj filter=FullyQualifiedName~FeaturePostMergeTailsTests
```
Мутационная проба (если сомневаешься в стороже): в `FeatureProjector.Slim` поставить `item.Acceptance = []` → тест `SlimRun_Acceptance_СовпадаетС_Исходным` обязан краснеть; `item.PlanSteps = null` → краснеет `SlimRun_PlanSteps_СовпадаютС_Исходными`. Откатить.

**Блокер (major):** в `SlimRun` или `Slim` появилось поле/коллекция сверх `PostMergeTails`; `acceptance`/`evidence`/`planSteps` срезаются (фронт потеряет критерии приёмки и шаги плана); `ResultMarkdown` начал ехать в ответе (вес ответа на больших фичах).
**Приемлемо:** новые вычисленные поля (не коллекции задач) в `FeatureRun` — если они не растят ответ покомандно; обнуление `Verification.Notes` — это контракт master.

### 2.3 Границы подсистем (critical)

**Что проверяем:** направление ссылок Main → Vertical → Core. Новая вертикальная логика фичи (`Projector/*`) не тянет Main и другие вертикали напрямую.

**Где:**
- Новые файлы фичи: `backend/ClaudeHomeServer.Tasks/Projector/FeatureStageIntervals.cs`, `StageIntervals.cs` — namespace `ClaudeHomeServer.Services.Tasks.Projector`, using только `ClaudeHomeServer.Models` (Core-«спина»).
- `backend/ClaudeHomeServer.Tasks/ClaudeHomeServer.Tasks.csproj:36` — единственная ссылка: `ClaudeHomeServer.Core`.
- `backend/ClaudeHomeServer.Guards.Tests/Services/SubsystemBoundaryTests.cs` — default-deny сторож с allow-list; `:78` — форс-загрузка сборки `Tasks` (`typeof(TasksSubsystem).Assembly`).
- `backend/ClaudeHomeServer.Guards.Tests/Services/SubsystemBoundaryCoverageTests.cs` — покрытие: новая вертикаль обязана добавить строку `typeof(...).Assembly`.

**Чем:**
```
run_tests target=backend/ClaudeHomeServer.Guards.Tests/ClaudeHomeServer.Guards.Tests.csproj filter=FullyQualifiedName~SubsystemBoundary
grep -rn "^using" backend/ClaudeHomeServer.Tasks/Projector/FeatureStageIntervals.cs backend/ClaudeHomeServer.Tasks/Projector/StageIntervals.cs
```
**Блокер (critical):** фича завела ссылку из вертикали Tasks на Main или на соседнюю вертикаль (в обход шва) — это ломает правило ADR-014 и цикл вертикалей.
**Приемлемо:** `using ClaudeHomeServer.Models` (Core-спина) — разрешено.

### 2.4 Доки совпадают с кодом (major)

**Что проверяем:** каждый упомянутый в доках символ/поле существует в коде, и утверждения доков не противоречат коду. Сверка Дениса (GC-DOCP) уже собрала промахи — её вывод ниже, **но сам GC-DOC (правка доков) не выполнен**: `docs/features/features-overview.md` и `docs/mockups/feature-gantt-7.md` в ветке идентичны master.

**Где (промахи, найденные GC-DOCP, ждут правки в GC-DOC):**

| Промах | Где в доке | Где в коде |
|---|---|---|
| «Агрегат не отдаёт `acceptance`/`planSteps`/`evidence`» — **неверно** | `docs/mockups/feature-gantt-7.md:236-240` | `TaskListItem.cs:27-42` (рефлексия), `FeatureTimelineBits.tsx:626-627` (фронт уже читает) |
| Мёртвая ссылка на `FeatureGanttModel.ts` | `feature-gantt-7.md:157`, `feature-gantt-6.md:174`, `features-overview-5.md:80` | файла нет в ветке (`grep` → 0) |
| «N/M» обещают у всех этапов | `feature-gantt-6.md:63-71`, `feature-gantt-7.md:121` | только у prod: `FeatureTimelineTab.tsx:126-134`, `FeatureStatsTab.tsx:114-120` |
| «Ось `axisFrac(t)`, `TIME = 0.86`» | `feature-gantt-6.md:182` | сигнатура `axisFrac(t, axis)`, `axis.timeFrac`: `FeatureLaneModel.ts:51, 40` |
| `features-overview.md` не описывает контракт Ганта вовсе | `:28` («Хронология» = `run.stages`, `stats.timeline`, `run.stepChains`) | `intervalsByStage`/`postMergeTails`/`GlueGap` — нет |
| `pauseAfterMinutes` назван полем бэка | — | его нет в бэке, считает фронт: `FeatureLaneModel.ts:277-282` |
| Живой хвост `max(ClaudeStartedAt, …)` не описан | — | `FeatureStageIntervals.cs:79-92` |
| `null` vs `{}` для `intervalsByStage` не описан | — | `FeatureLaneModel.ts:18-20, 216-219`; есть только в `feature-gantt-acceptance.md:28` |
| `docs/operations/feature-gantt-stand.md:104-106` (в master!) описывает `intervalsByStage`/`turns`/`postMergeTails` | — | в master-коде этих полей нет, только в ветке |

**Чем:**
```
grep -rn "intervalsByStage\|postMergeTails\|buildStageLanes\|foldProd\|FeatureLanePopover" docs/features/features-overview.md docs/mockups/feature-gantt-7.md
grep -rn "FeatureGanttModel\|buildGanttModel" frontend/src docs/   # ожидаем 0 в коде; в доках — промах
run_tests target=backend/ClaudeHomeServer.Guards.Tests/ClaudeHomeServer.Guards.Tests.csproj filter=FullyQualifiedName~ProjectMapHygieneGuard
```
**Блокер (major):** док утверждает несуществующий контракт или ссылается на удалённый файл; в коде есть поле/функция, не описанная ни в одном доке фичи (значит контракт не зафиксирован).
**Приемлемо:** док описывает плановое, а не сделанное (пока GC-DOC не выполнен) — но тогда это не блокер фичи, а незакрытая работа GC-DOC; зафиксировать и не возвращать фичу.

### 2.5 Состояния карточек = корзина бэка, никакой второй истины (critical)

**Что проверяем:** корзина шага на фронте (`bucketOf`) — зеркало бэковой (`FeatureStepAccounting`), а не самостоятельный источник. Плюс правила: «последнее ревью `accepted`/`accepted_with_remarks` закрывает шаг» и «шаг плана без задачи = `queue`/`planned`».

**Где:**
- `frontend/src/components/feature/FeatureProdCards.ts:194-203` — `bucketOf`: `chain.outcome === 'withdrawn' → withdrawn`, `'accepted' → accepted`, `'pending' → review`, `'running' && allDone → review`, иначе `work`.
- `frontend/src/components/feature/FeatureProdCards.ts:210-227` — `cardState`: берёт корзину и добавляет `stopped` (`:221-222` — `executorStoppedAt` новее `claudeStartedAt`).
- `frontend/src/components/feature/FeatureProdCards.ts:251-285` — `plannedCards`: `state: 'queue'`, `planned: true`, в конце ленты, дедуп по `planKey` (`:242-244`).
- Бэк — эталон: `backend/ClaudeHomeServer.Tasks/Projector/FeatureStepAccounting.cs:195-234` (корзина), `:269-292` (счётчики), `:36` `KeyOf` (trim), `:32` `StepKeyComparer` (OrdinalIgnoreCase).
- **Расхождение, которое нужно проверить:** фронт бэкового поля `bucket` не читает вообще — только `chain.outcome`/`chain.status`. Сравнение «зеркала» с эталоном живёт только в тестах.
- Тесты: `FeatureProdCards.test.ts:449` («закрытая хвостовая доработка после „принял с замечаниями“ не переоткрывает шаг»), `:458` («хвостовую доработку вернули — шаг переоткрыт»), `:483` («шаг плана без цепочки — карточка queue, planned=true»), `:502` («шаг плана уже в цепочке — не дублируется»), `:335` («заведённая цепочка никогда не queue»).

**Чем:**
```
run_tests target=backend/ClaudeHomeServer.Tasks.Tests/ClaudeHomeServer.Tasks.Tests.csproj filter=FullyQualifiedName~FeatureStepProgress
run_tests kind=vitest target=frontend files=[frontend/src/components/feature/FeatureProdCards.test.ts]
```
Мутационная проба: в `FeatureProdCards.ts` убрать `accepted_with_remarks` из правила закрытия → тест `:449` краснеет; в `FeatureStepAccounting.cs` сменить корзину `pending → review` на `work` → краснеет фронтовый `:483`/бэковый `FeatureStepProgress`.

**Блокер (critical):** фронт заводит корзину, расходящуюся с `FeatureStepAccounting` (шаг «принят» на экране и в `stepProgress` — разные вещи); правило закрытия по `accepted_with_remarks` отменено; `planned`-шаг дублирует заведённую цепочку.
**Приемлемо:** зеркало корзины во фронте (второй истины нет — источник один, `chain.outcome`), если расхождение ловится тестом.

### 2.6 Нет двойного счёта хвостов (major)

**Что проверяем:** хвост после мержа (`run.postMergeTails`) пересекается с шагами стадий по id. Шаг, показанный и как хвост, и как шаг стадии, не должен получить вторую строку и не должен попасть в счёт дважды.

**Где:**
- Бэк: `FeatureProjector.cs:86-93` — расчёт хвостов (любая незакрытая дочерняя задача, кроме merge, при `Merged`/`Closed`, порядок по `CreatedAt`); `:139-141` — хвосты исключены из `waitTask`; `:84` — и из `stoppedTask`.
- Фронт — механизм: `frontend/src/components/feature/featureListFold.ts:86-98` — `dedupe()` (ключ = `normKey(stepRef)`, первое вхождение побеждает, элементы без ключа не схлопываются); применяется в `foldProd` на `:129` — **одна и та же выборка для обеих веток, включая `showAll`** (`:130`).
- `featureListFold.ts:26-35` — комментарий-обоснование со ссылкой на `FeaturePostMergeTailsTests.cs:112-116`.
- Тесты: `featureListFold.test.ts:108` («шаг, попавший и в хвост после мержа, и в шаги стадии, не считается дважды»), `:64` и `:73` (инварианты GC-F4R2: `showAll` отдаёт тот же набор, что считает свёртка).
- **Пробел:** в `foldLast` (`:173-199`) дедупа нет намеренно (контракт «последние n элементов»). И — свёртка пока **не вызывается ни одним прод-потребителем**: `grep` по `frontend/src` даёт только `featureListFold.test.ts`; лента карточек рендерится вся (`FeatureProdCardsView.tsx:518-544`), кнопки «Показать все N» нет. Это GC-F8 (не выполнен).

**Чем:**
```
run_tests kind=vitest target=frontend files=[frontend/src/components/feature/featureListFold.test.ts]
run_tests target=backend/ClaudeHomeServer.Tasks.Tests/ClaudeHomeServer.Tasks.Tests.csproj filter=FullyQualifiedName~FeaturePostMergeTails
```
**Блокер (major):** хвост получает вторую строку в ленте или попадает в счёт дважды (счётчик «N шагов» больше реального).
**Приемлемо:** отсутствие дедупа в `foldLast` — контракт функции, не дефект; отсутствие прод-потребителя свёртки — незакрытый GC-F8, а не регресс.

### 2.7 Счётчик плана «N/M» один на обеих вкладках (major)

**Что проверяем:** одна и та же цифра «N/M» на «Хронологии» и на «Статистике». Сейчас — **не так**, и это уже зафиксированный major из GC-R-F12 (вердикт `returned`, карточка `28982a0b`), доработка GC-F12R (Полина, `faa8e134`) в работе.

**Где:**
- `frontend/src/components/feature/FeatureTimelineTab.tsx:126-134` — «Хронология»: `made = plan.done + plan.inWork`, `total = done + inWork + ahead`.
- `frontend/src/components/feature/FeatureStatsTab.tsx:114-120` (`planCounter`) — «Статистика»: `plan.done / total`.
- При `accepted=8, work=1, review=1, notCreated=1` → «Хронология» печатает `10/11`, «Статистика» — `8/11`. Подпись одна, расшифровки нет, а ссылка «Дорожки времени — на „Хронологии“» ведёт ровно туда.
- Знаменатель продублирован: `FeatureStatsTab.tsx:117` против `FeatureTimelineTab.tsx:129` — при правке одного места второе молча разойдётся.
- Тесты, которые закрепляют РАСХОЖДЕНИЕ (важно: тест не источник, а снимок): `FeatureTimelineTab.test.ts:1014` (ожидает `4/4`), `FeatureStatsTab.test.ts:653` (ожидает `8/11`).

**Чем:**
```
run_tests kind=vitest target=frontend files=[frontend/src/components/feature/FeatureStatsTab.test.ts,frontend/src/components/feature/FeatureTimelineTab.test.ts]
```
Проба: изменить в одной вкладке числитель → тест другой вкладки обязан краснеть. Если краснеет только свой — источник не общий, инвариант не выполнен.

**Блокер (major):** цифры на двух вкладках расходятся; после GC-F12R счётчик не стал единым источником (знаменатель в двух местах).
**Приемлемо:** расхождение цифр, если в подписи расшифровано, что означает N («принято» против «принято+в работе») — но лучше единый источник.

### 2.8 Живой ход и «идёт» (major)

**Что проверяем:** контракт живого интервала зафиксирован в типах, а слово «идёт» синтезирует вызывающий, а не модель интервалов.

**Где:**
- Тип: `frontend/src/types/featureStats.ts:37-38` — `StageInterval.live: boolean` («интервал дотянут до „сейчас“»); `:62` — `intervalsByStage` (`null` = approx); `:77` — `current.kind: 'waiting'|'stopped'|'merge_pending'`.
- `frontend/src/types/featureRun.ts:290-303` — `FeatureAttention.kind: 'wait'|'stopped'|'merge'`; `:109` `StageState`, `:112` `StageTone`.
- Модель: `FeatureLaneModel.ts:60-63` — `LaneIntervalState = 'done'|'live'|'wait'|'stalled'`; `:263-269` — синтез состояния интервала; `:210` — у закрытой/смерженной фичи live/wait/stalled гаснут.
- Бэк — живой хвост: `FeatureStageIntervals.cs:79-92` — `[max(ClaudeStartedAt, конец последнего закрытого хода), now]`, `Live=true`; `:103-108` — склейка: `Live` сохраняется, если живая часть была.
- Кто синтезирует «идёт»: `frontend/src/components/feature/FeatureTimelineBits.tsx:46-56` — `stageStatusLabel()` → `'идёт'` (state active, tone run/loop); `:428-434` — `turnLabel()` (ход, чей `to` позже «сейчас»); `FeatureStatsTab.tsx:96-104` — `stageStateWord()` → `'идёт'`. `stageStatusBadge()` (`:172-185`) «идёт» НЕ даёт — только «ждёт вас»/«стоит».
- Тип-контракт бэка: `backend/ClaudeHomeServer.Tasks/Projector/StageIntervals.cs:12-21` — `StageTurn`/`StageInterval` (публичные, GC-B6: были internal → CS0051 в сигнатуре публичной записи).

**Чем:**
```
run_tests target=backend/ClaudeHomeServer.Tasks.Tests/ClaudeHomeServer.Tasks.Tests.csproj filter=FullyQualifiedName~FeatureStageIntervalsTests
run_tests kind=vitest target=frontend files=[frontend/src/components/feature/FeatureLaneModel.test.ts]
```
**Блокер (major):** «идёт»/«стоит» выводится из фронтовых вычислений, а не из `live`/`attention`; тип `live` расходится с бековым `Live` (регистр/имя поля).
**Приемлемо:** несколько мест, синтезирующих одно и то же слово «идёт» (`stageStatusLabel`, `turnLabel`, `stageStateWord`) — пока это разные поверхности (строка этапа, ход, сводка).

### 2.9 Поповер/шторка: не в потоке раскладки, позиция только через `tooltipPlacement`, шторка — `ui/Modal` (major)

**Что проверяем:** поповер не сдвигает ленту (портал + `position: fixed`), позиция считается только штатным `tooltipPlacement`, шторка на 360 — штатный `ui/Modal`, а не самодельный div.

**Где:**
- Шов под шаг уже есть: `frontend/src/components/feature/FeatureStageLane.tsx:38-43` — пропсы `expandedAt`, `onSelect`, `onHover`; `:142-147` — `aria-haspopup="dialog"`, `aria-expanded`, обработчики зовут пропсы.
- **Потребителя нет:** `frontend/src/components/feature/FeatureTimelineTab.tsx:198-204` — `FeatureStageLane` вызывается **без** `expandedAt`/`onSelect`/`onHover`. Клик по интервалу ничего не открывает.
- `frontend/src/components/feature/FeatureLanePopover.tsx` — **файла нет** (и теста `FeatureLanePopover.test.ts` нет). Это GC-F6 (Полина, в работе, `6eb6dddc`).
- Штатные средства: `frontend/src/components/ui/Tooltip.tsx:46` — `tooltipPlacement`; `frontend/src/components/ui/Modal.tsx` — шторка.

**Чем:** после закрытия GC-F6 —
```
ls frontend/src/components/feature/FeatureLanePopover.test.ts
run_tests kind=vitest target=frontend files=[frontend/src/components/feature/FeatureLanePopover.test.ts]
grep -rn "role=\"dialog\"\|role=\"tooltip\"\|createPortal" frontend/src/components/feature/
```
Проверка «не в потоке раскладки» — только на стенде: `getBoundingClientRect().top` строки «Ревью» одинаков до наведения / при наведении / после закрепления (это пункт GC-QA п.2 и §E приёмки).

**Блокер (major):** поповер сдвигает ленту; позиция считается своей формулой вместо `tooltipPlacement`; шторка — самодельный div вместо `ui/Modal`.
**Приемлемо:** на HEAD этого кода просто нет — **⛔ незакрытая работа GC-F6**, не дефект фичи. Проверять после закрытия шага.

### 2.10 Снимок возврата не стирается `clearBackNav()` (major)

**Что проверяем:** дефект 4c4645e2 — эффект автоскрытия страницы фичи стирал указатель возврата раньше, чем тот успел восстановиться. Плюс: снимок вида (открытая карточка, прокрутка, этап) должен пережить переход «фича → задача → назад».

**Где:**
- `frontend/src/pages/WorkspacePage.tsx:1016-1024` — эффект автоскрытия страницы: `if (getBackNav()?.fromKind === 'chat') clearBackNav();` — гасится **только** указатель «чат → страница фичи»; указатель «фича → задача/чат» живёт (цель на экране, чип ведёт на страницу). Это и есть исправление дефекта.
- Прочие вызовы: `WorkspacePage.tsx:953` (страница не открыта — сброс), `:1048` (мобильный уход в сайдбар — безусловный сброс), `:1458` (выбор чата), `:1924` (вход в фичу не из чата), `:1958` (смена проекта — только `fromKind === 'chat'`).
- `frontend/src/lib/featureBackNav.ts:22-46` — состояние указателя; `:66-76` — `recordBackNav`/`clearBackNav`; `:94-102` — `recordFeatureReturn`.
- **Чего пока нет:** снимка вида (`stageKey`/`expandedKey`/`scrollY`) в `BackNav` нет — хранится только id фичи + вкладка + название. Это GC-F11 (Полина, `1b5ef103`, todo).
- Тесты: `frontend/src/lib/featureBackNav.test.ts` — 18 кейсов (`:9`–`:86`); **тестов на `recordFeatureReturn` и снимок вида в файле нет**.

**Чем:**
```
run_tests kind=vitest target=frontend files=[frontend/src/lib/featureBackNav.test.ts]
grep -n "clearBackNav" frontend/src/pages/WorkspacePage.tsx
```
Живая проверка (только стенд, §3): вернуться с задачи — карточка раскрыта, прокрутка ±2 px, фокус на карточке.

**Блокер (major):** `clearBackNav()` безусловно стирает указатель «фича → задача» в эффекте автоскрытия — возврат по чипу «← к фиче» перестаёт работать (сам дефект 4c4645e2); снимок вида не переживает переход.
**Приемлемо:** безусловный сброс при **настоящем** закрытии страницы (`:1048` — мобильный уход в сайдбар) — правильно; отсутствие снимка вида на HEAD — **⛔ незакрытая работа GC-F11**.

## 3. Чего финальное ревью НЕ сможет проверить (и почему это приемлемо)

Границы ревью — чтение кода и точечные прогоны. Живое поведение (пиксели, фокус, реальный возврат, гонки hover/click) проверяется на стенде. Это не пробел фичи, а разделение труда: **приёмку GC-QA (Вера, `7438ef19`) ведёт Вера**, ревью — чтение.

| Что не проверить в ревью | Чем закрывается | Почему приемлемо |
|---|---|---|
| Сдвига раскладки от поповера (пиксельный замер) | GC-QA п.2: `getBoundingClientRect().top` строки «Ревью» до/при/после | DOM-тесты не видят раскладку (урок W5: «стиль ≠ раскладка»); замер пиксельный — только в браузере |
| Ширина дорожки не меняется при раскрытии карточки | GC-QA п.2 (828/828) | то же |
| Клавиатура: одна точка табуляции, Esc возвращает фокус | GC-QA п.4 + §F приёмки | фокус и порядок обхода не выводятся из чтения JSX |
| Гонка hover/click в поповере (5 кликов подряд) | GC-F6 к.1 + GC-QA §E | состояние живёт в браузере; в тесте — только пропсы |
| Возврат с задачи 3 из 3 (карточка раскрыта, прокрутка ±2 px, фокус) | GC-QA п.3 + GC-F11 | повторность — только руками; снимка вида пока нет |
| Числа на экране = живой ответ API (интервалы 105, prod 49, review 39, …) | GC-QA §B4: сверка ответа `stats` с экраном | контракт поштучный есть в тестах, сверка «экран = API на реальных данных» — только на стенде |
| Тёмная тема страницы фичи | приёмка §P | ни одного автотеста на тёмную тему (приёмка §5.1) |
| Состояния «идёт»/«стоит» вживую | **только автотесты** — на срезе фича смержена, `attention` при хвостах обнулён | решение зафиксировано в приёмке §3 и §6 п.3: живых интервалов на срезе нет; вариант «живая фича» — GC-WT5 (Денис, в работе) |
| `ui_probe` вкладки «Статистика» | Playwright с кликом по вкладке | вкладки нет в URL — plain-проба по `path` её не ловит (приёмка §6.2) |
| Полный прогон тестов и сборка решения | гейт Марка | по плану GC-FIN: «Полный прогон — на гейте Марка. Мерж — только по слову Андрея» |
| Тяжёлый замер производительности склейки | `FeatureStageIntervalsTests.Build_Замер_18kХодов_Линейный` + тайминг-гейт | закрыт отдельным шагом (GC-B1-доработка), не в фокусе ревью |

**Правило для ревьюера:** если пункт из этой таблицы выглядит как «не проверено» — это **не находка**. Находка — только если пункт ПРОВЕРЯЕМ ревью и не держится. Формально: «не проверено ревью» ≠ «не покрыто фичей»; покрытие видно в приёмке §5.1–5.2.

## 4. Состояние фичи на момент составления (что ещё не сделано)

Это не список дефектов — список незакрытой работы. Каждый пункт помечен ⛔ выше.

| Шаг | Что не сделано | Влияние на ревью |
|---|---|---|
| GC-F6 (Полина) | `FeatureLanePopover.tsx` нет; пропсы `onSelect`/`expandedAt`/`onHover` не подключены | §2.9 проверять после закрытия |
| GC-F8 (Полина) | `foldProd`/`foldLast` не вызываются в проде; лента карточек без свёртки и без «Показать все N» | §2.6: механизм есть и покрыт тестами, потребителя нет |
| GC-F10 (Полина) | нет строки «хвосты после мержа · N» в ленте; нет `turns` в `ExecutorRow` | §2.6 и §2.8 проверять после |
| GC-F11 (Полина) | снимка вида (`stageKey`/`expandedKey`/`scrollY`) нет | §2.10 проверять после |
| GC-F12R (Полина) | счётчик «N/M» разный на вкладках | §2.7 — major уже выдан в GC-R-F12 |
| GC-DOC (Полина) | `features-overview.md` и `feature-gantt-7.md` идентичны master | §2.4: промахи из GC-DOCP ждут правки |
| GC-QA (Вера) | приёмка не начата | §3 — часть пунктов закрывается только ею |

**Плюс незакрытые миноры** из хвостов `ac3b5fd3`, `b4739c55`, `9b7593c8`, `f515fe22` — это minor, они не гейтят финальное ревью, но должны быть видны в списке.

## 5. Как фиксировать находки

По severity (в порядке убывания):

- **critical** — фича сломана концептуально: вторая истина о состоянии, нарушенная граница вертикалей, фронт пересчитывает конвейер.
- **major** — расхождение контракта между слоями или между двумя экранами: цифры разные, хвост посчитан дважды, дока противоречат коду, поповер ломает раскладку, возврат не восстанавливает вид.

Формат находки — как обычно: `файл:строка — суть — сценарий поломки (что человек увидит) — рекомендация`.

Отдельно к каждой находке: **проверенный SHA**. Ревью диапазона — `master...feature-gantt-c` на конкретном HEAD; при движении ветки указать, на каком коммите смотрелось.

**Что НЕ является находкой в этом ревью:**
- пункт из §3 (живое поведение) — закрывается приёмкой;
- незакрытая работа из §4 — это работа шага, а не дефект фичи; возвращать фичу из-за неё нельзя, максимум — отметить в задаче шага;
- minor из хвостов `ac3b5fd3` / `b4739c55` / `9b7593c8` / `f515fe22` — уже заведены, новой карточки не нужно.

## 6. Итог после проверки

Финал — вердикт по фиче целиком, а не по шагам. Форма: принято / принято с замечаниями / возвращено на доработку + список находок с severity + проверенный SHA.

Мерж — только по слову Андрея; полный прогон и сборка — на гейте Марка.
