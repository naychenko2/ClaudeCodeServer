// Дизайн-токены из макетов Claude Design.
// Единый источник правды для цветов, типографики, радиусов, теней и стилей контролов.

// === Семейства шрифтов ===
export const FONT = {
  sans:  "'Hanken Grotesk', -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif",
  serif: "'PT Serif', Georgia, serif",
  mono:  "'JetBrains Mono', 'Courier New', monospace",
} as const

// === Размерная шкала шрифтов (вместо magic numbers 9..34) ===
// Единая типографическая шкала. Вместо fontSize: 13/13.5/14/14.5… — FS.base / FS.md.
export const FS = {
  xs:      11,   // микро-метки, caption, бейджи
  sm:      12,   // вторичный текст, мелкие подписи
  base:    13,   // основной текст UI
  md:      14,   // поля ввода, обычные тексты
  lg:      16,   // заголовки секций, важный текст
  xl:      18,   // крупные подписи
  h2:      22,   // подзаголовки страниц
  h1:      28,   // заголовки страниц
  display: 34,   // hero-текст (логин, крупные empty states)
} as const

// === Цвета ===
// Значения — ссылки на CSS custom properties (см. lib/theme.css). Так все
// компоненты, читающие C.bgMain и т.п., автоматически переключаются между
// светлой и тёмной темой в рантайме без пересборки инлайн-стилей.
// Конкретные hex-значения обеих тем — в theme.css.
export const C = {
  // Фоны
  bgMain:      'var(--c-bg-main)',
  bgPanel:     'var(--c-bg-panel)',
  bgCard:      'var(--c-bg-card)',
  bgWhite:     'var(--c-bg-white)',
  bgSelected:  'var(--c-bg-selected)',
  bgInset:     'var(--c-bg-inset)',   // утопленные зоны/футеры панелей

  // Текст
  textHeading:   'var(--c-text-heading)',   // заголовки и акцентный текст
  textPrimary:   'var(--c-text-primary)',
  textSecondary: 'var(--c-text-secondary)',
  textMuted:     'var(--c-text-muted)',
  onAccent:      'var(--c-on-accent)',   // текст/иконки поверх accent-фона

  // Акцентный (ОСНОВНОЙ — оранжевый)
  accent:        'var(--c-accent)',
  accentLight:   'var(--c-accent-light)',
  accentMuted:   'var(--c-accent-muted)',
  accentSoft:    'var(--c-accent-soft)',   // disabled/loading состояние accent-кнопки

  // Границы
  border:      'var(--c-border)',
  borderLight: 'var(--c-border-light)',
  divider:     'var(--c-divider)',   // выраженная граница между панелями
  dashed:      'var(--c-dashed)',   // пунктирные границы кнопок «создать»
  track:       'var(--c-track)',   // дорожка выключенного переключателя

  // Оверлей модальных окон
  overlay:     'var(--c-overlay)',
  // Полупрозрачная подложка под текст поверх картинки
  glass:       'var(--c-glass)',

  // Статусы
  success:     'var(--c-success)',
  successBg:   'var(--c-success-bg)',
  successText: 'var(--c-success-text)',
  warning:     'var(--c-warning)',
  warningBg:   'var(--c-warning-bg)',
  warningText: 'var(--c-warning-text)',
  danger:       'var(--c-danger)',
  dangerBg:     'var(--c-danger-bg)',
  dangerText:   'var(--c-danger-text)',
  dangerBorder: 'var(--c-danger-border)',
  info:        'var(--c-info)',
  infoBg:      'var(--c-info-bg)',

  // Режим «План» (индиго-фиолет) — отдельный от accent
  plan:        'var(--c-plan)',
  planLight:   'var(--c-plan-light)',   // фон чипа/карточки плана
  planText:    'var(--c-plan-text)',   // текст на planLight
  planBorder:  'var(--c-plan-border)',

  // Хаб-навигатор верхнего уровня — «чернильная» гамма мимо accent
  navInk:      'var(--c-nav-ink)',      // заливка активного раздела (тёмная плашка/светлая в dark)
  onNavInk:    'var(--c-on-nav-ink)',   // текст/иконка на активной плашке

  // Diff
  diffAddBg:   'var(--c-diff-add-bg)',
  diffAddText: 'var(--c-diff-add-text)',
  diffRemBg:   'var(--c-diff-rem-bg)',
  diffRemText: 'var(--c-diff-rem-text)',

  // Вывод инструментов в чате
  // Тёмный «терминал» — только для консольных команд (Bash) и превью команд на запуск
  termBg:      'var(--c-term-bg)',   // тёмный фон терминала
  termText:    'var(--c-term-text)',   // обычный вывод на тёмном
  termError:   'var(--c-term-error)',   // вывод с ошибкой на тёмном
  // Светлая «панель вывода» — для текстовых результатов (Read/Grep/Glob и пр.)
  outputBg:    'var(--c-output-bg)',   // белый фон панели вывода
  outputBorder:'var(--c-output-border)',   // граница панели вывода
} as const

