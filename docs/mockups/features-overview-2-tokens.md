# Обзор фич, волна 2: таблица токенов макета (вариант А)

Привязка: эпик `dd859ee2-42a3-4701-bc9c-443c66230f20`, шаг **М0** (lane front). Таблицей пользуются шаги
**Ф1.2** (секция `FilterBar`, строка снимаемых чипов), **Ф1.3** (плашка А), **Ф2.4** (шапка, тревога, состояния),
**Ф2.5** (конвейер, шаги, чаты, задачи, лента), приёмки **П2** и **П4**.

Источники: макет `docs/mockups/features-overview-2.html` (вариант А), `frontend/src/lib/design.ts`,
`frontend/src/lib/theme.css`, компоненты `frontend/src/components/ui/`.

Как измерено: `getComputedStyle` в Playwright по 60 состояниям макета (светлая и тёмная, 1280 и 360;
список, закрытые, снимаемые чипы, `FilterBar`, страница в шести статусах, меню `⋯`, загрузка, ошибка,
«удалена»). Цвета переведены обратно в имена токенов по переменным макета. Скрипт и сырые числа:
`.cc-attachments/features-overview-2/m0-measure.mjs`, `m0-results.json`, скриншоты `m0-*.png` (папка не в git).

**Как читать.** «Макет» — замер. «Код» — что писать в продукте. Если в столбце «Код» стоит не то, что
в «Макете», это решение, и его причина — в разделе «Расхождения» (ссылка вида Р-n). Литерал без токена
(например `gap: 7`) допустим только там, где он прямо написан в таблице: шкалы `SP` и `FS` его не покрывают,
так уже сделано в плашке волны 1 (`FeatureGroupHeader.tsx`). Сырой hex и `rgba()` в `.tsx` запрещены
(`lint:design` их ловит).

## 0. Обязательные строки: ловушки `<button>`

В волне 1 их ловили дважды. Строки обязательны для **каждой** кнопки шагов Ф1.2–Ф2.6, которая не идёт
через `ui/Button`, `ui/IconButton`, `ui/Chip`, `ui/Toggle` или `ui/PressableArea`.

| Свойство | Код | Почему |
|---|---|---|
| рамка | `border: 'none'`, а если рамка нужна — **только longhand'ами** (`borderStyle`, `borderTopWidth`… `borderLeftColor`) | без сброса UA рисует 2px outset; шорткод плюс `borderLeft: undefined` React обнуляет (M1 волны 1) |
| шрифт | `fontFamily: FONT.sans` (или `font: 'inherit'`) | нативная `<button>` не наследует шрифт, получается системный Arial (M2 волны 1) |
| фон | `background: 'none'` или токен | UA-фон `buttonface` |
| цвет | `color: 'inherit'` или токен | UA-цвет `buttontext` |
| поля | `padding` и `margin` явно | UA-поля 1px 6px |
| выравнивание | `textAlign: 'left'` у кнопки-строки | UA центрирует |
| фокус | `:focus-visible` → `boxShadow: SHADOW.focus`, `outline: 'none'` | как у `IconButton` и `Chip` |
| вложенность | кнопка в кнопке запрещена: у плашки шеврон и тело — **соседние** кнопки внутри `div` | невалидная разметка, клик уходит не туда |

Проверка в П2 и П4: у каждой `button` окна `border-top-style` = `none` (кроме рамочных `gh`-кнопок и чипов) и
`font-family` начинается с `Hanken Grotesk`. В макете замер нашёл **0** нарушений.

## 1. Плашка группы А (Ф1.3)

Каркас: `div` (рамка, фон, кромка тревоги) → две соседние кнопки: шеврон и тело. Высота задана явно
межстрочием строк (Р-1), чтобы итог был ровно **55 px** в продукте, где у `body` нет `line-height` 1.45.

