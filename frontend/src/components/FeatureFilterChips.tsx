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
  features, selected, onSelect, isMobile = false, closedCount,
}: {
  features: FeatureIndexFeature[];
  selected: FeatureChip;
  onSelect: (chip: FeatureChip) => void;
  isMobile?: boolean;
  // Число «Закрытые» — из нарисованных групп (n3): единый источник с заголовком
  // секции. Не задано — все закрытые фичи индекса (featureChipCounts)
  closedCount?: number;
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
      {FEATURE_FILTER_CHIPS.map(({ value, label }) => {
        const isSel = selected === value;
        return (
          <Chip
            key={value}
            variant="toggle"
            selected={isSel}
            // Макет .fch: 26px на десктопе, 36px (тач-цель) на мобиле
            minHeight={isMobile ? 36 : 26}
            onClick={() => onSelect(value)}
          >
            {/* Подпись без переноса: на 360 чип «Ждут вас»/«К мержу» не должен
                распадаться на две строки (высота 36, одна строка с прокруткой) */}
            <span style={{ whiteSpace: 'nowrap' }}>{label}</span>
            {/* Число: на заливке выбранного чипа (C.accent) — onAccent (читается);
                не выбран — приглушённый textMuted (макет .fch .cnt). «Закрытые» —
                из нарисованных групп (n3), если задано, иначе все закрытые фичи */}
            <span style={{
              fontFamily: FONT.mono, fontSize: FS.xs,
              color: isSel ? C.onAccent : C.textMuted,
              opacity: isSel ? 0.9 : 0.75,
            }}>
              {value === 'closed' ? (closedCount ?? counts.closed) : counts[value]}
            </span>
          </Chip>
        );
      })}
    </div>
  );
}
