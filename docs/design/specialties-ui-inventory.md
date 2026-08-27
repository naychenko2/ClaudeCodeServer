# Инвентарь токенов и контролов для нового раздела «Специальности»

> **Для кого.** Это рабочий документ волны 1 — реестр того, чем разрешено
> рисовать новый экран «Специальности» в разделе «Персоны». Подаётся Майе
> (UI/UX-дизайнеру) перед её кликабельным макетом, чтобы макет сразу
> использовал существующие токены и компоненты, а не изобретал новые.
>
> **Что здесь.** Поимённый список токенов (`frontend/src/lib/design.ts`) и
> компонентов (`frontend/src/components/ui/`, `frontend/src/features/personas/`),
> которыми собираются карточка специальности, аватар, список персон и
> переключатель режима; снимок того, как сейчас устроены экраны «Персоны»
> визуально, и список того, чего в дизайн-системе **нет** — с указанием,
> чем заменить, не вводя новых компонентов.
>
> **Чего здесь НЕТ.** Никакой оценки качества, никаких предложений по
> визуальному стилю. Это только инвентарь; решения принимаются следующими
> волнами.

---

## 1. Источники правды

| Что | Где | Что в нём живёт |
|---|---|---|
| Токены | `frontend/src/lib/design.ts` | Цвета, шрифты, FS, R, SP, SHADOW, TB, ISLAND, Z, MODAL_W, CONTENT_MAX_W, CHAT_MAX_W, CHAT_GUTTER_L, FIELD, GROUP_COLORS |
| Конкретные hex-значения цветов | `frontend/src/lib/theme.css` | `:root[data-theme="light"]` и `:root[data-theme="dark"]` — тёмная тема усиливает тени и подкручивает фон |
| UI-кит | `frontend/src/components/ui/` | Контролы, панели, модалки, списки, тулбар |
| Доменные компоненты «Персон» | `frontend/src/features/personas/` | `PersonaAvatar`, `PersonaList`, `PersonaToolbar`, `PersonaPreview`, `PersonasHub`, `PersonasSpecialties`, `PersonasPage` |
| Адаптив | `frontend/src/lib/breakpoints.ts` | `useIsMobile`, `useWindowWidth`, `MOBILE_MAX`, `TABLET_MAX` |
| Эталон UI-кита (визуальный) | dev-only `#/ui-kit` → `UiKitPage.tsx` | Все компоненты в живую на одной странице |

Тёмная/светлая темы переключаются рантаймом через CSS-переменные — инлайн-стили через `var(--c-*)` работают в обеих темах. Хардкод hex в `.tsx` — дефект (см. `docs/design/guidelines.md`).

---

## 2. Токены из `design.ts`

### 2.1 Семейства шрифтов — `FONT`

```ts
FONT = {
  sans:  "'Hanken Grotesk', -apple-system, ...",
  serif: "'PT Serif', Georgia, serif",
  mono:  "'JetBrains Mono', 'Courier New', monospace"
}
```

**Где какой:**
- `FONT.serif` — заголовки страниц (`h1`, приглашения), вес 500–700.
- `FONT.sans` — обычные тексты интерфейса, подписи, элементы списков, тулбар.
- `FONT.mono` — идентификаторы, числовые чипы («S: opus», «1 из 5»), код, бейдж охвата.

### 2.2 Размерная шкала — `FS`

```
xs 11   sm 12   base 13   md 14   lg 16
xl 18   h2 22   h1 28    display 34
```

Рекомендуемое соответствие (для нового раздела):
- Заголовок раздела «Специальности» — `FONT.serif` + `FS.h1` (28), вес 500, как у хаба персон.
- Заголовок секции внутри карточки — `FONT.serif` + `FS.xl` (18), вес 700.
- Заголовок карточки специальности (имя роли) — `FONT.sans` + `FS.base` (13), вес 700.
- Подпись / пояснение — `FONT.sans` + `FS.sm` (12).
- Микро-метки («Админ», «Global», «N из M») — `FONT.sans` / `FONT.mono` + `FS.xs` (11).
- Длинные описания — `FONT.sans` + `FS.md` (14), `lineHeight: 1.5`.

### 2.3 Палитра цветов — `C`

Содержимое — ссылки на CSS-переменные; конкретные значения в `theme.css`.

| Группа | Токены | Назначение в будущем экране |
|---|---|---|
| Фоны | `bgMain`, `bgPanel`, `bgCard`, `bgWhite`, `bgSelected`, `bgInset`, `bgInsetSoft` | Подложка холста, фон островов, фон полей, акцентный фон выбранной строки, утопленный фон шапки острова |
| Текст | `textHeading`, `textPrimary`, `textSecondary`, `textMuted`, `onAccent`, `onDark` | Иерархия текстов; `onAccent` — на залитых кнопках; `onDark` — инициалы поверх фото |
| Accent | `accent`, `accentLight`, `accentMuted`, `accentSoft` | Основная залитая кнопка (`accent`), ховер строк сайдбара (`accentLight`), выбранная строка (`accentMuted`), loading accent-кнопки (`accentSoft`) |
| Границы | `border`, `borderLight`, `divider`, `dashed`, `track`, `smoke` | Бордеры карточек, пунктир «Новая специальность», дорожка toggle, мягкая дымка |
| Overlay | `overlay`, `glass`, `glassStrong`, `msgBg` | Подложка модалок; стекло для прозрачных панелей |
| Статусы | `success{,Bg,Text}`, `warning{,Bg,Text}`, `danger{,Bg,Text,Border}`, `info{,Bg}` | Плашка «Сохранить удалось» / «Не настроено ни одной модели» и пр. |
| Plan | `plan`, `planLight`, `planText`, `planBorder` | Если специальность рисуется в плановом режиме — те же чипы |
| Nav (хаб-навигатор) | `navInk`, `onNavInk` | Тёмная плашка активного раздела хаба — тут вряд ли понадобится |
| Mode «План» | то же `plan*` | Тот же чип в обоих местах |
| Output | `termBg`, `termText`, `termError`, `outputBg`, `outputBorder` | Подсветка сниппетов в карточке специальности |

### 2.4 Радиусы — `R`

```
sm 6   md 8   lg 10   xl 12   xxl 14
pill 9   modal 20   sheet 22   max 999   full '50%'
```

