# Эталон размеров режима «Исполнение фичи» по макетам v1/v2

Дата: 2026-10-18. Автор: Майя. Задача трекера `c990e835` — часть 1 из 3 дизайн-ревью «до пикселя»
(часть 2 — замеры прода у Веры, часть 3 — сверка по этой таблице).

**Источники:** `docs/mockups/turn-plan-feature-mode-v1.html` (CSS, строки 7–256),
`turn-plan-feature-mode-v2.html` (CSS, строки 7–311; разметка и инлайн-размеры — функции `glyph`, `coil`,
`chain`, `featureBody`, `conveyor`, `stepsList`, `stepDetails`, `drawArcs`, `pill`, `menu`, `planCard`,
`taskCard`, строки 467–746), `-v1-proposal.md` и `-v2-proposal.md` (спецификации для Киры),
`frontend/src/lib/design.ts` (шкалы `FS`, `SP`, `R`, `SHADOW`, `TB`), `theme.css`,
`components/ui/icons.ts` (`ICON_SIZE`), `components/ui/InlineSegmented.tsx`, `components/ui/Button.tsx`.

**Что эталон.** v2 перекрывает v1 там, где они расходятся (единственное расхождение CSS — цвет рельсы
конвейера, см. `rail`). Всё остальное в CSS v1 и v2 побайтно одинаково: строки 1–255 v2 совпадают с v1,
кроме строки 204.

## Как читать

- **Имя элемента** — устойчивое, его же использует Вера при замерах. Рядом — селектор макета.
- **Шрифт:** `семейство · размер/вес · line-height · letter-spacing`. Семейства: `sans` = `FONT.sans`
  (Hanken Grotesk), `mono` = `FONT.mono` (JetBrains Mono), `serif` = `FONT.serif` (PT Serif).
  Где line-height не задан, он наследуется от `body` макета: **1.45**. Вес не указан — 400.
- **Отступы:** `padding` в порядке CSS (верх/право/низ/лево), `gap` — зазор флекса/грида.
- **Размеры:** «деск» — окно 780 px, «360» — мобильная рамка 360 px (`.app.m`). Тач-цель — минимальная
  высота нажимаемой зоны на 360.
- **Шкала:** соответствие токенам. **⚠ вне шкалы** — значение макета не ложится ни в одну ступень
  `FS`/`SP`/`R`/`ICON_SIZE`; это отдельная находка, сводка в конце.
- Цвета даны токеном `C.*`; значения обеих тем — в таблице токенов ниже (сверены с `theme.css`,
  совпадают с `:root`/`[data-theme="dark"]` макетов 1:1).

## Токены цвета, которые использует режим

| Токен | CSS-переменная | Светлая | Тёмная |
|---|---|---|---|
| `C.bgMain` | `--c-bg-main` | `#F4F0E8` | `#201C18` |
| `C.bgPanel` | `--c-bg-panel` | `#EDE7DC` | `#272320` |
| `C.bgCard` | `--c-bg-card` | `#FBF8F2` | `#2E2A25` |
| `C.bgWhite` | `--c-bg-white` | `#FFFFFF` | `#2E2A25` |
| `C.bgSelected` | `--c-bg-selected` | `#E8E1D4` | `#38332D` |
| `C.textHeading` | `--c-text-heading` | `#2A251F` | `#F2ECE1` |
| `C.textPrimary` | `--c-text-primary` | `#39332B` | `#EDE6DB` |
| `C.textSecondary` | `--c-text-secondary` | `#756B5E` | `#B3A897` |
| `C.textMuted` | `--c-text-muted` | `#9A8F7E` | `#8A8072` |
| `C.onAccent` | `--c-on-accent` | `#FBF8F2` | `#201C18` |
| `C.accent` | `--c-accent` | `#D97757` | `#E38A6A` |
| `C.accentLight` | `--c-accent-light` | `#F4ECE1` | `#3A2E27` |
| `C.accentMuted` | `--c-accent-muted` | `#EAD3C5` | `#4A362C` |
| `C.border` | `--c-border` | `#E0D7C8` | `#3D3830` |
| `C.borderLight` | `--c-border-light` | `#E8E1D4` | `#34302A` |
| `C.divider` | `--c-divider` | `#DDD4C4` | `#454037` |
| `C.dashed` | `--c-dashed` | `#D0C6B4` | `#4A443B` |
| `C.progressTrack` | `--c-progress-track` | `#D8CFBE` | `#5C544A` |
| `C.success` | `--c-success` | `#5E8B4E` | `#7CB86A` |
| `C.successBg` | `--c-success-bg` | `#E9F1E8` | `#232E20` |
| `C.successText` | `--c-success-text` | `#3F7A4F` | `#8FCB79` |
| `C.warning` | `--c-warning` | `#C9923E` | `#E0AC5A` |
| `C.warningBg` | `--c-warning-bg` | `#FBEFE0` | `#322717` |
| `C.warningText` | `--c-warning-text` | `#8A6A28` | `#E7B96E` |
| `C.danger` | `--c-danger` | `#B4452F` | `#E8785E` |
| `C.dangerBg` | `--c-danger-bg` | `#FBF1EC` | `#331F1B` |
| `C.dangerText` | `--c-danger-text` | `#B4452F` | `#EE8A72` |
| `C.info` | `--c-info` | `#3E7CA6` | `#5FA0CC` |
| `C.infoBg` | `--c-info-bg` | `#E7EFF5` | `#1E2A33` |
| `SHADOW.focus` | `--shadow-focus` | `0 0 0 3px rgba(217,119,87,.14)` | `0 0 0 3px rgba(227,138,106,.24)` |
| `SHADOW.card` | `--shadow-card` | `0 2px 8px rgba(60,50,35,.05)` | `0 2px 10px rgba(0,0,0,.35)` |
| `SHADOW.dropdown` | `--shadow-dropdown` | `0 8px 28px rgba(60,50,35,.16)` | `0 10px 32px rgba(0,0,0,.55)` |
| `SHADOW.press` | `--shadow-press` | `inset 0 1px 3px rgba(60,50,35,.12)` | `inset 0 1px 4px rgba(0,0,0,.38)` |