| Элемент | Макет (светлая · 1280) | Код | Размер, px |
|---|---|---|---|
| обёртка `div` | фон `bgCard`, рамка 1 `borderLight`, R 10 | `background: C.bgCard`, рамка longhand 1px `C.borderLight`, `borderRadius: R.lg` | 1280: ширина 320 (панель 340 − поля); 360: 338 |
| обёртка: высота | 54,5 (Р-1) | итог из строк ниже, без `minHeight` у обёртки | **55** на 1280 и на 360 |
| кромка тревоги «ждёт вас», «к мержу» | `inset 3px 0 0 info` | `borderLeftWidth: 3`, `borderLeftColor: C.info` (longhand, как в волне 1; Р-2) | 3 |
| кромка тревоги «стоит» | `inset 3px 0 0 danger` | `borderLeftWidth: 3`, `borderLeftColor: C.danger` | 3 |
| наведение (без тревоги) | рамка `border`, `shadow-card` | `borderColor: C.border`, `boxShadow: SHADOW.card` | — |
| выбрана (открыта её страница) | рамка `accentMuted`, фон `accentLight` | `borderColor: C.accentMuted`, `background: C.accentLight` | — |
| шеврон: кнопка | ширина 26, вся высота, цвет `textMuted`, R `10 0 0 10` | `<button>` по разделу 0; `width: 26` (1280) / `40` (360); `color: C.textMuted`; `borderRadius: \`${R.lg}px 0 0 ${R.lg}px\`` | 26×53 · 40×53 |
| шеврон: наведение | фон `bgSelected`, цвет `textPrimary` | `background: C.bgSelected`, `color: C.textPrimary` | — |
| шеврон: иконка | `ChevronRight`, поворот 90° при раскрытии, 0,15 с | `ChevronRight size={ICON_SIZE.xs}`, `transform: rotate(90deg)`, `transition: transform 0.15s` | 14 |
| шеврон: доступность | `aria-label`, `aria-expanded` | «Свернуть / Развернуть чаты фичи», `aria-expanded`; клик — `stopPropagation`, `onOpen` не зовёт | — |
| тело: кнопка | поля `7 10 7 2`, промежуток строк 3 | `<button>` по разделу 0; `padding: '7px 10px 7px 2px'`; `display: flex`, `flexDirection: column`, `gap: 3` | 1280: 292×53; 360: 296×53 |
| строка 1 | `gap 7`, высота 18,8 | `display: flex`, `alignItems: center`, `gap: 7`, `lineHeight: '19px'` (Р-1) | 19 |
| строка 2 | `gap 7`, высота 16,7 | то же, `lineHeight: '17px'` | 17 |
| глиф состояния | 14×14 | `FeatureGlyph size={14}` (в волне 1 было 13; Р-3) | 14 |
| название | 13 / 700, `textHeading`, многоточие | `fontSize: FS.base`, `fontWeight: 700`, `color: C.textHeading`, `whiteSpace: nowrap`, `textOverflow: ellipsis`, `flex: 1`, `minWidth: 0` | 13 |
| «давность» справа в строке 1 | mono 11, `textMuted`; прячется при наведении | `fontFamily: FONT.mono`, `fontSize: FS.xs`, `color: C.textMuted`; под быстрым действием — `visibility: hidden` на `:hover` | 11 |
| мини-рельса | 7 сегментов 8×5, активный 15×5, `gap 2` | `FeatureRail` волны 1 без изменений | 75×5 |
| стопка аватаров | 16, кольцо 2 `bgCard`, нахлёст −5 | `PersonaAvatar size={16}`, `boxShadow: \`0 0 0 2px ${C.bgCard}\``, `marginLeft: -5` со второго | 16 |
| статус: обычный | 11,5 / 400, `textSecondary` | `fontSize: FS.xs` (Р-4), `fontWeight: 400`, `color: C.textSecondary`, `fontFamily: FONT.sans` | 11 |
| статус: «ждёт вас», «к мержу» | 11,5 / 600, `info` | `FS.xs`, 600, `color: C.info` | 11 |
| статус: «стоит» | 11,5 / 600, `dangerText` | `FS.xs`, 600, `color: C.dangerText` | 11 |
| статус: «доработка» | 11,5 / 600, `warningText` | `FS.xs`, 600, `color: C.warningText` | 11 |
| прогресс `4/6 ↺3` | mono 11, `textMuted` | `FONT.mono`, `FS.xs`, `C.textMuted`, `flexShrink: 0` | 11 |
| быстрое действие | `btn xs gh`, фон `bgWhite`, сверху справа 6/6, видно на `:hover` и `:focus-within` | `ui/Button size="xs" variant="ghost"` с `background: C.bgWhite`; `position: absolute`, `top: 6`, `right: 6`; `opacity` 0 → 1 | 24 в высоту |
| быстрое действие на 360 | скрыто | не рендерить: на 360 действие живёт на странице фичи | — |
| дети: отступ | `margin 2 0 8 14`, `paddingLeft 8`, линия слева 1 `divider` | `margin: '2px 0 8px 14px'`, `paddingLeft: SP.sm`, `borderLeft: \`1px solid ${C.divider}\`` | — |
| промежуток между группами | `margin 4 0 2` | `margin: '4px 0 2px'` | — |

**Состояния плашки.** Обычная · наведение · фокус с клавиатуры (`SHADOW.focus` у шеврона и тела по
отдельности) · тревога «ждёт / к мержу / стоит» · выбрана · закрытая (раздел 2). Загрузки и ошибки у
плашки нет: индекс приходит одним запросом на проект, пока его нет — список не рисуется вовсе.

**Тёмная тема.** Те же токены; замер подтвердил, что все цвета плашки в тёмной теме — токены
(`bgCard`, `borderLight`, `info`, `danger`, `dangerText`, `warningText`, `textMuted`).