**Типовые назначения:**
- Карточка специальности — `R.xl` (12). Совпадает с `cards.tsx` (`shellStyle` уже использует это).
- Остров-обёртка всего раздела — `R.xxl` (14) или `R.modal` (20), если крупная.
- Поле ввода, сегменты, кнопки форм — `R.xl` (12) и `R.xxl` (14).
- Pill-чип («Админ», «Глобальный», «По умолчанию») — `R.max` (полное скругление).
- Кружок аватара — `R.full` (50%).

### 2.5 Отступы — `SP`

```
xxs 2   xs 4   sm 8   md 12   lg 16
xl 24   xxl 32   xxxl 48
```

Базовая 4px-сетка. Использовать её, а не литералы.

### 2.6 Тени — `SHADOW`

- `SHADOW.card` — карточки в основной массе.
- `SHADOW.island` — большие острова (как `PersonasHub`-карточка «Активность»).
- `SHADOW.lift` — приподнятые элементы сверху.
- `SHADOW.dropdown` — выпадающие меню.
- `SHADOW.modal` — модальное окно.
- `SHADOW.focus` — focus-ring на полях.
- `SHADOW.fab` — плавающая круглая кнопка.
- `SHADOW.thumb` — бегунок toggle/pill.
- `SHADOW.press` — отклик на нажатие.
- `SHADOW.alert` — янтарный ореол «нужен ответ».

### 2.7 Слои — `Z`

`dropdown: 50`, `overlay: 900`, `modal: 1000`, `inspector: 1100`. У Popup/Menu свой локальный счётчик, но стартовый — из этой шкалы.

### 2.8 Модальные ширины — `MODAL_W`

```ts
MODAL_W = { confirm: 380, form: 440, wide: 720 }
```

Текущая обёртка `PersonasSpecialties` использует `MODAL_W.wide` для белой карточки со списком специальностей — это рабочая ширина. Если новый экран шире (плитки персон + сетка), ориентир `CONTENT_MAX_W = 1180`, и тогда нужен не «остров с фоном C.bgWhite внутри модалки», а **страница на холсте** через `PageCanvas` + `IslandScaffold`.

### 2.9 Ширины центрированного контента

```
CHAT_MAX_W = 950        // лента чтения — НЕ для нас
HOME_MAX_W = 1028       // дашборд главной
CONTENT_MAX_W = 1180    // сетка раздела — ДЛЯ НАС
CHAT_GUTTER_L = 16      // боковые поля ленты чтения
SPLASH_W = 448          // заставка
```

Раздел «Специальности» принадлежит «сетке раздела». Видимая ширина
контента — `CONTENT_MAX_W`, боковые отступы — на родительском
скролл-контейнере, чтобы у `box-sizing: border-box` не съедало видимую
часть (см. комментарий в `design.ts`).

### 2.10 Анимация раскрытия панелей — `PANEL_ANIM`

`PANEL_ANIM = '0.15s ease-out'`. Деликатная — для компенсации перекоса зон
(`useCenterOffset`). Использовать как есть, не плодить новых значений.

### 2.11 Базовый стиль текстового поля — `FIELD`

```ts
FIELD = {
  background:   C.bgWhite,
  border:       `1px solid ${C.border}`,
  borderRadius: R.xl,
  color:        C.textHeading,
  fontSize:     14,
  borderFocus:  C.accent
}
```

Это уже инкапсулировано в `TextField`/`TextArea`/`IconField`. Прямо тут
обращаться только если делаешь кастомный контрол поверх существующих.

### 2.12 Тулбар — `TB`

`heightDesktop: 52`, `heightMobile: 56`, `padX: 16`, `padXTablet: 10`, `padXMobile: 14`, `gap: 8`,
`bg: C.bgMain`, `iconHitDesktop: 32`, `iconHitMobile: 40`,
`iconColor: C.textMuted`, `iconColorHover: C.textPrimary`, `iconHoverBg: C.bgSelected`,
`iconRadius: R.md`, `pillTrack: C.bgSelected`, `pillRadius: R.pill`,
`pillThumbBg: C.bgWhite`, `pillThumbShadow: SHADOW.thumb`.

Пригодится, если в новом разделе будет свой тулбар (например, фильтры).

### 2.13 Панель-остров — `ISLAND`

```ts
ISLAND = {
  canvas: C.bgMain, bg: C.bgPanel, gap: 8, centerGap: 12,
  pad: 16, radius: R.xxl, border: C.borderLight,
  shadow: SHADOW.island, headerH: 40, headerBg: C.bgInset,
  ink: 'var(--canvas-ink)', glow: 'var(--canvas-glow)',
  patternAlpha: 'var(--canvas-alpha)', patternSize: 'var(--canvas-tile)',
  projectInkAlpha: 'var(--canvas-project-alpha)'
}
```

Если новый экран строится как остров внутри холста — опираться на эти
значения, а не на собственные литералы.

### 2.14 Палитра цветов групп — `GROUP_COLORS`

```ts
GROUP_COLORS = ['#3E7CA6', '#8E4A82', '#3F7A4F', '#C2693B', '#B4452F', '#4B6BB0', '#7A7250']
```

7 hex-значений для цветовых групп. В «Специальностях» пока не
задействована — но если появится визуальная категоризация специальностей
(«инженерные», «аналитические» и т.п.), это готовый источник. Аналогичный
паттерн — `AGENT_COLORS` в `components/AgentSelector.tsx` (не из ui-кита,
но это инвентарь цветов персон).

---

## 3. Компоненты из `components/ui/`

Ниже — публичный API каждого компонента, который может понадобиться новому
разделу. Экспорты — из `components/ui/index.ts`.

### 3.1 Контролы форм

#### `<Button>` (`Button.tsx`)

```ts
interface ButtonProps {
  variant?: 'primary' | 'secondary' | 'ghost' | 'ghostAccent' | 'ghostFilled' | 'danger' | 'dashed';
  size?: 'xs' | 'sm' | 'md' | 'lg';
  fullWidth?: boolean;
  loading?: boolean;
  disabled?: boolean;
  glow?: boolean;            // свечение под primary (логин)
  pill?: boolean;            // полное скругление
  leftIcon?: ReactNode;
  onClick?: (e: MouseEvent) => void;
  type?: 'button' | 'submit';
  title?: string;
  style?: CSSProperties;
  children: ReactNode;
}
```

- `variant="dashed"` — пунктирная залитая цветом `accent` кнопка («Новая
  специальность» в сайдбаре, как у `PersonaList`).
- `variant="primary"` + `pill` — залитая orange круглая (FAB `PersonaEditFab`).
- `variant="ghost"` — нейтральная обводка. Подходит для второстепенных
  действий в карточке специальности.

