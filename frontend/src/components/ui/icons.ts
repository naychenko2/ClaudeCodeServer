// Единая иконочная система на базе lucide-react.
// Стандарт: Feather-стиль — strokeWidth = ICON_STROKE (2), round caps, color = currentColor.
//
// Иконки импортируются напрямую из lucide-react в месте использования:
//   import { Plus, Search } from 'lucide-react'
//   <Plus size={ICON_SIZE.sm} strokeWidth={ICON_STROKE} />
// Размер — строго из ICON_SIZE (никаких magic numbers). Для спреда дефолтов — ICON_PROPS.

// Размерная шкала иконок (синхронизирована с FS — типографикой)
export const ICON_SIZE = {
  xs: 14,   // inline-иконки в тексте, микро-метки
  sm: 16,   // иконки кнопок/тулбара (по умолчанию)
  md: 18,   // акцентные иконки в шапках
  lg: 20,   // крупные иконки пустых состояний
  xl: 24,   // hero-иконки
} as const

// Единая толщина обводки для всех иконок (Feather-канон)
export const ICON_STROKE = 2 as const

// Дефолтные SVG-атрибуты — spread в любой lucide-компонент для единообразия:
//   <Plus {...ICON_PROPS} size={ICON_SIZE.sm} />
export const ICON_PROPS = {
  strokeWidth: ICON_STROKE,
  strokeLinecap: 'round' as const,
  strokeLinejoin: 'round' as const,
}

// Значок проекта (ADR-009 §5) рисует `GlyphIcon` из lib/projectGlyphs: имя резолвится
// по ПОЛНОМУ набору пакета lucide. Рукописной карты `GLYPHS` здесь больше нет — она
// знала 89 имён из ~2000 и молча превращала плитку в пустой квадрат; расхождение
// наборов держит сторож `features/projects/__tests__/projectGlyphNames.test.ts`.
export { GlyphIcon, isLucideIconName, LUCIDE_ICON_NAMES } from '../../lib/projectGlyphs'