## 2. Закрытые фичи и секция «Закрытые» (Ф1.2, Ф1.3)

| Элемент | Макет | Код | Размер, px |
|---|---|---|---|
| заголовок секции «Закрытые · N» | 10 / 700, капс, разрядка 0,06em, `textMuted`; поля `12 6 5` | `fontSize: FS.xxs`, 700, `textTransform: uppercase`, `letterSpacing: '0.06em'`, `color: C.textMuted`, `padding: '12px 6px 5px'` | 1280: высота 33 (мин. 30); 360: **40** |
| число в заголовке | mono 10 | `FONT.mono`, `FS.xxs`, `letterSpacing: 0` | 10 |
| подсказка справа | 11 / 400, без капса | `FS.xs`, 400, `textTransform: none`, `marginLeft: 'auto'` | 11 |
| закрытая плашка: обёртка | фон и рамка прозрачные | `background: 'transparent'`, рамка longhand цвета `'transparent'` | — |
| закрытая плашка: тело | поля `5 10 5 2` | `padding: '5px 10px 5px 2px'` | 1280: 50,5 (как в макете); 360: тело `minHeight: 52` → 54 |
| закрытая: название | 13 / 600, `textSecondary` | `FS.base`, 600, `C.textSecondary` | 13 |
| закрытая: статус «смержена» | 11,5, `successText` | `FS.xs`, `C.successText` | 11 |
| закрытая: статус «без мержа» | 11,5, `textSecondary` | `FS.xs`, `C.textSecondary` | 11 |
| закрытая: наведение | фон `bgCard` | `background: C.bgCard` | — |

## 3. Строка снимаемых чипов под шапкой (Ф1.2)

| Элемент | Макет | Код | Размер, px |
|---|---|---|---|
| строка | `flex-wrap`, `gap 6`, поля `0 12 8 14`, текст 12 `textMuted` («Показаны:») | `display: flex`, `flexWrap: wrap`, `gap: 6`, `padding: '0 12px 8px 14px'`, `fontSize: FS.sm`, `color: C.textMuted` | высота 32 (1280), 40 (360) |
| снимаемый чип | фон `accentLight`, рамка 1 `accentMuted`, текст `accent` 12 / 600, R 999, высота 24 | `ui/Chip` **soft** `selected` с `onRemove` и `removeLabel="Убрать «…»"` (возвратный чип — «Вернуть «…»», `SessionList.tsx:1056-1057`; R-Б1) (Р-5) | ≈ 23 на 1280 |
| снимаемый чип на 360 | высота 32, крестик 32×32 | `ui/Chip soft selected touch` | 34, крестик 32×32 |
| «Сбросить» | ссылка 12 / 600, `accent`, R 6 | `ui/Button size="xs" variant="ghost"` с цветом `C.accent`, без рамки | 1280: 24; **360: 40** (Р-6) |
| бейдж «скрыто N» у воронки | mono 10 / 700, фон `bgSelected`, рамка 1 `border`, `textSecondary`, R 999, 16×16, сдвиг −5/−5 | `FONT.mono`, `FS.xxs`, 700, `C.bgSelected`, `1px solid ${C.border}`, `C.textSecondary`, `R.max`; `top/right: -5` (360: `2`) | 16 |
| воронка-триггер | `IconButton` 28, при фильтре фон `accentMuted`, цвет `accent` | `ui/IconButton size="sm" active` (360: `size="lg"`) | 28 · 40 |

Пустой результат («Под фильтр ничего не попало»): `EmptyState compact inline`, заголовок `FS.base`
`FONT.serif` `C.textHeading`, текст `FS.sm` `C.textSecondary`.

## 4. Секция `FilterBar` «Фичи · что с ними» и тумблер «Показывать закрытые» (Ф1.2)

Секция встаёт в существующий поповер `FilterBar` по образцу `FilterBar.tsx:186–239`; размеры поповера
не меняются (320, `C.bgWhite`, `R.xl`, `SHADOW.dropdown`, поля `SP.md`). На 360 — существующая шторка
фильтров (`R.sheet`, `SHADOW.sheet`).

> **Исторично (R-Б1).** Тумблер «Показывать закрытые» удалён эпиком «Фильтр фич по состоянию»
> (`1d812b59`): отбор идёт по категории состояния (`kind`, ADR-031 §7), макет —
> [feature-state-filter-1.md](feature-state-filter-1.md). Строки про тумблер и подпись «Закрытые» —
> запись старого макета, не контракт продукта.

