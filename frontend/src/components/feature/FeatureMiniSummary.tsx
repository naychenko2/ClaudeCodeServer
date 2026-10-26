// Мини-сводка времени на вкладке «Хронология» (волна 3, Ф8, макет features-overview-3-final):
// тонкая лента (8 px, без подписей) + строка итогов (всего прошло · работа ≈ · ждёт/не работали)
// + очередь («в очереди {dur}» / «простояла в очереди {dur}», Q5H-2, макет feature-queue-1)
// + токены «вход+выход» и цена (при надёжных данных) + «Статистика ›» + пояснительная строка.
//
// Данные — ТЕ ЖЕ stats (features/{id}), что во вкладке «Статистика»; фронт не пересчитывает.
// Честность: stats === null → строки нет (без нулей); «≈» при source=approx;
// «принято N из M» и «все готово» НЕ показываем (прогресс — отдельный шаг Ф-pr).

import { C, R, FS, SP, FONT } from '../../lib/design';
import { Button } from '../ui/Button';
import { formatDuration, buildTimelineModel, kindBackground, minutesSince } from './FeatureStatsBits';
import { formatTokens, formatUsd } from './FeatureStatsFormat';
import { COMP_LABEL } from './FeatureStatsPulseModel';
import type { FeatureStats } from '../../types/featureStats';
import type { FeatureHold } from '../../types/featureRun';

// Значок точности «≈» (приблизительные данные).
function AccuracyBadge({ kind }: { kind: 'ap' }) {
  return (
    <span
      data-testid={`stats-acc-${kind}`}
      style={{
        fontFamily: FONT.mono,
        fontSize: FS.xxs, fontWeight: 700,
        background: C.bgSelected, color: C.textMuted,
        borderRadius: R.sm, padding: '0 4px',
        whiteSpace: 'nowrap', flexShrink: 0,
        display: 'inline-flex', alignItems: 'center',
      }}
    >
      ≈
    </span>
  );
}

export interface FeatureMiniSummaryProps {
  // Статистика фичи; null — бэк ещё не посчитал (строки нет).
  stats: FeatureStats | null;
  // Удержание фичи (только для строки «в очереди»): reason='queue' — фича стоит в очереди;
  // null/undefined — удержания нет (и старые данные). Позицию очереди здесь не показываем —
  // она в шапке (FeaturePageHeader); здесь только длительность.
  hold?: FeatureHold | null;
  // Клик → вкладка «Статистика».
  onOpenStats: () => void;
  isMobile?: boolean;
}

// Тонкая лента (8 px): сегменты в масштабе, без подписей.
function MiniTimeline({ stats }: { stats: FeatureStats }) {
  const closed = stats.timeline.current == null;
  const model = buildTimelineModel(stats.timeline, stats.source, stats.truncated, closed);
  if (!model.hasData) return null;

  return (
    <div
      data-testid="mini-timeline"
      style={{
        display: 'flex', gap: 1, height: 8, borderRadius: R.max, overflow: 'hidden',
        marginBottom: SP.xs,
      }}
    >
      {model.blocks.map((b, i) => {
        const w = b.type === 'break' ? 40 : Math.max(2, b.minutes);
        if (b.type === 'break') {
          return (
            <div
              key={`brk-${i}`}
              style={{
                flex: w, minWidth: 0,
                background: `repeating-linear-gradient(90deg, ${C.bgSelected} 0 3px, transparent 3px 6px)`,
              }}
            />
          );
        }
        return (
          <div key={`stg-${i}`} style={{ flex: w, minWidth: 0, display: 'flex', gap: 1 }}>
            {b.segments.map((seg, j) => (
              <div
                key={j}
                style={{
                  flex: Math.max(1, seg.minutes), minWidth: 0,
                  background: kindBackground(seg.kind),
                }}
              />
            ))}
          </div>
        );
      })}
    </div>
  );
}

