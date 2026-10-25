import { useState } from 'react';
import { C, R, FS, SP, FONT, SHADOW } from '../../lib/design';
import { setFeaturePageTab, focusFeatureStage } from '../../lib/featurePage';
import { openTaskInSection } from '../../lib/tasks';
import { EmptyState } from '../ui/EmptyState';
import { NavTargetButton } from './NavTargetButton';
import { recordFeatureReturn } from '../../lib/featureBackNav';
import { EMPTY_NAV_MARKS, type NavMarks, type FeatureNavCtx } from '../../lib/featureNavMarks';
import {
  buildTimelineModel, formatClock, formatDateTime, formatDuration,
  kindLabel, kindBackground, kindTextColor, nowCompressedFraction, blockWeight,
  type TimelineModel, type TimelineSegment,
} from './FeatureStatsBits';
import { buildGanttModel, type GanttModel, type GanttSegmentKind } from './FeatureGanttModel';
import { personaGroupLabel, modelGroupLabel, roleGroupLabel, formatTokens } from './FeatureStatsFormat';
import { plural } from '../../lib/plural';
import { usePersonas } from '../../lib/personas';
import { useModels } from '../../lib/models';
import { AGENT_COLORS } from '../AgentSelector';
import { StatsHero } from './StatsHero';
import { FeatureStatsTiles } from './FeatureStatsTiles';
import { FeatureStatsNow } from './FeatureStatsNow';
import { FeatureStatsLosses } from './FeatureStatsLosses';
import { FeatureBreakdownBlock } from './FeatureBreakdownBlock';
import { FeatureAnalysisLine } from './FeatureAnalysisLine';
import { stageLabelShort } from './FeatureAnalysisModel';
import type { FeatureStats, FeatureSpend, FeatureStatsSource, FeatureTimeline } from '../../types/featureStats';
import type { FeatureRun } from '../../types/featureRun';

// Вкладка «Статистика» (волна 5, ST-F14a; вариант A «Пульс» макета feature-stats-5).
// Порядок: строка анализа → значки точности → герой → плитки → «Сейчас работают» →
// «Главные потери» → Гант (ступени, W4-4F2) → «Кто и на чём работал» → свёрнутый
// «Подробнее» (то, что новые блоки не покрывают: «не работали», эпизоды ошибок,
// детали расхода). Без `run` (старые ответы) — прежняя полоса: герой, плитки, «сейчас»,
// потери и разрез требуют конвейера. Прежние блоки итогов/разрезов/расхода удалены в ST-F14б.
//
// Данные ТОЛЬКО из `stats` и `run` (features/{id}): фронт не пересчитывает, только раскладывает.
// Честность: нет stats (старые данные) → пустое состояние с причиной, а не нули.

export interface FeatureStatsTabProps {
  // Статистика фичи (features/{id} → stats); null — бэк ещё не посчитал (старые данные).
  stats: FeatureStats | null;
  // Конвейер фичи (features/{id} → run) — строки ступеней Ганта (W4-4F2). Без него
  // (старые ответы) — прежняя полоса из timeline.
  run?: FeatureRun | null;
  // Фича и её название — для точки возврата («← к фиче · Статистика»); без featureId
  // переходы работают, но чипа возврата нет.
  featureId?: string | null;
  featureTitle?: string | null;
  // Пометки «в архиве»/«задача закрыта» (buildNavMarks страницы); у статистики своих
  // агрегатов нет — без пропа пометок нет.
  marks?: NavMarks;
  // Тач-цели ≥ 40 на 360.
  isMobile?: boolean;
}

// Подпись сегмента на ленте: длительность (короткая) — только если влезает.
function segLabel(seg: TimelineSegment): string {
  return formatDuration(seg.minutes);
}

// Строка точности (Майя, P1): одна фраза FS.xs textMuted без повтора:
// «≈ по задачам — числа приблизительные» / «по журналу ходов»; при trunc —
// отдельная «журнал обрезан».
function AccuracyBadges({ source, truncated }: { source: 'approx' | 'journal'; truncated: boolean }) {
  const phrase = source === 'approx'
    ? '≈ по задачам — числа приблизительные'
    : 'по журналу ходов';
  return (
    <div
      data-testid="stats-accuracy"
      style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', marginBottom: SP.sm, fontFamily: FONT.sans }}
    >
      <span data-testid="stats-accuracy-phrase" style={{ fontSize: FS.xs, color: C.textMuted }}>
        {phrase}
      </span>
      {truncated && (
        <span data-testid="stats-accuracy-truncated" style={{ fontSize: FS.xs, color: C.warningText }}>
          журнал обрезан
        </span>
      )}
    </div>
  );
}