#### `<Field>`, `<FieldLabel>`, `<TextField>`, `<TextArea>`, `<IconField>` (`Field.tsx`)

```ts
// Базовая обёртка «лейбл + контрол + подсказка»
<Field label?: ReactNode hint?: ReactNode>{children}</Field>
<FieldLabel>{children}</FieldLabel>

// Однострочное поле ввода с focus-ring
<TextField
  value: string
  onChange: (v: string) => void
  placeholder?: string
  type?: string
  mono?: boolean
  autoFocus?: boolean
  disabled?: boolean
  letterSpacing?: string
  onEnter?: () => void
  onFocus?: () => void
  onBlur?: () => void
  onEscape?: () => void
  title?: string
  style?: CSSProperties
/>

// Многострочное поле с авто-ростом
<TextArea
  value: string
  onChange: (v: string) => void
  placeholder?: string
  autoGrow?: boolean
  minHeight?: number  // дефолт 80
  maxHeight?: number
  disabled?: boolean
  autoFocus?: boolean
  onKeyDown?: (e: KeyboardEvent<HTMLTextAreaElement>) => void
  style?: CSSProperties
/>

// Поле с иконкой-префиксом (поиск)
<IconField
  icon?: ReactNode
  value: string
  onChange: (v: string) => void
  placeholder?: string
  type?: string
  mono?: boolean
  disabled?: boolean
  letterSpacing?: string
  height?: number         // дефолт 50
  radius?: number         // дефолт R.xxl
  fontSize?: number       // дефолт 15
  style?: CSSProperties
  autoFocus?: boolean
  onEnter?: () => void
  inputRef?: Ref<HTMLInputElement>
/>
```

Поле внутри карточки специальности (например, описание роли) — это
`<Field label="Описание"><TextArea value={...} onChange={...} autoGrow
minHeight={80} maxHeight={200} /></Field>`. Стиль полей — из токена
`FIELD` (см. §2.11), никаких своих отступов/радиусов.

#### `<Toggle>` (`Toggle.tsx`)

```ts
interface ToggleProps {
  checked: boolean
  onChange: (v: boolean) => void
  disabled?: boolean
  width?: number          // дефолт 42
  height?: number         // дефолт 25
  focusable?: boolean     // Tab + стрелки
  onEnter?: () => void
  ariaLabel?: string
}
```

Тумблер on/off. Уже используется в хабе персон («Полная активность» и
т.п.). Для «Специальностей» это переключатель «назначено ли мне/всем».

#### `<PillSwitch<T extends string>>` (`PillSwitch.tsx`)

```ts
interface PillSwitchProps<T extends string> {
  value: T
  options: { value: T; label: string; icon?: ReactNode; title?: string }[]
  onChange: (v: T) => void
  fill?: boolean
  isMobile?: boolean
  draggable?: boolean
  persistKey?: string       // клавиша памяти позиции — для анимации между инстансами
  compact?: boolean
  autoCompact?: boolean     // сжимает до иконок при переполнении
  iconsOnly?: boolean
  variant?: 'default' | 'hub'  // 'hub' = тёмная «чернильная» плашка
  renderOption?: (opt: { value: T; label: string; icon?: ReactNode }, active: boolean) => ReactNode | null
}
```

Это **главный кандидат** на переключатель «Персоны | Специальности» —
он уже там живёт (`PersonasPage` использует его с `persistKey="cc_personas_mode"`).
Скользящая пилюля, опциональный drag пальцем, поддерживает произвольное
число сегментов.

#### `<InlineSegmented<T extends string>>` (`InlineSegmented.tsx`)

```ts
interface InlineSegmentedProps<T extends string> {
  value: T | null
  options: { value: T; label: string; tone?: { bg: string; fg: string } }[]
  onChange: (v: T) => void
  disabled?: boolean
  isMobile?: boolean
}
```

Компактный сегмент в строках списков (без скользящей пилюли и drag). Тон
активного сегмента можно задать свой (`{bg, fg}`). Подходит, если в
карточке специальности нужен узкий переключатель уровня («Сильная /
Средняя / Слабая»).

#### `<SegmentedControl>` (`Segmented.tsx`)

Один сегментированный контрол из трёх (или больше) вариантов. Базовый,
без богатого API. Если нужно больше двух сегментов — брать
`<PillSwitch>` или `<InlineSegmented>`.

#### `<Badge>` (`Badge.tsx`)

```ts
type BadgeTone = 'neutral' | 'accent' | 'success' | 'warning' | 'danger' | 'info' | 'plan'
type BadgeSize = 'xs' | 'sm'

interface BadgeProps {
  tone?: BadgeTone        // дефолт 'neutral'
  size?: BadgeSize        // дефолт 'sm'
  icon?: ReactNode        // иконка 11px слева
  dot?: boolean           // кружок в цвете тона
  children: ReactNode
  title?: string
  onClick?: (e: MouseEvent<HTMLElement>) => void
  active?: boolean
  disabled?: boolean
  style?: CSSProperties
}

const TONE_DOT: Record<BadgeTone, string>  // экспортируется
```

Для меток статуса и категорий в карточке специальности:
«Глобальная», «Админ», «Готово», «Не настроено». Если нужен **чип с
выпадающим меню** (выбор значения), `onClick` + `aria-haspopup="menu"`
— уже встроено.

#### `<Dot>` (`Dot.tsx`)

```ts
<Dot color: string size?: number>  // SVG-круг, дефолт 8
```

Цветная точка-индикатор. Использовать, когда у Badge нет лейбла, только
цвет (например, статус роли без подписи).

#### `<IntroDot>` (`IntroDot.tsx`)

```ts
<IntroDot size?: number style?: CSSProperties>  // дефолт 8
```

Точка-приглашение на аватаре. Для специальностей не нужна (этот
паттерн — про «знакомство не пройдено»).

#### `<BackButton>` (`BackButton.tsx`)

```ts
interface BackButtonProps {
  onClick: (e: MouseEvent) => void
  title?: string
  children?: ReactNode
  iconColor?: string         // дефолт C.textSecondary
  iconSize?: number          // дефолт 16
  style?: CSSProperties
}
```

Шеврон-влево + кликабельный текст. Уже используется в мобильной шапке
режима «Специальности» в `PersonasPage`. Если делаем отдельный внутренний
переход (например, из карточки специальности в детальный вид), он
пригодится в тулбаре.

#### `<EmptyState>` (`EmptyState.tsx`)