| Элемент | Макет | Код | Размер, px |
|---|---|---|---|
| подпись секции «Фичи · что с ними» | 11 / 700, капс, разрядка 0,06em, `textMuted`, отступ `10 0 6` | `SectionTitle` из `FilterBar.tsx:83` (`FS.xs`, 700, капс, `0.06em`, `C.textMuted`, `marginBottom: 6`); секция — `sectionStyle` | 11 |
| ряд чипов | `flex-wrap`, `gap 6` | `display: flex`, `flexWrap: wrap`, `gap: SP.xs` — как соседняя секция «Статус» (Р-7) | — |
| чип выключен | рамка 1 `borderLight`, фон прозрачный, `textSecondary` 12 / 600, поля `4 10` | `ui/Chip variant="toggle"` | ≈ 27 |
| чип включён | фон и рамка `accent`, текст `onAccent` | `ui/Chip variant="toggle" selected` | ≈ 27 |
| чип с нулём | `opacity 0.5` | `style={{ opacity: 0.5 }}` на обёртке; кликабелен | — |
| точка тона | 6×6, круг: «ждут вас» и «к мержу» `info`, «стоят» `danger`, «идут» `textHeading`; на включённом — `onAccent` | `width/height: 6`, `borderRadius: R.full`, фон токеном по тону | 6 |
| число в чипе | mono 10 / 400, `opacity 0.8` | `FONT.mono`, `FS.xxs`, `opacity: 0.8`, цвет наследуется | 10 |
| чипы на 360 | поля `7 13`, 14 px, высота 40 | `ui/Chip variant="toggle" large minHeight={40}` (Р-7) | 40 |
| подпись «Закрытые» | как подпись секции | как выше | 11 |
| тумблер «Показывать закрытые фичи · N» | строка 13 `textPrimary`, `gap 10`, мин. 32; дорожка 32×18 `progressTrack` | `ui/Toggle` справа + подпись слева (Р-8); строка `minHeight: 32` | 1280: 49; 360: `minHeight: 44` |
| тумблер: пояснение | 11 / 400, `textMuted` | `FS.xs`, `C.textMuted` | 11 |
| тумблер включён | дорожка `accent` | `ui/Toggle checked` (`C.accent`) | — |
| подвал поповера | рамка сверху 1 `divider`, поля `10 0 0`, текст 12 `textMuted`, `gap 8` | существующий подвал `FilterBar` | — |
| «Готово» (1280) | `btn xs pri` | `ui/Button size="xs" variant="primary"` | 24 |
| «Показать N фич» (360) | `btn md pri` | `ui/Button size="md" variant="primary"` | 42 |
| «Сбросить всё» | ссылка 12 / 600 `accent` | существующая ссылка `FilterBar`; на 360 `minHeight: 40` | 21 · 40 |

## 5. Страница фичи (Ф2.4, Ф2.5)

Страница стоит в центре вместо чата (чат остаётся в DOM, `display: none`). Ширина центра на 1280 при
панели 340 — 826.

### 5.1 Шапка

| Элемент | Макет | Код | Размер, px |
|---|---|---|---|
| шапка | поля `12 16 10`, линия снизу 1 `borderLight` | `padding: '12px 16px 10px'`, `borderBottom: \`1px solid ${C.borderLight}\`` | 1280: высота ≈ 151 |
| верхний ряд | `gap 6`, мин. 28, отступ снизу 6 | `display: flex`, `gap: 6`, `minHeight: 28`, `marginBottom: 6` | 28 · 40 |
| крошка «← К чату «…»» | 12 `textSecondary`, поля `4 8 4 4`, R 8, `gap 5`, макс. ширина 260, многоточие | `<button>` по разделу 0: `FS.sm`, `C.textSecondary`, `padding: '4px 8px 4px 4px'`, `borderRadius: R.md`, `gap: 5`, `maxWidth: 260` | 1280: 25; **360: 40** |
| крошка: наведение | фон `bgSelected`, цвет `textPrimary` | то же токенами | — |
| `⋯` | `IconButton` 28, справа | `ui/IconButton size="sm"` (360: `lg`), `marginLeft: 'auto'` | 28 · 40 |
| строка заголовка | `gap 9` | `display: flex`, `alignItems: center`, `gap: 9` | — |
| глиф | 14 | `FeatureGlyph size={14}` | 14 |
| название `h2` | PT Serif 20 / 700, `textHeading`, межстрочие 1,25; на 360 — 17 | `FONT.serif`, `fontSize: 20` (1280) / `17` (360) (Р-9), 700, `C.textHeading`, `lineHeight: 1.25`, `margin: 0` | 25 · 21 |
| мета-строка | 12 `textSecondary`, `gap 4 12`, `flex-wrap`, отступ сверху 6 | `FS.sm`, `C.textSecondary`, `gap: '4px 12px'`, `flexWrap: wrap`, `marginTop: 6` | 18 (1280), до 40 в две строки (360) |
| ветка | mono 11,5 + иконка ветки | `FONT.mono`, `FS.xs` (Р-4), `display: inline-flex`, `gap: SP.xs` | 11 |
| «идёт 1 дн 6 ч», «M задач» | 12 `textSecondary` | `FS.sm`, `C.textSecondary` | 12 |
| тег статуса | 11 / 600, поля `1 7`, R 6, `gap 4` | `fontSize: FS.xs`, 600, `padding: '1px 7px'`, `borderRadius: R.sm`, `gap: SP.xs` | 18 |
| тег «идёт» | фон `bgSelected`, текст `textHeading` | `C.bgSelected` / `C.textHeading` | — |
| тег «ждёт вас», «к мержу» | фон `infoBg`, текст `info` | фон `C.infoBg`, текст **`C.textPrimary`**, точка-глиф `C.info` (Р-10) | — |
| тег «стоит» | фон `dangerBg`, текст `dangerText`, кольцо 1 `danger` внутрь | `C.dangerBg`, `C.dangerText`, `boxShadow: \`inset 0 0 0 1px ${C.danger}\`` | — |
| тег «доработка» | фон `warningBg`, текст `warningText` | `C.warningBg`, `C.warningText` | — |
| тег «смержена» | фон `successBg`, текст `successText` | `C.successBg`, `C.successText` | — |
| тег «без мержа» | фон `bgSelected`, текст `textSecondary` | `C.bgSelected`, `C.textSecondary` | — |
| ряд действий | `gap 8`, `flex-wrap`, отступ сверху 10 | `gap: SP.sm`, `flexWrap: wrap`, `marginTop: 10` | — |
| главное действие | `btn sm pri` | `ui/Button size="sm" variant="primary"`; на 360 `minHeight: 40` | 33 · 40 |
| второе действие | `btn sm gh` | `ui/Button size="sm" variant="ghost"`; на 360 `minHeight: 40` | 35 · 40 |