// Лента: линейка этапов + сегменты + дорожка ревью + ось + легенда + «сейчас».
function Timeline({ model, isMobile }: { model: TimelineModel; isMobile: boolean }) {
  const { blocks, axis, hasReview, current } = model;

  // Клик по этапу/сегменту → вкладка «Хронология» к этому этапу (готовая навигация:
  // setFeaturePageTab + focusFeatureStage — тот же путь, что плашка/панель).
  const openStage = (key: string) => {
    setFeaturePageTab('timeline');
    focusFeatureStage(key);
  };

  // «Сейчас» на НАРИСОВАННОЙ (сжатой) шкале ленты: доля 0..1 ширины по моменту
  // current.since. По календарной шкале (nowFraction) отметка не совпала бы с лентой —
  // лента сжимает длинные паузы, поэтому позиция считается по весам блоков.
  const nowFrac = current ? nowCompressedFraction(blocks, current.since) : null;
  // Есть ли сжатые разрывы (для легенды «⫽ — длинная пауза сжата»).
  const hasBreak = blocks.some(b => b.type === 'break');

  return (
    <div data-testid="stats-timeline" style={{ fontFamily: FONT.sans }}>
      {/* Лента: на 360 — горизонтальная прокрутка (minWidth держит масштаб). */}
      <div
        data-testid="stats-timeline-scroll"
        style={{ overflowX: isMobile ? 'auto' : 'visible', WebkitOverflowScrolling: 'touch' }}
      >
        {/* position:relative на самой flex-строке: отметка «сейчас» — её потомок, едет
            вместе с прокруткой и лежит на НАРИСОВАННОЙ (640) ленте, а не на скроллпорте (360). */}
        <div style={{ display: 'flex', gap: 2, minWidth: isMobile ? 640 : undefined, alignItems: 'stretch', position: 'relative' }}>
          {blocks.map((b, i) => {
            const w = blockWeight(b);
            // Ревью: отдельная тонкая дорожка; ширина ∝ минутам ревью (доля от времени блока) —
            // ревью идёт вместе с производством, но не отнимает у основной ленты (макет).
            const reviewMin = b.type === 'stage' ? b.reviewMinutes : 0;
            const totalMin = b.type === 'stage' ? b.minutes + b.reviewMinutes : 0;
            const reviewPct = totalMin > 0 ? (reviewMin / totalMin) * 100 : 0;
            return (
              <div key={`${b.type}-${'key' in b ? b.key : i}`} style={{ flex: w, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
                {/* Линейка: имя этапа (кнопка → «Хронология») / подпись разрыва. */}
                {b.type === 'stage' ? (
                  <button
                    type="button"
                    data-testid={`stats-ruler-${b.key}-${i}`}
                    onClick={() => openStage(b.key)}
                    aria-label={`${b.label}: открыть этап в «Хронологии»`}
                    style={{
                      display: 'block', width: '100%', textAlign: 'left', cursor: 'pointer',
                      background: 'transparent', border: 'none',
                      padding: `0 ${SP.xs}px ${SP.xxs}px`, minHeight: isMobile ? 40 : 22,
                      fontFamily: FONT.sans, fontSize: FS.xs, fontWeight: 700, color: C.textSecondary,
                      overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                    }}
                  >
                    {b.label}
                  </button>
                ) : (
                  <div
                    data-testid="stats-ruler-break"
                    style={{
                      display: 'block', width: '100%', padding: `0 ${SP.xs}px ${SP.xxs}px`,
                      fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
                      overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                    }}
                  >
                    ⫽
                  </div>
                )}
                {/* Лента: main-сегменты (∝ минутам) / сжатый разрыв. Тач-цель на 360 ≥ 40. */}
                <div style={{ display: 'flex', gap: 1, height: isMobile ? 40 : 26, borderRadius: R.sm, overflow: 'hidden' }}>
                  {b.type === 'stage' ? (
                    b.segments.map((seg, j) => (
                      <button
                        key={j}
                        type="button"
                        data-testid={`stats-seg-${i}-${j}`}
                        data-kind={seg.kind}
                        onClick={() => openStage(b.key)}
                        aria-label={`${b.label} · ${kindLabel(seg.kind)} · ${formatDateTime(seg.from)} · ${segLabel(seg)}`}
                        title={`${b.label} · ${kindLabel(seg.kind)} · ${formatDateTime(seg.from)} – ${formatClock(seg.to)} · ${segLabel(seg)}`}
                        style={{
                          flex: Math.max(1, seg.minutes), minWidth: 0,
                          display: 'flex', alignItems: 'center', justifyContent: 'center',
                          background: kindBackground(seg.kind), color: kindTextColor(seg.kind),
                          border: 'none', cursor: 'pointer',
                          fontFamily: FONT.mono, fontSize: FS.xxs, fontWeight: 700,
                          overflow: 'hidden', whiteSpace: 'nowrap',
                        }}
                      >
                        {seg.minutes >= 45 ? segLabel(seg) : ''}
                      </button>
                    ))
                  ) : (
                    <div
                      data-testid="stats-seg-break"
                      style={{
                        flex: 1, minWidth: 0, display: 'flex', alignItems: 'center', justifyContent: 'center',
                        background: `repeating-linear-gradient(90deg, ${C.bgSelected} 0 3px, transparent 3px 6px)`,
                        color: C.textMuted, fontFamily: FONT.mono, fontSize: FS.xxs, fontWeight: 400,
                        overflow: 'hidden', whiteSpace: 'nowrap',
                      }}
                    >
                      {isMobile ? '⫽' : b.label}
                    </div>
                  )}
                </div>
                {/* Дорожка ревью: тонкая полоса, ширина = ДОЛЯ ревью в этапе (review/(main+review)
                    от ширины блока; блок ∝ main-минутам). В абсолютном масштабе полоса НЕ ∝
                    минутам ревью — это «доля ревью в этапе», а не абсолютное время ревью. */}
                {b.type === 'stage' && reviewMin > 0 && (
                  <div data-testid="stats-review-track" style={{ height: 4, marginTop: 2, display: 'flex' }}>
                    <div style={{ width: `${reviewPct}%`, height: '100%', borderRadius: R.max, background: C.warning }} />
                  </div>
                )}
              </div>
            );
          })}
          {/* Отметка «сейчас» — на нарисованной (сжатой) шкале: вертикальная линия в точке
              current.since (внутри блока — по его моменту; в разрыве — на разрыве). */}
          {nowFrac != null && current && (
            <div
              data-testid="stats-now-marker"
              style={{
                position: 'absolute', left: `${Math.round(nowFrac * 1000) / 10}%`, top: 0, bottom: 0,
                width: 2, background: C.accent, borderRadius: R.max, pointerEvents: 'none',
              }}
            />
          )}
        </div>
      </div>

      {/* Ось времени: от создания до «сейчас»/закрытия (отметка «сейчас» — на ленте, выше). */}
      <AxisLine axis={axis} />
      <CurrentRow current={current} />

      {/* Легенда: виды + «⫽ — длинная пауза сжата, ширина не в масштабе». */}
      <div
        data-testid="stats-legend"
        style={{ display: 'flex', alignItems: 'center', gap: SP.md, flexWrap: 'wrap', marginTop: SP.sm, fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted }}
      >
        {(['work', 'waiting', 'idle', 'error', 'notWorking'] as const).map(k => (
          <span key={k} style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
            <span style={{ width: 10, height: 10, borderRadius: 2, background: kindBackground(k) }} />
            {kindLabel(k)}
          </span>
        ))}
        {hasReview && (
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
            <span style={{ width: 14, height: 4, borderRadius: R.max, background: C.warning }} />
            ревью
          </span>
        )}
        {/* «⫽ — длинная пауза сжата» — только когда разрывы реально есть (иначе легенда
            обещает то, чего на ленте нет). */}
        {hasBreak && (
          <span data-testid="stats-legend-break" style={{ color: C.textMuted }}>
            ⫽ — длинная пауза сжата, не в масштабе
          </span>
        )}
      </div>
    </div>
  );
}

// === Ступени Ганта (W4-4F2, макет features-overview-4 §pt4, вариант А) ===
// У каждого этапа своя строка: имя слева, полоса на общей оси (сегменты «работа ·
// ожидание · простой» ∝ минутам), минуты справа. Подсказка — по наведению (на 360 —
// по касанию): разбивка по минутам и переход в «Хронологию». Строка — grid с
// колонкой 1fr: на 360 помещается без горизонтальной прокрутки. Этап со state
// 'skipped' (W4-DES-B) — строка «этапа не было», без минут и полосы.

// Ось времени: от создания до «сейчас»/закрытия. Общая для полосы и ступеней.
function AxisLine({ axis }: { axis: { from: string; to: string; endLabel: string } }) {
  return (
    <div
      data-testid="stats-axis"
      style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: SP.xs, fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}
    >
      <span>{formatDateTime(axis.from)} · создана</span>
      <span style={{ marginLeft: 'auto' }}>{axis.endLabel}</span>
    </div>
  );
}

// «Сейчас»: текущее состояние (waiting/stopped/merge_pending) — отдельная строка.
function CurrentRow({ current }: { current: { kind: string; since: string; label: string } | null }) {
  if (!current) return null;
  return (
    <div
      data-testid="stats-current"
      style={{
        display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: SP.sm,
        background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.md,
        padding: `${SP.xs}px ${SP.sm}px`, fontFamily: FONT.sans, fontSize: FS.xs, color: C.textSecondary,
      }}
    >
      <span style={{ width: 8, height: 8, borderRadius: R.full, background: C.accent, flexShrink: 0 }} />
      {current.label}
    </div>
  );
}

function Gantt({
  gantt,
  axis,
  current,
  isMobile,
}: {
  gantt: GanttModel;
  axis: { from: string; to: string; endLabel: string };
  current: { kind: string; since: string; label: string } | null;
  isMobile: boolean;
}) {
  // Открытая подсказка: наведение (десктоп) или касание (360).
  const [tipKey, setTipKey] = useState<string | null>(null);

  // Клик по ссылке подсказки → вкладка «Хронология» к этому этапу (тот же путь, что
  // плашка/панель: setFeaturePageTab + focusFeatureStage).
  const openStage = (key: string) => {
    setFeaturePageTab('timeline');
    focusFeatureStage(key);
    setTipKey(null);
  };

  // Доля оси [0..1] по моменту; null — ось или момент невалидны.
  const axisFrac = (iso: string | null): number | null => {
    if (!iso) return null;
    const a = new Date(axis.from).getTime();
    const b = new Date(axis.to).getTime();
    const t = new Date(iso).getTime();
    if (Number.isNaN(a) || Number.isNaN(b) || b <= a || Number.isNaN(t)) return null;
    return Math.min(1, Math.max(0, (t - a) / (b - a)));
  };

  // Цвет сегмента: трековые виды — из FeatureStatsBits, ревью — C.plan (макет k-rev).
  const segColor = (kind: GanttSegmentKind): string =>
    kind === 'review' ? C.plan : kindBackground(kind);

  // Какие виды реально есть на строках — легенда обещает только их.
  const kinds = new Set<GanttSegmentKind>();
  for (const r of gantt.rows) for (const s of r.segments) kinds.add(s.kind);

  return (
    <div data-testid="stats-gantt" style={{ fontFamily: FONT.sans }}>
      {gantt.rows.map((row) => {
        const left = axisFrac(row.from);
        // Правый край: row.to — край полосы (barEnd из модели Ганта); если края нет —
        // для текущего этапа текущий момент (axis.to): полоса дотягивается до «сейчас»,
        // а не исчезает.
        const right = axisFrac(row.to ?? (row.isCurrent ? axis.to : null));
        // Минимальная видимая ширина полосы (макет: min-width 3px) — чтобы этап в
        // 4 минуты не превратился в щель.
        const leftPct = left != null && right != null ? left * 100 : 0;
        const widthPct = left != null && right != null ? Math.max(0.6, (right - left) * 100) : 0;
        const totalMin = row.segments.reduce((a, s) => a + s.minutes, 0);
        // «Сейчас» на строке текущего этапа: правый край оси (текущий момент) — тот же
        // край, до которого дотягивается полоса выше.
        const showNow = row.isCurrent;
        // Разбивка для подсказки: только ненулевые части.
        const parts: [string, number][] = [];
        if (row.breakdown) {
          const b = row.breakdown;
          if (b.work > 0) parts.push(['работа', b.work]);
          if (b.waiting > 0) parts.push(['ожидание', b.waiting]);
          if (b.idle > 0) parts.push(['простой', b.idle]);
          if (b.error > 0) parts.push(['ошибка', b.error]);
          if (b.notWorking > 0) parts.push(['не работали', b.notWorking]);
        }

        return (
          <div key={row.key}>
            <button
              type="button"
              data-testid={`gantt-row-${row.key}`}
              data-stage={row.key}
              aria-label={`${row.label}: ${row.isSkipped ? 'этапа не было' : formatDuration(row.minutes)} — разбивка`}
              onMouseEnter={() => setTipKey(row.key)}
              onMouseLeave={() => setTipKey(null)}
              onClick={() => setTipKey(tipKey === row.key ? null : row.key)}
              style={{
                // Фиксированы только колонки имени и минут (86/52 на 360) — полоса
                // берёт остаток (1fr): горизонтального переполнения на 360 нет.
                display: 'grid',
                gridTemplateColumns: isMobile ? '86px 1fr 52px' : '110px 1fr 70px',
                gap: isMobile ? SP.sm : SP.md,
                alignItems: 'center',
                minHeight: isMobile ? 40 : 28,
                background: 'transparent',
                border: 'none',
                padding: 0,
                cursor: 'pointer',
                fontFamily: FONT.sans,
              }}
            >
              <span
                style={{
                  fontSize: FS.sm, fontWeight: 600, color: C.textHeading,
                  overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
                }}
              >
                {row.label}
              </span>
              <div
                data-testid={`gantt-track-${row.key}`}
                style={{
                  position: 'relative', height: 16, borderRadius: R.sm,
                  // Тики четвертей оси (макет .gtrack).
                  background: `repeating-linear-gradient(90deg, transparent 0 calc(25% - 1px), ${C.borderLight} calc(25% - 1px) 25%)`,
                }}
              >
                {row.isSkipped ? (
                  <span
                    data-testid="gantt-skipped"
                    style={{
                      position: 'absolute', left: 0, top: 0, bottom: 0,
                      display: 'flex', alignItems: 'center',
                      fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted,
                    }}
                  >
                    этапа не было
                  </span>
                ) : widthPct > 0 ? (
                  <div
                    data-testid={`gantt-bar-${row.key}`}
                    style={{
                      position: 'absolute', left: `${leftPct}%`, width: `${widthPct}%`, top: 2, height: 12,
                      borderRadius: R.sm, display: 'flex', overflow: 'hidden', minWidth: 3,
                    }}
                  >
                    {row.segments.map((seg, i) => (
                      <span
                        key={i}
                        data-testid={`gantt-seg-${row.key}-${i}`}
                        data-kind={seg.kind}
                        style={{
                          width: `${totalMin > 0 ? (seg.minutes / totalMin) * 100 : 0}%`,
                          background: segColor(seg.kind),
                        }}
                      />
                    ))}
                  </div>
                ) : null}
                {showNow && (
                  <div
                    data-testid="gantt-now-marker"
                    style={{
                      position: 'absolute', right: 0, top: -2, bottom: -2,
                      width: 2, background: C.accent, borderRadius: R.max, pointerEvents: 'none',
                    }}
                  />
                )}
              </div>
              <span
                data-testid={`gantt-minutes-${row.key}`}
                style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary, textAlign: 'right' }}
              >
                {row.isSkipped ? '' : formatDuration(row.minutes)}
              </span>
            </button>
            {tipKey === row.key && (
              <div
                data-testid="gantt-tip"
                style={{
                  background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.md,
                  padding: `${SP.xs}px ${SP.sm}px`, margin: `${SP.xxs}px 0 ${SP.sm}px`,
                  fontFamily: FONT.sans, fontSize: FS.xs, color: C.textSecondary,
                }}
              >
                <b style={{ color: C.textHeading, fontSize: FS.sm }}>
                  {row.label} · {row.isSkipped ? 'этапа не было' : formatDuration(row.minutes)}
                </b>
                {parts.length > 0 && (
                  <div data-testid="gantt-tip-breakdown" style={{ marginTop: SP.xxs }}>
                    {parts.map(([name, m]) => `${name} ${formatDuration(m)}`).join(' · ')}
                  </div>
                )}
                {row.from && (
                  <div style={{ marginTop: SP.xxs, fontFamily: FONT.mono, fontSize: FS.xxs, color: C.textMuted }}>
                    {formatDateTime(row.from)} – {row.isCurrent ? 'идёт' : formatClock(row.to ?? '')}
                  </div>
                )}
                {!row.isSkipped && (
                  <button
                    type="button"
                    data-testid="gantt-tip-open"
                    onClick={() => openStage(row.key)}
                    style={{
                      marginTop: SP.xs, background: 'transparent', border: 'none', cursor: 'pointer',
                      fontFamily: FONT.sans, fontSize: FS.xs, color: C.accent, fontWeight: 600,
                    }}
                  >
                    Открыть в «Хронологии»
                  </button>
                )}
              </div>
            )}
          </div>
        );
      })}

      {/* «Тихо» (W5-F3b, спецификация features-overview-5.md §«Точка 6»): последняя строка
          после этапов, перед осью. Состояние — в модели (из analysisLineState), здесь
          только показ. Сетка — та же, что у строк Ганта (имя · трек · минуты). */}
      {gantt.quiet && (
        <div
          data-testid="gantt-quiet"
          style={{
            display: 'grid',
            gridTemplateColumns: isMobile ? '86px 1fr 52px' : '110px 1fr 70px',
            gap: isMobile ? SP.sm : SP.md,
            alignItems: 'center',
            minHeight: isMobile ? 40 : 28,
          }}
        >
          <span
            style={{
              fontSize: FS.sm, fontWeight: 700, color: C.warningText,
              overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap',
            }}
          >
            тихо
          </span>
          <div
            style={{
              position: 'relative', height: 16, borderRadius: SP.xs,
              border: `1px dashed ${C.warning}`,
              background: `repeating-linear-gradient(135deg, transparent 0 4px, ${C.warningBg} 4px 8px)`,
            }}
          >
            <span
              style={{
                position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center',
                fontFamily: FONT.mono, fontSize: FS.xxs, color: C.warningText,
                overflow: 'hidden', whiteSpace: 'nowrap',
              }}
            >
              {gantt.quiet.from ? `${formatDateTime(gantt.quiet.from)} → сейчас` : 'сейчас'}{isMobile ? '' : ' · сжато'}
            </span>
          </div>
          <span
            style={{ fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700, color: C.warningText, textAlign: 'right' }}
          >
            {gantt.quiet.minutes != null ? formatDuration(gantt.quiet.minutes) : ''}
          </span>
        </div>
      )}

      <AxisLine axis={axis} />
      <CurrentRow current={current} />

      {/* Легенда: только виды, которые действительно есть на строках. */}
      <div
        data-testid="stats-legend"
        style={{ display: 'flex', alignItems: 'center', gap: SP.md, flexWrap: 'wrap', marginTop: SP.sm, fontFamily: FONT.sans, fontSize: FS.xs, color: C.textMuted }}
      >
        {(['work', 'waiting', 'idle', 'error', 'notWorking'] as const).filter(k => kinds.has(k)).map(k => (
          <span key={k} style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
            <span style={{ width: 10, height: 10, borderRadius: 2, background: kindBackground(k) }} />
            {kindLabel(k)}
          </span>
        ))}
        {kinds.has('review') && (
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
            <span style={{ width: 14, height: 4, borderRadius: R.sm, background: C.plan }} />
            ревью
          </span>
        )}
        {gantt.rows.some(r => r.isCurrent) && (
          <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
            <span style={{ width: 2, height: 10, background: C.accent }} />
            сейчас
          </span>
        )}
        {/* «Тихо» (W5-F3b): квадрат с той же рамкой и фоном, что трек строки. */}
        {gantt.quiet && (
          <span data-testid="gantt-legend-quiet" style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
            <span style={{ width: 10, height: 10, borderRadius: 2, border: `1px dashed ${C.warning}`, background: C.warningBg }} />
            тихо
          </span>
        )}
      </div>
    </div>
  );
}

