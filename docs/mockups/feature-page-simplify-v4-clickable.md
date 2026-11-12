# Страница фичи · v4: на компонентах продукта, «Разбор» одной страницей

- **Фича:** 11041582-a74b-4f54-9d6d-6c0e3e610245
- **Статус:** черновик
- **Файлы:** feature-page-simplify-v4-clickable.html, feature-page-simplify-v4-clickable.md

Задача 62a9d98f (role=design). Код продукта не меняется. v4 заменяет v3 ([feature-page-simplify-v3-clickable.md](feature-page-simplify-v3-clickable.md)), сам v3 остаётся в репозитории. Андрей высказал к v3 три претензии, v4 отвечает на каждую:

1. **Свой визуальный язык.** Каждый блок v4 снят с реального компонента: геометрия, токены и состояния взяты из кода. Режим пульта «Компоненты» обводит блок рамкой и подписывает имя компонента.
2. **Без второго уровня навигации.** «Разбор» стал одной прокручиваемой страницей с якорями. Сегментов «Цифры · Шкала · История · Чаты» больше нет.
3. **Больше аналитики, без списков.** Списков задач и чатов в «Разборе» нет. Вместо них шесть секций из данных `GET /api/tasks/features/{id}`, у каждой величины пометка К или Б.

## 1. Сценарии и что в них кликать

| # | Сценарий | Клики | Что видно |
|---|---|---|---|
| 1 | Список → фича → назад | строка фичи, затем ✕ (1240) или «‹ К списку» (360) | На 1240 сайдбар со строками `FeatureGroupHeader` слева, страница в центральном острове. Адрес `#/project/…/feature/…` |
| 2 | Нужно от тебя | «Ответить Александру» → «Отправить» → чип «‹ к фиче … · Ход» | Чат открывается переходом по маршруту `#/project/…/chat/…` и занимает центр, а не шторку. В шапке чата стоит `FeatureBackChip` с вкладкой, с которой ушли. Ответ снимает ожидание (Б), чип возвращает на «Ход» |
| 2′ | Что мешает | «Заблокировано» → «Открыть чат Дениса» → «Перезапустить шаг» → чип | Чат Дениса с причиной в `Notice danger`. Кнопка перезапуска помечена Б |
| 2″ | Ключевой чат (1240) | «Ключевой чат» в шапке | Чат открывается, но чипа возврата нет. Так в продукте сегодня, см. §7 |
| 3 | Вкладки и шаги | «Ход · Материалы · Разбор», «Все 44 шага этапа ›» | Вкладки — это `Tabs size="md"`: 38 px, на 360 — 44, активная подсвечена accent. Шаги раскрываются внутри «Производства» |
| 4 | Материалы | карточка плана → «‹ Все документы»; карточка файла → ✕; пульт «Ветка: удалена» | План читается **внутри вкладки** (`FeaturePlanReader`), адрес не меняется. Файл открывается **в центре вместо страницы** (`FileViewer`), адрес `#/project/…/file/<путь>`. Закрытие возвращает на страницу на ту же вкладку. У удалённой ветки карточка перечёркнута, в `FileViewer` показано `EmptyState` |
| 5 | Разбор | вкладка «Разбор», якоря «Время и срок … Хронология» | Одна страница: строка итога, шесть секций, якоря прокручивают тело страницы |
| 6 | Закрытая фича | «Закрыта» или «Закрытые · 44 ›» → «Обзор фич» → «Разбор ›» | «Итоги» (`FeatureClosedSummary`), затем тот же «Разбор» с данными закрытой фичи |

На пульте: состояние, ширина 1240 и 360, тема, ветка материала, пометки («К / Б», «Компоненты», «Нет») и «Сначала». Над прототипом строка адреса показывает, какой хеш ставит продукт при каждом переходе (`lib/nav.ts`).

## 2. Элемент макета → компонент продукта → файл

