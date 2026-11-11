// Карточка «Исходы ревью» блока «Возвраты и потери» вкладки «Разбор» (FP-R1, вариант Б:
// docs/mockups/feature-review-returns-v1.md «Рекомендация — Б», макет feature-review-returns-v1.html
// .bar/.leg, кадры .cc-attachments/review-returns/b-*.png). Заголовок — доля «с первого раза»,
// полоса 10 px «сразу / с замечаниями / возвращены», легенда — кнопки-чипы с aria-pressed:
// нажатие подсвечивает свой сегмент (прочие — opacity 0.25), повторное снимает. Все числа —
// из run.verdicts как есть (счётчики задач-вердиктов); verdicts.loops и returnsBySource здесь
// НЕ используются: они и есть причина, по которой прежний блок путал «52 возврата» и «20»
// (разбор Майи, п. 4–5). Отозванные в полосу и в total не входят (как прежние roundMarks).
// Высота карточки постоянна при любом числе ревью: значков на виток нет.

import { type CSSProperties, useState } from 'react';
import { C, R, SP, FS, FONT, SHADOW } from '../../lib/design';
import { Button } from '../ui/Button';
import type { FeatureVerdicts } from '../../types/featureRun';

export type OutcomeSegmentKey = 'accepted' | 'remarks' | 'returned';

export interface OutcomeSegment { key: OutcomeSegmentKey; count: number; pct: number }
export interface OutcomeBarModel {
  total: number;
  firstPassPct: number;
  segments: OutcomeSegment[];
  withdrawn: number;
}

// Модель — только из verdicts как есть: total = accepted + acceptedWithRemarks + returned
// (отозванные — не исход ревью, в знаменатель не входят), pct — от total.
export function outcomeBarModel(v: FeatureVerdicts): OutcomeBarModel {
  const total = v.accepted + v.acceptedWithRemarks + v.returned;
  const pct = (n: number) => (total > 0 ? Math.round((n / total) * 100) : 0);
  return {
    total,
    firstPassPct: pct(v.accepted),
    segments: [
      { key: 'accepted', count: v.accepted, pct: pct(v.accepted) },
      { key: 'remarks', count: v.acceptedWithRemarks, pct: pct(v.acceptedWithRemarks) },
      { key: 'returned', count: v.returned, pct: pct(v.returned) },
    ],
    withdrawn: v.withdrawn,
  };
}

// Подписи и цвета сегментов — по макету (.seg.ok/.rem/.ret = C.success/.warning/.danger):
// «замечание» и «возврат» разведены цветом и словом (чинит п. 4 разбора, не трогая бэк).
const SEG_META: Record<OutcomeSegmentKey, { label: string; color: string }> = {
  accepted: { label: 'Сразу', color: C.success },
  remarks: { label: 'С замечаниями', color: C.warning },
  returned: { label: 'Возвращены', color: C.danger },
};

// Карточка — геометрия FeatureReviewEtaScale (bgCard/borderLight/SHADOW.card/R.lg, поля SP.lg,
// на мобиле SP.md).
const CARD: CSSProperties = {
  background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg, boxShadow: SHADOW.card,
  padding: SP.lg, display: 'flex', flexDirection: 'column', gap: SP.sm, boxSizing: 'border-box',
};
const CARD_MOBILE: CSSProperties = { padding: SP.md };

// Шапка: «{pct} % ревью — с первого раза» (макет .bar-h .line-big: serif, FS.lg) и справа
// «отозвано N» (макет .t-hint), только когда отозванные есть.
const HEAD: CSSProperties = { display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: SP.md, flexWrap: 'wrap' };
const TITLE: CSSProperties = { fontFamily: FONT.serif, fontSize: FS.lg, fontWeight: 700, lineHeight: 1.3, color: C.textHeading, margin: 0 };
const HINT: CSSProperties = { fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted };

// Полоса 10 px на C.progressTrack (макет .bar); сегменты — flex по числу, нулевые не рисуются.
const BAR: CSSProperties = { display: 'flex', height: 10, borderRadius: R.max, overflow: 'hidden', background: C.progressTrack, gap: SP.xxs };
const SEG: CSSProperties = { height: '100%', transition: 'opacity 0.15s' };