**Тоны** (переменные `--tc` глиф/рамка, `--tt` текст, `--tb` фон):

| Тон | `--tc` | `--tt` | `--tb` |
|---|---|---|---|
| `wait` (ждёт вас) | `C.info` | `C.info` | `C.infoBg` |
| `loop` (возврат) | `C.warning` | `C.warningText` | `C.warningBg` |
| `crash` (исполнитель упал) | `C.danger` | `C.dangerText` | `C.dangerBg` |
| `run` (идёт) | `C.textHeading` | `C.textHeading` | `C.bgSelected` |
| `done` (смержена/закрыта) | `C.success` | `C.successText` | `C.successBg` |

## 1. Пилюля в конце ответа (`TurnPlanPill`)

| Элемент | Селектор | Шрифт | Текст | Фон | Отступы | Рамка | Радиус | Размеры / тач | Шкала |
|---|---|---|---|---|---|---|---|---|---|
| `pill` | `.tpp` | mono · 11/400 · 1.45 · 0 | `C.textMuted` | `C.bgSelected` | padding 4 11; gap 7 | нет; фокус `SHADOW.focus` | 8 | max-w 300, сжимается; деск — по контенту (≈24); 360 — min-h **28** | FS.xs, SP.xs; 11 ⚠, 7 ⚠; R.md |
| `pill-icon` (маршрут, список) | `.tpp .ic svg` | — | идёт `C.accent`; смержена/закрыта `C.textMuted` | — | — | stroke 2 | — | **12×12** | ⚠ ICON_SIZE.xs = 14 (спека v1 так и пишет) |
| `pill-glyph-wait` | `glyph('wait',12)` | — | ромб `C.info` | — | — | — | — | 12×12 | ⚠ вне ICON_SIZE |
| `pill-dot` (сигнал на значке плана) | `.tpp .ic .dot` | — | — | `C.info` | смещение right −3, top −3 | кольцо 1.5 `C.bgSelected` | full | 6×6 | 1.5 ⚠ |
| `pill-label` («фича» / «фичи») | `.tpp .k` (первый) | наследует | `C.textMuted` | — | — | — | — | на 360 **скрыт** | — |
| `pill-sep` («·») | `.tpp .d` | наследует | `C.textMuted`, opacity .45 | — | — | — | — | — | — |
| `pill-counter` (`acc/tot`) | `.tpp .k` | mono · 11/400 | `C.textMuted` | — | — | — | — | — | FS.xs |
| `pill-returns` (`↺N`) | `.tpp .ret` | mono · 11/400; при ↺ ≥ 3 — 700 | обычно `C.textMuted`; `.hot` — `C.warningText` | — | — | — | — | — | FS.xs |
| `pill-more` (`+N`) | `.tpp .more` | mono · 11/400 | `C.textMuted`, opacity .8 | — | — | — | — | — | FS.xs |
| `pill-text` | `.tpp .t` | mono · 11/400; тон wait/crash — 600 | `C.textMuted`; wait `C.info`; crash `C.dangerText` | — | — | — | — | обрезка многоточием | FS.xs |
| `meta-pill` (соседка слева, для сравнения) | `.meta-pill` | mono · 11/400 | `C.textMuted` | `C.bgSelected` | padding 4 11; gap 7 | нет | 8 | white-space nowrap | как `pill` |

## 2. Меню пилюли (поверхность «меню»)

