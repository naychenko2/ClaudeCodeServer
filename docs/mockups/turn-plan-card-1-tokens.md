# Токены: карточка «План хода» в ленте чата с фичей (turn-plan-card-1)

Для Киры. Файл реализации — `frontend/src/components/chat/ChatItemView.tsx`, `TodoPlanView`,
ветка «1. Живая + фичи». Другие ветки (без фич, снимок) не трогать. Сырой hex — дефект.
Ниже — после выбора варианта Андреем: общая часть + раздел выбранного варианта.

## Общее

| Элемент | Токены |
|---|---|
| Оболочка | `C.bgWhite`, `1px solid C.borderLight`, `R.xl` (12), `SHADOW.card`, `overflow: hidden` |
| Hover (десктоп) | рамка `C.border`; шеврон `C.textMuted` → `C.accent` |
| Tap (360) | фон `C.bgCard` на `:active` |
| Focus | `SHADOW.focus` на кнопке-карточке (через `:focus-visible`, как в `FeatureGroupHeader`) |
| Заголовок-этап | `FONT.serif`, `FS.md` (14), 700, `C.textHeading`; имя — `STAGE_NAME[stage.key]` |
| «этап N из K» / «3/7» | `FS.sm`, `C.textMuted` (в Б — `FONT.mono`, `FS.xs`) |
| Вторичный текст | `FS.sm`, `C.textSecondary`, `text-overflow: ellipsis` |
| Мини-рельс | сегмент 6×4, зазор 2, радиус 2; done — `C.success`, pending — `C.progressTrack`, absent — пунктир `C.dashed`; active — 14×4, по тону: run `C.accent`, wait `C.info`, stopped `C.danger`, loop `C.warning` |
| Глиф | `FeatureGlyph` 14 px; цвет как у тона активной стадии / проблемы |
| Аватары | `PersonaAvatar` 20 (360 — 22), кольцо 2px `C.bgWhite`, наезд −6; «+N» — фон `C.bgSelected`, текст `C.textSecondary`, `FS.xxs` 700; упавший — фон `C.dangerBg`, ждущий — `C.infoBg` |
| Чип проблемы | `FS.xs` 600, `R.sm`, padding 1×7; wait — `C.infoBg`/`C.info`; stop — `C.dangerBg`/`C.dangerText`; loop/тихо — `C.warningBg`/`C.warningText`; «+1 фича» — `C.bgSelected`/`C.textSecondary` |
| Шеврон | `ChevronRight` 16, `ICON_STROKE`, `C.textMuted` |
| Тач-цели 360 | карточка ≥ `TB.iconHitMobile` (40); кнопки в полосе В — высота 40 |

## Данные (только существующие поля `FeatureRun`)

- Этап: `run.stages.find(s => s.state === 'active')`, номер — индекс + 1, K — `run.stages.length`.
- Осталось: `stepProgress.total - stepProgress.accepted`; `planState === 'done'` → «все шаги
  приняты» (`isStepProgressDone`). Нет `stepProgress` (старый ответ) → строки «сколько» нет.
- Кто: уникальные `run.now[].personaId` + `run.attention?.personaId` первым.
- Проблема (одна, по приоритету): `attention.kind === 'stopped'` → `'wait'` → `'merge'` →
  `sum(returnsBySource.*.open) > 0`. Минуты — от `attention.since`.
- Две фичи: выбранная — `useSelectedFeatureId(sessionId)`, иначе `runs[0]`; чип «+N фич» при
  `runs.length > 1`.
- Клик по карточке — `openPillMenu(sessionId)` (как прежний «Подробнее»).

## Вариант А · Сводка в две строки
- Кнопка-карточка, padding 9×13×10 (360 — 10×13×11), сетка из двух строк, зазор 4.
- Строка 1: глиф · этап · «этап N из K» · рельс · [«+1 фича»] · шеврон справа.
- Строка 2: «осталось X из T · W в работе · R в ревью» (на 360 — без корзин) · справа чип и аватары (≤3).

## Вариант Б · Одна строка
- Кнопка-карточка, padding 10×13, min-height 40, `flex-wrap`, зазор 7.
- Рельс · этап · «N/K» · чип · «осталось X шагов» · аватары (≤2) · шеврон.
- 360: рельс скрыт, перенос после чипа (вторая строка: «осталось…», аватары, шеврон).

## Вариант В · Сначала проблема
- Оболочка — не кнопка (внутри две кнопки).
- Полоса проблемы (только если есть): padding 8×13, `borderBottom 1px C.divider`, фон `*Bg`,
  левая кромка `inset 3px 0 0` цветом тона (`C.info` / `C.danger` / `C.warning`). Текст `FS.sm`:
  имя 600 `C.textHeading`, причина `C.textSecondary` (на 360 — с новой строки). Кнопка —
  `Button` variant secondary size sm: «Ответить» (wait → `linkedSessionId`), «Открыть чат»
  (stopped), «Открыть» (merge → страница фичи), «Что вернули» (loop → попап на шаге).
- Тело: padding 9×13×10, зазор 6. Строка: этап · «этап N из K» · справа аватары.
- Полоса шагов: высота 6, `R` 3, фон `C.progressTrack`, зазор сегментов 1; accepted `C.success`,
  review `C.info`, work `C.accent`, notCreated — прозрачный (виден трек).
- Легенда: `FS.xs`, `C.textSecondary`, квадрат 8 с радиусом 2 цвета сегмента; только ненулевые;
  справа «Подробнее ›» (`FS.sm` 600 `C.accent`) → `openPillMenu`.

## Состояния (все варианты)
- **loading** (`useFeatureRunsLoading`, run ещё нет): строка-скелет высотой 40, `C.bgSelected`, без чисел.
- **error**: строка «Не удалось загрузить фичу · Повторить» (`C.textSecondary`, кнопка `C.accent` → `refetchFeatureRuns`).
- **empty** (фич нет): прежний полный чек-лист — не эта ветка.
- **Тёмная тема**: всё на CSS-переменных, проверено в макете (полоса notCreated — трек, а не `C.bgInset`: на тёмной inset почти чёрный).
