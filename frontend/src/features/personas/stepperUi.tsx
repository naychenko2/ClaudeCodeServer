import type { ReactNode } from 'react';
import { C, FONT, R } from '../../lib/design';

// Общий степпер «① → ② → ③ → …» для инлайн-панелей добавления (привязки персоны,
// правила автоматизации). Пройденные шаги кликабельны для возврата назад.
export function Stepper({ step, steps, accent, onStep }: {
  step: number;
  steps: { n: number; label: string }[];
  accent: string;
  onStep: (s: number) => void;
}) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 12, fontSize: 12, flexWrap: 'wrap' }}>
      {steps.map((it, i) => {
        const state = it.n === step ? 'act' : it.n < step ? 'done' : 'todo';
        return (
          <span key={it.n} style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <button
              onClick={() => state === 'done' && onStep(it.n)}
              style={{
                display: 'flex', alignItems: 'center', gap: 6, border: 'none', background: 'none',
                padding: 0, fontSize: 12, fontFamily: FONT.sans,
                cursor: state === 'done' ? 'pointer' : 'default',
                color: state === 'act' ? C.textHeading : state === 'done' ? C.accent : C.textMuted,
                fontWeight: state === 'act' ? 600 : 400,
              }}
            >
              <span style={{
                width: 18, height: 18, borderRadius: R.full, display: 'flex', alignItems: 'center',
                justifyContent: 'center', fontSize: 10.5, fontWeight: 700,
                background: state === 'act' ? accent : state === 'done' ? C.accentLight : C.bgSelected,
                color: state === 'act' ? '#fff' : state === 'done' ? C.accent : C.textMuted,
              }}>{it.n}</span>
              {it.label}
            </button>
            {i < steps.length - 1 && <span style={{ width: 24, height: 1, background: C.borderLight }} />}
          </span>
        );
      })}
    </div>
  );
}

// Хлебная крошка возврата на предыдущий шаг (сводка уже выбранного)
export function Crumb({ children, onClick }: { children: ReactNode; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      onMouseEnter={e => { e.currentTarget.style.background = C.accentLight; e.currentTarget.style.color = C.textPrimary; }}
      onMouseLeave={e => { e.currentTarget.style.background = C.bgSelected; e.currentTarget.style.color = C.textSecondary; }}
      style={{
        display: 'inline-flex', alignItems: 'center', gap: 6, marginTop: 10, maxWidth: '100%',
        background: C.bgSelected, border: 'none', borderRadius: R.md, padding: '4px 10px',
        fontSize: 12, color: C.textSecondary, cursor: 'pointer', fontFamily: FONT.sans,
        overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
      }}
    >
      {children}
    </button>
  );
}
