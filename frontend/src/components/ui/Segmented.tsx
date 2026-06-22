import { C, R } from '../../lib/design';

interface SegmentedControlProps<T extends string> {
  value: T;
  options: { value: T; label: string }[];
  onChange: (v: T) => void;
  columns?: number;   // сколько кнопок в ряд (по умолчанию — все в один ряд)
}

// === Сегмент-выбор кнопками (режим, модель): активный сегмент — accent ===
export function SegmentedControl<T extends string>({ value, options, onChange, columns }: SegmentedControlProps<T>) {
  const cols = columns ?? options.length;
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
      {options.map((o) => {
        const active = o.value === value;
        return (
          <button
            key={o.value}
            onClick={() => onChange(o.value)}
            style={{
              flex: `1 1 calc(${(100 / cols).toFixed(4)}% - 8px)`,
              padding: '9px 4px', borderRadius: R.lg, border: 'none', cursor: 'pointer',
              fontSize: 13, fontWeight: 600, fontFamily: 'inherit',
              background: active ? C.accent : C.bgPanel,
              color: active ? C.onAccent : C.textSecondary,
              transition: 'background 0.15s, color 0.15s',
            }}
          >
            {o.label}
          </button>
        );
      })}
    </div>
  );
}