### 5.2 Меню `⋯`

| Элемент | Макет | Код | Размер, px |
|---|---|---|---|
| меню | `bgWhite`, рамка 1 `border`, R 12, `shadow-dropdown`, поля 5, мин. ширина 220 | `ui/Menu` (там уже `C.bgWhite`, `R.xl`, `SHADOW.dropdown`) | 220 · на 360 во всю ширину минус 8+8 |
| пункт | 13 `textPrimary`, поля `9 10`, R 8, `gap 8` | пункт `ui/Menu` | 37 · **44** на 360 |
| пункты v1 | «Скопировать ветку», «Открыть эпик в «Задачах»» | только эти два; «Закрыть без мержа» и `msep` — за скобками волны | — |

### 5.3 Блок тревоги и итог

| Элемент | Макет | Код | Размер, px |
|---|---|---|---|
| блок «ждёт вас», «к мержу» | фон `infoBg`, R 10, поля `10 12`, `gap 9`, 12,5 `textPrimary`, отступ снизу 16 | `background: C.infoBg`, `borderRadius: R.lg`, `padding: '10px 12px'`, `gap: 9`, `fontSize: FS.base` (Р-4), `C.textPrimary`, `marginBottom: SP.lg` | 1280: 70; 360: 122 |
| блок «стоит» | фон `dangerBg`, остальное то же | `background: C.dangerBg` | — |
| глиф в блоке | 14, `align-items: flex-start` | `FeatureGlyph size={14}`, `alignItems: 'flex-start'` | 14 |
| выделение в тексте | 700 `textHeading` | `fontWeight: 700`, `C.textHeading` | — |
| кнопка в блоке | `btn xs pri` («Ответить») или `btn xs gh` с фоном `bgWhite` («Открыть его чат»); ряд `gap 6`, отступ сверху 8 | `ui/Button size="xs"` primary / ghost + `background: C.bgWhite`; на 360 `minHeight: 40` | 24 · 40 |
| итог «смержена» | фон `successBg`, R 10, поля `10 12`, `gap 10`, 12,5; выделение 700 `successText` | `C.successBg`, `R.lg`, `padding: '10px 12px'`, `gap: 10`, `FS.base`, выделение `C.successText` | 38 · 56 |
| итог «без мержа» | фон `bgSelected`, выделение `textHeading` | `C.bgSelected`, выделение `C.textHeading` | — |

### 5.4 Тело, блоки, конвейер

