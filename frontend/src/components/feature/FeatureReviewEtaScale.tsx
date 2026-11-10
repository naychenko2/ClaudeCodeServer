// Блок «Шкала срока» секции «Время и срок» вкладки «Разбор» (FS-FR1b; записка
// docs/mockups/feature-page-simplify-v4-clickable.md §2 «Каких компонентов нет» — шкала срока,
// §5 «1. Время и срок», в макете — блок .etab): четыре строки «подпись | дорожка | значение»
// на общей шкале 0…max — обещали на старте → сейчас осталось → по истории проекта → типичная
// фича — и «Согласие оценок» (ReviewKv). Геометрия карточки — как у FeatureVolumeCard
// (bgCard/borderLight/R.lg, поля SP.lg, на мобиле SP.md); дорожка — в геометрии ProgressBar
// (высота 4, R.max). Все числа — из brief и stats.typical как есть, формат один —
// formatDuration; своих величин фронт не считает. Встраивание — FS-FR: проп etaScale у
// FeatureReviewTime.

import { type CSSProperties } from 'react';
import { C, R, SP, FS, FONT } from '../../lib/design';
import { plural } from '../../lib/plural';
import { ReviewKv } from './FeatureReviewParts';
import { formatDuration } from './FeatureStatsBits';
import type { FeatureBrief, FeatureBriefAgreement } from '../../lib/featurePage';
import type { FeatureStats } from '../../types/featureStats';
import type { PersonaNameResolver } from './FeatureBriefModel';

export interface FeatureReviewEtaScaleProps {
  // null — сводки нет (старый ответ бэка): строк brief нет, остаётся только «Типичная фича».
  brief: FeatureBrief | null;
  stats: FeatureStats;
  // Имя оценщика для строки «Сейчас осталось»; нет резолвера или персоны — строки источника нет.
  nameOf?: PersonaNameResolver;
  isMobile?: boolean;
}

// Слова согласия — те же, что в «Сроке» сводки (FeatureBriefModel.ts:632-634): одно понятие —
// один язык. Отдельного словаря в экспорте FeatureBriefModel нет (строки инлайн в
// estimateView), поэтому здесь своя копия; свести к одному словарю — правка вне файлов шага.
const AGREEMENT_LABEL: Record<FeatureBriefAgreement, string> = {
  within: 'сходится',
  above: 'дольше, чем по истории',
  below: 'быстрее, чем по истории',
};

// Строка шкалы: bar — отрезок [from..to] на общей шкале, tick — вертикальная риска на from
// (обещание на старте — точка на шкале, а не длительность). text — значение в правой ячейке,
// hint — строка источника под подписью.
interface EtaRow {
  key: 'first' | 'now' | 'hist' | 'typ';
  label: string;
  hint: string | null;
  text: string;
  from: number;
  to: number;
  tick: boolean;
  color: string;
}

// Строки в порядке записки §5. Строки без данных нет: null или 0 — не значение.
function buildRows(brief: FeatureBrief | null, stats: FeatureStats, nameOf?: PersonaNameResolver): EtaRow[] {
  const rows: EtaRow[] = [];
  const note = brief?.note ?? null;

  const initial = brief?.initialEtaMinutes ?? null;
  if (initial != null && initial > 0) {
    rows.push({ key: 'first', label: 'Обещали на старте', hint: null, text: `≈ ${formatDuration(initial)}`, from: initial, to: initial, tick: true, color: C.textMuted });
  }

  const eta = note?.estimate?.etaMinutes ?? null;
  if (eta != null && eta > 0) {
    const name = note?.byPersonaId ? nameOf?.(note.byPersonaId) ?? null : null;
    rows.push({ key: 'now', label: 'Сейчас осталось', hint: name ? `оценил ${name}` : null, text: `≈ ${formatDuration(eta)}`, from: 0, to: eta, tick: false, color: C.accent });
  }

  const hist = brief?.estimate ?? null;
  if (hist != null && hist.p75 > 0) {
    const n = hist.sampleSize;
    rows.push({
      key: 'hist', label: 'По истории проекта',
      hint: `по ${n} ${plural(n, 'фиче', 'фичам', 'фичам')}`,
      text: `${formatDuration(hist.p25)} – ${formatDuration(hist.p75)}`,
      from: hist.p25, to: hist.p75, tick: false, color: C.info,
    });
  }

  const dur = stats.typical?.duration ?? null;
  if (dur != null && dur.p75 > 0) {
    rows.push({
      key: 'typ', label: 'Типичная фича',
      hint: `разброс ${formatDuration(dur.p25)} – ${formatDuration(dur.p75)}`,
      text: formatDuration(dur.median),
      from: dur.p25, to: dur.p75, tick: false, color: C.progressTrack,
    });
  }

  return rows;
}