// «Задача» по taskId сегмента/этапа: NavTargetButton (открывает задачу в «Задачах»).
// run нужен для projectId: задачи фичи лежат в проекте её эпика (тот же путь, что
// StatsHero:147) — без projectId openTaskInSection ведёт в личный «Календарь» (дефект ee6825ff).
function TaskButtons({ model, run, nav, isMobile }: { model: TimelineModel; run?: FeatureRun | null; nav?: FeatureNavCtx; isMobile: boolean }) {
  const marks = nav?.marks ?? EMPTY_NAV_MARKS;
  // Собираем уникальные taskId по этапам (из сегментов) — по одному «задача» на этап.
  const byStage = new Map<string, string | null>();
  for (const b of model.blocks) {
    if (b.type !== 'stage') continue;
    // taskId — из main-сегментов, иначе из дорожки ревью (оба несут задачу).
    const tid = (b.segments.find(s => s.taskId) ?? b.reviewSegments.find(s => s.taskId))?.taskId ?? null;
    if (tid && !byStage.has(b.key)) byStage.set(b.key, tid);
  }
  if (byStage.size === 0) return null;
  return (
    <div
      data-testid="stats-tasks"
      style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', marginTop: SP.md }}
    >
      {[...byStage.entries()].map(([stage, taskId]) => (
        <span key={stage} style={{ display: 'inline-flex', alignItems: 'center', gap: 4, fontSize: FS.xs, color: C.textMuted }}>
          {stageLabelShort(stage)}
          <NavTargetButton
            kind="task"
            taskId={taskId}
            taskClosed={marks.taskClosed(taskId)}
            isMobile={isMobile}
            onOpen={() => {
              if (nav?.featureId && taskId) recordFeatureReturn(nav.featureId, { kind: 'task', id: taskId }, nav.featureTitle);
              openTaskInSection({ id: taskId!, projectId: run?.epic?.projectId ?? undefined });
            }}
          />
        </span>
      ))}
    </div>
  );
}

