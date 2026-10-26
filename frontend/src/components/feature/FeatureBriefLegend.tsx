// Легенда мини-полоски жизни фичи (макет feature-summary-1, «Легенда мини-полоски»).
// Подписи и цвета — только kindLabel()/kindBackground(); показываем лишь те виды, что
// реально есть на полоске, а «⫽ — длинная пауза сжата» — только при настоящем разрыве.
import { C, FONT, FS, SP } from '../../lib/design';
import { kindBackground, kindLabel, type SegmentKind } from './FeatureStatsBits';

const ORDER: readonly SegmentKind[] = ['work', 'waiting', 'idle', 'error', 'notWorking'];

interface Props {
  // Виды, присутствующие на полоске (порядок и дубли не важны).
  kinds: Iterable<SegmentKind>;
  // На полоске есть сжатый разрыв.
  hasBreak?: boolean;
}

export function FeatureBriefLegend({ kinds, hasBreak = false }: Props) {
  const present = new Set(kinds);
  const shown = ORDER.filter(k => present.has(k));
  if (shown.length === 0 && !hasBreak) return null;

  const item = { display: 'inline-flex', alignItems: 'center', gap: SP.xs } as const;
  return (
    <div
      data-testid="brief-legend"
      style={{
        display: 'flex', alignItems: 'center', flexWrap: 'wrap', columnGap: SP.md, rowGap: SP.xs,
        fontFamily: FONT.sans, fontSize: FS.xs, color: C.textSecondary,
      }}
    >
      {shown.map(k => (
        <span key={k} data-testid={`brief-legend-${k}`} style={item}>
          <span aria-hidden style={{ width: 10, height: 10, borderRadius: 2, flexShrink: 0, background: kindBackground(k) }} />
          {kindLabel(k)}
        </span>
      ))}
      {hasBreak && (
        <span data-testid="brief-legend-break" style={item}>⫽ — длинная пауза сжата</span>
      )}
    </div>
  );
}