| Элемент | Макет | Код | Размер, px |
|---|---|---|---|
| тело страницы | поля `14 16 20`, прокрутка | `padding: '14px 16px 20px'`, `overflow: auto`, `flex: 1`, `minHeight: 0` | — |
| колонки (1280) | сетка `1fr 300`, `gap 18`, `align-items: start` | `gridTemplateColumns: 'minmax(0,1fr) 300px'`, `gap: 18` | 476 + 300 |
| колонки (360) | — | одна колонка, порядок: тревога, конвейер, чаты фичи, шаги, задачи, события (Р-11) | — |
| блок | отступ снизу 18 | `marginBottom: 18` | — |
| подпись блока («Конвейер», «Шаги», «Чаты фичи · N», «Задачи», «События») | 11 / 700, капс, разрядка 0,06em, `textMuted`, отступ снизу 6, `gap 6` | `FS.xs`, 700, `textTransform: uppercase`, `letterSpacing: '0.06em'`, `C.textMuted`, `marginBottom: 6` | 16 |
| правая часть подписи (`4/6 ↺3`, число) | без капса | `marginLeft: 'auto'`, `textTransform: none`, `letterSpacing: 0` | — |
| конвейер | `PipelineFlow` волны 1 с исполнителями | переиспользовать `components/chat/feature/PipelineFlow.tsx`, не копировать | — |
| этап: строка | сетка `18 · 1fr · auto`, `gap 9`, поля `6 0` | если `PipelineFlow` не даёт такой строки — `gridTemplateColumns: '18px minmax(0,1fr) auto'`, `columnGap: 9`, `padding: '6px 0'` | 31 |
| этап: линия | 2 px, пройден `success`, впереди `progressTrack`, пунктир `dashed` | `C.success` / `C.progressTrack` / `2px dashed ${C.dashed}` | 2 |
| этап: название | 13 `textPrimary`; активный 700 `textHeading`; впереди `textMuted` | `FS.base` + те же токены | 13 |
| этап активный: подложка | `bgCard`, R 10, поля `7 8`, поля наружу −8 | `C.bgCard`, `R.lg`, `padding: '7px 8px'`, `margin: '2px -8px'` | 52 |
| этап: справа | 11,5 `textMuted`, `gap 6` | `FS.xs` (Р-4), `C.textMuted`, `gap: 6` | 11 |
| этап: подстрока («Ш3 · Кира · круг 3 · 8 мин») | 11,5 `textSecondary` | `FS.xs`, `C.textSecondary`, `gridColumn: '2 / 4'`, `marginTop: SP.xxs` | 11 |
| этап: стопка исполнителей | 16, кольцо 2 `bgCard` | как в плашке | 16 |

### 5.5 Шаги, чаты фичи, задачи

| Элемент | Макет | Код | Размер, px |
|---|---|---|---|
| строка-кнопка | 12,5, поля `7 8`, поля наружу −8, R 8, `gap 8`, мин. 36 | `<button>` по разделу 0; `FS.sm` (Р-4), `padding: '7px 8px'`, `margin: '0 -8px'`, `width: 'calc(100% + 16px)'`, `borderRadius: R.md`, `gap: SP.sm`, `minHeight: 36` | 36 · **44** на 360 |
| строка: наведение | фон `bgCard` | `C.bgCard` | — |
| строка: текст | `textPrimary`, многоточие; выделение 700 `textHeading` | `C.textPrimary`, `nowrap` + `ellipsis`, `flex: 1`, `minWidth: 0` | — |
| шаг: код «Ш3» | mono 11,5 / 700 `textHeading` | `FONT.mono`, `FS.xs`, 700, `C.textHeading` | 11 |
| строка: справа («круг 3», время) | 11 `textMuted` | `FS.xs`, `C.textMuted`, `flexShrink: 0` | 11 |
| аватар в строке | 18 | `PersonaAvatar size={18}` | 18 |
| ключевой чат: строка | фон `accentLight`, текст 700 `textHeading`; первым в списке | `C.accentLight`; текст 700 `C.textHeading` | — |
| метка «ключевой» | mono 10,5 / 700, фон `accentLight`, текст `accent`, R 6, поля `0 5` | `FONT.mono`, `FS.xxs` (Р-4), 700, `C.accentLight`, `C.accent`, `R.sm`, `padding: '0 5px'` | 15 |
| метка роли («Ш1 · фронт») | mono 10,5 / 400, фон `bgSelected`, `textSecondary` | `FONT.mono`, `FS.xxs`, `C.bgSelected`, `C.textSecondary`, `R.sm`, `padding: '0 5px'` | 15 |
| архивный чат | (в макете нет) | текст `C.textMuted`, метка «в архиве» как метка роли | — |
| «шагов нет» | заголовок PT Serif 14 / 700, текст 12,5 `textSecondary`, поля `14 10 6` | `EmptyState compact inline`: заголовок `FONT.serif` `FS.md` 700 `C.textHeading`; текст `FS.sm` (Р-4) `C.textSecondary` | 93 |
| счётчики задач | теги «готово / в работе / …», ряд `gap 6`, отступ снизу 6 | теги раздела 5.1, `gap: 6`, `marginBottom: 6` | 18 |

### 5.6 Лента событий

