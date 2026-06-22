// Дизайн-токены из макетов Claude Design
export const C = {
  // Фоны
  bgMain:      '#F4F0E8',
  bgPanel:     '#EDE7DC',
  bgCard:      '#FBF8F2',
  bgWhite:     '#FFFFFF',
  bgSelected:  '#E8E1D4',

  // Текст
  textHeading:   '#2A251F',   // заголовки и акцентный текст
  textPrimary:   '#39332B',
  textSecondary: '#756B5E',
  textMuted:     '#9A8F7E',

  // Акцентный (ОСНОВНОЙ — оранжевый)
  accent:        '#D97757',
  accentLight:   '#F4ECE1',
  accentMuted:   '#EAD3C5',

  // Границы
  border:      '#E0D7C8',
  borderLight: '#E8E1D4',

  // Статусы
  success:     '#5E8B4E',
  successBg:   '#E9F1E8',
  successText: '#3F7A4F',
  warning:     '#C9923E',
  warningBg:   '#FBEFE0',
  warningText: '#8A6A28',
  danger:      '#B4452F',
  dangerBg:    '#FBF1EC',
  dangerText:  '#B4452F',
  info:        '#3E7CA6',
  infoBg:      '#E7EFF5',

  // Diff
  diffAddBg:   '#E8F5E9',
  diffAddText: '#1B5E20',
  diffRemBg:   '#FFEBEE',
  diffRemText: '#B71C1C',
} as const

// Кнопки
export const BTN = {
  primary: {
    background: C.accent,
    color: '#FBF8F2',
    border: 'none',
    borderRadius: 11,
    padding: '11px 20px',
    fontSize: 14,
    fontWeight: 600,
    cursor: 'pointer',
  },
  secondary: {
    background: C.bgPanel,
    color: C.textSecondary,
    border: 'none',
    borderRadius: 11,
    padding: '11px 20px',
    fontSize: 14,
    fontWeight: 600,
    cursor: 'pointer',
  },
  ghost: {
    background: 'none',
    color: C.textSecondary,
    border: `1px solid ${C.border}`,
    borderRadius: 9,
    padding: '6px 12px',
    fontSize: 13,
    cursor: 'pointer',
  },
  danger: {
    background: C.danger,
    color: '#FBF8F2',
    border: 'none',
    borderRadius: 9,
    padding: '8px 16px',
    fontSize: 13,
    cursor: 'pointer',
  },
} as const

// Тулбары — единая система (высота, паддинги, icon-кнопка, pill-переключатель)
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
  iconRadius: 8,
  pillTrack: C.bgSelected,
  pillRadius: 9,
  pillThumbBg: C.bgWhite,
  pillThumbShadow: '0 1px 3px rgba(42,37,31,0.12)',
} as const
