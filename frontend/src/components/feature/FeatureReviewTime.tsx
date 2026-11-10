// Секция «Время и срок» вкладки «Разбор» (FS-FR1; записка docs/mockups/feature-page-simplify-v4-clickable.md
// §5 «1. Время и срок», в макете — секция rz-time): сверху плитки «Работали» / «Ждали вас»
// (FeatureStatsTiles с only), ниже — «Прошло» и «Ошибки» (ReviewKv), затем полоса состава
// времени с легендой. Все числа — из stats.timeline.totals и run.summary как есть; своё
// фронт считает только в одном месте — доли в % в легенде (сумма долей видимых видов = 100 ± 1).
// Шкалу срока (etaScale) делает FS-FR1b: приходит пропом и рисуется последней.

import { type CSSProperties, type ReactNode } from 'react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { plural } from '../../lib/plural';
import { FeatureStatsTiles } from './FeatureStatsTiles';
import { ReviewKv, type ReviewKvRow } from './FeatureReviewParts';
import { formatDuration, kindBackground } from './FeatureStatsBits';
import { COMP_LABEL, type CompositionKind } from './FeatureStatsPulseModel';
import type { FeatureRun } from '../../types/featureRun';
import type { FeatureStats, FeatureTimeline } from '../../types/featureStats';

export interface FeatureReviewTimeProps {
  run: FeatureRun;
  stats: FeatureStats;
  isMobile?: boolean;
  // Шкала срока (FS-FR1b): рисуется последней; здесь только слот под неё.
  etaScale?: ReactNode;
}

// Порядок видов на полосе — по записке §5: работа → ожидание → очередь → ошибки → простой.
const KINDS: CompositionKind[] = ['work', 'waiting', 'queued', 'error', 'idle'];

// Минуты вида из totals; null — бэк не считал (approx) или поля нет (старый ответ) —
// вида нет ни на полосе, ни в легенде. Своих полей не заводим.
function kindMinutes(totals: FeatureTimeline['totals'], kind: CompositionKind): number | null {
  switch (kind) {
    case 'work': return totals.workWallMinutes ?? null;
    case 'waiting': return totals.waitingMinutes ?? null;
    case 'queued': return totals.queuedMinutes ?? null;
    case 'error': return totals.errorMinutes ?? null;
    case 'idle': return totals.idleMinutes ?? null;
  }
}

// Полоса — геометрия полосы корзин FeatureVolumeCard (высота 8, зазор 2, flex по минутам),
// скругление pill (R.max): тонкая полоса читается цельной, ноль и null в полосу не входят.
const BAR: CSSProperties = { display: 'flex', gap: 2, height: 8, borderRadius: R.max, overflow: 'hidden' };

// Легенда (макет .legend; тот же приём, что у легенды «Готовности» FeatureBriefBlock.tsx:247):
// flexWrap — на 360 переносится, горизонтального переполнения нет; длительность — bold.
const LEGEND: CSSProperties = { display: 'flex', flexWrap: 'wrap', gap: `${SP.xs}px ${SP.md}px`, fontSize: FS.xs, color: C.textSecondary, fontFamily: FONT.sans };
const LEGEND_ITEM: CSSProperties = { display: 'inline-flex', alignItems: 'center', gap: SP.xs };
const LEGEND_VALUE: CSSProperties = { color: C.textHeading, fontWeight: 600 };

export function FeatureReviewTime({ run, stats, isMobile = false, etaScale }: FeatureReviewTimeProps) {
  const totals = stats.timeline.totals;

  // «Прошло»: у закрытой фичи — длительность итога (мерж/закрытие), иначе — стена от старта
  // до конца таймлайна. Признак закрытости — общий по странице (FeatureTimelineTab.tsx:513).
  const closed = run.closed === true || run.state === 'merged' || run.state === 'closed';
  const elapsed = closed ? run.summary?.durationMinutes ?? totals.elapsedMinutes : totals.elapsedMinutes;

  // «Ошибки» — «N мин · K эпизодов»: часть без значения в строку не входит; строкy нет —
  // value null, ReviewKv её не рендерит (как в «Подробнее»: нули не показываются).
  const errorParts: string[] = [];
  if (totals.errorMinutes != null && totals.errorMinutes > 0) errorParts.push(formatDuration(totals.errorMinutes));
  if (totals.errorCount != null && totals.errorCount > 0) {
    errorParts.push(`${totals.errorCount} ${plural(totals.errorCount, 'эпизод', 'эпизода', 'эпизодов')}`);
  }

  const kvRows: ReviewKvRow[] = [
    { label: 'Прошло', value: elapsed > 0 ? formatDuration(elapsed) : null },
    { label: 'Ошибки', value: errorParts.length > 0 ? errorParts.join(' · ') : null },
  ];

  // Состав: вид со значением null (approx) или 0 в полосу и легенду не попадает.
  const parts = KINDS
    .map((kind) => ({ kind, minutes: kindMinutes(totals, kind) }))
    .filter((p): p is { kind: CompositionKind; minutes: number } => p.minutes != null && p.minutes > 0);
  const sum = parts.reduce((a, p) => a + p.minutes, 0);

  return (
    <div data-testid="review-time" style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      <FeatureStatsTiles run={run} stats={stats} isMobile={isMobile} only={['work', 'waiting']} />
      <ReviewKv rows={kvRows} />
      {sum > 0 && (
        <>
          <div data-testid="review-time-bar" style={BAR}>
            {parts.map((p) => (
              <div
                key={p.kind}
                data-testid="review-time-seg"
                data-seg={p.kind}
                style={{ flex: `${p.minutes} 1 0`, minWidth: 0, background: kindBackground(p.kind) }}
              />
            ))}
          </div>
          <div data-testid="review-time-legend" style={LEGEND}>
            {parts.map((p) => (
              <span key={p.kind} data-testid="review-time-legend-item" data-seg={p.kind} style={LEGEND_ITEM}>
                <span aria-hidden style={{ width: 8, height: 8, borderRadius: R.max, background: kindBackground(p.kind), flexShrink: 0 }} />
                {COMP_LABEL[p.kind]} <b style={LEGEND_VALUE}>{formatDuration(p.minutes)}</b> · {Math.round((p.minutes / sum) * 100)} %
              </span>
            ))}
          </div>
        </>
      )}
      {etaScale}
    </div>
  );
}