| Элемент | Макет | Код | Размер, px |
|---|---|---|---|
| событие | сетка `44 · 16 · 1fr`, `gap 8`, поля `5 0`, 12 `textSecondary` | `gridTemplateColumns: '44px 16px minmax(0,1fr)'`, `columnGap: SP.sm`, `padding: '5px 0'`, `FS.sm`, `C.textSecondary` | 27 |
| время | mono 10,5 `textMuted`, отступ сверху 1 | `FONT.mono`, `FS.xxs` (Р-4), `C.textMuted`, `paddingTop: 1` | 10 |
| время: не сегодня | ДВЕ строки в колонке 44: дата `dd.mm` сверху, время `ЧЧ:ММ` снизу (N2: «dd.mm ЧЧ:ММ» одной строкой в 44 px не влезает — минуты уходили под аватар); сегодня — только `ЧЧ:ММ` | `eventTimeParts`: `display: flex`, `flexDirection: column`, `alignItems: flex-start`; сетка `44 · 16 · 1fr` не меняется | 10 |
| аватар | 16 | `PersonaAvatar size={16}` | 16 |
| кто | 600 `textHeading` | `fontWeight: 600`, `C.textHeading` | — |
| ссылка на чат события | (в макете нет) | только при `sessionId`; `C.accent`, без подчёркивания, наведение `C.accentLight`; без `sessionId` — обычный текст | — |
| «Показать все» | (в макете нет) | `ui/Button size="xs" variant="ghost"`; на 360 `minHeight: 40` | 24 · 40 |
| «Событий пока нет» | `empty sm`, 12,5 `textSecondary` | `FS.sm`, `C.textSecondary`, `padding: '14px 10px 6px'` | — |

### 5.7 Состояния страницы

| Состояние | Макет | Код | Размер, px |
|---|---|---|---|
| загрузка: строки | 6 полос 14 px разной ширины, `bgSelected`, R 6, `gap 10`, пульс 1,4 с | `height: 14`, `background: C.bgSelected`, `borderRadius: R.sm`, `gap: 10`, пульс `opacity` 1 → 0,35; при `prefers-reduced-motion` без анимации | 14 |
| загрузка: блок | 120, отступ сверху 6 | `height: 120`, `marginTop: 6` | 120 |
| загрузка: шапка | — | в шапке вместо названия — полоса 20×50%; крошка уже рабочая | — |
| ошибка | фон `dangerBg`, R 12, поля 12, `gap 10`, 12,5; выделение 700 `dangerText`; кнопка `btn xs gh` фон `bgWhite` | `ui/Notice tone="danger"` с `title="Не удалось загрузить фичу."` и действием «Повторить» `ui/Button size="xs" variant="ghost"` (Р-12) | ≈ 74 · 108 |
| «Фичу удалили» (404) | `empty`: круг 44 `bgSelected` с иконкой `textMuted`; заголовок PT Serif 16 / 700; текст 12,5 `textSecondary` макс. 300; кнопка `btn sm gh` | `EmptyState compact` (иконка в круге 44, `iconRadius={R.full}`), заголовок `titleSize={FS.lg}`, текст `FS.sm`; действие `ui/Button size="sm" variant="ghost"`, на 360 `minHeight: 40` | 220 |
| подсистема выключена (503) | (в макете нет) | `ui/Notice tone="info" title="Подсистема задач выключена"`, текст «Страница фичи недоступна, чаты работают как обычно»; без кнопки повтора | ≈ 60 |

## 6. Ширина 360 (сводка)

| Что | Код на 360 |
|---|---|
| шеврон плашки | ширина 40, высота плашки 55 |
| быстрое действие плашки | не рендерится |
| заголовок секции «Закрытые» | `minHeight: 40` |
| снимаемые чипы | `ui/Chip soft selected touch` (34, крестик 32) |
| «Сбросить» в строке чипов | `minHeight: 40` |
| фильтр | шторка фильтров `FilterBar`; чипы `large minHeight={40}`; тумблер — строка `minHeight: 44` |
| страница: шапка | крошка «← К чату «…»» или «← К списку», `minHeight: 40`; `⋯` — `IconButton lg`; `h2` 17 |
| страница: тело | одна колонка, поля `14 16 20` (Р-11) |
| кнопки страницы | `minHeight: 40` у всех `ui/Button` |
| строки шагов, чатов, задач | `minHeight: 44` |
| пункты меню `⋯` | `minHeight: 44` |

## Расхождения и решения

- **Р-1. Плашка 54,5 вместо 55.** Макет считает высоту из межстрочия `body` 1,45 (18,8 + 3 + 16,7 + 14 + 2 = 54,5).
  В продукте у `body` межстрочия нет, высота уехала бы иначе. Решение: межстрочие строк задаём явно,
  `19px` и `17px`: 7 + 19 + 3 + 17 + 7 + 1 + 1 = **55**. Приёмка П2 проверяет 55 ± 0,5.