```ts
interface EmptyStateProps {
  icon: ReactNode         // SVG / emoji
  title: string
  subtitle?: ReactNode
  action?: ReactNode      // кнопка или ряд кнопок
  compact?: boolean       // узкие сайдбары
  inline?: boolean        // не тянуть на height:100%
}
```

Уже применён в `PersonasHub`. Штатно использовать его для пустого среза
«персон, работающих по этой роли» в карточке специальности.

### 3.2 Контейнеры

#### `<Modal>` (`Modal.tsx`)

```ts
interface ModalProps {
  width?: number                    // дефолт 440
  title?: ReactNode
  subtitle?: ReactNode
  footer?: ReactNode
  onClose: () => void
  closeOnBackdrop?: boolean
  hideCloseButton?: boolean
  children?: ReactNode
  cardStyle?: CSSProperties
}
```

Модалка: десктоп — центрированная карточка, мобил — bottom-sheet. Escape
+ клик по оверлею + крестик в углу. Уже все диалоги «Персон» через
`<ConfirmDialog>` или прямо через `<Modal>`.

#### `<ConfirmDialog>` (`ConfirmDialog.tsx`)

```ts
interface ConfirmDialogProps {
  title: string
  subtitle?: ReactNode
  confirmLabel?: string           // дефолт 'Подтвердить'
  confirmVariant?: 'primary' | 'danger'
  cancelLabel?: string
  onConfirm: () => void | Promise<void>
  onCancel: () => void
}
```

Если «удалить специальность» / «сбросить к наследованию» — это он.

#### `<ModalActions>` (`ModalActions.tsx`)

Низкоуровневая пара «Отмена + Подтвердить» для прямого использования
внутри `<Modal>`. Применять, если подтверждение — нестандартное (третья
кнопка).

#### `<Menu>`, `<MenuItem>`, `<MenuSep>` (`Menu.tsx`)

Выпадающее меню. В этой задаче не критично — но если в карточке
специальности потребуется контекстное меню («Дублировать», «Сбросить»,
«Открыть в полном виде») — это оно. См. `Menu.tsx` отдельно, тут не
раскрываю сигнатуру.

#### `<Island>` + `<IslandHeader>` (`Island.tsx`)

```ts
<Island
  bg?: string                 // дефолт ISLAND.bg
  borderColor?: string        // дефолт ISLAND.border
  shadow?: string             // дефолт ISLAND.shadow
  style?: CSSProperties
  rootProps?: HTMLAttributes<HTMLDivElement>
  rootRef?: (el: HTMLDivElement | null) => void
  children: ReactNode
/>

<IslandHeader
  icon?: ReactNode
  title: string
  badge?: string | null
  leading?: ReactNode
  actions?: ReactNode
  headerProps?: HTMLAttributes<HTMLDivElement> & { draggable?: boolean; ref?: Ref<HTMLDivElement> }
  children?: ReactNode
/>
```

Стандартный контейнер любой панели в духе Rider Islands. Скруглённая
карточка на общем фоне-холсте с тенью. Готовый шаблон того, как должна
выглядеть обёртка каждой «большой» секции нового раздела (например,
остров с подписями специальностей + сеткой плиток).

#### `<IslandScaffold>` (`IslandScaffold.tsx`)

Готовая раскладка: левый сайдбар + центр + правый сайдбар на холсте с
боковыми полями по `ISLAND.pad`. Уже используется в `PersonasPage`.

#### `<PageCanvas>` (`PageCanvas.tsx`)

Обёртка для всей страницы — рисует фоновый дудл-холст (`CanvasBackdrop`).
Любой новый экран в стиле раздела (полностраничный, не модалка)
оборачивается именно в `<PageCanvas>`.

#### `<CanvasBackdrop>` (`CanvasBackdrop.tsx`)

Дудл-фоновая подложка. Не используется напрямую — она часть `<PageCanvas>`.

#### `<PanelShell>`, `<PanelRail>`, `<RailHat>`, `<RailCapsule>`, `<RailFlyout>`, `<RailIconButton>`, `<RailSep>` (`PanelRail.tsx` и пр.)

Тяжёлая система: левый реестр панелей с DnD, flyout-меню, многосекционный
слой. Если новый экран «Специальности» живёт на отдельной странице, сайдбар
— это `<PersonaList>`, а тяжёлая панельная система не нужна.

#### `<SidebarSection>` (`SidebarSection.tsx`)

Секция в сайдбаре с заголовком и опциональным раскрытием. Если в
`<PersonaList>` появятся вложенные группы по типу специальности — это
готовая обёртка с правильным стилем.

### 3.3 Системные

#### `<LoadingScreen>`, `<LoadingOverlay>` (`LoadingScreen.tsx`)

Экраны загрузки. На будущий раздел «Специальности» — нужны только если
загрузка/сохранение слоя занимает заметное время.

#### `<FileTypeTile>` (`FileTypeTile.tsx`)

Файл-плитка с иконкой по расширению. Может пригодиться, если в карточке
специальности нужны превью связанных файлов (инструкции в знаниях),
но это не центральный кейс.

#### `<FileStatusBadge>` (`FileStatusBadge.tsx`)

Статусный бейдж файла. Не нужен для специальностей.

#### `<ChatTopicIcon>`, `<ChatTopicBackdrop>` (`ChatTopicIcon.tsx`)

Иконка/обложка чата. Не нужны.

#### `<SidebarSplitter>`, `<Splitter>`, `<IslandSidebarSplitter>`, `<IslandSplitter>` (`Splitter.tsx` и др.)

Разделители панелей. В новом разделе не используются — у него нет
resize-рельсы.

#### `<TocRow>` (`TocRow.tsx`)

Строка оглавления (для панели «Документация»). Не нужен.

#### `<WaitingIndicator>` (`WaitingIndicator.tsx`)

Кольца ожидания (на месте `<Домик с кольцами>` в индикаторе ответа).
Сюда не относится.

---

## 4. Доменные компоненты «Персон» — что использовать

Полный список уже собран в `frontend/src/features/personas/`. Ниже — те,
что реально пригодятся новому разделу, с публичным API.

### 4.1 `<PersonaAvatar>` (`PersonaAvatar.tsx`)

```ts
interface PersonaAvatarProps {
  persona: Persona
  size?: number    // пиксели кружка; дефолт 40
  fill?: boolean   // аватар тянется по родителю (для плавных анимаций FAB)
  speaking?: string // hex-цвет колец «сейчас говорит» (из контекста ленты)
}
```

