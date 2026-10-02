import { Minus, Plus } from 'lucide-react';
import { C, FS, R, SP } from '../../lib/design';
import { IconButton } from './IconButton';
import { ICON_SIZE, ICON_STROKE } from './icons';

const TOUCH_BOX = { width: 44, height: 44 } as const;

// Счётчик «− N +» (сколько вариантов сгенерировать). Кнопки — IconButton xs.
// У «+» на потолке объясняем ПРИЧИНУ (maxHint: «Эта операция даёт один вариант»):
// просто погасшая кнопка выглядит поломкой. Подсказка висит на обёртке, потому что
// disabled-кнопка не получает событий мыши и свой title показывает не везде.
export function Stepper({ value, min = 1, max, onChange, maxHint, ariaLabel, touch }: {
  value: number;
  min?: number;
  max: number;
  onChange: (v: number) => void;
  maxHint?: string;
  ariaLabel?: string;
  // Тач-версия: кнопки 44×44 вместо 24×24
  touch?: boolean;
}) {
  const atMin = value <= min;
  const atMax = value >= max;
  const set = (v: number) => onChange(Math.max(min, Math.min(max, v)));

  return (
    <span
      role="group"
      aria-label={ariaLabel}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: SP.xxs, padding: SP.xxs / 2,
        border: `1px solid ${C.border}`, borderRadius: R.md, background: C.bgWhite,
      }}
    >
      <IconButton size="xs" title="Меньше" disabled={atMin} onClick={() => set(value - 1)} style={touch ? TOUCH_BOX : undefined}>
        <Minus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
      </IconButton>
      <span aria-live="polite" style={{
        minWidth: SP.lg, textAlign: 'center', fontSize: FS.sm, fontWeight: 600,
        color: C.textHeading, fontVariantNumeric: 'tabular-nums',
      }}>
        {value}
      </span>
      <span title={atMax ? maxHint : undefined} style={{ display: 'inline-flex' }}>
        <IconButton
          size="xs"
          // На потолке нативную подсказку даёт обёртка, кнопке — только имя с причиной
          {...(atMax && maxHint ? { ariaLabel: `Больше: ${maxHint}` } : { title: 'Больше' })}
          disabled={atMax}
          onClick={() => set(value + 1)}
          style={touch ? TOUCH_BOX : undefined}
        >
          <Plus size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} />
        </IconButton>
      </span>
    </span>
  );
}
