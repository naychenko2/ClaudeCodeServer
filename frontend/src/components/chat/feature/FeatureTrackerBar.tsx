import type { CSSProperties } from 'react';
import { C, R } from '../../../lib/design';
import type { TruthBar } from './featureTruthModel';

// Полоса трекера — ОДИН рендер на все поверхности (В2 шапки, С1 плашки, П2 попапа,
// «По трекеру» раскрытия): высота 4, зазор 2, R.max (макет v3 .bar / v4 .bar).
// Ширины сегментов — flex по числам корзин (бэковых); нулевых корзин нет в модели —
// пустые части скрываются. Пустой бар (нет данных) — не рисуется.
// «в master» — НЕ отдельная корзина: подсветка начала принятой части (часть внутри
// «принято», как полоска «Готовности» страницы фичи). Реальное число
// ортогонального счётчика — в aria (ниже), не в ширине.
// Тон подсветки — штриховка successText/successBg (135°, 2px/4px): на полосе 4 px
// сплошной successText неотличим от success «принято» в обеих темах (дизайн-ревью F2,
// п. 1, bar-master-vs-plain.png), различие — узором, не оттенком; приём тот же, что
// штриховка «в работе» на странице фичи (FeatureBriefBlock).
const MASTER_HATCH = `repeating-linear-gradient(135deg, ${C.successText} 0 2px, ${C.successBg} 2px 4px)`;
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
        <span key={s.key} data-testid={`feature-bar-${s.key}`} style={{ display: 'flex', height: '100%', flex: s.count, background: s.color }}>
          {s.key === 'accepted' && bar.masterDraw > 0 && (
            <>
              {/* Доля, а не весь сегмент: один flex-ребёнок с flex:N при базисе 0 растягивается
                  на всю ширину (3 из 7 рисовалось как 7 из 7 — дизайн-ревью F2, п. 1). Второй
                  ребёнок (остаток принятой части, без своей заливки — сквозит фон сегмента)
                  задаёт пропорцию masterDraw : accepted−masterDraw. */}
              <i data-testid="feature-bar-master" style={{ display: 'block', height: '100%', flex: bar.masterDraw, background: MASTER_HATCH }} />
              {s.count > bar.masterDraw && <i data-testid="feature-bar-master-rest" style={{ display: 'block', height: '100%', flex: s.count - bar.masterDraw }} />}
            </>
          )}
        </span>
      ))}
    </div>
  );
}

// aria-подпись полосы: «Готовность 56 %: 4 в master, 5 принято, 2 в ревью…».
// «в master» — реальное число ортогонального счётчика бэка (может быть больше
// «принято» — это не ошибка, а правило бэка), поэтому вне списка корзин.
export function trackerBarAria(bar: TruthBar): string {
  const parts = bar.segments.map(s => `${s.count} ${s.label}`);
  if (bar.inMaster > 0) parts.unshift(`${bar.inMaster} в master`);
  return `Готовность ${bar.percent} %: ${parts.join(', ')}`;
}
