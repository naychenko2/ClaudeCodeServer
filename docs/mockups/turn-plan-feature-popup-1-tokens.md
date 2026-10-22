# Попап плана хода, «Исполнение фичи»: таблица токенов

Привязка: эпик `9919bd75-0b1c-4fda-8196-72354cbcba29`, задача `3a4f64d1` (lane front). Макет —
[`turn-plan-feature-popup-1.html`](turn-plan-feature-popup-1.html), записка —
[`turn-plan-feature-popup-1.md`](turn-plan-feature-popup-1.md). Формат как у
[`features-overview-3-final-tokens.md`](features-overview-3-final-tokens.md): «Макет» — что нарисовано, «Код» —
что писать в продукте.

**Правила в силе:** ловушки `<button>` (`border: 'none'`, `fontFamily: FONT.sans`, фокус `SHADOW.focus`, кнопка в
кнопке запрещена); сырой hex и `rgba()` в `.tsx` запрещены (`npm run lint:design`). Значения обеих тем сверены с
`theme.css` 20.10. **Новых токенов не нужно**; тёмная тема проверена на всех состояниях.

## 1. Оболочка попапа

| Элемент | Макет | Код | 1280 · 360, px |
|---|---|---|---|
| попап | `bgWhite`, рамка `border`, `R.lg`, `SHADOW.dropdown`; 380 × до 520, на 360 во всю ширину до 560 | `ui/Menu` как сейчас (`pillMenuWidths`, `pillMenuFullWidth`, `menuMaxHeight`) | 380 · 344 |
| шапка | липкая, `bgWhite`, поля `9 12`, линия снизу `divider` | как в `FeatureModeBody` (`variant="pill"`); счётчик убран | — |
| переключатель | `InlineSegmented size="sm"`: трек `pillTrack`, активный `accentLight`/`accent` 600 | `ModeSwitcher` без изменений | 26 · 40 |
| тело | поля `10 12 12`, зазор 10 | `bodyPad` как сейчас | — |
| подпись секции | 10 / 700 капс `textMuted`, `letter-spacing .06em` | `SectionLabel` (есть), `FS.xxs` | — |
| подвал «Открыть страницу фичи» | 12 / 600 `accent`, `ArrowRight` 13 со сдвигом 3 при наведении, линия сверху `divider`, поля `8 13` | стиль нынешнего `FeatureCardFooter`, `FS.sm`; на 360 `minHeight: 44` | 33 · 44 |

## 2. Выбор фичи и шапка фичи

| Элемент | Макет | Код | 1280 · 360, px |
|---|---|---|---|
| чип фичи | `bgCard`, рамка `borderLight`, `R.md`, 12; выбран — `accentLight`, рамка `accentMuted`, `textHeading` 600; глиф 12 | `FeaturePicker` как сейчас **без точки `●`** | 26 · 40 |
| глиф | 14 | `FeatureGlyph size={14}`; «не начата» и «закрыта» — `empty` (пунктирный круг `textMuted`) | 14 |
| название | mono 13 / 700 `textHeading`, одна строка, многоточие, `title` с полным | `FONT.mono`, `FS.base`, `minWidth: 0`, `textOverflow: 'ellipsis'` | — |
| тег статуса | 11 / 600, поля `1 7`, `R.sm`; точка 6 `info` у «ждёт вас» и «ждёт согласия на мерж» | `StatusTag` страницы; тоны — таблица 3 финальной записки страницы | 18 |
| мета | 12 `textSecondary`, ветка mono 11 `textMuted` + `GitBranch` 12, `gap 4 12`, перенос | `FS.sm`, `FS.xs`, `flexWrap: 'wrap'` | — |

## 3. Строка состояния

Каркас: `R.lg`, поля `8 10`, зазор 7, 12; глиф 14 сверху; выделение 700 `textHeading`, пояснение `textSecondary`.

| Тон | Состояния | Код |
|---|---|---|
| «сейчас» | идёт, мерж идёт | `C.bgCard`, рамка `C.borderLight` |
| `info` | ждёт вас, мерж ждёт согласия | `C.infoBg`; текст `C.textPrimary` (не `C.info` целиком, как сейчас) |
| `danger` | стоит | `C.dangerBg`, выделение `C.dangerText` |
| нейтральный | не начата, закрыта без мержа | `C.bgSelected` |
| `success` | смержена | `C.successBg`, выделение `C.successText` |
| главное действие | `btn xs pri` («Ответить Кире →», «Чат Дениса →», «Мерж…») | `ui/Button size="xs" variant="primary"`; на 360 `minHeight: 40` |
| вторичное | «Перезапустить» `btn xs gh` | `ui/Button variant="ghost"` + прежний `ConfirmDialog` |
| пара «чат · задача» | как на странице (раздел 9а) | `NavTargetButton`; в строке состояния на 360 — с текстом, `minHeight: 40` |
| загрузка | скелет: полосы `bgSelected`, `R.sm` | `ui/Skeleton`, если есть в ките; иначе блоки на `C.bgSelected` |
| ошибка | `dangerBg`, рамка `dangerBorder`, `R.lg`; «Повторить» `btn xs sec` | `ui/Notice` danger + `ui/Button variant="secondary"` |