// Раскрытие «Подробнее» помним между заходами (план ST-F14).
export const MORE_OPEN_KEY = 'featureStats.moreOpen';
function readMoreOpen(): boolean {
  try { return localStorage.getItem(MORE_OPEN_KEY) === '1'; } catch { return false; }
}
function writeMoreOpen(open: boolean): void {
  try { localStorage.setItem(MORE_OPEN_KEY, open ? '1' : '0'); } catch { /* приватный режим/квота */ }
}

// Кольцо фокуса строки «Подробнее» (P2): инлайн-стиль не умеет :focus-visible —
// класс инжектится один раз (тот же приём, что ui/Chip и FeatureBreakdownShared).
const MORE_FOCUS_CLASS = 'cc-stats-more';
if (typeof document !== 'undefined' && !document.getElementById('cc-stats-more-style')) {
  const el = document.createElement('style');
  el.id = 'cc-stats-more-style';
  el.textContent = `.${MORE_FOCUS_CLASS}:focus-visible{outline:none;box-shadow:${SHADOW.focus};}`;
  document.head.appendChild(el);
}

// «Подробнее» (ST-F14a, макет feature-stats-5.md §«Правки фронта», п. 8): только то,
// что новые блоки НЕ покрывают: «не работали» (в составе героя его нет), эпизоды
// ошибок, детали расхода (кэш, «данные с», общие чаты, модели без цены). Нечего
// добавить — раскрывашки нет (пустой «Подробнее» не бывает).
function MoreDetails({ totals, spend, source, isMobile }: {
  totals: FeatureTimeline['totals'];
  spend: FeatureSpend | null;
  source: FeatureStatsSource;
  isMobile: boolean;
}) {
  const rows: string[] = [];
  const approx = source === 'approx' ? '≈ ' : '';
  if (totals.notWorkingMinutes != null && totals.notWorkingMinutes > 0) {
    rows.push(`не работали — ${approx}${formatDuration(totals.notWorkingMinutes)}`);
  }
  if (totals.errorCount != null && totals.errorCount > 0) {
    rows.push(`ошибки: ${totals.errorCount} ${plural(totals.errorCount, 'эпизод', 'эпизода', 'эпизодов')}`);
  }
  if (spend) {
    // Кэш — отдельной строкой подробно; в итог плитки «Расход» он тоже входит (tilesModel).
    const cache = spend.cacheReadTokens + spend.cacheCreationTokens;
    if (cache > 0) {
      rows.push(`кэш: ${formatTokens(spend.cacheReadTokens)} чтение · ${formatTokens(spend.cacheCreationTokens)} создание`);
    }
    // «Данные с …» — только если расход ненулевой (иначе у пустой фичи это дата эпика).
    const nonzero = spend.inputTokens + spend.outputTokens + cache > 0 || spend.costUsd > 0;
    if (nonzero) rows.push(`данные с ${new Date(spend.from).toLocaleDateString('ru-RU')}`);
    // Метод оценки shared — из spend.shared.method, не из source (расход и лента могут
    // считаться разными способами) — то же правило, что в прежнем блоке Spend.
    if (spend.shared) {
      rows.push(
        `в т. ч. доля общих чатов ≈ ${formatTokens(spend.shared.inputTokens + spend.shared.outputTokens)} токенов` +
        ` (оценка, по ${spend.shared.method === 'journal' ? 'журналу' : 'задачам ≈'})`,
      );
    }
    if (spend.costPartial && spend.hiddenCostModels.length > 0) {
      rows.push(`моделей без цены: ${spend.hiddenCostModels.join(', ')}`);
    }
  }
  const [open, setOpen] = useState(readMoreOpen);
  if (rows.length === 0) return null;
  return (
    <details
      data-testid="stats-more"
      open={open}
      onToggle={e => {
        const next = (e.currentTarget as HTMLDetailsElement).open;
        if (next === open) return;
        setOpen(next);
        writeMoreOpen(next);
      }}
      style={{ margin: `${SP.md}px 0 0` }}
    >
      <summary
        data-testid="stats-more-summary"
        className={MORE_FOCUS_CLASS}
        style={{
          // Карточка-строка по макету .more: высота ≥ 44 (тач-цель на 360), рамка и
          // фон карточки; кольцо фокуса — класс MORE_FOCUS_CLASS (:focus-visible).
          display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: SP.sm,
          minHeight: 44, padding: `${SP.md}px ${SP.lg}px`,
          background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
          cursor: 'pointer', fontSize: FS.sm, fontWeight: 600, color: C.textSecondary, fontFamily: FONT.sans,
        }}
      >
        <span>Подробнее</span>
        <span aria-hidden>▾</span>
      </summary>
      <div
        style={{
          marginTop: SP.sm, padding: isMobile ? `${SP.sm}px ${SP.md}px` : `${SP.md}px ${SP.lg}px`,
          background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
          display: 'flex', flexDirection: 'column', gap: SP.xs,
          fontFamily: FONT.mono, fontSize: FS.xs, color: C.textSecondary,
        }}
      >
        {rows.map((r, i) => <span key={i} data-testid="stats-more-row">{r}</span>)}
      </div>
    </details>
  );
}