| Элемент | Селектор | Шрифт | Текст | Фон | Отступы | Рамка | Радиус | Размеры / тач | Шкала |
|---|---|---|---|---|---|---|---|---|---|
| `menu` | `.menu` | — | — | `C.bgWhite` | padding **5** | 1 `C.border`; тень `SHADOW.dropdown` | 12 | деск: w 380 (min 280, max 380), right 22, bottom 58, max-h 520 (режим фичи) / 340 (план); 360: left/right 8, bottom 62, max-h 560 | R.xl, SHADOW.dropdown; 5 ⚠, 22 ⚠, 58 ⚠, 62 ⚠; 380/520/560 — нет токена |
| `menu-inner` | `.menu .in` | — | — | — | padding 9 12 11; в режиме фичи padding-top 0 | — | — | прокрутка внутри | SP.md; 9 ⚠, 11 ⚠ |
| `menu-header` | `.menu .hd` | — | — | в режиме фичи `C.bgWhite` (липкая) | padding-top 9 (фича), padding-bottom 7, margin-bottom 5; gap 8 | низ 1 `C.divider` | — | sticky top 0, z 2 | SP.sm; 9 ⚠, 7 ⚠, 5 ⚠ |
| `menu-title` (чат без фичи) | `.hd .ttl` | serif · 13/700 | `C.textHeading` | — | — | — | — | — | FS.base |
| `mode-switch` (дорожка) | `.seg` = `InlineSegmented size="sm"` | — | — | `C.bgSelected` | padding 2; gap 2 | — | 10 | высота 24 (20 + 2 + 2) | SP.xxs, R.lg (= TB.pillRadius + 1) |
| `mode-switch-btn` | `.seg button` | sans · 11/600 | неактивная `C.textMuted`; активная `C.accent` | активная `C.accentLight`, иначе прозрачный | padding 2 8 (360: 2 10) | нет; фокус `SHADOW.focus` | 7 | деск min-h 20; 360 min-h **40** | FS.xs, SP.xxs, SP.sm; 10 ⚠; R 7 ⚠ (= TB.pillRadius − 2, так и в коде компонента) |
| `counter` (шапка) | `.cnt` | mono · 11/400 | `C.textMuted` | — | margin-left auto | — | — | nowrap | FS.xs |

## 3. Карточка плана в ленте (поверхность «карточка»)

| Элемент | Селектор | Шрифт | Текст | Фон | Отступы | Рамка | Радиус | Размеры / тач | Шкала |
|---|---|---|---|---|---|---|---|---|---|
| `plan-card` | `.card` | — | — | `C.bgWhite` | — | 1 `C.borderLight`; тень `SHADOW.card` | 12 | ширина ленты: деск 780 − 2×22, 360 − 2×12 | R.xl, SHADOW.card; поля ленты 22 ⚠ (360: 12 = SP.md) |
| `plan-card-header` | `.card .ch` | — | — | — | padding 10 13; gap 8 | низ 1 `C.divider` | — | — | SP.sm; 10 ⚠, 13 ⚠ |
| `plan-card-icon` | `ICON.sq` | — | `C.accent` | — | — | stroke 2 | — | 15×15 | ⚠ вне ICON_SIZE (14/16) |
| `plan-card-title` | `.card .ch .ttl` | serif · 14/700 | `C.textHeading` | — | — | — | — | на 360 при фиче **скрыт** | FS.md |
| `plan-card-body` | `.card .cb` | — | — | — | padding 7 13 10 | — | — | — | 7 ⚠, 13 ⚠, 10 ⚠ |
| `plan-card-footer` («Задачи фичи в трекере →») | `.card .cf` | sans · 12/600 | `C.accent` | нет | padding 8 14; gap 5 | верх 1 `C.divider` | — | w 100%; фокус `SHADOW.focus`; стрелка 13 | FS.sm, SP.sm; 14 ⚠, 5 ⚠, 13 ⚠ |

## 4. Тело режима (общее для меню и карточки)

| Элемент | Селектор | Шрифт | Текст | Фон | Отступы | Рамка | Радиус | Размеры / тач | Шкала |
|---|---|---|---|---|---|---|---|---|---|
| `feature-body` | `.fx` | — | — | — | gap 10 (колонка) | — | — | — | 10 ⚠ |
| `section-label` («Сейчас», «Конвейер», «Фичи чата · N») | `.sec` | sans · 10/700 · 1.45 · .06em, UPPERCASE | `C.textMuted` | — | margin 2 0 −4 | — | — | — | ⚠ 10 вне FS (как `CellLabel`); SP.xxs; −4 = −SP.xs |
| `feature-picker` | `.fsel` | — | — | — | gap 4; 360: в одну строку, горизонтальная прокрутка, padding-bottom 2 | — | — | — | SP.xs, SP.xxs |
| `feature-chip` | `.fsel button` | sans · 12/400; выбранный 600 | `C.textSecondary`; выбранный `C.textHeading` | `C.bgCard`; выбранный `C.accentLight`; hover `C.bgSelected` | padding 2 8; gap 5 | 1 `C.borderLight`; выбранный `C.accentMuted`; фокус `SHADOW.focus` | 8 | деск min-h **26**; 360 min-h **40**, max-w 220, не сжимается | FS.sm, SP.xxs, SP.sm, R.md; 26 ⚠, 5 ⚠ |
| `feature-chip-glyph` | `glyph(*,12)` | — | по состоянию | — | — | — | — | 12×12 | ⚠ вне ICON_SIZE |
| `feature-chip-pin` («●» — эту фичу показывает пилюля) | `.fsel .pin` | sans · 10/700 | `C.accent` | — | — | — | — | — | ⚠ 10 вне FS |
| `feature-head` | `.fx .fh` | — | — | — | gap 8, выравнивание по базовой линии, перенос | — | — | — | SP.sm |
| `feature-title` | `.fx .fn` | sans · 13/600 | `C.textHeading` | — | — | — | — | — | FS.base |
| `feature-branch` | `.fx .br` | mono · 11/400 | `C.textMuted` | — | — | — | — | — | FS.xs |
| `attention-banner` (ждёт вас / упал) | `.alert.t-wait` / `.t-crash` | sans · 12.5/400; `b` — 700 | тон `--tt` (wait `C.info`, crash `C.dangerText`) | тон `--tb` (`C.infoBg` / `C.dangerBg`) | padding 8 10; gap 8 (колонка); строка `.r` gap 8 | нет | 8 | — | ⚠ 12.5 вне FS; SP.sm; 10 ⚠; R.md |
| `attention-glyph` | `glyph('wait'/'crash',14)` | — | ромб `C.info` / квадрат `C.danger` | — | margin-top 2 | — | — | 14×14 | ICON_SIZE.xs |
| `attention-actions` | `.acts` | — | — | — | gap 6, перенос | — | — | — | ⚠ 6 вне SP |
| `button-xs` (кнопки плашки) | `.btn` = `Button size="xs"` | sans · 12/600 | `C.textSecondary`; hover `C.textPrimary` | secondary `C.bgPanel`; ghost прозрачный; hover `C.bgSelected` | padding 0 9; gap 5 | ghost — 1 `C.border`; нажатие `SHADOW.press`; фокус `SHADOW.focus` | 8 | деск h **24** | FS.sm, R.md; 9 ⚠, 5 ⚠ (так и в `Button.tsx`) |
| `button-xs` на 360 | `.app.m .btn` | sans · **13**/600 | как выше | как выше | padding 0 12 | как выше | **10** | min-h **40** | FS.base, SP.md, R.lg (в коде это `Button size="sm"`: см. находку Н-12) |
| `rec` (совет, только v1) | `.recs` / `.rec` | sans · 12.5/400 | `C.textPrimary` | — | список gap 4; строка padding 6 0 (первая — 2 0 0), gap 8; кнопки margin-top 5 | верх 1 `C.borderLight` (кроме первой) | — | глиф 13, margin-top 2 | ⚠ 12.5; 6 ⚠, 5 ⚠, 13 ⚠ |

