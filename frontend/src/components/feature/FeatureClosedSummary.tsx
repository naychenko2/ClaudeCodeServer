// «Итоги» закрытой фичи (FS-FI): строка итога и плитки — первый экран закрытой
// фичи вместо «от тебя» и «Объём | Срок» (макет v2, состояние «Закрыта»).
// Только рендер: числа берём из ответа как есть, ничего не пересчитываем.
// «Возвраты» — только настоящие, run.verdicts.returned (FP-H1): summary.loops —
// это returned + замечания, в герое под словом «возвраты» не показывается; нет
// verdicts (старый ответ) — плитки возвратов нет, не падаем. Замечаний в герое
// нет вовсе: они в полосе «Разбора» (FeatureReviewReturns). «Хвосты» названы
// так же, как в «Объёме» (FeatureReviewVolume), — «открыто после мержа» (FP-R3):
// «доработки» — имя followUp («Доработки: A из T»), не этой величины.
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
import type { FeatureSummary, FeatureVerdicts } from '../../types/featureRun';

export interface FeatureClosedSummaryProps {
  summary: FeatureSummary;
  // run.verdicts как есть (FP-H1): плитка «возвратов» строится только по verdicts.returned.
  // null / отсутствует (старый ответ) — плитки возвратов нет.
  verdicts?: FeatureVerdicts | null;
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
  summary, verdicts, initialEtaMinutes, tailsCount, showCloseHint, followUp, isMobile = false, onOpenReview,
}: FeatureClosedSummaryProps) {
  // Метки «шагов» и «возвратов» склоняются по числу (тот же приём, что «Все N шагов»
  // в FeatureProgressTab): «1 / шаг», «2 / возврата», «7 / шагов». Метка «открыто после
  // мержа» не склоняется (FP-R3): неизменяемая подпись, число стоит рядом, в самой плитке.
  const tiles = [
    { key: 'steps', label: plural(summary.steps, 'шаг', 'шага', 'шагов'), value: summary.steps },
  ];
  // «Возвраты» — только настоящие (FP-H1): verdicts.returned. summary.loops — returned +
  // замечания — под этим словом не показывается; замечаний в герое нет (они в «Разборе»).
  // Нет verdicts (старый ответ) — плитки нет, не падаем.
  if (verdicts?.returned != null) {
    tiles.push({ key: 'loops', label: plural(verdicts.returned, 'возврат', 'возврата', 'возвратов'), value: verdicts.returned });
  }
  // То же имя, что в «Объёме» (FeatureReviewVolume): «открыто после мержа», не «хвосты» и не
  // «доработки» (FP-R3): «доработки» здесь — только строка followUp («Доработки: A из T»).
  tiles.push({ key: 'tails', label: 'открыто после мержа', value: tailsCount });

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