export function FeatureStatsTab({ stats, run = null, featureId = null, featureTitle = null, marks, isMobile = false }: FeatureStatsTabProps) {
  // Хуки выше ранних return: labelOf/colorOf нужны только с run, но хук не может
  // быть условным (react-hooks/rules-of-hooks) — тот же приём, что в FeatureStatsNow.
  const personas = usePersonas();
  useModels(); // реактивность: догрузка каталога моделей меняет подписи
  const nav: FeatureNavCtx = { featureId, featureTitle, marks };
  // Строка анализа (W5-F1) — про фичу, а не про статистику: показываем и в пустых
  // состояниях вкладки, поэтому вынесена до ранних return. Без run (старые ответы) — нет.
  const line = run ? <FeatureAnalysisLine run={run} marks={marks} isMobile={isMobile} /> : null;

  // Пустое состояние: stats нет (старые данные) — причина, а не нули.
  if (!stats) {
    return (
      <>
        {line}
        <EmptyState
          compact
          icon="📊"
          title="Статистики пока нет"
          subtitle="Появится, когда будет посчитано время фичи."
        />
      </>
    );
  }

  // Закрыта/смержена: current == null (контракт: null — закрыта).
  const closed = stats.timeline.current == null;
  const model = buildTimelineModel(stats.timeline, stats.source, stats.truncated, closed);

  // Пустое состояние: нет ни сегментов, ни текущего состояния (макет: «Считать пока нечего»).
  if (!model.hasData) {
    return (
      <>
        {line}
        <EmptyState
          compact
          icon="📊"
          title="Считать пока нечего"
          subtitle="Статистика появится с первой задачей исполнителя."
        />
      </>
    );
  }

  // «Пульс» (ST-F14a): герой, плитки, «Сейчас работают», «Главные потери» — всё из
  // run + stats; без run (старые ответы) этих блоков нет, остаётся прежняя полоса.
  const costPartial = stats.spend?.costPartial ?? false;

  // Ступени Ганта (W4-4F2) — из run (конвейера). Без run или без строк (старые
  // ответы) — прежняя полоса: честная деградация, а не пустой блок.
  const gantt = run ? buildGanttModel(run, stats, Date.now()) : null;

  // labelOf/colorOf — те же источники подписей, что у прежнего Breakdown:
  // персона → имя, роль → roleLabel, модель → modelLabel. Цвет — цвет аватара персоны
  // (AGENT_COLORS); остальным — '' (палитра ShareRing решает сама). Ось приходит от
  // оболочки разбивки: по ключу её не угадать (UUID недоступной персоны не отличить
  // от id модели — он ушёл бы в подпись сырым).
  const labelOf = (key: string, axis: 'personas' | 'models' | 'roles'): string => {
    if (axis === 'personas') return personaGroupLabel(key, personas);
    if (axis === 'roles') return roleGroupLabel(key);
    return modelGroupLabel(key);
  };
  const colorOf = (key: string): string => {
    const c = personas.find(x => x.id === key)?.avatar?.color;
    return c ? AGENT_COLORS[c] ?? '' : '';
  };

  return (
    <div data-testid="feature-stats-tab" style={{ fontFamily: FONT.sans }}>
      {line}
      <AccuracyBadges source={stats.source} truncated={stats.truncated} />
      {run && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.lg, marginTop: SP.lg }}>
          <StatsHero run={run} stats={stats} isMobile={isMobile} />
          <FeatureStatsTiles run={run} stats={stats} isMobile={isMobile} />
          <FeatureStatsNow run={run} nav={nav} isMobile={isMobile} />
          <FeatureStatsLosses run={run} stats={stats} nav={nav} isMobile={isMobile} />
        </div>
      )}
      {gantt && gantt.rows.length > 0 ? (
        <Gantt gantt={gantt} axis={model.axis} current={model.current} isMobile={isMobile} />
      ) : (
        <Timeline model={model} isMobile={isMobile} />
      )}
      <TaskButtons model={model} run={run} nav={nav} isMobile={isMobile} />
      {run && (
        <div style={{ marginTop: SP.lg }}>
          <FeatureBreakdownBlock
            breakdown={stats.breakdown}
            costPartial={costPartial}
            run={run}
            labelOf={labelOf}
            colorOf={colorOf}
            nav={nav}
            isMobile={isMobile}
          />
        </div>
      )}
      <MoreDetails totals={stats.timeline.totals} spend={stats.spend} source={stats.source} isMobile={isMobile} />
    </div>
  );
}