## 5. «Сейчас»

| Элемент | Селектор | Шрифт | Текст | Фон | Отступы | Рамка | Радиус | Размеры / тач | Шкала |
|---|---|---|---|---|---|---|---|---|---|
| `now-line` | `.now` | — | — | `C.bgCard` | padding 8 10; gap 9 | 1 `C.borderLight` | 10 | выравнивание по верху | SP.sm, R.lg; 10 ⚠, 9 ⚠ |
| `now-avatar` | `.av` | sans · 11/700 | `C.accent` | `C.accentMuted` | — | — | full | 22×22 | FS.xs; 22 — нет токена (`PersonaAvatar` 22 по спеке v1) |
| `now-who` (кто · шаг) | `.now .a` | sans · 13/600 | `C.textHeading` | — | — | — | — | — | FS.base |
| `now-what` | `.now .b` | sans · 12/400 | `C.textSecondary` | — | margin-top 1 | — | — | — | FS.sm; 1 ⚠ |
| `now-time` («идёт 12 мин») | `.now .tm` | mono · 11/400 | `C.textMuted` | — | — | — | — | nowrap | FS.xs |

## 6. Конвейер

| Элемент | Селектор | Шрифт | Текст | Фон | Отступы | Рамка | Радиус | Размеры / тач | Шкала |
|---|---|---|---|---|---|---|---|---|---|
| `rail` | `.cv > li::before` | — | — | **v2: `C.progressTrack`**; v1: `C.textMuted` с opacity .55 (отменено v2) | left 8 (центр), top 22, bottom −6 | — | 2 | ширина `2 + (проходы − 1) × 1.5`, потолок 8; у последней строки нет | 2 = SP.xxs; ⚠ дробные 3.5/5/6.5, 22 ⚠, 6 ⚠ |
| `rail-todo` (будущий этап) | `.cv > li.todo::before` | — | — | — | — | слева 2 dashed `C.dashed` | — | ширина 0 | — |
| `rail-dot` (глиф этапа) | `.row .gw` + `glyph(*,16)` | — | по состоянию (см. «Глифы») | `C.bgWhite` (подложка, перекрывает рельсу) | padding-top 1 | — | full | глиф 16×16, колонка 16 | ICON_SIZE.sm |
| `stage-row` | `.row` | — | — | прозрачный; hover `C.bgSelected` | padding 5 4 5 0; грид 16 / 1fr / auto, gap 9 | нет; фокус `SHADOW.focus` | 8 | деск min-h **32**; 360 min-h **40** | TB.iconHitDesktop/Mobile, R.md, SP.xs; 5 ⚠, 9 ⚠ |
| `stage-name` | `.row .nm` / `.nm b` | sans · 13/400, имя этапа — 600 | `C.textPrimary`, имя `C.textHeading`; будущий этап — `C.textMuted` | — | gap 6, перенос | — | — | — | FS.base; 6 ⚠ |
| `stage-meta` | `.row .ms` | sans · 12/400 | `C.textSecondary` | — | margin-top 1 | — | — | — | FS.sm; 1 ⚠ |
| `stage-chevron` | `.row .chev` | — | `C.textMuted` | — | — | stroke 2 | — | 14×14; раскрыт — поворот 180°, .15s | ICON_SIZE.xs |
| `stage-right` | `.row .rt` | — | — | — | padding-top 1; gap 4 | — | — | — | SP.xs; 1 ⚠ |
| `stage-badge` (`↺N ревью/проверка/финал`) | `.badge` | sans · 11/700 · **18px** | `C.textSecondary`; `.hot` `C.warningText`; `.dng` `C.dangerText` | `C.bgSelected`; `.hot` `C.warningBg`; `.dng` `C.dangerBg` | padding 0 7; gap 3 | нет | 999 | высота 18 | FS.xs, R.max; 7 ⚠, 3 ⚠, lh 18 — нет токена |
| `stage-tag` («стоит сейчас», «ждёт вас») | `.tag` | sans · 11/700 · 18px | тон `--tt` | тон `--tb` | padding 0 8; gap 4 | 1 тон `--tc` | 999 | высота 20 (18 + рамка) | FS.xs, SP.sm, SP.xs, R.max |
| `lane-bars` (дорожки производства) | `.lanes` | — | — | — | margin 2 0 4 25; gap 5 | — | — | — | 25 ⚠, 5 ⚠ |
| `lane-row` | `.lane` | sans · 12/400 | `C.textSecondary` | — | грид auto / 1fr / auto, gap 8 | — | — | — | FS.sm, SP.sm |
| `lane-track` | `.lane .tr` / `.tr i` | — | — | дорожка `C.progressTrack`, заливка `C.textMuted` | — | — | 999 | высота `2 + возвраты × 0.5`, потолок 8 | R.max; ⚠ дробные |
| `stage-rounds` (витки под строкой этапа) | `.stg-rounds` / `.lbl2` | подпись sans · 11/400 | `C.textMuted` | — | margin 0 0 4 25; gap 3 | — | — | — | FS.xs; 25 ⚠, 3 ⚠ |
| `arcs` (вариант «Дуги», только деск) | `.cv.arcs`, `.arcsvg` | число — mono · 10/700 (SVG) | ревью `C.textSecondary`, проверка `C.info`, финал `C.accent` | — | колонка справа 46; шаг дуги 7 | stroke 1.5, у i-й дуги opacity `1 − i × .15` | — | — | ⚠ 10, 46, 7, 1.5 |

