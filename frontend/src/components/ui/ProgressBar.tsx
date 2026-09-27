import type { CSSProperties } from 'react';
import { C, R } from '../../lib/design';

export type ProgressTone = 'accent' | 'success' | 'warning' | 'danger';

const FILL: Record<ProgressTone, string> = {
  accent: C.accent, success: C.success, warning: C.warning, danger: C.danger,
};

// Тонкая полоса прогресса 4px на дорожке C.track. value — проценты 0..100.
// estimate — прогноз, а не факт: заливка приглушённым accentSoft.
// transition — анимация ширины: частые тики таймера плавнее идут на linear.
export function ProgressBar({ value, tone = 'accent', estimate, transition = 'width .3s ease', style }: {
  value: number;
  tone?: ProgressTone;
  estimate?: boolean;
  transition?: string;
  style?: CSSProperties;
}) {
  const pct = Math.max(0, Math.min(100, value));
  return (
    <span
      role="progressbar"
      aria-valuenow={Math.round(pct)}
      aria-valuemin={0}
      aria-valuemax={100}
      style={{ display: 'block', height: 4, borderRadius: R.max, background: C.track, overflow: 'hidden', ...style }}
    >
      <span style={{
        display: 'block', height: '100%', width: `${pct}%`, borderRadius: R.max,
        background: estimate && tone === 'accent' ? C.accentSoft : FILL[tone], transition,
      }} />
    </span>
  );
}