// === Радиусы (единая шкала) ===
export const R = {
  sm:    6,       // мелкие теги/чипы
  md:    8,       // icon-hover, мелкие кнопки тулбара
  lg:    10,      // сегменты, компактные кнопки
  xl:    12,      // поля ввода и кнопки форм
  xxl:   14,      // крупные поля/кнопки (логин)
  pill:  9,       // pill-переключатели
  modal: 20,      // карточки модальных окон
  sheet: 22,      // верхние углы мобильной шторки (bottom-sheet)
  max:   999,     // pill-форма чипов/тумблеров (полное скругление; full='50%' даёт эллипс на неквадрате)
  full:  '50%',   // круги (аватары, тумблеры)
} as const

// === Размерная шкала отступов (4px-сетка, вместо magic numbers) ===
export const SP = {
  xxs:  2,
  xs:   4,
  sm:   8,
  md:   12,
  lg:   16,
  xl:   24,
  xxl:  32,
  xxxl: 48,
} as const

// === Тени ===
// Значения — CSS-переменные (theme.css): на тёмной теме тени усилены.
export const SHADOW = {
  focus:    'var(--shadow-focus)',      // focus-ring контролов
  card:     'var(--shadow-card)',       // лёгкая тень карточек
  dropdown: 'var(--shadow-dropdown)',   // выпадающие меню
  modal:    'var(--shadow-modal)',      // модальные окна
  sheet:    'var(--shadow-sheet)',      // мобильная шторка (тень кверху)
  button:   'var(--shadow-button)',     // свечение основной кнопки
  fab:      'var(--shadow-fab)',        // плавающая круглая кнопка (FAB) с accent-заливкой
  fabNeutral: 'var(--shadow-fab-neutral)', // тот же ореол, но нейтральный — FAB без заливки
  thumb:    'var(--shadow-thumb)',      // бегунок переключателя
} as const

// === Слои (z-index) ===
export const Z = {
  dropdown: 50,    // выпадающие меню (composer и т.п.)
  modal:    1000,  // модальные окна и шторки
} as const

// === Палитра цветов групп проектов (индикатор-полоска заголовка группы) ===
export const GROUP_COLORS = [
  '#3E7CA6',  // синий
  '#8E4A82',  // фиолетовый
  '#3F7A4F',  // зелёный
  '#C2693B',  // оранжевый
  '#B4452F',  // красный
  '#4B6BB0',  // индиго
  '#7A7250',  // хаки
] as const

// === Ширины модальных окон (единая шкала вместо 340/360/380/400/420) ===
export const MODAL_W = {
  confirm: 380,   // компактные подтверждения (удаление и т.п.)
  form:    440,   // формы (создание/редактирование)
} as const

// === Ширина колонки чата ===
// Общий предел для ленты сообщений и композера — держим их в одной центрированной
// колонке, чтобы на широких экранах текст не растягивался во всю ширину.
export const CHAT_MAX_W = 950

// === Базовый стиль текстового поля (для контролов в обёртках с иконкой) ===
export const FIELD = {
  background:   C.bgWhite,
  border:       `1px solid ${C.border}`,
  borderRadius: R.xl,
  color:        C.textHeading,
  fontSize:     14,
  // focus-состояние применяют контролы через borderFocus/SHADOW.focus
  borderFocus:  C.accent,
} as const

// === Тулбары — единая система (высота, паддинги, icon-кнопка, pill-переключатель) ===
export const TB = {
  heightDesktop: 52,
  heightMobile: 56,
  padX: 16,
  padXMobile: 14,
  gap: 8,
  bg: C.bgPanel,
  borderBottom: `1px solid ${C.border}`,
  iconHitDesktop: 32,
  iconHitMobile: 40,
  iconColor: C.textMuted,
  iconColorHover: C.textPrimary,
  iconHoverBg: C.bgSelected,
  iconRadius: R.md,
  pillTrack: C.bgSelected,
  pillRadius: R.pill,
  pillThumbBg: C.bgWhite,
  pillThumbShadow: SHADOW.thumb,
} as const
