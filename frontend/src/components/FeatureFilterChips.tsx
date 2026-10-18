import { C, FONT, FS, SP } from '../lib/design';
import { Chip } from './ui/Chip';
import { FEATURE_FILTER_CHIPS, featureChipCounts } from '../lib/chatGroups';
import type { FeatureChip, FeatureIndexFeature } from '../types/featureIndex';

// Чипы-фильтры фич (макет features-overview.html, .chips/.fch): ряд toggle-чипов
// со счётчиками — Открытые · Ждут вас · Стоят · К мержу · Закрытые.
//
// Принадлежность фичи к чипу НЕ вычисляет фронт: бэк отдаёт готовый chips[]
// (контракт «Контракт индекса фич — утверждён (Александр, 18.10)»), фронт только
// считает числа по chips[] и отдаёт выбранный чип наверх (SessionList фильтрует).
//
// Раскладка: на десктопе чипы переносятся на несколько строк; на мобиле (360) —
// одна строка с горизонтальной прокруткой, высота 36 (тач-цель, TB.iconHitMobile
// округлён вниз под компактный ряд: 36 ≥ минимума досягаемости пальцем).
export function FeatureFilterChips({
  features, selected, onSelect, isMobile = false,
}: {
  features: FeatureIndexFeature[];
  selected: FeatureChip;
  onSelect: (chip: FeatureChip) => void;
  isMobile?: boolean;
}) {
  const counts = featureChipCounts(features);
  return (
    <div
      role="group"
      aria-label="Фильтр фич"
      style={{
        display: 'flex',
        alignItems: 'center',
        gap: SP.xs,
        // Только нижний отступ: горизонтальный задаёт контейнер списка (SP.sm),
        // свой горизонтальный padding дал бы двойной сдвиг ряда относительно групп
        padding: `0 0 ${SP.sm}px`,
        // Мобила: одна строка, горизонтальная прокрутка (скроллбар скрыт);
        // десктоп: перенос на строки, прокрутки нет
        flexWrap: isMobile ? 'nowrap' : 'wrap',
        overflowX: isMobile ? 'auto' : 'visible',
        overflowY: 'hidden',
        scrollbarWidth: 'none',
      }}
    >
      {FEATURE_FILTER_CHIPS.map(({ value, label }) => (
        <Chip
          key={value}
          variant="toggle"
          selected={selected === value}
          minHeight={isMobile ? 36 : undefined}
          onClick={() => onSelect(value)}
        >
          {label}
          <span style={{ fontFamily: FONT.mono, fontSize: FS.xs, opacity: 0.75, color: C.textMuted }}>
            {counts[value]}
          </span>
        </Chip>
      ))}
    </div>
  );
}