| Элемент v4 | Компонент | Файл | Что взято |
|---|---|---|---|
| Сайдбар и центр на 1240 | `DesktopWorkspace`, `Island` | `pages/workspace/DesktopWorkspace.tsx`, `components/ui/Island.tsx` | острова R.xl на холсте bgMain, SHADOW.island, зазор |
| Строка фичи в списке | `FeatureGroupHeader` + `FeatureTrackerBar` | `components/FeatureGroupHeader.tsx`, `components/chat/feature/FeatureTrackerBar.tsx` | ≈55 px, R.lg, borderLight, bgCard; выбор — accentLight/accentMuted; тревога — кромка 3 px warning/danger; ряд 1 — название 13/700 и mono 11; ряд 2 — аватары 16 с нахлёстом −5, статус 11, mono-счёт; полоса h4 gap 2 |
| «Закрытые · 44 ›» | `featureListFold` | `components/feature/featureListFold.ts` | компактная строка закрытой фичи без полосы |
| Шапка страницы и файла | `CenterViewShell` → `Toolbar` | `components/CenterViewShell.tsx`, `components/Toolbar.tsx`, `lib/design.ts` (`TB`) | высота 52 / 56, поля 16 / 14, фон bgMain, линия border; слева ✕ (`ToolbarIconButton` 32) или `BackButton` на 360; имя mono 700 13 |
| Глиф фичи | `FeatureGlyph` | `components/feature/FeaturePage.tsx:419` | 14 px перед именем |
| Тег статуса | `StatusTag` | `components/feature/FeatureStatusTag.tsx` | FS.xs 600, высота строки 16, поля 2 4, R.sm; на 360 тега нет, как в `FeaturePageToolbar` |
| «Ключевой чат», «⋯» | `Button sm ghost`, `ToolbarIconButton` + `Menu` | `components/ui/Button.tsx`, `components/feature/FeaturePageHeader.tsx` (`FeaturePageToolbar`) | min-h 32, поля 7 13, R.lg, FS.base 600 |
| Тело страницы | `FeaturePage` | `components/feature/FeaturePage.tsx:434` | фон bgCard, поля 16 16 24, собственная прокрутка |
| Плашка «Нужно от тебя / Что мешает» | `FeaturePageAlert` по геометрии + тон `Notice` warning/danger | `components/feature/FeaturePageHeader.tsx`, `components/ui/Notice.tsx` | поля 8 12, R.lg, FS.base; фон и рамка warningBg/warning или dangerBg/dangerBorder. Содержание новое — шаг FS-FY |
| «Объём» | `FeatureVolumeCard` (FS-FV, принят) | `components/feature/FeatureVolumeCard.tsx` (ветка `feat/features-simplify`) | bgCard, borderLight, R.lg, поле 16, зазор 8; число sans 600 22/18; полоса корзин 8 px, зазор 2, success/info/accent/progressTrack |
| «Срок» | компонента нет, шаг FS-FE | — | та же геометрия, что у `FeatureVolumeCard`, плюс `ProgressBar` (h4, R.max) |
| Вкладки «Ход · Материалы · Разбор» | `FeaturePageTabs` → `Tabs size="md"` | `components/feature/FeaturePageTabs.tsx`, `components/ui/Tabs.tsx` | 38 px, на 360 — 44; поля 0 10 / 0 12; активная — 600, accent, линия 2 px accent; полоса липкая, фон bgWhite |
| «Ход» | `FeatureProgressTab` (FS-FH, принят) + `ActiveList` | `components/feature/FeatureProgressTab.tsx` (ветка `feat/features-simplify`), `components/feature/FeatureBriefActive.tsx` | прошлые этапы — FS.sm secondary, текущий — 600, будущие — muted; «Все N шага этапа» — `Button` без рамки accent |
| «Ответить» в строке «Сейчас» | `Button xs ghostFilled` | `components/ui/Button.tsx` | h24, на 360 растёт до 32 (в продукте — `minHeight` в месте вызова) |
| «Материалы»: группы | `GroupHeader` | `components/feature/FeaturePlansTab.tsx:112` | FS.xs 700, uppercase .04em, muted, mono-счётчик |
| Карточки документов | `PlanCard`, `OtherDocCard`, `MockupCard` | `components/feature/FeaturePlansTab.tsx`, `FeatureMockups.tsx` | bgCard (при наведении bgSelected), 1px border, R.md, поле 12 |
| Читалка плана | `FeaturePlanReader` + `Button ghost sm` «Все документы» | `components/feature/FeaturePlanReader.tsx`, `FeaturePlansTab.tsx:241` | деталь на всю ширину вкладки, кнопка возврата 44 px |
| Метка роли документа | `Badge` tone plan | `components/ui/Badge.tsx` | поля 1 8, R.max, FS.xs 600 |
| Открытый файл | `FileViewer` в `CenterViewShell` + `FileTypeTile` | `components/FileViewer.tsx`, `components/ui/FileTypeTile.tsx` | шапка та же, что у страницы; на 360 подпись «Файлы» |
| «Из ветки X · только просмотр» | `Notice` info | `components/ui/Notice.tsx` | поля 8 12, R.md, FS.sm. Строка новая, Б (FS-F1b/F1c) |
| «Файл удалён вместе с веткой» | `EmptyState` compact | `components/EmptyState.tsx` | поле 24, иконка 44 bgSelected/muted, заголовок 16 |
| Чат | `ChatHeaderBar` + лента + поле ввода | `components/chat/ChatHeaderBar.tsx` | **упрощённо**: в макете только шапка с чипом, сообщения и поле ввода |
| Чип возврата | `FeatureBackChip` (→ `BackButton`) | `components/chat/FeatureBackChip.tsx` | «к фиче «…»» FS.sm 600 secondary + «· вкладка» 500 muted, сжимается с многоточием; на 360 min-h 40 |
| Строка итога «Разбора» | `FeatureAnalysisLine` + «Резюме фичи» (`FeatureAnalysisSummary`) | `components/feature/FeatureAnalysisLine.tsx`, `FeatureAnalysisSummary.tsx` | одна фраза; резюме — отдельный `POST …/summary` |
| Якоря «Разбора» | `Button sm ghost pill` | `components/ui/Button.tsx` | без состояния выбора: это переход к месту, а не переключатель; на 360 — 40 px и горизонтальная прокрутка |
| Заголовок секции | `GroupHeader` | `FeaturePlansTab.tsx:112` | тот же, что у групп «Материалов» |
| Плитки | `FeatureStatsTiles` | `components/feature/FeatureStatsTiles.tsx` | 4 колонки, на 360 — 2; bgCard, borderLight, R.lg, SHADOW.card, поле 16/12; подпись 12/600 secondary; число serif 700 22/18; подсказка 11 muted; метки ✓/↩ 14×14 |
| Полоса состава времени | `FeatureStatsPulseModel` + `kindBackground` | `components/feature/FeatureStatsPulseModel.ts`, `FeatureStatsBits.ts:111` | цвета видов: работа — accent, ждали — info, очередь — smoke, ошибки — danger, простой — progressTrack |
| «Главные потери» | `FeatureStatsLosses` | `components/feature/FeatureStatsLosses.tsx` | живая карточка — infoBg/info, остальные — bgCard/borderLight |
| «Кто работал» | `FeatureBreakdownTable` **без переключателя меры** | `components/feature/FeatureBreakdownBlock.tsx`, `FeatureBreakdownTable.tsx` | время, токены и $ одной таблицей; на 360 колонка токенов скрыта |
| Гант этапов | дорожки `FeatureTimelineTab` | `components/feature/FeatureTimelineTab.tsx` | по `stats.timeline.intervalsByStage` |
| Лента событий | строки `FeatureEventsTab` | `components/feature/FeatureEventsTab.tsx` | время mono 11 + текст; в «Разборе» — только ключевые виды |
| Тост после «Отправить» | компонента нет | — | заглушка прототипа, в продукте ответ видно в самой ленте чата |