Круглый аватар: `<img>` если есть фото, иначе инициалы на палитре
`AGENT_COLORS` (через `agentDotColor`). Использовать в каждой строке
списка персон карточки специальности — без вариантов. **Размеры вокруг
карточек:**
- 80 — hero студии (`PersonaPreview`)
- 48 — карточка приглашения
- 40 — витрина ассистентов (`PersonasHub`)
- 32 — сайдбар
- 26 — ряд приветствия

### 4.2 `<PersonaFace>` и `<PersonaBackdrop>` (`PersonaFace.tsx`)

```ts
// PersonaFace — лицо без рамки, потребитель задаёт геометрию и маску
interface PersonaFaceProps {
  persona: Persona
  align: 'left' | 'right' | 'center'
  fontSize: number | string
  style: CSSProperties
}

// PersonaBackdrop — фото у правого края + цветная вуаль
interface PersonaBackdropProps {
  persona: Persona
  width?: number            // дефолт 84
  fontSize?: number         // дефолт 38
  neutral?: boolean         // отключить цветную вуаль
}
```

Для «обложки» специальности (если она решит быть картинкой), `PersonaAvatar`
достаточно; `PersonaBackdrop` пригодится, только если карточка специальности
хочет повторить паттерн карточки чата («лицо справа + вуаль влево»).

### 4.3 `<PersonaList>` (`PersonaList.tsx`)

```ts
type PersonaListMode = 'global' | 'all'

interface PersonaListProps {
  personas: Persona[]
  selectedId: string | null
  onSelect: (id: string) => void
  onNew: () => void
  mode?: PersonaListMode                  // только в глобальном разделе
  onModeChange?: (m: PersonaListMode) => void
  projects?: Project[]                    // для группировки в режиме «Все»
  dashedNewButton?: boolean               // общий стиль с сайдбаром чатов
  teamCenter?: { active: boolean; onClick: () => void }
}
```

Сам список персон в сайдбаре раздела. Прямо сейчас он универсален:
используется и в глобальном хабе, и в панели «Команда» проекта, и в
местах, где нужна группировка по проектам. Новый раздел может
переиспользовать его как чёрный ящик, если нужно показать «персоны,
работающие по этой роли» внутри карточки специальности в виде боковой
колонки.

### 4.4 `<PersonasHub>` (`PersonasHub.tsx`)

Витрина раздела «Персоны» — шапка + сетка карточек ассистентов + правая
лента «Активность». Эталон того, как новый экран «Специальности» может
выглядеть в стиле раздела (большая центральная карточка + правый сайдбар
со сводкой). Прямо сейчас `PersonasHub` не рендерится, когда
`specialtiesMode` открыт, — но шаблон центральной зоны тот же.

### 4.5 `<PersonasSpecialties>` (`PersonasSpecialties.tsx`)

Текущая реализация раздела — обёртка вокруг
`features/specialties/SpecialRulesTab`, который, в свою очередь, рисует
карточки специальностей вручную через `cards.tsx`. Внутри:

- `AnySpecialtyCard` — закреплённая карточка «Любая специальность».
- `RuleGroupCard` — группа одинаковых наборов (аккордеон).
- `RuleSpecCard` — отдельная роль.
- `UnruledRoleCard` — роль без правил, но с персонами (показывает срез).

Все четыре — **один и тот же корпус** (`shellStyle` в `cards.tsx`):
белая карточка со `R.xl`, обводка `border`, в раскрытом виде —
`accentMuted`. Используется тот же шаблон: `shellStyle({ open, highlight })`
+ `bodyStyle` (пунктир сверху + `C.bgCard` фон).

### 4.6 `<PersonaToolbar>` (`PersonaToolbar.tsx`)

Тулбар студии персоны (кнопки вида, FAB, теги). Содержит плашку
специальности, которая нам, возможно, пригодится как образец для
карточки специальности в обратном направлении — что показать про
**саму роль** (а не про персону).

### 4.7 `<PersonaPreview>` (`PersonaPreview.tsx`)

Read-only визитка персоны. Уже содержит **мостик T9** (`onOpenSpecialties`)
— кнопку «Специальность: … →», которая ведёт в раздел «Специальности».
Это готовый проп для будущего макета — клик по мостику должен открывать
детальный вид **этой** специальности.

### 4.8 `useSpecialtiesCoverage` (`useSpecialtiesCoverage.ts`)

```ts
function useSpecialtiesCoverage(isAdmin: boolean): string | null
// Возвращает "N из M" или null, если ничего не настроено
```

Уже висит на переключателе режима как бейдж охвата. Если новый экран
внутренне тоже считает охват — переиспользовать.

---

## 5. Как сейчас устроены экраны «Персоны» визуально

**Главный источник — `PersonasPage.tsx`** (рендерилка), плюс три
подрежима центральной зоны.

### 5.1 Раскладка (десктоп)

```
┌────────────────────────────────────────────────────────────────────────────┐
│ HubHeader: «Персоны» в верхней таб-полосе                                  │
├──────────┬────────────────────────────────────────────────┬───────────────┤
│ Сайдбар  │  Центральная зона (холст)                        │  Правая панель│
│ (Panel   │  • modeSwitcher (PillSwitch)                     │  (опционально)│
│  Zone)   │  • mode === 'hub':        PersonasHub           │               │
│ Persona  │    (шапка + витрина карточек + лента «Активн.»)  │               │
│ List:    │  • mode === 'specialties': PersonasSpecialties   │               │
│  поиск   │    (белая карточка MODAL_W.wide + SpecialRulesTab)              │
│ + «Глоб.│  • selected: PersonaStudio                       │               │
│  /Все»   │  • creating: PersonaWizard                      │               │
│ + список │                                                 │               │
└──────────┴────────────────────────────────────────────────┴───────────────┘
```

`IslandScaffold` держит эту трёхколонную сетку. Боковые отступы холста —
`ISLAND.pad` (16). Зазор между островами — `ISLAND.gap` (8).

### 5.2 Раскладка (мобила / планшет)

`PersonaList` рендерится как полноэкранный список; центральная зона —
либо отдельный экран студии/создания/specialties, либо сам список.
Таббар `modeSwitcher` живёт над списком, приглашение «Познакомьтесь» —
тоже сверху (`mobileInviteCard`).

### 5.3 Переключатель режима центра

- Компонент: `<PillSwitch<'hub' | 'specialties'>>`.
- Опции: `{ value: 'hub', label: 'Персоны' }` и
  `{ value: 'specialties', label: 'Специальности' }`.