const CARD: CSSProperties = {
  background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
  padding: SP.lg, display: 'flex', flexDirection: 'column', gap: SP.sm, boxSizing: 'border-box',
};

// Строка «подпись | дорожка | значение» (макет .erow: 150px 1fr 76px). На 360 — две колонки
// (подпись и значение), дорожка уходит на свою строку на всю ширину (макет .w360 .erow .track
// { grid-column:1/-1; order:3 }): подпись стоит над дорожкой, горизонтального переполнения нет.
const ROW: CSSProperties = { display: 'grid', gridTemplateColumns: '150px 1fr 76px', gap: SP.sm, alignItems: 'center' };
const ROW_MOBILE: CSSProperties = { gridTemplateColumns: '1fr 76px' };
// Дорожка — геометрия ProgressBar default: высота 4, скругление R.max; риска выступает за
// её край на 3 px (макет .tick), поэтому overflow не скрыт.
const TRACK: CSSProperties = { position: 'relative', height: 4, borderRadius: R.max, background: C.bgInset };
const TRACK_MOBILE: CSSProperties = { gridColumn: '1 / -1', order: 3 };
const BAR: CSSProperties = { position: 'absolute', top: 0, bottom: 0, borderRadius: R.max, minWidth: 2 };
const TICK: CSSProperties = { position: 'absolute', top: -3, bottom: -3, width: 2, borderRadius: R.max };
const LABEL: CSSProperties = { fontFamily: FONT.sans, fontSize: FS.sm, color: C.textSecondary, minWidth: 0 };
const HINT: CSSProperties = { display: 'block', fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted };
const VALUE: CSSProperties = { fontFamily: FONT.sans, fontSize: FS.sm, fontWeight: 600, color: C.textHeading, textAlign: 'right', fontVariantNumeric: 'tabular-nums' };
const CAPTION: CSSProperties = { fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted };

export function FeatureReviewEtaScale({ brief, stats, nameOf, isMobile = false }: FeatureReviewEtaScaleProps) {
  const rows = buildRows(brief, stats, nameOf);
  if (rows.length === 0) return null;
  // Шкала общая: 0…max из всех показанных значений (правый край каждой полосы) — строки
  // сравнимы между собой; процент — по этой шкале, знак шкалы — в подписи под строками.
  const max = Math.max(...rows.map((r) => r.to));
  const pct = (v: number) => Math.max(0, Math.min(100, Math.round((v / max) * 100)));
  const agreement = brief?.note?.estimate?.agreement ?? null;

  return (
    <div data-testid="review-eta" style={CARD}>
      {rows.map((r) => {
        const left = pct(r.from);
        const width = Math.max(pct(r.to) - left, 0);
        return (
          <div key={r.key} data-testid="review-eta-row" data-row={r.key} style={isMobile ? { ...ROW, ...ROW_MOBILE } : ROW}>
            <span style={LABEL}>
              {r.label}
              {r.hint ? <span data-testid="review-eta-hint" style={HINT}>{r.hint}</span> : null}
            </span>
            <span data-testid="review-eta-track" style={isMobile ? { ...TRACK, ...TRACK_MOBILE } : TRACK}>
              {r.tick
                ? <span data-testid="review-eta-tick" style={{ ...TICK, left: `${left}%`, background: r.color }} />
                : <span data-testid="review-eta-bar" style={{ ...BAR, left: `${left}%`, width: `${width}%`, background: r.color }} />}
            </span>
            <span style={VALUE}>{r.text}</span>
          </div>
        );
      })}
      <div data-testid="review-eta-scale" style={CAPTION}>{`шкала: 0 — ${formatDuration(max)}`}</div>
      <ReviewKv rows={[{ label: 'Согласие оценок', value: agreement ? AGREEMENT_LABEL[agreement] : null }]} />
    </div>
  );
}