**Каких компонентов нет, и что с этим делать:**
- **Карточка «Срок»** (FS-FE). Её делаем на геометрии `FeatureVolumeCard`, отдельный примитив не нужен.
- **Шкала срока** («обещали / сейчас / по истории / типичная»). Это новый блок. Он собран из той же карточки и дорожки `ProgressBar`, и `ui/` не нужен: блок живёт только в «Разборе».
- **`FeatureChatDoor`** в коде не существует. В постановке это имя переходов в чат, а на деле это пара `recordFeatureReturn` + `openChatById` (§3).
- **Общей плитки** `Card` / `StatTile` в `ui/` нет: плитки свёрстаны прямо в `FeatureStatsTiles`. Предлагаю переиспользовать их стиль как есть и не заводить примитив, пока нет второго потребителя вне фич.

## 3. Поведение → откуда взято

| Поведение в v4 | Как в продукте сегодня | Откуда |
|---|---|---|
| Файл из «Материалов» открывается в центре вместо страницы, адрес `#/project/{pid}/file/{path}` | `onOpenFile` = `handleOpenFileFromTree`, тот же обработчик, что у дерева файлов: `setOpenFile` → `navPush({file})`. Страница фичи остаётся смонтированной и прячется (`display:none`), действует правило «последнее действие выигрывает» | `pages/WorkspacePage.tsx:2111-2124, 2539`; `pages/workspace/DesktopWorkspace.tsx:546`; `lib/centerOccupancy.ts`; `components/feature/FeatureCenter.tsx:29-50`; `lib/nav.ts:60, 171` |
| Закрытие файла возвращает на страницу фичи с той же вкладкой | `backFromFile`: адрес возвращается к сегменту фичи, флаг `yielded` снимается | `WorkspacePage.tsx:2244-2252` |
| На 360 файл и страница занимают всю зону, слева `BackButton` | `CenterViewShell` на мобиле рисует `BackButton`, на десктопе — ✕; у файла подпись «Файлы» | `components/CenterViewShell.tsx:72-89`; `FileViewer.tsx:1920`; `WorkspacePage.tsx:2564-2566` |
| План (текст задачи) читается внутри вкладки, «‹ Все документы» возвращает к сетке | `FeaturePlansTab` показывает деталь на всю ширину вкладки. Ссылки между документами открываются в той же читалке | `FeaturePlansTab.tsx:236-253`; `FeaturePlanReader.tsx:208-226` |
| Документ из ветки читается по ветке | Сегодня так умеет только читалка: `?featureId=` берёт сначала worktree фичи, потом корень. `onOpenFile` ветку **не передаёт**, поэтому файл из ветки в `FileViewer` читается из основного дерева | `FeaturePlanReader.tsx:219-224`; `features/files/api.ts:85-87`; `WorkspacePage.tsx:2111` |
| «Ответить» и «Открыть чат» — переход в чат по маршруту, в шапке чип «‹ к фиче … · вкладка» | `recordFeatureReturn(featureId, {kind:'chat'})` + `openChatById` → событие `cc-open-url` → `#/project/{pid}/chat/{id}`. Чип `FeatureBackChip` возвращает на запомненную вкладку | `FeaturePage.tsx:313-330`; `lib/openChat.ts:37-43`; `lib/featureBackNav.ts:142`; `components/chat/FeatureBackChip.tsx` |
| «Ключевой чат» открывается без чипа | `onOpenKeyChat` зовёт `openChatById` без `recordFeatureReturn` | `FeaturePage.tsx:135` |
| Escape закрывает файл и страницу | `useEscapeClose` в `CenterViewShell` | `CenterViewShell.tsx:65` |
| Возврат системным «назад» | popstate восстанавливает фичу из снимка | `WorkspacePage.tsx:1969-1973` |

