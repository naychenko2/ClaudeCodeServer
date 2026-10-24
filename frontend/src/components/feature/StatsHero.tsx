// Герой-карточка «Пульса» вкладки «Статистика» (волна 5, ST-F4; вариант A макета
// feature-stats-5.html). Состояние выбирает pickHeroState (ST-F3) — здесь только
// рендер четырёх состояний: running / waiting / merged / few.
// Чип — из featureRunStatusTag (замечание Глеба ST-F3, п. а: при attention.kind=
// 'stopped' чип «стоит», а не «идёт»; п. б: state 'closed' — «закрыта», не «смержена»);
// «только начала» — не run.state, а состояние героя (few), тон notStarted.
// Решение Андрея: в «ждёт вас» герой показывает только «ждёт N мин» и ghost
// «Открыть задачу» — вопрос и кнопка чата остаются в строке анализа, здесь не дублируются.
// Полосу состава строим от суммы частей (п. в): проценты totals могут дать 101 %,
// поэтому ширина сегмента — его минуты делённые на сумму частей, а не на elapsed.
// Доли — проценты из percentText (п. г), не десятые доли.

import { type CSSProperties } from 'react';
import { C, R, FS, SP, FONT, SHADOW } from '../../lib/design';
import { Button } from '../ui/Button';
import { StatusTag, featureRunStatusTag, type FeatureRunTag } from './FeatureStatusTag';
import { openTaskInSection } from '../../lib/tasks';
import { formatDuration, formatDateTime, minutesSince, kindBackground } from './FeatureStatsBits';
import { FEW_DATA_MIN, pickHeroState, compositionRows, type HeroState } from './FeatureStatsPulseModel';
import type { FeatureRun } from '../../types/featureRun';
import type { FeatureStats } from '../../types/featureStats';

export interface StatsHeroProps {
  run: FeatureRun;
  stats: FeatureStats;
  isMobile?: boolean;
}

// Чип героя: единая точка правды — featureRunStatusTag. Исключение одно: «few» —
// состояния героя нет среди 11 run.state, его подписывает тон notStarted.
function heroTag(run: FeatureRun, state: HeroState): FeatureRunTag {
  if (state === 'few') return { label: 'только начали', tone: 'notStarted' };
  return featureRunStatusTag(run.state);
}

// Карточка: bgCard, рамка borderLight, SHADOW.card, R.lg; поля SP.lg (360 — SP.md),
// две колонки 1fr 1.25fr (360 — одна).
function heroStyle(isMobile: boolean): CSSProperties {
  return {
    display: 'grid',
    gridTemplateColumns: isMobile ? '1fr' : '1fr 1.25fr',
    gap: isMobile ? SP.md : SP.lg,
    padding: isMobile ? `${SP.md}px ${SP.md + 2}px` : `${SP.lg}px ${SP.lg + 2}px`,
    borderRadius: R.lg,
    background: C.bgCard,
    border: `1px solid ${C.borderLight}`,
    boxShadow: SHADOW.card,
    marginBottom: SP.md,
  };
}

// Подпись секции правой колонки: FS.xxs, UPPERCASE, textMuted (макет §Токены).
const SEC_LABEL: CSSProperties = {
  fontSize: FS.xxs, textTransform: 'uppercase', letterSpacing: 0.6,
  color: C.textMuted, fontFamily: FONT.sans, marginBottom: SP.xs,
};

// Пунктирная заметка (few и «нет данных»): рамка dashed, текст textMuted.
const DASH_NOTE: CSSProperties = {
  border: `1px dashed ${C.border}`, borderRadius: R.md, padding: SP.sm,
  color: C.textMuted, fontSize: FS.sm, fontFamily: FONT.sans, lineHeight: 1.5,
};

