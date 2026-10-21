import { C, R, FS, SP, FONT } from '../../lib/design';
import { setFeaturePageTab, focusFeatureStage } from '../../lib/featurePage';
import { openTaskInSection } from '../../lib/tasks';
import { EmptyState } from '../ui/EmptyState';
import { NavTargetButton } from './NavTargetButton';
import {
  buildTimelineModel, formatClock, formatDateTime, formatDuration,
  kindLabel, kindBackground, kindTextColor, nowFraction,
  type TimelineModel, type TimelineBlock, type TimelineSegment,
} from './FeatureStatsBits';
import type { FeatureStats } from '../../types/featureStats';

// Вкладка «Статистика» (волна 3, Ф7a, макет features-overview-3-final §«Статистика»).
// БЛОК 1 — лента времени: линейка этапов (кнопки → «Хронология»), лента сегментов
// (работа/ждёт/простой/ошибка/не работали) с СЖАТИЕМ длинных пауз (> 2 ч → «разрыв · N ч»),
// дорожка ревью, ось времени с отметкой «сейчас», легенда и значки точности.
// БЛОКИ 2 (карточки, «Дольше всех», разрезы, расход) — в Ф7b.
//
// Данные ТОЛЬКО из `stats` (features/{id}): фронт не пересчитывает, только раскладывает.
// Честность: нет stats (старые данные) → пустое состояние с причиной, а не нули.

export interface FeatureStatsTabProps {
  // Статистика фичи (features/{id} → stats); null — бэк ещё не посчитал (старые данные).
  stats: FeatureStats | null;
  // Тач-цели ≥ 40 на 360.
  isMobile?: boolean;
}

// Вес блока на ленте (flex): этап — сумма минут его сегментов; разрыв — фиксированный
// (сжатая пауза не в масштабе). Минимум, чтобы узкий блок не исчез.
function blockWeight(b: TimelineBlock): number {
  if (b.type === 'break') return 40; // фиксированная ширина сжатого разрыва
  return Math.max(2, b.minutes);
}

// Подпись сегмента на ленте: длительность (короткая) — только если влезает.
function segLabel(seg: TimelineSegment): string {
  return formatDuration(seg.minutes);
}