## 4. Что убрано из v3

- **Правая шторка просмотрщика документа.** Файл теперь открывается в центре, план — внутри вкладки (§3).
- **Шторка «так откроется чат».** Вместо неё настоящий переход в чат с чипом возврата.
- **Сегменты «Цифры · Шкала · История · Чаты»** во вкладке «Разбор».
- **Список чатов фичи** в «Разборе». Чаты доступны чипами исполнителей в «Ходе» и в шапке («Ключевой чат»).
- **Список задач** в «Разборе». Шаги остаются в «Ходе» раскрытием «Все 44 шага этапа».
- **Самодельные стили v3**: serif-заголовок шапки 16 px вместо mono 13, вкладки с тёмной активной подписью вместо accent, кнопки 44 px на 360 вместо размеров кита. Теперь всё по ките (§2).

## 5. «Разбор»: секции и величины

Принцип: сверху итог одной строкой, ниже детали. Первый экран («Объём N из M», «Срок ≈ N») в «Разборе» не повторяется. Там его раскладка: из чего сложилось время, как менялся срок, кто сколько стоил.

**Шапка «Разбора»**
| Величина | Поле | |
|---|---|---|
| Итог одной фразой: сколько идёт, работали, ждали, возвраты, $ | `brief.elapsedMinutes`, `stats.timeline.totals.workWallMinutes / waitingMinutes`, `run.verdicts.returned`, `stats.spend.costUsd` | К |
| Источник и полнота цифр | `stats.source`, `stats.truncated` | К |
| «Резюме фичи» текстом | `POST /api/tasks/features/{id}/summary` (отдельный эндпоинт) | К |

**1. Время и срок**
| Величина | Поле | |
|---|---|---|
| Прошло | `totals.elapsedMinutes` (у закрытой — `run.summary.durationMinutes`) | К |
| Работали (по стене) и сумма агентов | `totals.workWallMinutes`, `totals.agentsSumMinutes` | К |
| Ждали тебя: всего, сколько раз, дольше всего | `totals.waitingMinutes`, `waitingCount`, `longestWait` | К |
| Ошибки: минуты и эпизоды | `totals.errorMinutes`, `errorCount` | К |
| Состав времени: работа, ожидание, очередь, ошибки, простой | `totals.*Minutes`, `queuedMinutes`, `idleMinutes` (в режиме approx часть — null) | К |
| Обещали на старте | `brief.initialEtaMinutes` | К |
| Сейчас осталось, кто оценил | `brief.note.estimate.etaMinutes`, `note.byPersonaId` | К |
| Вилка по истории проекта | `brief.estimate.p25 / p75`, `sampleSize` | К |
| Типичная фича проекта | `stats.typical.duration` (медиана, p25, p75) — бэк отдаёт, фронт пока не читает | К |
| Согласие оценок модели и истории | `brief.note.estimate.agreement` | К |
| Сдвиг срока с начала | — | Б |
| Точность срока у закрытой (факт против обещания) | — | Б |