## 7. Детали этапа и шаги

| Элемент | Селектор | Шрифт | Текст | Фон | Отступы | Рамка | Радиус | Размеры / тач | Шкала |
|---|---|---|---|---|---|---|---|---|---|
| `stage-details` | `.det` | — | — | `C.bgCard` | margin 2 0 8 25; padding 8 10; gap 8 | 1 `C.borderLight` | 10 | — | SP.sm, R.lg; 25 ⚠, 10 ⚠ |
| `details-heading` | `.det .h` | sans · 10/700 · .06em, UPPERCASE | `C.textMuted` | — | — | — | — | — | ⚠ 10 вне FS |
| `details-item` (строка задачи) | `.det li` / `.s` | sans · 12/400 | `C.textPrimary`; пояснение `C.textSecondary` | — | список gap 4; строка gap 7 | — | — | — | FS.sm, SP.xs; 7 ⚠ |
| `details-avatar` | `.av.sm` | sans · 10/700 | `C.accent` | `C.accentMuted` | — | — | full | 18×18 | ⚠ 10 вне FS |
| `details-link` («Открыть задачу →») | `.det .lnk` | sans · 12/600 | `C.accent` | нет | padding 2 0; gap 5 | нет | — | деск min-h 24; 360 min-h **40**; стрелка 13 | FS.sm, SP.xxs; 5 ⚠, 13 ⚠ |
| `hint` / `nodata` | `.hint` / `.nodata` | sans · 12/400 (`nodata` — курсив) | `C.textSecondary` / `C.textMuted` | — | — | — | — | — | FS.sm |
| `lane-heading` (дорожка над шагами) | `.lanehd` | sans · 11/400 | `C.textMuted` | — | margin-top 4 | — | — | — | FS.xs, SP.xs |
| `steps` | `.steps` | — | — | — | gap 2 | — | — | — | SP.xxs |
| `step-row` | `.step` | sans · 12/400 | `C.textPrimary` | hover и раскрытый — `C.bgSelected` | padding 4; грид 14 / 1fr / auto, gap 7 | нет; фокус `SHADOW.focus` | 8 | деск min-h **28**; 360 min-h **40** | FS.sm, SP.xs, R.md; 7 ⚠, 28 ⚠ |
| `step-glyph` | `glyph(*,14)` | — | по состоянию | — | — | — | — | 14×14 | ICON_SIZE.xs |
| `step-ref` («Ш3», «A5.1») | `.step .mono` | mono · **12**/400 (наследует от строки) | `C.textMuted` | — | — | — | — | — | FS.sm (см. находку Н-14) |
| `step-closed-note` («· закрыт без …») | `.step .s` | sans · 12/400 | `C.textMuted` | — | — | — | — | — | FS.sm |
| `step-right` | инлайн `span` | — | — | — | gap 4 | — | — | — | SP.xs |
| `step-duration` / «ждёт ревью» | `.mut` | mono · 11/400 | `C.textMuted` | — | — | — | — | — | FS.xs |
| `source-tag` (ревью / проверка / финал) | `.src.review` / `.check` / `.final` | sans · **10.5**/700 · 17px | `C.textSecondary` / `C.info` / `C.accent` | `C.bgSelected` / `C.infoBg` / `C.accentLight` | padding 0 6; gap 3 | нет | 6 | высота 17 | R.sm; ⚠ 10.5, 6, 3, 17 |
| `step-details` | `.det` + инлайн | — | — | `C.bgWhite` (не `bgCard`) | margin-left **0** (полная ширина и на деске, и на 360); padding 8 10; gap 8 | 1 `C.borderLight` | 10 | — | SP.sm, R.lg; 10 ⚠ |
| `step-details-summary` (Ш · время · круги · итог) | инлайн `div` / `b` | sans · 12/400, итог 700 | `C.textSecondary`, итог `C.textHeading` | — | — | — | — | — | FS.sm |