## 4. Этапы и исполнители

| Элемент | Макет | Код | 1280 · 360, px |
|---|---|---|---|
| строка этапа | `<button aria-expanded>`, сетка `16 · 1fr · auto`, зазор 9, `R.md`; наведение `bgSelected`; текущий — `bgCard` | раздел 0 ловушек | **32 · 44** |
| ось | линия 2 `progressTrack` слева в 8 | `C.progressTrack` | 2 |
| точка | 14: пройден `success`; идёт `textHeading` + кольцо 3 `bgSelected`; ждёт `info` + кольцо `infoBg`; стоит `danger` + кольцо `dangerBg`; «по шагам» `bgWhite` + inset 3 `textHeading`; впереди — inset 2 `progressTrack`; не было — inset 1,5 `dashed` | как на «Хронологии» страницы | 14 |
| название | 13 / 600 `textHeading`; впереди и не было — `textMuted`, строка не кнопка | `FS.base` | — |
| состояние справа | 11 `textMuted`, слово 600 (`info` / `dangerText` / `textHeading`), время mono | `FS.xs`, `FONT.mono` | — |
| аватары этапа | 18, до 3, нахлёст 4, кольцо 1,5 `bgWhite`; на 360 скрыты | `PersonaAvatar size={18}` | 18 |
| шеврон | 12 `textMuted`, поворот 90° | `ChevronRight` | 12 |
| список исполнителей | отступ слева 25, линия 1 `divider`, строка мин. 36 (360 — 44) | — | 36 · 44 |
| кнопка-аватар | 32×36 (360 — 40×40), аватар 24, `aria-label` «{имя}. Модель чата: …» | `<button>` по разделу 0 | 36 · 40 |
| чип модели | `navInk`/`onNavInk`, mono 10 / 700, `R.sm`, поля `0 4`, кольцо 1,5 `bgWhite` | как в «Чатах» страницы: `FS.xxs`, `C.navInk`, `C.onNavInk` | 14 |
| подсказка модели | `navInk`/`onNavInk`, 12, `R.md`, макс. 260, поля `7 10`: «Модель чата: Авто → Claude Opus 5.5 (слот «сильная»)» | `ui/Tooltip`; на таче тап открывает, повторный закрывает | — |
| имя | 12 / 600 `textHeading` | `FS.sm` | — |
| метка роли | mono 10 / 700, `bgSelected`/`textSecondary`, `R.sm`, поля `0 5`; на 360 скрыта | `RoleChip` страницы | 15 |
| статус и задача | 11 `textMuted`, многоточие; «ждёт вас» `info` 600, «стоит» `dangerText` 600 | `FS.xs` | — |
| «чата нет» | 11 `textMuted`, не кнопка; на 360 скрыт | — | — |
| пара «чат · задача» | 24, `R.sm`, рамка `borderLight`; на 360 только иконки 40×40, подпись в `aria-label` | `NavTargetButton` | 24 · 40 |

## 5. Модалка мержа, тост

Как на странице (раздел 10 финальных токенов): `ui/ConfirmDialog`, `MODAL_W.form`, заголовок PT Serif 18 / 700
«Смержить «…» в master?», «Отмена» `gh` + «Мерж» `pri`; на 360 — шторка, кнопки пополам по 40. Тост — `navInk` /
`onNavInk`, `R.lg`, десктоп снизу, 360 сверху под шапкой.

## Расхождения

- **Р-1. Пометки `.be` «Б-n» и пунктир `.new`** — обвязка макета, в продукт не идут.
- **Р-2. Цвет тревоги `info`.** Сейчас весь текст плашки `C.info` на `C.infoBg`; в макете, как на странице, текст
  `C.textPrimary`, выделение `C.textHeading` — контраст выше в обеих темах.
- **Р-3. Высота строки этапа 32 на десктопе** — `TB.iconHitDesktop`, как в нынешнем `PipelineFlow`; на 360 — 44
  (как строки шагов страницы), а не 40.
