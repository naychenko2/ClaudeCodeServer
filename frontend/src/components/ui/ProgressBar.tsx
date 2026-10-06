import { useState, type CSSProperties } from 'react';
import { C, R } from '../../lib/design';

// muted — вторичный прогресс (полоса под карточкой инструмента в ленте): серая заливка не спорит
// с акцентом главного действия, а конец видимой дорожки по-прежнему говорит, сколько осталось
export type ProgressTone = 'accent' | 'muted' | 'success' | 'warning' | 'danger';

const FILL: Record<ProgressTone, string> = {
  accent: C.accent, muted: C.textMuted, success: C.success, warning: C.warning, danger: C.danger,
};

// Высота дорожки: default — 4px, thin — 3px (строка карточки в ленте, где 4px давят;
// 2px на тёмном фоне почти не читались)
export const PROGRESS_H = { default: 4, thin: 3 } as const;
const HEIGHT = PROGRESS_H;

// Тонкая полоса живёт в ленте на фоне карточки: дорожка — своим, более заметным токеном
// (C.progressTrack), а малая заливка (5%) не короче нескольких пикселей — иначе на узкой
// ленте её не видно вовсе
const TRACK = { default: C.track, thin: C.progressTrack } as const;
const MIN_FILL_PX = { default: 0, thin: 6 } as const;

// Период бегущего отрезка — тот же, что у .cc-progress-run в index.css
const RUN_PERIOD_MS = 1400;

// Прогноз — пунктир полным тоном по дорожке. Сплошной приглушённый accentSoft на C.track
// в светлой теме сливался с дорожкой: заливку было не видно вовсе. Штрихи полного тона
// видны в обеих темах, а разрывы отличают оценку от факта
const estimateFill = (color: string) =>
  `repeating-linear-gradient(90deg, ${color} 0 6px, transparent 6px 9px)`;

// Полоса прогресса на дорожке C.track. value — проценты 0..100.
// estimate — прогноз, а не факт: заливка пунктиром (estimateFill).
// transition — анимация ширины: частые тики таймера плавнее идут на linear.
// indeterminate — сколько осталось, неизвестно: по дорожке бежит отрезок, value игнорируется.
// Фаза отрезка привязана к часам, а не к моменту монтирования: несколько полос подряд в
// ленте бегут в такт, а не вразнобой.
// label — подпись для скринридера; у неопределённой полосы по умолчанию «Выполняется».
export function ProgressBar({ value, tone = 'accent', estimate, transition = 'width .3s ease', indeterminate, size = 'default', label, style }: {
  value: number;
  tone?: ProgressTone;
  estimate?: boolean;
  transition?: string;
  indeterminate?: boolean;
  size?: keyof typeof HEIGHT;
  label?: string;
  style?: CSSProperties;
}) {
  const [runDelay] = useState(() => -(Date.now() % RUN_PERIOD_MS));
  const pct = Math.max(0, Math.min(100, value));
  const track: CSSProperties = { display: 'block', height: HEIGHT[size], borderRadius: R.max, background: TRACK[size], overflow: 'hidden', ...style };
  if (indeterminate) return (
    <span role="progressbar" aria-busy="true" aria-label={label ?? 'Выполняется'} style={track}>
      <span className="cc-progress-run" style={{ display: 'block', height: '100%', width: '40%', borderRadius: R.max, background: FILL[tone], animationDelay: `${runDelay}ms` }} />
    </span>
  );
  return (
    <span
      role="progressbar"
      aria-valuenow={Math.round(pct)}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-label={label}
      style={track}
    >
      <span style={{
        display: 'block', height: '100%', width: `${pct}%`, borderRadius: R.max,
        minWidth: pct > 0 ? MIN_FILL_PX[size] : 0,
        background: estimate && (tone === 'accent' || tone === 'muted') ? estimateFill(FILL[tone]) : FILL[tone], transition,
      }} />
    </span>
  );
}
