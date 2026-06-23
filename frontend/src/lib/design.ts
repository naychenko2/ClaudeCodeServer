// Дизайн-токены из макетов Claude Design.
// Единый источник правды для цветов, типографики, радиусов, теней и стилей контролов.

// === Семейства шрифтов ===
export const FONT = {
  sans:  "'Hanken Grotesk', -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif",
  serif: "'PT Serif', Georgia, serif",
  mono:  "'JetBrains Mono', 'Courier New', monospace",
} as const

// === Цвета ===
export const C = {
  // Фоны
  bgMain:      '#F4F0E8',
  bgPanel:     '#EDE7DC',
  bgCard:      '#FBF8F2',
  bgWhite:     '#FFFFFF',
  bgSelected:  '#E8E1D4',
  bgInset:     '#E7E0D2',   // утопленные зоны/футеры панелей

  // Текст
  textHeading:   '#2A251F',   // заголовки и акцентный текст
  textPrimary:   '#39332B',
  textSecondary: '#756B5E',
  textMuted:     '#9A8F7E',
  onAccent:      '#FBF8F2',   // текст/иконки поверх accent-фона

  // Акцентный (ОСНОВНОЙ — оранжевый)
  accent:        '#D97757',
  accentLight:   '#F4ECE1',
  accentMuted:   '#EAD3C5',
  accentSoft:    '#E8A990',   // disabled/loading состояние accent-кнопки

  // Границы
  border:      '#E0D7C8',
  borderLight: '#E8E1D4',
  divider:     '#DDD4C4',   // выраженная граница между панелями
  dashed:      '#D0C6B4',   // пунктирные границы кнопок «создать»
  track:       '#D8CFBE',   // дорожка выключенного переключателя

  // Оверлей модальных окон
  overlay:     'rgba(23,19,15,0.42)',

  // Статусы
  success:     '#5E8B4E',
  successBg:   '#E9F1E8',
  successText: '#3F7A4F',
  warning:     '#C9923E',
  warningBg:   '#FBEFE0',
  warningText: '#8A6A28',
  danger:       '#B4452F',
  dangerBg:     '#FBF1EC',
  dangerText:   '#B4452F',
  dangerBorder: '#F5C6BF',
  info:        '#3E7CA6',
  infoBg:      '#E7EFF5',

  // Режим «План» (индиго-фиолет) — отдельный от accent
  plan:        '#6C5CB0',
  planLight:   '#EEEBF8',   // фон чипа/карточки плана
  planText:    '#4E4196',   // текст на planLight
  planBorder:  '#D6CFEF',

  // Diff
  diffAddBg:   '#E8F5E9',
  diffAddText: '#1B5E20',
  diffRemBg:   '#FFEBEE',
  diffRemText: '#B71C1C',

  // Вывод инструментов в чате
  // Тёмный «терминал» — только для консольных команд (Bash) и превью команд на запуск
  termBg:      '#2A251F',   // тёмный фон терминала
  termText:    '#D8CFC0',   // обычный вывод на тёмном
  termError:   '#F0B8AC',   // вывод с ошибкой на тёмном
  // Светлая «панель вывода» — для текстовых результатов (Read/Grep/Glob и пр.)
  outputBg:    '#FFFFFF',   // белый фон панели вывода
  outputBorder:'#DED4C2',   // граница панели вывода
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
  full:  '50%',   // круги (аватары, тумблеры)
} as const

// === Тени ===
export const SHADOW = {
  focus:    '0 0 0 3px rgba(217,119,87,0.14)',   // focus-ring контролов
  card:     '0 2px 8px rgba(60,50,35,0.05)',     // лёгкая тень карточек
  dropdown: '0 8px 28px rgba(60,50,35,0.16)',    // выпадающие меню
  modal:    '0 24px 60px rgba(23,19,15,0.40)',   // модальные окна
  sheet:    '0 -8px 40px rgba(23,19,15,0.22)',   // мобильная шторка (тень кверху)
  button:   '0 4px 14px rgba(217,119,87,0.30)',  // свечение основной кнопки
  fab:      '0 6px 18px rgba(217,119,87,0.42)',  // плавающая круглая кнопка (FAB)
  thumb:    '0 1px 3px rgba(42,37,31,0.12)',     // бегунок переключателя
} as const

// === Слои (z-index) ===
export const Z = {
  dropdown: 50,    // выпадающие меню (composer и т.п.)
  modal:    1000,  // модальные окна и шторки
} as const

// === Ширины модальных окон (единая шкала вместо 340/360/380/400/420) ===
export const MODAL_W = {
  confirm: 380,   // компактные подтверждения (удаление и т.п.)
  form:    440,   // формы (создание/редактирование)
} as const

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