## 8. Витки: пружина, бусины, цепочка кругов

| Элемент | Селектор | Шрифт | Текст | Фон | Отступы | Рамка | Радиус | Размеры / тач | Шкала |
|---|---|---|---|---|---|---|---|---|---|
| `coil` (вариант «Пружина») | `.coil` + SVG | — | — | — | gap 5 | эллипсы rx 5 / ry 6.4, шаг 7; вернул — `C.warning` stroke 2; с правками — `C.warning` 1.6 пунктир 2.4 1.8; принял — `C.success` 2; принял Глеб — `C.success` 1.6 пунктир; идёт — `C.textHeading` 1.6 пунктир 2 2, пульс | — | SVG h 16, w `12 + (N − 1) × 7`; до 6 колец, дальше `+N` | ⚠ 5, 7, дробные |
| `coil-caption` («3 витка · ↺2») | `.coil .cap` / `b` | mono · 11/400; `↺` — 700 | `C.textMuted`; `↺N` — `C.warningText` | — | — | — | — | nowrap | FS.xs |
| `chain` (вариант «Дуги») | `.chain` / `.ar` | стрелка sans · 11/400 | стрелка `C.textMuted` | — | gap 3, перенос | — | — | до 4 бусин, дальше `+N →` | FS.xs; 3 ⚠ |
| `bead` | `.bead.ret` / `.fix` / `.ok` / `.run` / `.ext` | sans · 11/700 · 18px | ret `C.warningText`; fix `C.warningText`; ok `C.successText`; run `C.textHeading`; ext `C.successText` | ret `C.warningBg`; fix `C.bgCard`; ok `C.successBg`; run `C.bgSelected`; ext `C.bgCard` | padding 0 7; gap 3 | 1 прозрачная; fix — dashed `C.warning`; run — dashed `C.dashed`; ext — solid `C.success` | 999 | высота 20 (18 + рамка) | FS.xs, R.max; 7 ⚠, 3 ⚠ |
| `round-chain` (цепочка кругов шага) | `.tl2` | — | — | — | padding-left 2 | — | — | — | SP.xxs |
| `round-item` | `.tl2 li` | sans · 12/400 | `C.textPrimary` | — | padding 3 0 5; грид 14 / 1fr, gap 7 | — | — | глиф 13, margin-top 2 | FS.sm; 3 ⚠, 5 ⚠, 7 ⚠, 13 ⚠ |
| `round-rail` | `.tl2 li::before` | — | — | `C.progressTrack`; ветка возврата `.back` — `C.warning` opacity .55 | left 6, top 17, bottom −3 | — | 2 | ширина 2 | SP.xxs; 6 ⚠, 17 ⚠ |
| `round-title` («Круг 2» + бусина + кто·когда) | `.tl2 .t1` / `b` | sans · 12/700 | `C.textPrimary` | — | gap 4 6, перенос | — | — | — | FS.sm; 6 ⚠ |
| `round-time` | `.tl2 .tm2` | mono · 11/400 | `C.textMuted` | — | — | — | — | nowrap | FS.xs |
| `round-reason` | `.tl2 .t2` | sans · 12/400 | `C.textSecondary` | — | margin-top 1; перенос в любом месте | — | — | — | FS.sm; 1 ⚠ |
| `round-avatar` | `.av.sm` | sans · 10/700 | `C.accent` | `C.accentMuted` | — | — | full | 18×18 | ⚠ 10 |

## 9. Итог, пустое состояние, легенда

| Элемент | Селектор | Шрифт | Текст | Фон | Отступы | Рамка | Радиус | Размеры / тач | Шкала |
|---|---|---|---|---|---|---|---|---|---|
| `summary` (смержена) | `.fin` | — | — | `C.successBg` | padding 9 11; gap 10 | нет | 10 | глиф `done` 18 | R.lg; ⚠ 9, 11, 10, 18 (ICON_SIZE.md = 18 — совпадает) |
| `summary` (спайк закрыт) | `.fin` + инлайн | — | — | `C.bgSelected` | как выше | нет | 10 | глиф `closed` 18 | как выше |
| `summary-title` | `.fin .a` | sans · 13/700 | смержена `C.successText`; закрыт `C.textHeading` | — | — | — | — | — | FS.base |
| `summary-sub` (дата · срок · задачи · шаги · ↺) | `.fin .b.mono` | **mono** · 12/400 | `C.textSecondary` | — | — | — | — | — | FS.sm |
| `empty` | `.empty` | sans · 12.5/400 | `C.textSecondary` | `C.bgCard` | padding 10 12 | 1 dashed `C.dashed` | 10 | — | R.lg, SP.md; ⚠ 12.5, 10 |
| `legend` | `.legend` | sans · 11/400 | `C.textMuted` | — | gap 4 12, перенос | — | — | глиф `none` 11; пружина без подписи (h 16) | FS.xs, SP.xs, SP.md; ⚠ глиф 11 |

