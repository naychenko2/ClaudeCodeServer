// Ценовые классы моделей (free | cheap | expensive) — ЕДИНЫЙ словарь на фронте:
// подпись и цвет класса. Класс приходит с бэка (аналитика расхода, /api/models
// costTier, разбивка расхода фичи) — словарь «какая модель какого класса» на
// фронте НЕ держим, красим только по ключу.
//
// Модуль намеренно зависит лишь от lib/design: его берут и разделы аналитики
// расхода, и фича «Статистика фичи» (компоненты/feature) — тянуть туда
// весь кит оболочки ради трёх строк словаря незачем.

import { C } from './design';

// Класс → подпись (легенда, столбик дня) и цвет. Цвета — токены темы: обе темы
// корректны автоматически.
export const COST_TIERS: Record<string, { label: string; color: string }> = {
  free: { label: 'Бесплатные', color: C.success },
  cheap: { label: 'Дёшево', color: C.info },
  expensive: { label: 'Дорого', color: C.warning },
};

// Порядок классов (стек столбика дня, легенда).
export const COST_TIER_KEYS = ['free', 'cheap', 'expensive'] as const;
export type CostTierKey = typeof COST_TIER_KEYS[number];

// Подпись класса для пилюли (единственное число): «бесплатный» / «дёшево» / «дорого».
export const COST_TIER_PILL: Record<string, string> = {
  free: 'бесплатный',
  cheap: 'дёшево',
  expensive: 'дорого',
};

// Цвет класса. Неизвестный или null-класс → нейтральный цвет (не выдуманный).
export const costTierColor = (t?: string | null) =>
  (t && COST_TIERS[t] ? COST_TIERS[t].color : C.textMuted);