// Значки точности (макет: «≈ по задачам (≈)» / «по журналу»; «журнал обрезан»).
function AccuracyBadges({ source, truncated }: { source: 'approx' | 'journal'; truncated: boolean }) {
  // approx → «≈» + «по задачам (≈)»; journal → «по журналу».
  const main = source === 'approx'
    ? <span data-testid="stats-accuracy-approx" style={{ fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700, color: C.textSecondary }}>≈</span>
    : <span data-testid="stats-accuracy-journal" style={{ fontFamily: FONT.mono, fontSize: FS.xs, fontWeight: 700, color: C.textSecondary }}>по журналу</span>;
  return (
    <div
      data-testid="stats-accuracy"
      style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', marginBottom: SP.sm, fontFamily: FONT.sans }}
    >
      <span style={{ fontSize: FS.xs, color: C.textSecondary }}>
        {source === 'approx' ? 'по задачам (≈)' : 'по журналу'}
      </span>
      {main}
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

  // «Сейчас» на оси: доля 0..1 по моменту current.since.
  const nowFrac = current ? nowFraction(axis.from, axis.to, current.since) : null;

  return (
    <div data-testid="stats-timeline" style={{ fontFamily: FONT.sans }}>
      {/* Лента: на 360 — горизонтальная прокрутка (minWidth держит масштаб). */}
      <div
        data-testid="stats-timeline-scroll"
        style={{ overflowX: isMobile ? 'auto' : 'visible', WebkitOverflowScrolling: 'touch' }}
      >
        <div style={{ display: 'flex', gap: 2, minWidth: isMobile ? 640 : undefined, alignItems: 'stretch' }}>
          {blocks.map((b, i) => {
            const w = blockWeight(b);
            // Ревью: тонкая дорожка под блоком, если блок имеет сегменты track=review.
            const reviewMin = b.type === 'stage' ? b.reviewMinutes : 0;
            return (
              <div key={`${b.type}-${'key' in b ? b.key : i}`} style={{ flex: w, minWidth: 0, display: 'flex', flexDirection: 'column' }}>
                {/* Линейка: имя этапа (кнопка → «Хронология») / подпись разрыва. */}
                {b.type === 'stage' ? (
                  <button
                    type="button"
                    data-testid={`stats-ruler-${b.key}`}
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
                {/* Лента: сегменты (∝ минутам) / сжатый разрыв. */}
                <div style={{ display: 'flex', gap: 1, height: isMobile ? 34 : 26, borderRadius: R.sm, overflow: 'hidden' }}>
                  {b.type === 'stage' ? (
                    b.segments.map((seg, j) => (
                      <button
                        key={j}
                        type="button"
                        data-testid={`stats-seg-${seg.kind}`}
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
                        background: 'repeating-linear-gradient(90deg, var(--c-bg-selected) 0 3px, transparent 3px 6px)',
                        color: C.textMuted, fontFamily: FONT.mono, fontSize: FS.xxs, fontWeight: 400,
                        overflow: 'hidden', whiteSpace: 'nowrap',
                      }}
                    >
                      {isMobile ? '⫽' : b.label}
                    </div>
                  )}
                </div>
                {/* Дорожка ревью: тонкая полоса под блоком (ревью идёт по шагам вместе с производством). */}
                <div style={{ height: 4, marginTop: 2, borderRadius: R.max, background: reviewMin > 0 ? C.warning : 'transparent' }} />
              </div>
            );
          })}
        </div>
      </div>

      {/* Ось времени: от создания до «сейчас»/закрытия + отметка «сейчас». */}
      <div
        data-testid="stats-axis"
        style={{ display: 'flex', alignItems: 'center', gap: SP.sm, marginTop: SP.xs, fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted }}
      >
        <span>{formatDateTime(axis.from)} · создана</span>
        {nowFrac != null && current && (
          <span
            data-testid="stats-now-marker"
            style={{
              flex: 1, position: 'relative', height: 2, background: C.divider,
            }}
          >
            <span
              style={{
                position: 'absolute', left: `${Math.round(nowFrac * 100)}%`, top: -6,
                width: 2, height: 14, background: C.accent, borderRadius: R.max,
              }}
            />
          </span>
        )}
        <span style={{ marginLeft: 'auto' }}>{axis.endLabel}</span>
      </div>

      {/* «Сейчас»: текущее состояние (waiting/stopped/merge_pending) — отдельная строка. */}
      {current && (
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
      )}

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
        <span data-testid="stats-legend-break" style={{ color: C.textMuted }}>
          ⫽ — длинная пауза сжата, не в масштабе
        </span>
      </div>
    </div>
  );
}

// «Задача» по taskId сегмента/этапа: NavTargetButton (открывает задачу в «Задачах»).
function TaskButtons({ model, isMobile }: { model: TimelineModel; isMobile: boolean }) {
  // Собираем уникальные taskId по этапам (из сегментов) — по одному «задача» на этап.
  const byStage = new Map<string, string | null>();
  for (const b of model.blocks) {
    if (b.type !== 'stage') continue;
    const tid = b.segments.find(s => s.taskId)?.taskId ?? null;
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
          <NavTargetButton kind="task" taskId={taskId} isMobile={isMobile} onOpen={() => openTaskInSection({ id: taskId! })} />
        </span>
      ))}
    </div>
  );
}

// Короткая подпись этапа для ряда «задача» (без полного имени, чтобы влезло).
function stageLabelShort(key: string): string {
  const names: Record<string, string> = {
    cut: 'Разрез', plan: 'План', prod: 'Производство', review: 'Ревью',
    qa: 'Проверка', final: 'Финал', merge: 'Мерж',
  };
  return names[key] ?? key;
}

export function FeatureStatsTab({ stats, isMobile = false }: FeatureStatsTabProps) {
  // Пустое состояние: stats нет (старые данные) — причина, а не нули.
  if (!stats) {
    return (
      <EmptyState
        compact
        icon="📊"
        title="Статистики пока нет"
        subtitle="Появится, когда будет посчитано время фичи."
      />
    );
  }

  // Закрыта/смержена: current == null (контракт: null — закрыта).
  const closed = stats.timeline.current == null;
  const model = buildTimelineModel(stats.timeline, stats.source, stats.truncated, closed);

  // Пустое состояние: нет ни сегментов, ни текущего состояния (макет: «Считать пока нечего»).
  if (!model.hasData) {
    return (
      <EmptyState
        compact
        icon="📊"
        title="Считать пока нечего"
        subtitle="Статистика появится с первой задачей исполнителя."
      />
    );
  }

  return (
    <div data-testid="feature-stats-tab" style={{ fontFamily: FONT.sans }}>
      <AccuracyBadges source={stats.source} truncated={stats.truncated} />
      <Timeline model={model} isMobile={isMobile} />
      <TaskButtons model={model} isMobile={isMobile} />
    </div>
  );
}
