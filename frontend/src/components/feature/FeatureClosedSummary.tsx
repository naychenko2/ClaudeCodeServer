// «Итоги» закрытой фичи (FS-FI): строка итога и три плитки — первый экран закрытой
// фичи вместо «от тебя» и «Объём | Срок» (макет v2, состояние «Закрыта»).
// Только рендер: числа берём из run.summary как есть, ничего не пересчитываем.
// Четвёртой плитки «точность срока» нет — этой величины в контракте нет.

import { type CSSProperties } from 'react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { CLOSED_NO_MERGE_STATUS, formatDuration } from '../../lib/chatGroups';
import { Button } from '../ui/Button';
import type { FeatureSummary } from '../../types/featureRun';

export interface FeatureClosedSummaryProps {
  summary: FeatureSummary;
  // brief.initialEtaMinutes — первое обещание срока; нет — «обещали» не пишем.
  initialEtaMinutes?: number | null;
  // run.postMergeTails.length
  tailsCount: number;
  isMobile?: boolean;
  onOpenReview?: () => void;
}

const LINE: CSSProperties = {
  margin: 0, fontFamily: FONT.sans, fontSize: FS.lg, fontWeight: 600, color: C.textHeading,
};

function tileStyle(isMobile: boolean): CSSProperties {
  return {
    background: C.bgCard,
    border: `1px solid ${C.borderLight}`,
    borderRadius: R.lg,
    padding: isMobile ? SP.md : SP.lg,
  };
}

const BIG: CSSProperties = {
  fontFamily: FONT.serif, fontSize: FS.xl, fontWeight: 600, color: C.textHeading, lineHeight: 1.2,
};

const LABEL: CSSProperties = { fontFamily: FONT.sans, fontSize: FS.sm, color: C.textSecondary };

// Строка итога: «Смержена 10 ноября · шла 3 ч 20 мин, обещали ≈ 2 ч»;
// без даты мержа — «закрыта без мержа · шла …» (текст — CLOSED_NO_MERGE_STATUS).
function summaryLine(summary: FeatureSummary, initialEtaMinutes: number | null | undefined): string {
  const took = `шла ${formatDuration(summary.durationMinutes)}`;
  const promised = initialEtaMinutes != null ? `, обещали ≈ ${formatDuration(initialEtaMinutes)}` : '';
  const merged = summary.mergedAt ? new Date(summary.mergedAt) : null;
  if (!merged || Number.isNaN(merged.getTime())) {
    const status = CLOSED_NO_MERGE_STATUS;
    return `${status.charAt(0).toUpperCase()}${status.slice(1)} · ${took}${promised}`;
  }
  const date = merged.toLocaleDateString('ru-RU', { day: 'numeric', month: 'long' });
  return `Смержена ${date} · ${took}${promised}`;
}

export function FeatureClosedSummary({
  summary, initialEtaMinutes, tailsCount, isMobile = false, onOpenReview,
}: FeatureClosedSummaryProps) {
  const tiles = [
    { key: 'steps', label: 'шагов', value: summary.steps },
    { key: 'loops', label: 'возвратов', value: summary.loops },
    { key: 'tails', label: 'хвосты', value: tailsCount },
  ];

  return (
    <div data-testid="closed-summary" style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      <p data-testid="closed-summary-line" style={LINE}>{summaryLine(summary, initialEtaMinutes)}</p>
      <div
        data-testid="closed-summary-tiles"
        style={{
          display: 'grid',
          gridTemplateColumns: isMobile ? 'repeat(2, minmax(0, 1fr))' : `repeat(${tiles.length}, minmax(0, 1fr))`,
          gap: isMobile ? SP.sm : SP.md,
        }}
      >
        {tiles.map((t) => (
          <div key={t.key} data-testid={`closed-tile-${t.key}`} style={tileStyle(isMobile)}>
            <div style={BIG}>{t.value}</div>
            <div style={LABEL}>{t.label}</div>
          </div>
        ))}
      </div>
      {onOpenReview && (
        <div>
          <Button variant="ghost" size="sm" onClick={onOpenReview} dataTestId="closed-summary-review">
            Разбор ›
          </Button>
        </div>
      )}
    </div>
  );
}