// Строка итогов: «идёт N · работа ≈ N · ждёт вас N» + очередь + токены + цена.
function SummaryRow({ stats, hold }: { stats: FeatureStats; hold?: FeatureHold | null }) {
  const t = stats.timeline.totals;
  const isApprox = stats.source === 'approx';
  const closed = stats.timeline.current == null;
  const verb = closed ? 'шла' : 'идёт';

  // «Ждёт вас» / «не работали» — только из данных бэка (фронт не пересчитывает):
  //   journal + waitingMinutes != null → «ждала вас N» (N = totals.waitingMinutes);
  //   current.kind === 'waiting' → «ждёт вас» (длительность текущего ожидания — не поле
  //     бэка: `current` несёт только `since`, без минут — число не вычисляем, минор);
  //   иначе → «не работали ≈ N» (N = totals.notWorkingMinutes).
  let waitPart: string;
  if (stats.source === 'journal' && t.waitingMinutes != null) {
    waitPart = `ждала вас ${formatDuration(t.waitingMinutes)}`;
  } else if (stats.timeline.current?.kind === 'waiting') {
    waitPart = 'ждёт вас';
  } else {
    waitPart = `не работали ${isApprox ? '≈ ' : ''}${formatDuration(t.notWorkingMinutes)}`;
  }

  // Очередь (Q5H-2, макет feature-queue-1 §«Состояния», разрез §8): две взаимоисключающие формы.
  //   · фича СТОИТ в очереди (hold.reason='queue') → «в очереди {dur}», dur — с момента постановки
  //     (hold.since — тот же источник, что возраст паузы в FeaturePageHeader; арифметика — общий
  //     помощник minutesSince, нижняя граница 1 мин как у паузы);
  //   · фича уже запущена (удержания нет) и стояла в очереди → «простояла в очереди {dur}»,
  //     dur — минуты отдаёт бэк (totals.queuedMinutes, R-Q3B-2, ADR-031 §«Время в очереди (queued)»):
  //     при null/0/нет поля строки нет, фронт не пересчитывает.
  // Формы не показываются вместе: при активном удержании — только живая (прошлые стояния в неё
  // не складываются), «простояла» — только когда удержания нет.
  // Слово вида — из единого словаря составных видов COMP_LABEL (Q5H-3): та же лексика,
  // что у состава героя и «Подробнее»; «простояла» — префикс к тому же слову.
  let queuePart: { label: string; minutes: number } | null = null;
  if (hold?.reason === 'queue') {
    const sinceMin = minutesSince(hold.since, Date.now());
    if (sinceMin != null) queuePart = { label: COMP_LABEL.queued, minutes: Math.max(1, sinceMin) };
  } else if (t.queuedMinutes != null && t.queuedMinutes > 0) {
    queuePart = { label: `простояла ${COMP_LABEL.queued}`, minutes: t.queuedMinutes };
  }

  // Токены «вход+выход» (spend).
  const spend = stats.spend;
  const tokens = spend ? spend.inputTokens + spend.outputTokens : null;
  const cost = spend && !spend.costPartial ? spend.costUsd : null;

  return (
    <div
      data-testid="mini-summary-row"
      style={{
        display: 'flex', alignItems: 'center', gap: '0 12px', flexWrap: 'wrap',
        fontSize: FS.sm, color: C.textSecondary, fontFamily: FONT.sans,
      }}
    >
      <span>
        {verb} <b style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textHeading }}>{formatDuration(t.elapsedMinutes)}</b>
      </span>
      <span>
        работа <b style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textHeading }}>{isApprox ? '≈ ' : ''}{formatDuration(t.workWallMinutes)}</b>
      </span>
      <span>{waitPart}</span>
      {queuePart && (
        <span>
          {queuePart.label} <b style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textHeading }}>{formatDuration(queuePart.minutes)}</b>
        </span>
      )}
      {tokens != null && tokens > 0 && (
        <span>
          <b style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textHeading }}>{formatTokens(tokens)}</b> токенов
        </span>
      )}
      {cost != null && (
        <span>
          <b style={{ fontFamily: FONT.mono, fontSize: FS.sm, color: C.textHeading }}>{formatUsd(cost)}</b>
        </span>
      )}
    </div>
  );
}

// Мини-сводка: лента + строка итогов + «Статистика ›» + пояснение.
// stats === null → null (строки нет, честно).
export function FeatureMiniSummary({ stats, hold, onOpenStats, isMobile = false }: FeatureMiniSummaryProps) {
  // Честность: stats нет → строки нет (без нулей).
  if (!stats) return null;

  const isApprox = stats.source === 'approx';

  return (
    <div
      data-testid="feature-page-mini-summary"
      style={{
        background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.lg,
        padding: `${SP.sm}px ${SP.sm}px`, marginBottom: SP.md, fontFamily: FONT.sans,
      }}
    >
      {/* Тонкая лента (8 px, без подписей). */}
      <MiniTimeline stats={stats} />

      {/* Строка итогов + «Статистика ›». */}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <div style={{ flex: 1, minWidth: 0 }}>
          <SummaryRow stats={stats} hold={hold} />
        </div>
        {/* Кнопка-кит (не самодельный <button>): «Статистика ›» → вкладка «Статистика». */}
        <Button
          variant="ghost"
          size="xs"
          dataTestId="mini-summary-go-stats"
          ariaLabel="Открыть вкладку «Статистика»"
          onClick={onOpenStats}
          style={{
            background: 'transparent', border: 'none', color: C.accent,
            fontSize: FS.xs, whiteSpace: 'nowrap', flexShrink: 0,
            minHeight: isMobile ? 40 : undefined,
          }}
        >
          Статистика ›
        </Button>
      </div>

      {/* Значок точности + пояснительная строка. */}
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, marginTop: SP.xs, flexWrap: 'wrap' }}>
        {isApprox && <AccuracyBadge kind="ap" />}
        <span style={{ fontSize: FS.xxs, color: C.textMuted, fontFamily: FONT.sans }}>
          Порядок этапов, их состояние и кто работал — точные; «≈» — по задачам этапа. Ревью идёт по шагам вместе с производством.
        </span>
      </div>
    </div>
  );
}