## 10. Карточка задачи и чип «Шаг N фичи»

| Элемент | Селектор | Шрифт | Текст | Фон | Отступы | Рамка | Радиус | Размеры / тач | Шкала |
|---|---|---|---|---|---|---|---|---|---|
| `task-card` | `.card.tc` | — | — | `C.bgWhite` | — | 1 `C.borderLight` + слева **3** `C.accent`; hover рамка `C.accentMuted`; тень `SHADOW.card` | 12 | — | R.xl; 3 ⚠ |
| `task-card-header` | `.tc .th` | — | — | `C.accentLight` | padding 9 12; gap 9 | низ 1 `C.divider` | — | — | SP.md; ⚠ 9 |
| `task-card-badge` | `.tc .th .bd` | — | `C.accent` | `C.accentMuted` | — | — | full | 26×26, иконка 14 | ⚠ 26 |
| `task-card-title` («Задача создана») | инлайн `span.serif` | serif · 14/700 | `C.textHeading` | — | — | — | — | — | FS.md |
| `task-card-meta` («tasks · create · …») | инлайн `span.mono` | mono · 11/400 | `C.textMuted` | — | margin-left auto | — | — | — | FS.xs |
| `task-card-body` | `.tc .tb` | — | — | — | padding 11 14 12 | — | — | — | SP.md; ⚠ 11, 14 |
| `task-card-name` | `.tc .tt` | sans · **15**/700 · **1.35** | `C.textHeading` | — | — | — | — | — | ⚠ 15 вне FS (14/16), ⚠ lh 1.35 |
| `task-card-cell-label` | `.cl` | sans · 10/700 · .06em, UPPERCASE | `C.textMuted` | — | margin-bottom 4 | — | — | — | ⚠ 10; SP.xs |
| `task-card-cell-value` | `.cv2` | sans · 12.5/600 | `C.textPrimary` | — | gap 6 | — | — | — | ⚠ 12.5, 6 |
| `task-card-branch` | `.cv2.mono` + инлайн | mono · **11.5**/400 | `C.textPrimary` | — | — | — | — | обрезка многоточием | ⚠ 11.5 вне FS |
| `chip` («Шаг N фичи · Производство, …») | `.featchip` | sans · 12/400 | `C.textSecondary`; hover `C.textPrimary` | `C.bgCard`; hover `C.bgSelected` | padding 3 9; gap 6; margin-top 11 | 1 `C.borderLight` | 8 | деск min-h **28**; 360 min-h **40** | FS.sm, R.md; ⚠ 3, 9, 6, 11, 28 |
| `chip-icon` | `ICON.route` | — | `C.accent` | — | — | stroke 2 | — | 12×12 | ⚠ вне ICON_SIZE |
| `task-card-footer` («Открыть задачу →») | `.card .cf` | sans · 12/600 | `C.accent` | — | padding 8 14; gap 5 | верх 1 `C.divider` | — | — | как `plan-card-footer` |

## 11. Глифы состояний (SVG, viewBox 16)

| Глиф | Форма и цвет | Где и каким размером |
|---|---|---|
| `crash` | квадрат 11, rx 1.5, заливка `C.danger` | пилюля 12, плашка 14 |
| `wait` | ромб, заливка `C.info` | пилюля 12, чип фичи 12, плашка 14, этап 16 |
| `loop` | дуга со стрелкой, stroke 2.2 / 2 `C.warning` | этап 16, шаг 14, круг-возврат 13 |
| `run` | кольцо r 6.5 stroke 1.5 `C.textHeading` (пульс 1.6s, opacity .25↔.9; reduced-motion — .5) + точка r 3.5 | этап 16, шаг 14, строка задачи 13, чип 12 |
| `await` | кольцо + стрелки часов, `C.textHeading` 1.5 | шаг 14 |
| `done` | круг r 7 `C.success` + галка `C.onAccent` 1.8 | итог 18, круг «принял» 13 |
| `past` | круг r 6.5 `C.bgSelected`, обводка 1 `C.textMuted` + галка `C.textMuted` 1.6 | этап 16, шаг 14, строка задачи 13, чип 12 |
| `closed` | как `past`, вместо галки черта | шаг 14, итог 18, чип 12 |
| `none` (нет в трекере) | круг r 6 `C.dashed` 1.5 перечёркнут | этап 16, легенда 11 |
| `todo` (ещё не начат) | круг r 6, пунктир 2.5 2 `C.dashed` 1.5 | этап 16 |
| `do` (сделал) | точка r 4 `C.textMuted` | цепочка кругов 13 |
| `ghost` (ревью вне трекера) | круг r 6, пунктир `C.warning` 1.5 | цепочка кругов 13 |

Размеры глифов: 11, 12, 13, 14, 16, 18. В `ICON_SIZE` ложатся только 14 (`xs`), 16 (`sm`), 18 (`md`).

## Итог: что считать в эталоне

- Строк элементов с устойчивыми именами: **106** (таблицы 1–10), плюс 12 глифов (таблица 11), 33 токена
  цвета/тени и 5 тонов. Всего строк эталона: **156**.
