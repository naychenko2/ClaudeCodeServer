import { C, R } from '../../../lib/design';
import type { FeatureRun } from '../../../types/featureRun';

// Выбор фичи, когда в чате их несколько: горизонтальный ряд чипов.
// Каждый чип — глифа состояния + имя фичи; выбранная подсвечена, у той, что
// показывает пилюля, — маркер. На мобиле ряд скроллится горизонтально.

// Глифа состояния фичи: маленький SVG-знак (12 px).
function StateGlyph({ state }: { state: FeatureRun['state'] }) {
  const common = { width: 12, height: 12, viewBox: '0 0 16 16', 'aria-hidden': true as const };
  switch (state) {
    case 'wait':
      return (
        <svg {...common}><path d="M8 1.5 14.5 8 8 14.5 1.5 8Z" fill={C.info} /></svg>
      );
    case 'done':
      return (
        <svg {...common}>
          <circle cx="8" cy="8" r="7" fill={C.success} />
          <path d="M4.8 8.2 7 10.3l4.3-4.6" fill="none" stroke={C.onAccent} strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      );
    case 'closed':
      return (
        <svg {...common}>
          <circle cx="8" cy="8" r="6.5" fill={C.bgSelected} stroke={C.textMuted} strokeWidth="1" />
          <path d="M5 8h6" stroke={C.textMuted} strokeWidth="1.6" strokeLinecap="round" />
        </svg>
      );
    default: // run
      return (
        <svg {...common}>
          <circle cx="8" cy="8" r="6.5" fill="none" stroke={C.accent} strokeWidth="1.5" />
          <circle cx="8" cy="8" r="3.5" fill={C.accent} />
        </svg>
      );
  }
}

export function FeaturePicker({ runs, selected, onSelect }: {
  runs: FeatureRun[];
  selected: number;
  onSelect: (index: number) => void;
}) {
  if (runs.length <= 1) return null;
  return (
    <div
      role="radiogroup"
      aria-label="Фича"
      style={{
        display: 'flex', gap: 4, overflowX: 'auto', scrollbarWidth: 'none',
        paddingBottom: 2, marginBottom: 2,
      }}
    >
      {runs.map((run, i) => {
        const on = i === selected;
        return (
          <button
            key={run.id}
            role="radio"
            aria-checked={on}
            title={run.branch ?? run.name}
            onClick={() => onSelect(i)}
            style={{
              display: 'inline-flex', alignItems: 'center', gap: 5, flexShrink: 0, maxWidth: 220,
              border: `1px solid ${on ? C.accentMuted : C.borderLight}`,
              background: on ? C.accentLight : C.bgCard,
              color: on ? C.textHeading : C.textSecondary,
              borderRadius: R.md, padding: '2px 8px', minHeight: 26, fontSize: 12,
              cursor: 'pointer', fontWeight: on ? 600 : 400,
            }}
          >
            <StateGlyph state={run.state} />
            <span style={{ whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0 }}>
              {run.name}
            </span>
          </button>
        );
      })}
    </div>
  );
}
