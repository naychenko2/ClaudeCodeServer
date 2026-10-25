// Общее для кольца, таблицы и карточек блока «Кто и на чём работал» (волна 5):
// палитра групп, подписи мер и ролей, стили чипов и живого чипа. Раньше копировалось
// в три файла — правка подписи роли или стиля чипа разъезжалась.

import type { CSSProperties } from 'react';
import { C, FS, SP, R } from '../../lib/design';
import { roleLabel } from '../../lib/roleLabel';
import type { Measure, BreakdownChip } from './FeatureBreakdownModel';
import type { FeatureBreakdownGroup } from '../../types/featureStats';

// Палитра групп — токены записки (§Токены), по порядку; больше групп, чем цветов — цикл.
export const ARC_COLORS = [C.accent, C.info, C.plan, C.success, C.warning, C.progressTrack];

export const MEASURES: Measure[] = ['time', 'tokens', 'cost'];
export const MEASURE_WORD: Record<Measure, string> = { time: 'время', tokens: 'токены', cost: 'деньги' };

// Цвет группы — по месту ключа в СТАБИЛЬНОМ порядке входа (а не в сортировке по мере):
// кольцо и таблица сортируются по-разному, цвет дуги обязан совпасть с цветом строки.
// colorOf может вернуть undefined (персона неизвестна) — тогда палитра.
export function groupColor(key: string, groups: FeatureBreakdownGroup[], colorOf?: (k: string) => string | undefined): string {
  const own = colorOf?.(key);
  if (own) return own;
  const index = groups.findIndex(g => g.key === key);
  return ARC_COLORS[Math.max(0, index) % ARC_COLORS.length];
}

// Цвет аватара персоны в чипе: ключ — не обязательно группа оси, поэтому палитра по
// хешу ключа (без colorOf все чипы не должны быть одного цвета).
export function chipColor(key: string, colorOf?: (k: string) => string | undefined): string {
  const own = colorOf?.(key);
  if (own) return own;
  const h = [...key].reduce((a, ch) => (a * 31 + ch.charCodeAt(0)) >>> 0, 0);
  return ARC_COLORS[h % ARC_COLORS.length];
}

// Подпись чипа: модель — нормализованное имя (groupChips уже снимает «Авто → »); роль —
// слово roleLabel без шага; для 'step' roleLabel пуст — нейтральное «шаг».
export function chipLabel(chip: BreakdownChip, labelOf?: (k: string) => string): string {
  if (chip.kind === 'model') return chip.key;
  if (chip.kind === 'role') return roleLabel(chip.key, null, null) || (chip.key === 'step' ? 'шаг' : chip.key);
  return labelOf ? labelOf(chip.key) : chip.key;
}

// Чипы «Модели и роли» / связи разрезов: нейтральные рамки (макет .mdl); персона —
// аватар + имя.
export const CHIP: CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: SP.xs,
  fontSize: FS.xxs, padding: `1px ${SP.xs}px`,
  border: `1px solid ${C.border}`, color: C.textSecondary,
  borderRadius: R.sm, whiteSpace: 'nowrap',
};
export const CHIP_AV: CSSProperties = {
  width: 18, height: 18, borderRadius: R.full, display: 'inline-flex',
  alignItems: 'center', justifyContent: 'center',
  fontSize: FS.xxs, fontWeight: 700, color: C.onAccent, flexShrink: 0,
};

// Аватар персоны 22 и квадрат цвета группы 12 (макет .av / .sw).
export const AV: CSSProperties = {
  width: 22, height: 22, borderRadius: R.full, display: 'inline-flex',
  alignItems: 'center', justifyContent: 'center',
  fontSize: FS.xs, fontWeight: 700, color: C.onAccent, flexShrink: 0,
};
export const SW: CSSProperties = { width: 12, height: 12, borderRadius: R.sm, flexShrink: 0 };

// Живые чипы персоны (макет .chip): «работает» — accent на accentLight, «ждёт вас» — info.
export const LIVE: CSSProperties = {
  display: 'inline-flex', alignItems: 'center', gap: SP.xs, height: 20,
  padding: `0 ${SP.xs}px`, borderRadius: R.max, fontSize: FS.xxs, fontWeight: 600,
};
export function liveChipStyle(live: string): CSSProperties {
  return live === 'работает'
    ? { ...LIVE, background: C.accentLight, color: C.accent }
    : { ...LIVE, background: C.infoBg, color: C.info };
}
