import { C, R, FS, SP, FONT } from '../../lib/design';
import type { FeatureRun } from '../../types/featureRun';
import { StatusTag, type FeatureRunTag } from './FeatureStatusTag';
import { epicMetaLine, stagesMetaLine, progressWidth } from './FeaturePageHeroModel';

// «Шапка в теле» страницы фичи (F09, макет feature-page P1–P8): тег состояния + мета
// «эпик {id} · дд.мм», заголовок serif 22, метастрока этапов и полоса прогресса.
// Метастрока и полоса — на всех вкладках (живут над ними); данные — run.meta (B02):
// нет meta или этапов 0 (легаси) — строки/полосы нет.
export function FeaturePageHero({ title, tag, run }: { title: string; tag: FeatureRunTag | null; run: FeatureRun | null }) {
  const meta = run?.meta;
  const epicLine = epicMetaLine(run?.epic?.id, run?.epic?.createdAt);
  const stagesLine = stagesMetaLine(meta);
  const pct = stagesLine ? progressWidth(meta) : null;
  return (
    <div data-testid="feature-page-hero" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, marginBottom: SP.md }}>
      {(tag || epicLine) && (
        <div style={{ display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: SP.sm }}>
          {tag && <StatusTag tag={tag} testId="feature-page-status" />}
          {epicLine && (
            <span data-testid="feature-page-epic-meta" style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textMuted }}>
              {epicLine}
            </span>
          )}
        </div>
      )}
      <h1 data-testid="feature-page-hero-title" style={{ margin: 0, fontFamily: FONT.serif, fontSize: 22, fontWeight: 700, lineHeight: 1.25, color: C.textHeading, overflowWrap: 'anywhere' }}>
        {title}
      </h1>
      {stagesLine && (
        <div data-testid="feature-page-stages-meta" style={{ fontSize: FS.sm, color: C.textSecondary }}>{stagesLine}</div>
      )}
      {pct != null && (
        <div
          data-testid="feature-page-progress"
          role="progressbar"
          aria-valuemin={0}
          aria-valuemax={100}
          aria-valuenow={pct}
          aria-label="Прогресс фичи"
          style={{ height: 4, borderRadius: R.sm, background: C.bgInset, overflow: 'hidden' }}
        >
          <div style={{ width: `${pct}%`, height: '100%', background: C.accent }} />
        </div>
      )}
    </div>
  );
}
