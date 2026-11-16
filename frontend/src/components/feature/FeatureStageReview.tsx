// Блок «Ревью» экрана этапа (D1c): вердикты задач ревью, блокеры, вопрос человеку.
// Презентационный: вердикт, блокер и ожидание приходят с бэка, фронт их не вычисляет.

import { CircleDashed } from 'lucide-react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { Badge, type BadgeTone } from '../ui/Badge';
import { EmptyState } from '../ui/EmptyState';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { MarkdownContent } from '../chat/MarkdownContent';
import { markdownToPlain } from '../../lib/markdownPlain';
import { SEC_LABEL } from './FeatureStageArtifacts';
import { formatDateTime } from './FeatureStatsBits';
import type { StageReview, StageReviewItem, StageReviewRound } from '../../types/featureStage';

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
      {/* Заметки — отчёт ревьюера (markdown: списки, **, `кавычки`): общий рендер, как в читалке плана. */}
      {/* overflowWrap — как на :35/:76/:81/:91: длинный путь file:line в заметке не должен выходить за карточку на 360. */}
      {item.notes && <div data-testid="stage-review-notes" style={{ fontSize: FS.sm, lineHeight: 1.5, color: C.textPrimary, minWidth: 0, overflowWrap: 'anywhere' }}><MarkdownContent text={item.notes} /></div>}
      {item.verifiedAt && <div style={{ fontSize: FS.xs, color: C.textMuted }}>{formatDateTime(item.verifiedAt)}</div>}
    </div>
  );
}

const REASON: Record<string, string> = {
  size: 'размер', 'side-effect': 'побочный эффект', contract: 'контракт',
  robustness: 'устойчивость', quality: 'качество', other: 'другое',
};

function roundWord(n: number) {
  const m = n % 100, d = n % 10;
  return m >= 11 && m <= 14 ? 'раундов' : d === 1 ? 'раунд' : d >= 2 && d <= 4 ? 'раунда' : 'раундов';
}

// Раунд сам решает, структурный ли он: режим «без подробностей» — по r.structured каждого
// раунда, а не по общему признаку блока (у блока он «все раунды структурные»).
function Round({ r, total }: { r: StageReviewRound; total: number }) {
  const legacy = !r.structured;
  const b = verdictBadge(r.verdict);
  const meta = [
    r.reviewer?.name,
    legacy ? `${total} ${roundWord(total)}` : `раунд ${r.round}`,
    r.at ? formatDateTime(r.at) : '',
    r.returnReason ? `причина «${REASON[r.returnReason] ?? r.returnReason}»` : '',
  ].filter(Boolean).join(' · ');
  return (
    <div data-testid={`stage-review-round-${r.round}`} style={{
      display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0,
      padding: SP.sm, background: C.bgCard, borderRadius: R.md,
      border: `1px solid ${r.blockers.length > 0 ? C.dangerBorder : C.borderLight}`,
    }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <Badge tone={b.tone} size="xs">{b.text}</Badge>
        <span style={{ flex: '1 1 160px', minWidth: 0, fontSize: FS.xs, color: C.textSecondary, overflowWrap: 'anywhere' }}>{meta}</span>
      </div>
      {!legacy && r.blockers.map((bl, i) => (
        <div key={i} data-testid="stage-review-blocker" style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm, minWidth: 0 }}>
          <Badge tone="danger" size="xs">блокер</Badge>
          <span style={{ flex: 1, minWidth: 0, fontSize: FS.sm, color: C.textPrimary, overflowWrap: 'anywhere' }}>
            {bl.file && <span style={{ fontFamily: FONT.mono, fontSize: FS.xs }}>{bl.file}{bl.line != null ? `:${bl.line}` : ''}{bl.text ? ' — ' : ''}</span>}
            {/* Строка вырезана из заметок (ReviewNotesParser): инлайновые ** и `кавычки` остаются в ней — снимаем плоской версией. */}
            {markdownToPlain(bl.text)}
          </span>
        </div>
      ))}
      {!legacy && r.remarks.map((t, i) => (
        <div key={i} data-testid="stage-review-remark" style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm, minWidth: 0 }}>
          <Badge tone="neutral" size="xs">замечание</Badge>
          <span style={{ flex: 1, minWidth: 0, fontSize: FS.sm, color: C.textPrimary, overflowWrap: 'anywhere' }}>{markdownToPlain(t)}</span>
        </div>
      ))}
    </div>
  );
}

function PendingRound({ r, executorName }: { r: StageReviewRound; executorName?: string | null }) {
  return (
    <div data-testid={`stage-review-round-${r.round}`} style={{
      display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', minWidth: 0,
      padding: SP.sm, background: C.bgCard, border: `1px dashed ${C.borderLight}`, borderRadius: R.md,
    }}>
      <Badge tone="neutral" size="xs">раунд {r.round}</Badge>
      <span style={{ flex: '1 1 160px', minWidth: 0, fontSize: FS.sm, color: C.textSecondary }}>
        ждёт доработку исполнителя{executorName ? ` (${executorName})` : ''}
      </span>
      <span data-testid="stage-review-auto" style={{ fontSize: FS.xs, color: C.textMuted }}>запустится сам</span>
    </div>
  );
}

export function FeatureStageReview({ review, executorName }: { review: StageReview; executorName?: string | null }) {
  const items = [...(review.own ? [review.own] : []), ...review.reviews];
  const rounds = review.reviewRounds?.rounds ?? [];
  // Подпись про старый формат — только когда сервер прямо сказал, что фича старая (hint='legacy').
  const legacyHint = review.reviewRounds?.hint === 'legacy';
  const done = rounds.filter(r => !r.pending);
  return (
    <section data-testid="stage-review" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0 }}>
      <div style={SEC_LABEL}>Ревью</div>
      {rounds.length > 0 ? (
        <>
          {rounds.map(r => r.pending
            ? <PendingRound key={r.round} r={r} executorName={executorName} />
            : <Round key={r.round} r={r} total={done.length} />)}
          {legacyHint && (
            <div data-testid="stage-review-legacy" style={{ fontSize: FS.xs, color: C.textMuted }}>
              Подробностей нет: ревью старой фичи не хранило блокеры.
            </div>
          )}
        </>
      ) : items.length > 0 ? (
        items.map(i => <Item key={i.taskId} item={i} />)
      ) : (
        <div data-testid="stage-review-empty">
          <EmptyState compact inline icon={<CircleDashed size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />}
            title="Ревью ещё не было"
            subtitle="Ревьюер подключится, когда исполнитель закроет задачу." />
        </div>
      )}
    </section>
  );
}
