import { C, R } from '../../lib/design';

// Необязательные свойства отдельного сегмента — общие для SegmentedControl,
// InlineSegmented и IconSegmented:
// - disabled — сегмент нельзя выбрать (кнопка выключена);
// - muted — приглушён, но кликабелен: выбор пока бессмыслен, а клик ведёт дальше
//   (например, «Обработка» без выбранного звука открывает меню «Что обработать?»);
// - title — своя подсказка у сегмента.
export interface SegmentOptionState {
  disabled?: boolean;
  muted?: boolean;
  title?: string;
}

// Приглушённый сегмент живой: текст того же цвета, что у обычного невыбранного (без
// opacity — иначе контраст падает ниже 3:1 и он неотличим от выключенного), а
// «вторичность» читается пунктирной рамкой. C.textMuted как цвет текста не годится:
// в светлой теме на фонах сегментов он даёт 2,06–2,58:1. Рамка — border, а не
// outline: outline перебил бы кольцо фокуса с клавиатуры
export const SEG_MUTED_BORDER = `1px dashed ${C.textMuted}`;

interface SegmentedControlProps<T extends string> {
  value: T;
  options: ({ value: T; label: string } & SegmentOptionState)[];
  onChange: (v: T) => void;
  columns?: number;   // сколько кнопок в ряд (по умолчанию — все в один ряд)
  touch?: boolean;    // тач-раскладка: сегмент не ниже 40
}

// === Сегмент-выбор кнопками (режим, модель): активный сегмент — accent ===
export function SegmentedControl<T extends string>({ value, options, onChange, columns, touch }: SegmentedControlProps<T>) {
  const cols = columns ?? options.length;
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8 }}>
      {options.map((o) => {
        const active = o.value === value;
        return (
          <button
            key={o.value}
            onClick={() => onChange(o.value)}
            disabled={o.disabled}
            title={o.title}
            style={{
              flex: `1 1 calc(${(100 / cols).toFixed(4)}% - 8px)`,
              padding: '9px 4px', borderRadius: R.lg, border: 'none', cursor: 'pointer',
              fontSize: 13, fontWeight: 600, fontFamily: 'inherit',
              background: active ? C.accent : C.bgPanel,
              color: active ? C.onAccent : C.textSecondary,
              transition: 'background 0.15s, color 0.15s',
              ...(touch ? { minHeight: 40 } : null),
              // Рамка съедает по пикселю — поля меньше на столько же, размер сегмента прежний
              ...(o.disabled ? { cursor: 'not-allowed', opacity: 0.45 } : o.muted && !active ? { border: SEG_MUTED_BORDER, padding: '8px 3px' } : null),
            }}
          >
            {o.label}
          </button>
        );
      })}
    </div>
  );
}