export function StatsHero({ run, stats, isMobile = false }: StatsHeroProps) {
  const state = pickHeroState(run, stats);
  const nowMs = Date.now();
  const totals = stats.timeline.totals;
  const rows = compositionRows(totals);
  const tag = heroTag(run, state);

  // Большое число и подпись — по состоянию (копирайт макета, waiting — по решению
  // Андрея: только текущее ожидание, без вопроса и чата).
  let big: string;
  let sub: string;
  if (state === 'waiting') {
    const mins = minutesSince(run.attention?.since, nowMs) ?? 0;
    big = formatDuration(mins);
    sub = 'фича стоит, пока нет ответа';
  } else if (state === 'merged') {
    const s = run.summary;
    big = formatDuration(s?.durationMinutes ?? totals.elapsedMinutes);
    sub = s
      ? `от разреза до мержа · ${s.tasks} задач · ${s.steps} шагов · ${s.loops} возврата`
      : 'от разреза до закрытия';
  } else if (state === 'few') {
    big = formatDuration(totals.elapsedMinutes);
    sub = 'идёт разрез · считать доли пока рано';
  } else {
    big = formatDuration(totals.elapsedMinutes);
    const work = rows.find(r => r.kind === 'work');
    sub = work ? `прошло с начала · из них работали ${work.percentText}` : 'прошло с начала';
  }

  // Переход «Открыть задачу»: задача тревоги, иначе эпик фичи. Ни того, ни другого —
  // кнопки нет (кнопки только ui/Button).
  const taskId = run.attention?.taskId ?? run.epic?.id ?? null;

  // Ширины сегментов — от суммы частей (п. в): сумма ширинок всегда 100 %.
  const partsSum = rows.reduce((a, r) => a + r.minutes, 0);

  return (
    <div data-testid={`hero-${state}`} style={heroStyle(isMobile)}>
      <div>
        <div style={{ display: 'flex', alignItems: 'center', gap: SP.xs, marginBottom: SP.sm }}>
          <StatusTag tag={tag} testId="hero-chip" />
          {state === 'merged' && run.summary?.mergedAt && (
            <span style={{ fontSize: FS.xs, color: C.textMuted, fontFamily: FONT.sans }}>
              {formatDateTime(run.summary.mergedAt)}
            </span>
          )}
        </div>

        {/* Число героя: FONT.serif, FS.display (360 — FS.h1). */}
        <div
          data-testid="hero-big"
          style={{
            fontFamily: FONT.serif,
            fontSize: isMobile ? FS.h1 : FS.display,
            color: C.textHeading,
            lineHeight: 1.15,
          }}
        >
          {big}
        </div>
        <div style={{ fontSize: FS.sm, color: C.textMuted, fontFamily: FONT.sans, marginTop: SP.xs }}>
          {sub}
        </div>

        {state === 'waiting' && taskId && (
          <div style={{ marginTop: SP.sm }}>
            <Button
              variant="ghost" size={isMobile ? 'md' : 'sm'} fullWidth={isMobile}
              dataTestId="hero-open-task"
              onClick={() => openTaskInSection({ id: taskId, projectId: run.epic?.projectId ?? undefined })}
            >
              Открыть задачу
            </Button>
          </div>
        )}
      </div>

      <div>
        {state === 'few' ? (
          <div data-testid="hero-few-note" style={DASH_NOTE}>
            Доли «работа · ждали вас · простой» появятся после {FEW_DATA_MIN} мин работы.
            Возвраты — после первого ревью. Сравнение с типичной фичей — у смерженной.
          </div>
        ) : rows.length === 0 ? (
          <div data-testid="hero-no-data" style={DASH_NOTE}>
            Время ещё не разложено: сегментов нет.
          </div>
        ) : (
          <>
            <div style={SEC_LABEL}>Из чего сложилось время</div>
            <div
              data-testid="hero-mix-bar"
              style={{
                display: 'flex', height: 14, borderRadius: R.sm,
                background: C.bgSelected, overflow: 'hidden',
              }}
            >
              {rows.map(r => (
                <i
                  key={r.kind}
                  data-testid={`hero-mix-${r.kind}`}
                  style={{ width: `${(r.minutes / partsSum) * 100}%`, background: kindBackground(r.kind) }}
                />
              ))}
            </div>
            {rows.map(r => (
              <div
                key={r.kind}
                data-testid={`hero-mix-row-${r.kind}`}
                style={{
                  display: 'flex', alignItems: 'center', gap: SP.xs,
                  marginTop: SP.xs, fontSize: FS.xs, fontFamily: FONT.sans, color: C.textPrimary,
                }}
              >
                <span style={{ width: 8, height: 8, borderRadius: R.full, background: kindBackground(r.kind), flexShrink: 0 }} />
                <span style={{ flex: 1, minWidth: 0 }}>{r.label}</span>
                <span style={{ fontFamily: FONT.mono, color: C.textMuted }}>{formatDuration(r.minutes)}</span>
                <span style={{ fontFamily: FONT.mono }}>{r.percentText}</span>
              </div>
            ))}
          </>
        )}
      </div>
    </div>
  );
}
