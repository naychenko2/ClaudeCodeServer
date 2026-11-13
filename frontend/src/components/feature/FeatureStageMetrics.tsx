// Блок «Метрики» экрана этапа (D1d; мини-разрез D0 §4/§5 docs/research/ai-fabrika-stage-metrics-cut-2026-11.md):
// время, ходы, токены/стоимость и ожидание человека раздельно по исполнителю, ревью и человеку.
// Фронт ничего не досчитывает — числа из `metrics` как есть, форматтеры те же, что у вкладки
// «Ревью» (formatDuration, formatTokens, formatUsd). Единственное исключение — тикающее
// «ждёт сейчас» от waitingNow.since (nowMs приходит пропсом, запросов блок не делает).
// Правило «null — словом, а не нулём»: нет расхода — «не учитывался», нет минут — «нет данных».
// metrics пуст (легаси-этап) — блок не рисуется. Раскладка: три карточки в сетке auto-fit —
// на 360 встают столбиком, на 1280 — рядом.

import { type CSSProperties } from 'react';
import { C, FS, SP, R, FONT } from '../../lib/design';
import { ReviewKv, type ReviewKvRow } from './FeatureReviewParts';
import { NO_DATA } from './FeatureLabels';
import { formatDuration, formatDateTime, minutesSince } from './FeatureStatsBits';
import { formatTokens, formatUsd, modelGroupLabel } from './FeatureStatsFormat';
import type { StageMetrics, StageMetricsShare, StageMetricsHuman } from '../../types/featureStats';

export interface FeatureStageMetricsProps {
  metrics: StageMetrics | null | undefined;
  // «Сейчас» для «ждёт сейчас»; по умолчанию Date.now() (в тестах задаётся).
  nowMs?: number;
}

const CARD: CSSProperties = {
  display: 'flex', flexDirection: 'column', gap: SP.xs, minWidth: 0,
  padding: SP.md, background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
};
const CARD_TITLE: CSSProperties = {
  fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700,
  letterSpacing: '0.04em', textTransform: 'uppercase', color: C.textMuted,
};
const CAPTION: CSSProperties = { fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted };

function minutesText(minutes: number | null, approx: boolean, live: boolean): string {
  if (minutes == null) return NO_DATA;
  return `${approx ? '≈ ' : ''}${formatDuration(minutes)}${live ? ' · идёт' : ''}`;
}

function shareRows(s: StageMetricsShare, approx: boolean): ReviewKvRow[] {
  const turns = s.turns == null ? NO_DATA : s.errorTurns ? `${s.turns} (упавших ${s.errorTurns})` : String(s.turns);
  const noSpend = 'не учитывался';
  return [
    { label: 'Время', value: minutesText(s.minutes, approx, s.live) },
    { label: 'Ходы', value: turns },
    { label: 'Вход', value: s.tokens ? formatTokens(s.tokens.input) : noSpend },
    { label: 'Выход', value: s.tokens ? formatTokens(s.tokens.output) : null },
    { label: 'Чтение кэша', value: s.tokens ? formatTokens(s.tokens.cacheRead) : null },
    { label: 'Запись кэша', value: s.tokens ? formatTokens(s.tokens.cacheCreation) : null },
    { label: 'Стоимость', value: s.costUsd == null ? null : formatUsd(s.costUsd) },
    {
      label: 'Модели без цены',
      value: s.hiddenCostModels.length > 0 ? s.hiddenCostModels.map(modelGroupLabel).join(', ') : null,
    },
    { label: 'Цена неполная', value: s.costPartial ? 'часть моделей без цены' : null },
    { label: 'Модели', value: s.models.length > 0 ? s.models.map(modelGroupLabel).join(', ') : null },
  ];
}

function humanRows(h: StageMetricsHuman, nowMs: number): ReviewKvRow[] {
  const waiting = h.waitingNow;
  const sinceMin = waiting ? minutesSince(waiting.since, nowMs) : null;
  return [
    { label: 'Ожидание', value: h.minutes == null ? NO_DATA : formatDuration(h.minutes) },
    { label: 'Ожиданий', value: h.waits == null ? null : String(h.waits) },
    { label: 'Ответов человека', value: h.replies == null ? 'не учитывался' : String(h.replies) },
    {
      label: 'Ждёт сейчас',
      value: waiting
        ? `${sinceMin != null ? formatDuration(sinceMin) : formatDateTime(waiting.since)}${waiting.reason ? ` · ${waiting.reason}` : ''}`
        : null,
    },
  ];
}

function Card({ id, title, rows }: { id: string; title: string; rows: ReviewKvRow[] }) {
  return (
    <div data-testid={`stage-metrics-${id}`} style={CARD}>
      <span style={CARD_TITLE}>{title}</span>
      <ReviewKv rows={rows} />
    </div>
  );
}

export function FeatureStageMetrics({ metrics, nowMs }: FeatureStageMetricsProps) {
  if (!metrics) return null;
  const approx = metrics.mode === 'approx';
  const now = nowMs ?? Date.now();

  return (
    <div data-testid="stage-metrics" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))', gap: SP.md }}>
        <Card id="executor" title="Исполнитель" rows={shareRows(metrics.executor, approx)} />
        {metrics.review && <Card id="review" title="Ревью" rows={shareRows(metrics.review, approx)} />}
        <Card id="human" title="Человек" rows={humanRows(metrics.human, now)} />
      </div>
      {metrics.idleMinutes != null && (
        <span data-testid="stage-metrics-idle" style={CAPTION}>
          Простой этапа: {formatDuration(metrics.idleMinutes)}
        </span>
      )}
      <span data-testid="stage-metrics-hint" style={CAPTION}>
        Время исполнителя — как полосы «Хронологии»; паузы дольше 2 мин — простой. «Ответов человека» — ходы, начатые вами.
        {approx ? ' Журнал неполный: время исполнителя приближённое, у ревью и человека — без данных.' : ''}
      </span>
    </div>
  );
}
