import type { CSSProperties } from 'react';
import { R } from '../../../lib/design';
import type { TruthBar } from './featureTruthModel';

// Полоса трекера — ОДИН рендер на все поверхности (В2 шапки, С1 плашки, П2 попапа,
// «По трекеру» раскрытия): высота 4, зазор 2, R.max (макет v3 .bar / v4 .bar).
// Ширины сегментов — flex по числам корзин (бэковых); нулевых корзин нет в модели —
// пустые части скрываются. Пустой бар (нет данных) — не рисуется.
export function FeatureTrackerBar({ bar, ariaLabel, style, testId }: {
  bar: TruthBar;
  ariaLabel?: string;
  style?: CSSProperties;
  testId?: string;
}) {
  if (bar.segments.length === 0) return null;
  return (
    <div
      data-testid={testId}
      role="img"
      aria-label={ariaLabel}
      style={{ display: 'flex', gap: 2, height: 4, borderRadius: R.max, overflow: 'hidden', flexShrink: 0, ...style }}
    >
      {bar.segments.map(s => (
        <span key={s.key} data-testid={`feature-bar-${s.key}`} style={{ display: 'block', height: '100%', flex: s.count, background: s.color }} />
      ))}
    </div>
  );
}

// aria-подпись полосы: «Готовность 56 %: 3 в master, 4 принято, 2 в ревью…».
export function trackerBarAria(bar: TruthBar): string {
  return `Готовность ${bar.percent} %: ${bar.segments.map(s => `${s.count} ${s.label}`).join(', ')}`;
}