- Память: `persistKey="cc_personas_mode"`.
- Бейдж «N из M» рядом с подписью — отдельным span (не встроен в PillSwitch).
- Источник бейджа — `useSpecialtiesCoverage(me.role === 'admin')`.
- Подпись в режиме specialties: «Роль задаёт, какие модели, доступы и
  инструкции получит персона по умолчанию.» (`FS.sm`, `C.textSecondary`,
  `maxWidth: 640`).

### 5.4 Сайдбар раздела «Персоны»

- Заголовок рисует `<PanelShell>`. Сам список — `<PersonaList>`.
- Сверху списка — **залитая кнопка «Новая персона»**
  (`background: C.accent, color: C.onAccent`, радиус `R.md`,
  `padding: '8px 12px'`, `fontSize: 13, fontWeight: 500`, иконка `Plus`).
  В варианте для панели «Команда» — пунктирная (`Button variant="dashed"`).
- Если `onModeChange` передан — `<PillSwitch<PersonaListMode>>` с
  опциями «Глобальные / Все».
- Строки персон: `<PersonaAvatar size={32}>` + блок текста (роль, имя,
  описание). Активная строка — фон `C.accentMuted`, hover — `C.accentLight`.
- Группы «Пантеон OmO» и (в режиме «Все») проекты — заголовок группы
  (`fontSize: 10.5, fontWeight: 700, color: C.textMuted, textTransform:
  uppercase, letterSpacing: '0.06em'`, рамка сверху `1px solid C.border`).
- Пустой список — короткий текст `C.textMuted` (не `<EmptyState>` —
  компонент сочтён избыточным для такого крошечного случая).

### 5.5 Витрина ассистентов (`PersonasHub`)

- Обёрнута в `<PageCanvas>` — на общем фоне-холсте лежит контентная зона.
- Шапка: serif `<h1>` (28/500) + параграф `C.textMuted` (14) +
  карточка-виджет «Что умеет персона» (300×`auto`, `R.xxl`, фон `C.bgWhite`).
- Сетка карточек ассистентов: `grid-template-columns: repeat(auto-fill,
  minmax(150px, 1fr))`, `gap: 12`. Карточка `C.bgWhite`, бордер `C.border`,
  `R.xxl`, `padding: 14`.
- В углу карточки — ссылка «Поговорить» (`C.accent`, `fontWeight: 700`).
- Под витриной — карточка-создание (`Sparkles` иконка в `accentLight`
  круге, заголовок и подзаголовок, primary-кнопка «Создать»).
- Правая колонка — `<PersonaActivityFeed>`, заполняет 300px; на мобиле —
  в собственной карточке под витриной.

### 5.6 Студия персоны (`PersonaStudio`)

- Холст (`hero={true}`) → сверху `<PersonaToolbar mode="edit">`,
  тонкая полоска цвета персоны (`{accent}55`, 2px) → контент.
- Тулбар: 4 таба через `<PillSwitch>` (Предпросмотр / Знания / Задачи /
  Проактивность / Память) — `<PillSwitch>` с `persistKey`.
- Кнопки сохранения/отмены/назад — в `actions` тулбара.
- Контент: `<PersonaPreview>` (по умолчанию), `<PersonaForm>` (правка),
  `<PersonaBindingsPanel>`, `<PersonaTasksPanel>`, `<PersonaAutomationPanel>`,
  `<PersonaMemoryPanel>`.
- На мобиле поверх появляется `<PersonaEditFab>` (accent-кнопка с `Plus`).

### 5.7 Режим «Специальности» (текущий)

- Поверх центральной зоны — белая карточка с `R.xl`, `border: 1px solid
  borderLight`, `padding: SP.md` (мобил) или `SP.lg` (десктоп), max-width
  `MODAL_W.wide` (720).
- Внутри — `<SpecialRulesTab>` из `features/specialties/SpecialRulesTab.tsx`:
  - Карточка «Любая специальность» (закреплена первой, всегда раскрыта).
  - Карточки одинаковых наборов (аккордеон).
  - Карточки отдельных ролей (аккордеон).
  - Карточки ролей без правил, но с персонами.
- В карточке — три строки `TierFieldRow` (Сильная / Средняя / Слабая),
  жёлтая плашка про применимость, срез «Кто работает» + объяснения T8/T3.
- Перед карточками — селектор уровня настройки
  (`global` / `owner` / `user`) через `<InlineSegmented>`.
- Сверху — таб-полоса `PillSwitch<'global' | 'owner' | 'user'>`.

---

## 6. Карта «элемент будущего экрана → что использовать»

