// «Итоги» закрытой фичи (FS-FI): строка итога и три плитки — первый экран закрытой
// фичи вместо «от тебя» и «Объём | Срок» (макет v2, состояние «Закрыта»).
// Только рендер: числа берём из run.summary как есть, ничего не пересчитываем.
// Четвёртой плитки «точность срока» нет — этой величины в контракте нет.
// Подсказка «Закройте фичу, когда доработки не нужны» — только у merged (FS-AC3b):
// фича остаётся «смержена», пока человек сам не нажмёт «Закрыть» (FeaturePageHeader).
// Состояние приходит из родителя через showCloseHint (FeatureFirstScreen знает brief.kind):
// у closed подсказки нет, в том числе у закрытой после мержа — у неё mergedAt валиден.

import { type CSSProperties } from 'react';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { CLOSED_NO_MERGE_STATUS, formatDuration } from '../../lib/chatGroups';
import { plural } from '../../lib/plural';
import { Button } from '../ui/Button';
import type { FeatureSummary } from '../../types/featureRun';

export interface FeatureClosedSummaryProps {
  summary: FeatureSummary;
  // brief.initialEtaMinutes — первое обещание срока; нет — «обещали» не пишем.
  initialEtaMinutes?: number | null;
  // run.postMergeTails.length
  tailsCount: number;
  // Подсказка «Закройте…» только у merged (ревью Глеба FS-AC3b): у closed-after-merge
  // mergedAt остаётся валидным — состояние передаёт родитель (brief.kind), не дата.
  showCloseHint: boolean;
  // Доработки после мержа (run.stepProgress.followUpTotal/followUpAccepted, FS-PB2):
  // отдельная строка под итогом; сам итог и плитки не меняются. null / total=0 — строки нет.
  followUp?: { total: number; accepted: number } | null;
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

// Малые приглушённые строки под итогом: «Доработки: 1 из 3» (FS-FVp; числа без
// склонений) и подсказка «Закройте…» (FS-AC3b).
const SUBTLE: CSSProperties = { margin: 0, fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted };

// Дата мержа: валидная mergedAt — есть, null/мусор — нет. Наличие подсказки
// «Закройте…» решает состояние из родителя (showCloseHint), а не эта дата.
function mergedDate(summary: FeatureSummary): Date | null {
  const merged = summary.mergedAt ? new Date(summary.mergedAt) : null;
  return merged && !Number.isNaN(merged.getTime()) ? merged : null;
}

// Строка итога: «Смержена 10 ноября · шла 3 ч 20 мин, обещали ≈ 2 ч»;
// без даты мержа — «закрыта без мержа · шла …» (текст — CLOSED_NO_MERGE_STATUS).
function summaryLine(summary: FeatureSummary, initialEtaMinutes: number | null | undefined): string {
  const took = `шла ${formatDuration(summary.durationMinutes)}`;
  const promised = initialEtaMinutes != null ? `, обещали ≈ ${formatDuration(initialEtaMinutes)}` : '';
  const merged = mergedDate(summary);
  if (!merged) {
    const status = CLOSED_NO_MERGE_STATUS;
    return `${status.charAt(0).toUpperCase()}${status.slice(1)} · ${took}${promised}`;
  }
  const date = merged.toLocaleDateString('ru-RU', { day: 'numeric', month: 'long' });
  return `Смержена ${date} · ${took}${promised}`;
}

export function FeatureClosedSummary({
  summary, initialEtaMinutes, tailsCount, showCloseHint, followUp, isMobile = false, onOpenReview,
}: FeatureClosedSummaryProps) {
  // Метки плиток склоняются по числу (тот же приём, что «Все N шагов» в FeatureProgressTab):
  // «1 / шаг», «2 / возврата», «7 / шагов»; 0 — родительный множественный («0 хвостов»).
  const tiles = [
    { key: 'steps', label: plural(summary.steps, 'шаг', 'шага', 'шагов'), value: summary.steps },
    { key: 'loops', label: plural(summary.loops, 'возврат', 'возврата', 'возвратов'), value: summary.loops },
    { key: 'tails', label: plural(tailsCount, 'хвост', 'хвоста', 'хвостов'), value: tailsCount },
  ];

  return (
    <div data-testid="closed-summary" style={{ display: 'flex', flexDirection: 'column', gap: SP.md }}>
      <p data-testid="closed-summary-line" style={LINE}>{summaryLine(summary, initialEtaMinutes)}</p>
      {showCloseHint && mergedDate(summary) && (
        <p data-testid="closed-summary-close-hint" style={SUBTLE}>Закройте фичу, когда доработки не нужны</p>
      )}
      {followUp && followUp.total > 0 && (
        <p data-testid="closed-summary-followup" style={SUBTLE}>Доработки: {followUp.accepted} из {followUp.total}</p>
      )}
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
