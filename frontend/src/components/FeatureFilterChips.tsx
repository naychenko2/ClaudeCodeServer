import { C, FONT, FS, R, SP } from '../lib/design';
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
          <button
            key={value}
            type="button"
            onClick={() => onSelect(value)}
            aria-pressed={isSel}
            style={{
              // m1: фильтр-чип по макету .fch: выбранный — accentLight + рамка accentMuted
              // + текст textHeading 600; невыбранный — bgCard + borderLight + textSecondary 400;
              // радиус R.max (999, полная пилюля). Не variant="toggle" Chip'а: тот заливает
              // C.accent и спорит с кнопкой «+ Чат» в шапке.
              display: 'inline-flex', alignItems: 'center', gap: 4,
              padding: '0 9px',
              borderRadius: R.max,
              border: `1px solid ${isSel ? C.accentMuted : C.borderLight}`,
              background: isSel ? C.accentLight : C.bgCard,
              color: isSel ? C.textHeading : C.textSecondary,
              fontSize: FS.sm, fontWeight: isSel ? 600 : 400, fontFamily: FONT.sans,
              cursor: 'pointer', whiteSpace: 'nowrap', flexShrink: 0,
              // Макет .fch: 26px на десктопе, 36px (тач-цель) на мобиле
              minHeight: isMobile ? 36 : 26, boxSizing: 'border-box',
            }}
          >
            {/* Подпись без переноса: на 360 чип «Ждут вас»/«К мержу» не должен
                распадаться на две строки (высота 36, одна строка с прокруткой) */}
            <span style={{ whiteSpace: 'nowrap' }}>{label}</span>
            {/* Число: mono 11, приглушённый textMuted (макет .fch .cnt). «Закрытые» —
                из нарисованных групп (n3), если задано, иначе все закрытые фичи */}
            <span style={{
              fontFamily: FONT.mono, fontSize: FS.xs,
              color: C.textMuted, opacity: 0.75,
            }}>
              {value === 'closed' ? (closedCount ?? counts.closed) : counts[value]}
            </span>
          </button>
        );
      })}
    </div>
  );
}