| Элемент будущего экрана | Токены / компоненты |
|---|---|
| **Обёртка раздела** | `<PageCanvas>` + `<IslandScaffold>` (или только `<PageCanvas>`, если без боковых панелей). Боковые отступы — `ISLAND.pad` (16). Внутренние зазоры — `ISLAND.gap` (8). |
| **Заголовок раздела «Специальности»** | `FONT.serif` + `FS.h1` (28) + `C.textHeading` + `lineHeight: 1.28` + `letterSpacing: '-0.01em'` + `maxWidth: 600` (как `h1` в `PersonasHub`). |
| **Подзаголовок раздела** | `FONT.sans` + `FS.sm` (12) + `C.textSecondary` + `lineHeight: 1.5` + `maxWidth: 640` (как в `PersonasPage`). |
| **Переключатель режима «Карта специальностей \| Мои назначения \| Мои правила»** (если будет) | `<PillSwitch<T>>` с `persistKey` и опциональным `draggable`. Тоны — `variant="default"` (нейтральная дорожка). |
| **Карточка специальности (общий корпус)** | Карточка: фон `C.bgWhite`, бордер `1px solid C.border` (`C.accentMuted` если раскрыта), радиус `R.xl`, отступы `SP.md` (мобил) / `SP.lg` (десктоп), тень `SHADOW.card`. Highlight (выделение через клавиатуру) — `boxShadow: '0 0 0 2px C.accent'`, `transition: 'box-shadow 0.2s, border-color 0.15s'`. Это и есть `shellStyle` в `cards.tsx` — взять её как готовый шаблон, не плодить третий. |
| **Шапка карточки специальности (имя роли + сводка)** | `FONT.sans` + `FS.base` (13) + `C.textHeading` + `fontWeight: 700` для имени; `FS.xs` (11) + `C.textMuted` для сводки. Хедер — кнопка `<button>` с `aria-expanded`, padding `11px 14px`, hover — `C.bgSelected` (через глобальный CSS-класс `.cc-sr-head`, заведён в `cards.tsx`). |
| **Chevron раскрытия** | `<ChevronDown>` из `lucide-react`, размер `ICON_SIZE.sm` (`15`), `ICON_STROKE` (`2`), цвет `C.textMuted`, поворот 180° при open. |
| **Тело карточки (раскрытое)** | `borderTop: 1px dashed C.border`, `padding: 4px 14px SP.md`, фон `C.bgCard`. `bodyStyle` в `cards.tsx` уже готов. |
| **Поле «уровень модели» (Сильная/Средняя/Слабая)** | Внутри карточки: три `<Field>`-строки с `<RoutePicker>` (`features/components/RoutePicker.tsx`). На уровне токенов — `C.bgWhite`, `border`/`borderFocus`, `R.xl`, `FIELD` group. |
| **Плашка-предупреждение (на «выделение» роли из группы и пр.)** | `FONT.sans` + `FS.xs` (11) + `C.warningText` + фон `C.warningBg`, `R.md`, `padding: '6px 10px'`. Тонко — только для warning. Для info — `C.info` + `C.infoBg`. Для успеха — `C.success*` (не используется в текущем `cards.tsx`). |
| **Плашка статуса специальности («Глобальная», «Админ», «Не настроено»)** | `<Badge tone="accent" size="xs" dot>` или `tone="neutral"` / `tone="warning"` / `tone="danger"`. Для «N из M» — `<Badge tone="neutral" size="xs">` или отдельный `<span>` (как на `PillSwitch` в `PersonasPage`). |
| **Кликабельная ссылка внутри карточки («выделить», «Вернуть наследование»)** | `<LinkAction>` из `cards.tsx`: `<button>` с `fontSize: FS.xs`, `fontWeight: 600`, `C.accent`, `textDecoration: underline`, `textUnderlineOffset: 2`. |
| **Срез «Кто работает по этой роли» — строка персоны** | Кнопка-строка с `<PersonaAvatar size={26>` + имя + бейдж роли (если их несколько) + 1–3 мини-чипа моделей (`FONT.mono`, `FS.xs`, `bg: C.bgSelected`, `R.sm`). Цветовая плашка роли — `<Badge tone="neutral" size="xs">`. |
| **Срез — заголовок секции** | `FONT.sans` + `FS.xs` + `fontWeight: 700` + `C.textMuted` + `textTransform: 'uppercase'` + `letterSpacing: '0.07em'` (идентично заголовку группы «Пантеон OmO» в `PersonaList`). |
| **Срез — empty-state** | `<EmptyState compact>` с иконкой `Users`, заголовком «По этой роли пока никто не работает», подписью и CTA «Назначить персоне →». |
| **Срез — строка-объяснение T8** (для слоёв global/user) | Обычный `<div>` со `sliceWrapStyle`: `marginTop: SP.md`, `paddingTop: SP.md`, `borderTop: 1px dashed C.border`, `FS.xs` + `C.textSecondary` + `lineHeight: 1.5`. Готовый `PersonaSliceExplanation` в `cards.tsx`. |
| **Аватар персоны в любой строке** | `<PersonaAvatar size={N}>`. Размер: 24 — плотные ряды чипов, 26 — строки среза, 32 — сайдбар, 40 — витрина, 80 — hero. |
| **Список персон в боковой колонке «Специальности»** | `<PersonaList>` как чёрный ящик (фильтрованный `personas` снаружи). Можно дополнительно обернуть каждую группу в `<SidebarSection>`, если нужны раскрывающиеся группы по типу. |
| **Карточка пустого среза / пустого слоя** | `<EmptyState icon={<Users/>} title="..." subtitle="..." action={<Button variant="primary">...}` в центре экрана, или `compact` в сайдбаре. |
| **Кнопка «Добавить специальность» (FAB или обычная)** | `<Button variant="primary" size="md" leftIcon={<Plus/>}>` (внутри карточки раздела) или `<Button variant="dashed" size="md">` (пунктирная, в углу сайдбара, как у `<PersonaList>` для «Новая персона»). FAB с акцентом — только для мобилы: `<PersonaEditFab accent={...} onClick={...}>`. |
| **Тоггл «назначено мне / назначено всем»** | `<Toggle checked={own} onChange={setOwn} focusable ariaLabel="...">`. Дефолт 42×25; в плотных рядах — кастомный размер. |
| **Поле ввода в карточке (например, поиск специальности)** | `<Field label="Поиск"><IconField icon={<Search/>} value={...} onChange={...} /></Field>`. Или просто `<TextField>` без обёртки. |
| **Поиск по специальностям в шапке раздела** | `<IconField icon={<Search/>}>` в `IslandHeader.actions` или выше сетки. |
| **Сегментированный выбор уровня (Сильная / Средняя / Слабая)** | `<InlineSegmented<T>>` (компактный, для строк) или `<SegmentedControl>` (одиночный). Если уровень — заголовок секции, а не строка, лучше `<InlineSegmented>` ради того же ритма. |
| **Подтверждение сброса / удаления** | `<ConfirmDialog>` через `<Modal>` под ширину `MODAL_W.confirm` (380). |
| **Полноэкранный визард создания / правки специальности** | `<Modal width={MODAL_W.wide}>` с `title`, `subtitle`, `footer={<ModalActions>}`. На мобиле превращается в bottom-sheet автоматически. |
| **Холст за всем** | Через `<PageCanvas>` (включает `CanvasBackdrop`). |
| **Drag-and-drop назначение персоны на специальность** (если будет) | Готового нет. См. §7 — чего не хватает. |
| **Иконография** | `lucide-react`. Размеры — `ICON_SIZE.xs|sm|md` (`12 / 15 / 17`), `strokeWidth` — `ICON_STROKE` (2). Цвет — `C.textMuted` для пассивных, `C.accent` для активных. |

---

## 7. Чего в дизайн-системе не хватает и чем заменить без новых компонентов

> Задача явно требует «без новых компонентов». Этот раздел — карта того,
> что **может** понадобиться, и подсказка, чем заменить из уже
> существующего (или простого композита).

### 7.1 Общий `<Card>` / `<Disclosure>` для карточек специальностей

Сейчас такой абстракции в UI-ките нет. Аналог — функция `shellStyle` в
`features/specialties/specialRules/cards.tsx`, используемая четырьмя
карточками (`AnySpecialtyCard`, `RuleGroupCard`, `RuleSpecCard`,
`UnruledRoleCard`). Для нового раздела ровно тот же шаблон, скопированный
**внутри** нового файла (как сейчас в `cards.tsx`). Если делать обобщение,
оно должно жить в `ui-kit` — но это уже следующая задача, не текущая.

