// Блок «Метрики» экрана этапа (D1d; мини-разрез D0 §4/§5 docs/research/ai-fabrika-stage-metrics-cut-2026-11.md):
// время, ходы, токены/стоимость и ожидание человека раздельно по исполнителю, ревью и человеку.
// Фронт ничего не досчитывает — числа из `metrics` как есть, форматтеры те же, что у вкладки
// «Ревью» (formatDuration, formatTokens, formatUsd). Единственное исключение — тикающее
// «ждёт сейчас» от waitingNow.since (nowMs приходит пропсом, запросов блок не делает).
// Правило «null — словом, а не нулём»: нет расхода — «не учитывался», нет минут — «нет данных».
// metrics пуст — блок не рисуется. F16: сверху полоса времени из трёх сегментов и таблица
// «Кто × Время/Ходы/Токены/Ожидание» с «Итого»; прежние три карточки (цена, модели, кэш) — в
// раскрытии под таблицей. Мало замеров — пустое состояние, легаси (approx) — Notice и «Этап целиком».

import { type CSSProperties, type ReactNode } from 'react';
import { Info } from 'lucide-react';
import { Notice } from '../ui/Notice';
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

const DASH = '—';
const TH: CSSProperties = { textAlign: 'left', padding: `${SP.xs}px ${SP.sm}px`, ...CARD_TITLE };
const TD: CSSProperties = { padding: `${SP.xs}px ${SP.sm}px`, fontFamily: FONT.sans, fontSize: FS.sm, color: C.textPrimary, borderTop: `1px solid ${C.borderLight}` };
const NUM: CSSProperties = { textAlign: 'right', fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' };

// «1:15» — часы:минуты, как в таблице макета.
function hm(minutes: number | null | undefined, approx = false): string {
  if (minutes == null) return DASH;
  const m = Math.round(minutes);
  return `${approx ? '≈ ' : ''}${Math.floor(m / 60)}:${String(m % 60).padStart(2, '0')}`;
}

function tokenSum(s: StageMetricsShare | null): number | null {
  if (!s?.tokens) return null;
  const t = s.tokens;
  return t.input + t.output + t.cacheRead + t.cacheCreation;
}

function addNullable(...xs: Array<number | null | undefined>): number | null {
  const known = xs.filter((x): x is number => x != null);
  return known.length ? known.reduce((a, b) => a + b, 0) : null;
}

function Table({ head, children, testId }: { head: string[]; children: ReactNode; testId: string }) {
  return (
    <div style={{ overflowX: 'auto' }}>
      <table data-testid={testId} style={{ width: '100%', borderCollapse: 'collapse' }}>
        <thead><tr>{head.map((h, i) => <th key={h} style={{ ...TH, ...(i > 0 ? NUM : null) }}>{h}</th>)}</tr></thead>
        <tbody>{children}</tbody>
      </table>
    </div>
  );
}

function Row({ cells, total }: { cells: string[]; total?: boolean }) {
  return (
    <tr style={total ? { fontWeight: 700, background: C.bgInset } : undefined}>
      {cells.map((c, i) => <td key={i} style={{ ...TD, ...(i > 0 ? NUM : null), ...(c === DASH ? { color: C.textMuted } : null) }}>{c}</td>)}
    </tr>
  );
}

function TimeBar({ metrics }: { metrics: StageMetrics }) {
  const parts = [
    { id: 'executor', label: 'исполнитель', color: C.accent, min: metrics.executor.minutes },
    { id: 'review', label: 'ревью', color: C.info, min: metrics.review?.minutes ?? null },
    { id: 'human', label: 'ждало вас', color: C.warning, min: metrics.human.minutes },
  ].filter(p => p.min != null && p.min > 0);
  const total = parts.reduce((a, p) => a + (p.min as number), 0);
  if (total <= 0) return null;
  return (
    <div data-testid="stage-metrics-bar" style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      <div style={{ display: 'flex', height: SP.sm, borderRadius: R.sm, overflow: 'hidden', background: C.borderLight }}>
        {parts.map(p => (
          <i key={p.id} data-testid={`stage-metrics-seg-${p.id}`} style={{ width: `${((p.min as number) / total) * 100}%`, background: p.color }} />
        ))}
      </div>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: `${SP.xs}px ${SP.md}px`, ...CAPTION }}>
        {parts.map(p => (
          <span key={p.id} style={{ display: 'inline-flex', alignItems: 'center', gap: SP.xs }}>
            <i style={{ width: SP.sm, height: SP.sm, borderRadius: R.sm, background: p.color, display: 'inline-block' }} />
            {p.label} {formatDuration(p.min as number)}
          </span>
        ))}
      </div>
    </div>
  );
}

