// Вкладка «Ход» страницы фичи (записка v2 §2 п. 5, R9–R11): этапы списком, «кто сейчас»,
// шаги внутри этапа «Производство». Только отрисовка: текущий этап отдаёт бэк
// (run.stages[].state === 'active'), на фронте он не вычисляется.
// Порядок: прошлые этапы одной строкой → текущий (раскрыт) → будущие строками → «Дальше».
// Единственное состояние — локальный флаг раскрытия шагов; шкалы/ганта здесь нет.

import { useState } from 'react';
import type { ReactNode } from 'react';
import { C, FS, FONT, SP } from '../../lib/design';
import { Button } from '../ui/Button';
import { formatDuration } from '../../lib/chatGroups';
import { plural } from '../../lib/plural';
import type { FeatureStage } from '../../types/featureRun';
import type { FeatureBriefActive, FeatureBriefNext } from '../../lib/featurePage';
import { ActiveList } from './FeatureBriefActive';
import type { BriefActiveRow } from './FeatureBriefModel';
import { minutesSince, stageLabel } from './FeatureStatsBits';

// Этап «Производство» — по ключу, не по подписи.
const PROD_KEY = 'prod';

export interface FeatureProgressTabProps {
  stages: FeatureStage[];
  next: FeatureBriefNext[];
  active: FeatureBriefActive[];
  nameOf: (personaId: string) => string | null;
  isMobile: boolean;
  // Готовый список шагов (передаёт FS-FP); нет — кнопки «Все шаги» нет.
  stepsSlot?: ReactNode;
  // Число шагов для подписи кнопки; нет — «Все шаги этапа».
  stepsTotal?: number;
  onOpenActive?: (taskId: string) => void;
  // Для давности строк «Сейчас» (тесты фиксируют время).
  nowMs?: number;
}

function activeRows(active: FeatureBriefActive[], nameOf: (id: string) => string | null, nowMs: number): BriefActiveRow[] {
  return active.map(a => {
    const who = a.personaId ? nameOf(a.personaId) : null;
    const what = a.stepRef ?? stageLabel(a.stage);
    const title = who ? `Сейчас ${who} делает ${what}` : `Сейчас идёт ${what}`;
    const m = minutesSince(a.since, nowMs);
    return {
      taskId: a.taskId, state: a.state, title, who: null, stopped: null,
      time: m != null && m > 0 ? formatDuration(m) : null,
      ariaLabel: `${title}, открыть задачу`,
    };
  });
}

export function FeatureProgressTab({
  stages, next, active, nameOf, isMobile, stepsSlot, stepsTotal, onOpenActive, nowMs,
}: FeatureProgressTabProps) {
  const [stepsOpen, setStepsOpen] = useState(false);
  // skipped/absent — этапа не было, в ходе не показываем.
  const shown = stages.filter(s => s.state !== 'skipped' && s.state !== 'absent');
  const past = shown.filter(s => s.state === 'done');
  const current = shown.filter(s => s.state === 'active');
  const future = shown.filter(s => s.state === 'pending');
  const rows = activeRows(active, nameOf, nowMs ?? Date.now());
  const nextFirst = next[0] ?? null;
  const nextWho = nextFirst?.personaId ? nameOf(nextFirst.personaId) : null;

  return (
    <div data-testid="progress-tab" style={{ display: 'flex', flexDirection: 'column', gap: SP.md, fontFamily: FONT.sans, fontSize: FS.base, color: C.textPrimary }}>
      {past.length > 0 && (
        <div data-testid="progress-past" style={{ fontSize: FS.sm, color: C.textSecondary }}>
          {past.map(s => stageLabel(s.key)).join(', ')} · готово
        </div>
      )}
      {current.map((s, i) => (
        <div key={s.key} data-testid="progress-current" data-stage={s.key} style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          <div style={{ fontWeight: 600 }}>{stageLabel(s.key)}</div>
          {i === 0 && <ActiveList view={{ heading: null, sub: null, answer: null, rows, tail: null }} isMobile={isMobile} onOpenTask={onOpenActive} />}
          {s.key === PROD_KEY && stepsSlot != null && (
            <>
              <Button
                variant="ghost"
                size="xs"
                dataTestId="progress-steps-toggle"
                ariaExpanded={stepsOpen}
                onClick={() => setStepsOpen(v => !v)}
                style={{
                  alignSelf: 'flex-start', height: 'auto', minHeight: 40,
                  background: 'transparent', border: 'none', color: C.accent, fontSize: FS.sm,
                }}
              >
                {stepsTotal != null ? `Все ${stepsTotal} ${plural(stepsTotal, 'шаг', 'шага', 'шагов')} этапа ›` : 'Все шаги этапа ›'}
              </Button>
              {stepsOpen && <div data-testid="progress-steps">{stepsSlot}</div>}
            </>
          )}
        </div>
      ))}
      {future.map(s => (
        <div key={s.key} data-testid="progress-future" style={{ color: C.textMuted }}>{stageLabel(s.key)}</div>
      ))}
      {nextFirst && (
        <div data-testid="progress-next" style={{ fontSize: FS.sm, color: C.textSecondary }}>
          Дальше: {stageLabel(nextFirst.stage)}{nextWho ? ` · ${nextWho}` : ''}
        </div>
      )}
    </div>
  );
}