**Замена без новых компонентов:** локальная функция/компонент в новом
файле, копирующая `shellStyle` + `bodyStyle` из `cards.tsx` 1:1. Сейчас
они уже канонические (одинаковые у четырёх карточек), и новая карточка
может смело их повторить.

### 7.2 Общий `<CardAccordion>` с одинаковой логикой раскрытия

У `cards.tsx` каждая карточка сама ведёт своё `open`. Если в новом разделе
таких карточек будет штук пять — стоит иметь общий компонент, но это
«новая территория». До тех пор — та же ручная `<button>` с
`aria-expanded` + классом `.cc-sr-head` для hover (как заведено в
`cards.tsx`).

### 7.3 DnD-таргет для «назначить персону на специальность»

В UI-ките нет готового drop-таргета (`<PanelDropGuide>` существует, но это
**для панелей**, а не для карточек специальностей). Если нужно простое
назначение — лучше его делать через меню `<Badge onClick>` (как
`Menu`-база у персон) или через `<ToolbarOverflowMenu>`. DnD — за рамками
текущей задачи.

### 7.4 Превью-картинка специальности

У специальностей сейчас нет иконки/обложки; если макет Майи покажет
большую иллюстрацию роли — `<img>` напрямую через `api.personas.avatarUrl`,
как у самой персоны. Альтернативно — пустая плитка того же ритма, что
`<FileTypeTile>` (нейтральная подложка `C.bgPanel`, иконка
`lucide-react` по центру).

### 7.5 Список множественного выбора (например, «выбрать персон для роли»)

Не существует. Заменить — `<Modal>` с прокручиваемым списком чекбоксов
(обычные `<input type="checkbox">` + `<PersonaAvatar>` + текст). Если
список маленький (< 7) — обойтись чипами выбора с крестиком, там в
дизайн-системе нет готового примитива, но собирается через `<Badge
size="xs" tone="accent">` + `<X>` рядом.

### 7.6 Горизонтальная полоса прогресса / картина уровней

В `cards.tsx` используется `<LevelsPicture>` для визуализации «Сильная /
Средняя / Слабая» по доле цепочек (`segmentBg` / `segmentBorder` /
`segmentLabel`). Это специфический для специальностей компонент. Если
новому разделу нужна похожая полоса — она уже есть; копировать
подход (а не код — это фича специальностей).

### 7.7 Большая карточка-обложка специальности (примерно как hero `PersonaPreview`)

Готовой заготовки нет. Прямой путь — взять структуру hero из
`PersonaPreview`: `<div style={{display:'flex', gap:18, ...}}>` с
`<PersonaAvatar size={80}>` слева и текстовым блоком справа. Это уже
канон в проекте.

### 7.8 «Пагинация» специальностей (если каталог большой)

Ничего готового. Но если понадобится — структура та же, что у проектного
списка (`InfinityPage` либо простая кнопка «Показать ещё»).

### 7.9 Визуальная иерархия «специальность с дочерними подвидами»

В UI-ките нет `<Tree>`-компонента. Если нужно — копировать логику группы
`Пантеон OmO` в `<PersonaList>`: тот же `groupHeader`-блок + плоский
список строк. Раскрытие секции — отдельным `useState` или
`<SidebarSection>`.

### 7.10 Маркировка «требует ответа» / состояние pending для специальностей

Аналог «alert» в плашках — но в дизайн-системе нет общего «alert»-чипа.
`SHADOW.alert` припасена под это. Чип-реализация — `<Badge tone="warning"
size="xs" dot>` либо целый `<div>` со стилем плашки (как `info`-блок
«matchesAny» в `RuleGroupCard`).

### 7.11 Accessibility-резюме

Готовые узоры:
- Аккордеон: `<button aria-expanded={open}>` + контент после него
  (как в `cards.tsx`).
- Тоггл: `<Toggle role="switch" aria-checked={checked}>` (уже так).
- Переключатель режима: `<PillSwitch>` оборачивает кнопки — у каждой
  свой `aria-label`, активная проставляется через `aria-pressed`-семантику.
- Список персон: `<div role="listbox" aria-label="Список персон">` +
  строки `<button role="option" aria-selected={active}>`. Это уже
  сделано в `<PersonaList>`.

Все паттерны стандартны для проекта — новая страница должна их
наследовать.

---

## 8. Что НЕ входит в инвентарь (границы)

- Тексты. Специальности называются по каталогу (`SpecialtyCatalogEntry`),
  тексты для новых строк — отдельно по текстам в `docs/features/...`.
- Данные. Какие специальности и что у них внутри (`specialties/model.ts`)
  — отдельный слой, на UI-инвентарь не влияет.
- Бэкенд-эндпоинты. Стор персон живёт в `lib/personas.ts`, но новые
  запросы (если появятся) — по API, не по UI.
- Дизайн-решения. Этот документ — реестр; решения «как именно расположить
  элементы» принимает Майя в макете.

---

## 9. Контрольные ссылки

- `frontend/src/lib/design.ts` — токены.
- `frontend/src/lib/theme.css` — конкретные hex-значения цветов.
- `frontend/src/components/ui/index.ts` — публичные экспорты UI-кита.
- `frontend/src/components/ui/*.tsx` — сами компоненты.
- `frontend/src/features/personas/PersonasPage.tsx` — верхний роутинг раздела.
- `frontend/src/features/personas/PersonasHub.tsx` — текущий визуал витрины.
- `frontend/src/features/personas/PersonaList.tsx` — текущий визуал сайдбара.
- `frontend/src/features/personas/PersonaAvatar.tsx`, `PersonaFace.tsx` — аватары.
- `frontend/src/features/specialties/specialRules/cards.tsx` — карточки специальностей (эталон).
- `frontend/src/features/specialties/specialRules/model.ts` — структуры данных специальностей.
- `frontend/src/features/personas/useSpecialtiesCoverage.ts` — бейдж «N из M».
- `frontend/dev/UiKitPage.tsx` — живая витрина всех UI-компонентов (dev-only).
- `docs/design/guidelines.md` — канон дизайн-системы (цвета/типографика/контролы).
- `docs/design/target-devices.md` — приоритетные ширины и точка перехода.
- `docs/design/audit.md` — карта конвенций и контрольного линта.