**2. Объём**
| Величина | Поле | |
|---|---|---|
| Всего, приняты, на ревью, в работе, не заведены, сняты, уже в master | `run.stepProgress.*` | К |
| Состояние плана | `stepProgress.planState` | К |
| По линиям: шагов, принято, возвратов | `run.lanes[]` | К |
| У закрытой: шагов, задач, открытых, пачек ревью, слияний, хвостов | `run.summary.*`, `run.reviews`, `run.merges[]`, `run.postMergeTails` | К |
| Медиана шага по проекту | `stats.typical.stepMinutes` | К |
| Сколько раз менялся знаменатель | — | Б |
| Медиана шага этой фичи | — | Б |

**3. Возвраты и потери**
| Величина | Поле | |
|---|---|---|
| Приняты, с замечаниями, возвращены, отозваны | `run.verdicts` | К |
| Откуда возвраты: ревью, проверка, финал (сколько открыто) | `run.returnsBySource` | К |
| Витки ✓/↩ | `run.stepChains[].rounds` | К |
| Дольше всего ждём / ждали | `attention.since` или `totals.longestWait` | К |
| Больше всего витков и причина | `stepChains[].loops`, `rounds[].reason` | К |
| Дольше всех сейчас (у закрытой — самый долгий этап) | `stats.longestNow`; `run.stages[].approxMinutes` | К |
| Цена возврата во времени | — | Б |
| Главная потеря одной фразой | — | Б |

**4. Кто работал**
| Величина | Поле | |
|---|---|---|
| По персонам: время, токены, $ | `stats.breakdown.personas[]` | К |
| По моделям: время, $, класс цены | `stats.breakdown.models[]` (`tier`) | К |
| По ролям: время | `stats.breakdown.roles[]` | К |

**5. Расход**
| Величина | Поле | |
|---|---|---|
| Всего $ и полнота цены | `stats.spend.costUsd`, `costPartial` | К |
| Токены: вход, выход, чтение кэша, запись кэша | `stats.spend.*Tokens` | К |
| Доля общих чатов | `stats.spend.shared` | К |
| Модели без цены | `stats.spend.hiddenCostModels` | К |
| С какого момента есть данные | `stats.spend.from` | К |
| Доля кэша одной цифрой | — | Б |
| Стоимость по шагам | — | Б |
| Стоимость возвратов | — | Б |
| Дороже или дешевле типичной фичи | — | Б |

**6. Хронология**
| Величина | Поле | |
|---|---|---|
| Гант этапов с длительностями | `stats.timeline.intervalsByStage`, `timeline.from / to` | К |
| Ключевые события: вердикты, остановки, ожидания, мерж | `events[]` (`kind`, `at`, `personaId`, `stepRef`) | К |
| Всего событий в истории | `eventsTotal` | К |

Числа в фикстурах «Разбора» — образцы значений поля, в подсказке у каждого так и написано. Люди, шаги, документы и времена событий взяты из трекера на 10.11.

## 6. Что нужно от бэка

| Величина | Предложение | Шаг плана |
|---|---|---|
| Сдвиг срока с начала | журнал оценок `estimate_set`, разница первой и текущей | FS-O2a, FS-O2b |
| Сколько раз менялся знаменатель | тот же журнал | FS-O2a |
| Точность срока у закрытой | `summary.durationMinutes / initialEtaMinutes` бэком, а не фронтом | новый, к FS-O2b |
| Медиана шага этой фичи | агрегат по `stepChains.durationMinutes` | новый |
| Цена возврата во времени | минуты от вердикта «вернуть» до повторной сдачи | новый |
| Главная потеря одной фразой | `brief.lossLine` | новый (был в v3, §3) |
| Доля кэша | `spend.cacheShare` | новый |
| Стоимость по шагам и по возвратам | spend по `taskId` в ответе фичи; сейчас только `/api/spend?task=` | новый |
| Дороже или дешевле типичной | медиана `spend.costUsd` в `stats.typical` | новый |
| «Из ветки X» у материала, «удалён вместе с веткой» | как в v3 | FS-B2, FS-B3a, FS-B3b |
| Фраза «нужно от тебя / что мешает», снятие ожидания ответом | как в v3 | FS-E1, FS-E3a, FS-E3b, FS-E4 |
| «Часы стоят» | как в v3 | FS-V2, FS-V4 |
| Перезапуск шага из чата задачи | кнопка в UI; сейчас перезапуск есть только у `tasks_run_executor` | новый (вопрос Андрею) |

