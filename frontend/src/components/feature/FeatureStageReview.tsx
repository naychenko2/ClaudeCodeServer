// Блок «Ревью» экрана этапа (D1c): вердикты задач ревью, блокеры, вопрос человеку.
// Презентационный: вердикт, блокер и ожидание приходят с бэка, фронт их не вычисляет.

import { C, R, FS, SP } from '../../lib/design';
import { Badge, type BadgeTone } from '../ui/Badge';
import { SEC_LABEL } from './FeatureStageArtifacts';
import { formatDateTime } from './FeatureStatsBits';
import type { StageReview, StageReviewItem } from '../../types/featureStage';

const VERDICT: Record<string, { text: string; tone: BadgeTone }> = {
  accepted: { text: 'принято', tone: 'success' },
  accepted_with_remarks: { text: 'принято с замечаниями', tone: 'warning' },
  returned: { text: 'возвращено', tone: 'danger' },
  withdrawn: { text: 'снято', tone: 'neutral' },
};

function verdictBadge(v: string | null) {
  if (!v) return { text: 'вердикта нет', tone: 'neutral' as BadgeTone };
  return VERDICT[v] ?? { text: v, tone: 'neutral' as BadgeTone };
}

function Item({ item }: { item: StageReviewItem }) {
  const b = verdictBadge(item.verdict);
  return (
    <div data-testid={`stage-review-${item.taskId}`} style={{
      display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0,
      padding: SP.sm, background: C.bgCard, border: `1px solid ${item.blocker ? C.dangerBorder : C.borderLight}`, borderRadius: R.md,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <span style={{ flex: '1 1 160px', minWidth: 0, fontSize: FS.sm, color: C.textHeading, overflowWrap: 'anywhere' }}>{item.title}</span>
        {item.blocker && <Badge tone="danger" size="xs">блокер</Badge>}
        <Badge tone={b.tone} size="xs">{b.text}</Badge>
      </div>
      {item.returnReason && <div style={{ fontSize: FS.xs, color: C.textSecondary }}>Причина: {item.returnReason}</div>}
      {item.notes && <div style={{ fontSize: FS.sm, color: C.textPrimary, whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }}>{item.notes}</div>}
      {item.verifiedAt && <div style={{ fontSize: FS.xs, color: C.textMuted }}>{formatDateTime(item.verifiedAt)}</div>}
    </div>
  );
}

export function FeatureStageReview({ review }: { review: StageReview }) {
  const items = [...(review.own ? [review.own] : []), ...review.reviews];
  const aw = review.awaitingHuman;
  return (
    <section data-testid="stage-review" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0 }}>
      <div style={SEC_LABEL}>Ревью</div>
      {aw && (
        <div data-testid="stage-awaiting-human" style={{ fontSize: FS.sm, color: C.warningText }}>
          Ждёт ответа человека с {formatDateTime(aw.since)}{aw.reason ? `: ${aw.reason}` : ''}
        </div>
      )}
      {items.length === 0 ? (
        <div data-testid="stage-review-empty" style={{ fontSize: FS.sm, color: C.textSecondary }}>Ревью по этапу пока нет.</div>
      ) : (
        items.map(i => <Item key={i.taskId} item={i} />)
      )}
    </section>
  );
}