- Поверхности: меню пилюли (табл. 2 + тело 4–9) и карточка плана (табл. 3 + то же тело); десктоп и 360
  разведены в колонке «Размеры / тач».

## Находки: макет вне шкалы и расхождения внутри макетов

**Типографика (FS: 11 / 12 / 13 / 14 / 16 …):**

- **Н-1. 10 px** — подписи секций (`section-label`), `details-heading`, `task-card-cell-label`, аватар 18
  (`details-avatar`, `round-avatar`), булавка `feature-chip-pin`, числа дуг. Ступени `FS` ниже 11 нет.
  Это наследство `CellLabel`; решать либо `FS.xxs: 10` в шкалу, либо переводить на `FS.xs`.
- **Н-2. 12.5 px** — `attention-banner`, `rec`, `empty`, `task-card-cell-value`, описание карточки задачи.
- **Н-3. 10.5 px** — `source-tag`.
- **Н-4. 11.5 px** — `task-card-branch`.
- **Н-5. 15 px и line-height 1.35** — `task-card-name` (между `FS.md` 14 и `FS.lg` 16).
- **Н-6. Шкалы line-height в проекте нет:** макет держит 1.45 у `body`, 1.35 у имени задачи и
  фиксированные 18 / 17 px у бейджей, тегов, бусин и метки источника. Сверять на проде придётся
  в пикселях, а не по токену.

**Отступы (SP: 2 / 4 / 8 / 12 / 16 / 24 …):**

- **Н-7. Нечётные и промежуточные отступы встречаются повсюду:** 1, 3, 5, 6, 7, 9, 10, 11, 13, 14, 17, 22,
  25. Самые заметные: `pill` padding 4 11 и gap 7; `menu` padding 5; `menu-inner` 9 12 11; шапка карточки
  10 13, тело 7 13 10; `stage-row` gap 9 и padding 5; `step-row` gap 7; отступ деталей под рельсой 25;
  `now-line` и `attention-banner` padding 8 10; `summary` 9 11. Часть пришла из готовых компонентов
  (`Button xs` padding 0 9 — так и в `Button.tsx`), часть — собственная.
- **Н-8. Дробные толщины** рельсы конвейера (2 → 3.5 → 5 → 6.5 → 8) и дорожек (`+0.5` за возврат)
  не ложатся в 4-px сетку. Это намеренная метрика, а не сбой, но при сверке нужен допуск.

**Радиусы (R: 6 / 8 / 10 / 12 / 999 / 50 %):**

- **Н-9. 7 px** — кнопка `mode-switch-btn` (`TB.pillRadius − 2`, так и в `InlineSegmented.tsx`);
  **2 px** — скругление рельс (`rail`, `round-rail`). Остальные радиусы в шкале.

**Иконки (ICON_SIZE: 14 / 16 / 18 / 20 / 24):**

- **Н-10. 11, 12, 13, 15 px** — глиф легенды 11; значок пилюли, глифы пилюли и чипов фичи, значок чипа
  «Шаг N фичи» 12; глифы цепочки кругов, строк задач, советов и стрелка ссылок 13; значок шапки
  карточки 15.
- **Н-11. Сама спецификация v1 противоречит макету:** про значок пилюли она пишет `ICON_SIZE.xs` (14),
  а макет рисует его 12. На проде возможны оба значения, и оба «по макету». Решение в части 3:
  эталон — 12 (так нарисовано и согласовано), либо поднять до 14 вместе со всеми глифами пилюли.

**Высоты и тач-цели:**

- **Н-12. Мобильная кнопка плашки:** макет даёт 40 px / 13 px / padding 0 12 / R 10, спека v1 называет
  её `Button size="sm"`, а в `Button.tsx` у `sm` `minHeight` **32**, не 40. Если на проде кнопка 32, это
  не ошибка Киры, а дыра в спеке: нужна явная тач-высота 40.
- **Н-13. Пилюля на 360 — тач-цель 28 px**, меньше 40 у всех остальных нажимаемых элементов режима.
  Макет унаследовал это от `TurnPlanPill`, в спеке v1/v2 не оговорено. Отдельная находка для решения.
- Деск-высоты вне `TB`: `step-row` 28, `chip` 28, `feature-chip` 26, `details-link` 24. 24 совпадает
  с `Button xs`, 32/40 у `stage-row` — с `TB.iconHitDesktop/Mobile`.

**Согласованность внутри макета:**

- **Н-14. `step-ref` набран моно 12, а все остальные моно-подписи режима — 11** (`pill`, `counter`,
  `feature-branch`, `now-time`, `step-duration`, `round-time`, `coil-caption`). Размер наследуется от
  строки шага, отдельно не задан. На проде он мог стать и 11, и 12 — оба «по макету».
- **Н-15. Рельса конвейера v1 → v2:** v1 рисует `C.textMuted` с opacity .55, v2 — `C.progressTrack`
  (найдено в ревью v2). Эталон — v2, а спецификация v1 для Киры до сих пор называет старый цвет.
- **Н-16. `step-details` на десктопе тоже без отступа слева** (`margin-left 0`, фон `C.bgWhite`), хотя
  в предложении v2 это сказано только про мобилу. Эталон — макет: 0 на обеих ширинах.
- Значения тем в макетах совпадают с `theme.css` 1:1 — расхождений цвета на уровне токенов нет.