## 7. Что v4 меняет в плане реализации (для Егора)

| Шаг | Было | Стало |
|---|---|---|
| **FS-FR** вкладка «Разбор» | сегменты «Цифры · Шкала · История · Чаты» | одна страница: строка итога (`FeatureAnalysisLine` + «Резюме»), якоря, шесть секций из §5. Собирается из существующих `FeatureStatsTiles`, `FeatureStatsLosses`, `FeatureBreakdownTable`, дорожек `FeatureTimelineTab` и строк `FeatureEventsTab`. Новые блоки — шкала срока и таблицы `kv` |
| **FS-FTABS** стор вкладок | вкладка + сегмент «Разбора» | только вкладка. Сегмента нет, стор сегмента не нужен. Якорь хранить не надо: открытие «Разбора» начинается сверху |
| **FS-FP** три вкладки, снятие шести старых | «Чаты», «Шаги», «События», «Статистика» уходят в «Разбор» | «Чаты» и «Шаги» не переезжают никуда: шаги остаются раскрытием в «Ходе», чаты — переходами из «Хода» и шапки. `FeatureChatsTab` и `FeatureTasksTab` снимаются со страницы. Переключатели разреза и меры в `FeatureBreakdownBlock` для «Разбора» не нужны |
| **FS-F1c** проводка `onOpenFile(path, {taskId})` | — | остаётся. Добавить: `FileViewer`, открытый со страницы фичи, показывает `Notice info` «Из ветки X · только просмотр», а на 360 подпись `BackButton` говорит «К фиче», а не «Файлы». Сейчас ведёт назад к фиче, а подписан «Файлы» |
| **FS-F1e** «Материалы» из `materials[]` | карточки открывают правую шторку (v3) | план — в `FeaturePlanReader` внутри вкладки, файл и макет — через `onOpenFile` в центре (как сегодня) |
| **FS-F1f** карточка макета с `ownerTaskId` | шторка | `MockupCard` внутри вкладки, «Открыть» уводит в `FileViewer` по `onOpenFile` с `taskId` автора |
| **FS-FS / FS-FY** первый экран | — | плашка «Нужно от тебя» — геометрия `FeaturePageAlert` с тоном `Notice`. «Ответить» — `recordFeatureReturn` + `openChatById`, без промежуточной шторки |
| новый (мелкий) | — | `onOpenKeyChat` пишет `recordFeatureReturn`, чтобы и у ключевого чата был чип «к фиче» (`FeaturePage.tsx:135`) |

## 8. Проверка

Скрипт `.cc-attachments/v4-clickable/check.mjs` (Playwright, headless Chromium). Он проходит все сценарии кликами в четырёх прогонах: 1240 (окно 1300) и 360, светлая и тёмная тема. После каждого шага сверяет состояние (адрес `/feature/`, `/chat/`, `/file/`, наличие чипа, 44 шага, читалка внутри вкладки, шесть секций «Разбора» и ни одного `tablist` или `aria-pressed` внутри, прокрутку якорем) и считает:
- ошибки консоли;
- переполнение документа и выход элементов за рамку прототипа;
- на 360 — кнопки и поля меньше 32 px, включая пульт.

Итог: `1240-light 23/23, 1240-dark 23/23, 360-light 22/22, 360-dark 22/22`, ошибок консоли 0, итог «ЗЕЛЁНЫЙ». Сценарий 2″ (ключевой чат) идёт только на 1240: на 360 этой кнопки в шапке нет. Проверку самой проверки делала мутацией: убрала правило высоты xs-кнопок на 360, прогон покраснел на 7 шагах («Ответить 71×24», «Резюме фичи 97×24», «Перезапустить шаг 140×24»), после возврата правила снова зелёный.

Скриншоты — в `.cc-attachments/v4-clickable/`, 53 кадра: светлая тема по всем шагам, тёмная — по «Разбору» и закрытой фиче.
