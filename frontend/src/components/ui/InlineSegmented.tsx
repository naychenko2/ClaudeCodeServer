import type { ReactNode } from 'react';
import { C, FS, SP, TB } from '../../lib/design';

// === Компактный inline-сегмент (режимы в строках списков) ===
// Отличия от PillSwitch: без скользящей пилюли и drag (строка и так плотная),
// у каждого сегмента может быть свой тон активного состояния (авто — акцент,
// всегда — info, выкл — muted), value=null — «ничего не выбрано» (состояние
// «дефолт, привязки нет»). Тач-цели — по TB (mobile 40 / desktop 32).
// icon — необязательная иконка слева от подписи; используется сегментами
// «Текстом / Схемой» в PlanSection/PlanReviewView (нужен был свой локальный
// SegmentedToggle с точно такой же геометрией — единая точка закрывает оба).
export function InlineSegmented<T extends string>({ value, options, onChange, disabled, isMobile }: {
  value: T | null;
  options: { value: T; label: string; tone?: { bg: string; fg: string }; icon?: ReactNode }[];
  onChange: (v: T) => void;
  disabled?: boolean;
  isMobile?: boolean;
}) {
  return (
    <div style={{
      display: 'flex', gap: SP.xxs, background: TB.pillTrack,
      borderRadius: TB.pillRadius + 1, padding: SP.xxs, flexShrink: 0,
      opacity: disabled ? 0.55 : 1, cursor: disabled ? 'wait' : undefined,
    }}>
      {options.map(o => {
        const active = o.value === value;
        const tone = active ? (o.tone ?? { bg: C.accentLight, fg: C.accent }) : null;
        return (
          <button
            key={o.value}
            disabled={disabled}
            aria-pressed={active}
            onClick={() => onChange(o.value)}
            style={{
              display: 'inline-flex', alignItems: 'center', gap: 4,
              border: 'none', borderRadius: TB.pillRadius - 2,
              cursor: disabled ? 'wait' : 'pointer',
              fontFamily: 'inherit', fontSize: FS.xs, fontWeight: 600,
              padding: `${SP.xs}px ${SP.sm}px`,
              minHeight: isMobile ? TB.iconHitMobile : TB.iconHitDesktop,
              background: tone ? tone.bg : 'transparent',
              color: tone ? tone.fg : C.textMuted,
              transition: 'background 0.12s, color 0.12s',
            }}
          >
            {o.icon}
            {o.label}
          </button>
        );
      })}
    </div>
  );
}