export function FeatureStageMetrics({ metrics, nowMs }: FeatureStageMetricsProps) {
  if (!metrics) return null;
  const approx = metrics.mode === 'approx';
  const now = nowMs ?? Date.now();

  const ex = metrics.executor;
  const rv = metrics.review;
  const hu = metrics.human;
  // «Мало замеров»: журнал полный, но ревью ещё нет и человек не ждал и не отвечал.
  const sparse = !approx && rv == null && (hu.waits ?? 0) === 0 && hu.waitingNow == null && (hu.replies ?? 0) === 0;

  const details = (
    <details data-testid="stage-metrics-details">
      <summary style={{ ...CAPTION, cursor: 'pointer', minHeight: 40, display: 'flex', alignItems: 'center' }}>Подробности: цена, модели, кэш</summary>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))', gap: SP.md }}>
        <Card id="executor" title="Исполнитель" rows={shareRows(ex, approx)} />
        {rv && <Card id="review" title="Ревью" rows={shareRows(rv, approx)} />}
        <Card id="human" title="Человек" rows={humanRows(hu, now)} />
      </div>
    </details>
  );

  return (
    <div data-testid="stage-metrics" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      {approx ? (
        <>
          <Notice tone="info" icon={Info} data-testid="stage-metrics-legacy">
            Старая фича: ходы и токены по задаче тогда не записывались. Время — оценка по интервалам этапа, без разбивки на исполнителя, ревью и вас.
          </Notice>
          <Table testId="stage-metrics-table" head={['Кто', 'Время', 'Ходы', 'Токены']}>
            <Row total cells={['Этап целиком', hm(ex.minutes ?? metrics.window.elapsedMinutes, true), DASH, DASH]} />
          </Table>
        </>
      ) : sparse ? (
        <div data-testid="stage-metrics-empty" style={{ ...CARD, alignItems: 'flex-start' }}>
          <span style={{ ...CARD_TITLE, textTransform: 'none', fontSize: FS.sm, color: C.textPrimary }}>Замеров пока мало</span>
          <span style={CAPTION}>
            Исполнитель работает {ex.minutes == null ? NO_DATA : formatDuration(ex.minutes)}. Разбивка по ревью и вам появится, когда они подключатся.
          </span>
        </div>
      ) : (
        <>
          <TimeBar metrics={metrics} />
          <Table testId="stage-metrics-table" head={['Кто', 'Время', 'Ходы', 'Токены', 'Ожидание']}>
            <Row cells={['Исполнитель', hm(ex.minutes), ex.turns == null ? DASH : String(ex.turns), tokenSum(ex) == null ? DASH : formatTokens(tokenSum(ex) as number), DASH]} />
            {rv && <Row cells={['Ревью', hm(rv.minutes), rv.turns == null ? DASH : String(rv.turns), tokenSum(rv) == null ? DASH : formatTokens(tokenSum(rv) as number), DASH]} />}
            <Row cells={['Человек', DASH, hu.replies == null ? DASH : `${hu.replies} (из них)`, DASH, hm(hu.minutes)]} />
            <Row total cells={[
              'Итого',
              hm(addNullable(ex.minutes, rv?.minutes, null)),
              (() => { const t = addNullable(ex.turns, rv?.turns); return t == null ? DASH : String(t); })(),
              (() => { const t = addNullable(tokenSum(ex), tokenSum(rv)); return t == null ? DASH : formatTokens(t); })(),
              hm(hu.minutes),
            ]} />
          </Table>
          <span data-testid="stage-metrics-wait-hint" style={CAPTION}>Ожидание — время, пока этап стоял с вопросом к вам.</span>
        </>
      )}
      {!sparse && details}
      {metrics.idleMinutes != null && (
        <span data-testid="stage-metrics-idle" style={CAPTION}>
          Простой этапа: {formatDuration(metrics.idleMinutes)}
        </span>
      )}
      <span data-testid="stage-metrics-hint" style={CAPTION}>
        Время исполнителя — как полосы «Хронологии»; паузы дольше 2 мин — простой. «Ответов человека» — ходы, начатые вами; они уже входят в ходы исполнителя и ревью, поэтому в «Итого» не прибавляются.
        {approx ? ' Журнал неполный: время исполнителя приближённое, у ревью и человека — без данных.' : ''}
      </span>
    </div>
  );
}