// Легенда (макет .leg): ряд pill-кнопок min-height 32; на 360 — столбик во всю ширину,
// min-height 40, процент прижат вправо (макет .m .leg, .leg .pc { margin-left:auto }).
const LEG: CSSProperties = { display: 'flex', flexWrap: 'wrap', gap: `${SP.xs}px ${SP.sm}px` };
const LEG_MOBILE: CSSProperties = { flexDirection: 'column' };
const CHIP: CSSProperties = {
  justifyContent: 'flex-start', gap: SP.xs, padding: `0 ${SP.sm}px`, borderRadius: R.max, minHeight: 32,
  background: C.bgWhite, border: `1px solid ${C.borderLight}`, color: C.textPrimary, fontWeight: 400,
};
const CHIP_SELECTED: CSSProperties = { background: C.bgSelected, border: `1px solid ${C.textSecondary}` };
const CHIP_MOBILE: CSSProperties = { width: '100%', minHeight: 40 };
const DOT: CSSProperties = { width: 8, height: 8, borderRadius: R.max, flexShrink: 0 };
const COUNT: CSSProperties = { color: C.textHeading, fontWeight: 600, fontVariantNumeric: 'tabular-nums' };
const PCT: CSSProperties = { marginLeft: 'auto', paddingLeft: SP.sm, color: C.textMuted, fontVariantNumeric: 'tabular-nums' };

const EMPTY: CSSProperties = { fontFamily: FONT.sans, fontSize: FS.sm, color: C.textMuted };

// Наведение/фокус легенды — по образцу FeatureStatsLosses (инжект CSS-правила): hover —
// C.bgSelected с рамкой C.border, фокус с клавиатуры — кольцо SHADOW.focus. Цепляем по
// data-testid: Button не принимает className и произвольные атрибуты, а dataTestId — часть
// его контракта (тесты находят чипы по тем же id).
if (typeof document !== 'undefined' && !document.getElementById('cc-outcome-chip-style')) {
  const el = document.createElement('style');
  el.id = 'cc-outcome-chip-style';
  el.textContent = `[data-testid^="returns-chip-"]:hover{background:${C.bgSelected};border-color:${C.border};}`
    + `[data-testid^="returns-chip-"]:focus-visible{outline:none;box-shadow:${SHADOW.focus};}`;
  document.head.appendChild(el);
}

export function FeatureReturnsOutcomeBar({ verdicts, isMobile = false }: { verdicts: FeatureVerdicts; isMobile?: boolean }) {
  const m = outcomeBarModel(verdicts);
  const [focusKey, setFocusKey] = useState<OutcomeSegmentKey | null>(null);

  // 0 ревью — карточки нет, только строка (состояние «Б» из записки).
  if (m.total === 0) {
    return <div data-testid="returns-empty" style={EMPTY}>Ревью ещё не было</div>;
  }

  const allFirst = m.segments.every((s) => s.key === 'accepted' || s.count === 0);
  const title = allFirst ? `Все ${m.total} ревью — с первого раза` : `${m.firstPassPct} % ревью — с первого раза`;
  const shown = m.segments.filter((s) => s.count > 0);

  return (
    <div data-testid="returns-outcome" style={isMobile ? { ...CARD, ...CARD_MOBILE } : CARD}>
      <div style={HEAD}>
        <p data-testid="returns-title" style={TITLE}>{title}</p>
        {m.withdrawn > 0 ? <span data-testid="returns-withdrawn" style={HINT}>{`отозвано ${m.withdrawn}`}</span> : null}
      </div>
      <div
        data-testid="returns-bar"
        role="img"
        aria-label={`Исходы ревью: сразу ${m.segments[0].count}, с замечаниями ${m.segments[1].count}, возвращены ${m.segments[2].count} из ${m.total}`}
        style={BAR}
      >
        {shown.map((s) => (
          <span
            key={s.key}
            data-testid="returns-seg"
            data-seg={s.key}
            title={`${SEG_META[s.key].label}: ${s.count} из ${m.total}`}
            style={{ ...SEG, flex: s.count, background: SEG_META[s.key].color, opacity: focusKey && focusKey !== s.key ? 0.25 : 1 }}
          />
        ))}
      </div>
      <div style={isMobile ? { ...LEG, ...LEG_MOBILE } : LEG}>
        {m.segments.map((s) => {
          const pressed = focusKey === s.key;
          return (
            <Button
              key={s.key}
              variant="ghost"
              size="sm"
              dataTestId={`returns-chip-${s.key}`}
              ariaPressed={pressed}
              title={`Подсветить долю «${SEG_META[s.key].label}»`}
              onClick={() => setFocusKey(pressed ? null : s.key)}
              style={{ ...CHIP, ...(pressed ? CHIP_SELECTED : null), ...(isMobile ? CHIP_MOBILE : null) }}
            >
              <span data-testid="returns-dot" style={{ ...DOT, background: SEG_META[s.key].color }} />
              <span>{SEG_META[s.key].label}</span>
              <span data-testid="returns-count" style={COUNT}>{s.count}</span>
              <span data-testid="returns-pct" style={PCT}>{`${s.pct} %`}</span>
            </Button>
          );
        })}
      </div>
    </div>
  );
}