- **Р-2. Кромка тревоги.** В макете это `box-shadow: inset 3px 0 0`, в волне 1 — `borderLeftWidth: 3` longhand'ами.
  Оставляем способ волны 1: он уже проверен (M1), и на наведении `SHADOW.card` не спорит с кромкой.
  Сдвиг содержимого на 2 px высоту не меняет.
- **Р-3. Глиф 14 вместо 13.** В волне 1 стоял 13, в макете волны 2 — 14 и в плашке, и в шапке страницы.
  Берём 14: в двухстрочной плашке он выравнивается по строке 19 px.
- **Р-4. Размеры шрифта вне шкалы `FS`.** 12,5 → `FS.base` (блоки тревоги, итог) или `FS.sm` (строки шагов, чатов,
  пустые состояния); 11,5 → `FS.xs` (статус плашки, ветка, подписи конвейера); 10,5 → `FS.xxs` (метки «ключевой»
  и роли, время в ленте); 9,5 и 9 у аватаров — внутренний размер `PersonaAvatar`. Правило то же, что в волне 1
  (паспорт `1922d20`).
- **Р-5. Снимаемый чип.** В макете он нарисован отдельно (12 / 600, высота 24). В продукте это уже есть:
  `ui/Chip` soft `selected` с `onRemove` даёт те же `accentLight` / `accentMuted` / `accent`, но шрифт `FS.xs`
  и высота около 23. Берём `ui/Chip`, свой чип не заводим. Известное: `accent` на `accentLight` в светлой теме —
  контраст 2,67 : 1; это стиль `ui/Chip` во всём продукте, в этой волне не трогаем.
- **Р-6. «Сбросить» на 360 — цель 21 px.** У `.lnk` в строке чипов на 360 нет `min-height`: замер дал 21,4.
  Ставим `minHeight: 40`.
- **Р-7. Чипы фильтра на 360.** Макет — 14 px, `ui/Chip large` — `FS.base` (13). Берём компонент как есть.
  Скругление `ui/Chip toggle` — `R.pill` (9), в макете 999, и шаг ряда в секции «Статус» — `SP.xs` (4), в макете 6:
  так выглядят чипы `FilterBar` «Чатов» сегодня, единообразие с соседней секцией важнее.
- **Р-8. Тумблер.** Макет рисует 32×18 на `progressTrack`. В продукте `ui/Toggle` — 42×25 на `C.track`
  (в тёмной теме `track` `#453F37`, а `progressTrack` `#5C544A`). Берём `ui/Toggle`.
- **Р-9. `h2` 20 и 17 вне шкалы.** Ближайшие `FS.xl` 18 и `FS.h2` 22 меняют ритм шапки. Литералы 20 и 17 — так же,
  как заголовки панелей и мобильной шапки в продукте (`mhd .ttl` 17). Новый токен не заводим ради одного места.
- **Р-10. Тег «ждёт вас» — контраст.** `info` на `infoBg` в светлой теме — 3,89 : 1, ниже 4,5 для 11 px.
  Текст тега — `C.textPrimary`, тон держит фон `C.infoBg` и глиф `C.info` (то же правило уже в `ui/Notice`).
  Статус плашки (`info` на `bgCard`, 4,27 : 1, 11 / 600) остаётся как в волне 1.
- **Р-11. 360 в макете нарисован веткой варианта В.** Для варианта А на 360 `page()` уходит в ветку панели:
  сворачиваемые секции и крестик «Закрыть» вместо крошки. Записка говорит иначе: «на 360 это отдельный экран
  со стрелкой назад». Решение: экран с крошкой (`minHeight: 40`) и одной колонкой в порядке из раздела 5.4,
  секции не сворачиваются.
- **Р-12. Ошибка — `ui/Notice`.** Макет рисует свою плашку (`R` 12, поля 12). `ui/Notice tone="danger"` даёт
  `R.md`, поля `SP.sm SP.md`, рамку `dangerBorder` и `FS.sm`. Берём компонент: свой блок ошибки — дефект по конвенции.
- **Р-13. Сырой hex.** Вне блока токенов макета сырого цвета нет: все замеренные цвета в обеих темах — токены
  `C.*`, тени — `SHADOW.card`, `SHADOW.dropdown`, `SHADOW.focus`, `SHADOW.thumb`, `SHADOW.sheet`. Новых
  токенов в `theme.css` не нужно. Значения `--shadow-island` и `--shadow-sheet` в блоке макета устарели
  относительно `theme.css` — продукт берёт `SHADOW.*`. `--shadow-drawer` в `theme.css` нет, им пользуется
  только вариант Б, к волне не относится.
- **Р-14. Мелочь макета вне волны.** Кнопка «Вернуть в работу» (класс `btn sec`) ловит стиль заголовка
  секции `.sec` (капс, разрядка). Кнопка за скобками волны, в продукт не идёт.
