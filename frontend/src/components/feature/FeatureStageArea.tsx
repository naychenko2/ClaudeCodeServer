// Область выбранного этапа под графом процесса (C3b). Выбор живёт у страницы и привязан к
// featureId: смена фичи сбрасывает его на defaultSelected (resolveSelectedStage — чистая
// функция, отдельного эффекта сброса нет, чужой выбор не переживает переход).
// Наполнение — задачи стадии и их статусы из FeatureRun как есть (запросов не добавляет);
// раскрытая задача показывает существующие «Приёмку» и «Итог» (FeatureStepDetails — она сама
// лениво догружает полный текст, только пока раскрыта). Место под экран этапа D1c — слот.

import { useState, type ReactNode } from 'react';
import { Workflow } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { Button } from '../ui/Button';
import { EmptyState } from '../ui/EmptyState';
import { stageStatusLabel } from './FeatureTimelineBits';
import { taskStatusLabel } from './FeatureLabels';
import { nodeTitle } from './FeatureProcessGraph';
import { stageNodeId, defaultSelected } from './FeatureProcessGraphModel';
import { allFeatureTasks } from './FeatureTasksTab';
import { FeatureStepDetails } from './FeatureStepDetails';
import type { FeatureRun, FeatureStage, FeatureTask } from '../../types/featureRun';

// Выбор стадии на странице: ключ — featureId + id стадии.
export interface StageSelection { featureId: string; id: string }

// Какая стадия выбрана: явный выбор — только если он этой же фичи и такая стадия ещё есть
// в run; иначе defaultSelected (первая active, иначе последняя done, иначе null).
export function resolveSelectedStage(
  sel: StageSelection | null, featureId: string, run: FeatureRun,
): string | null {
  if (sel && sel.featureId === featureId && (run.stages ?? []).some(s => stageNodeId(s) === sel.id)) {
    return sel.id;
  }
  return defaultSelected(run);
}

// Признак маршрута (показывать ли граф) живёт одним предикатом в lib/featurePage
// (hasRouteOfRun) — там же, где имя вкладки «Процесс»; здесь его копии нет (минор 8910d5b6).

function TaskRow({ task, isMobile, onOpenTask }: { task: FeatureTask; isMobile: boolean; onOpenTask?: (id: string) => void }) {
  const [open, setOpen] = useState(false);
  const touch = isMobile ? { minHeight: 40 } : undefined;
  return (
    <div data-testid={`stage-task-${task.id}`} style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <span style={{ flex: '1 1 160px', minWidth: 0, fontSize: FS.base, color: C.textHeading, overflowWrap: 'anywhere' }}>
          {task.stepRef ? `${task.stepRef} · ` : ''}{task.title}
        </span>
        <span data-testid={`stage-task-status-${task.id}`} style={{ fontSize: FS.xs, color: C.textMuted }}>
          {taskStatusLabel(task.status)}
        </span>
        <Button size="xs" variant="ghost" style={touch} dataTestId={`stage-task-toggle-${task.id}`}
          ariaLabel={`${open ? 'Скрыть' : 'Показать'} приёмку и итог задачи «${task.title}»`}
          onClick={() => setOpen(v => !v)}>
          {open ? 'Скрыть' : 'Приёмка и итог'}
        </Button>
        {onOpenTask && (
          <Button size="xs" variant="ghost" style={touch} onClick={() => onOpenTask(task.id)}>Задача</Button>
        )}
      </div>
      {open && (
        <FeatureStepDetails taskId={task.id} acceptance={task.acceptance} evidence={task.evidence} isMobile={isMobile} />
      )}
    </div>
  );
}

export interface FeatureStageAreaProps {
  run: FeatureRun;
  selectedId: string | null;
  isMobile: boolean;
  onOpenTask?: (taskId: string) => void;
  // «Открыть этап →»: переход к отдельному экрану этапа (F10).
  onOpenStage?: () => void;
  // Дополнительное место под областью этапа.
  slot?: ReactNode;
}

// Задачи этапа в порядке stage.taskIds (общий источник для области и экрана этапа).
export function stageTasksOf(run: FeatureRun, stage: FeatureStage): FeatureTask[] {
  const byId = new Map(allFeatureTasks(run).map(t => [t.id, t]));
  return (stage.taskIds ?? []).map(id => byId.get(id)).filter((t): t is FeatureTask => !!t);
}

export function FeatureStageArea({ run, selectedId, isMobile, onOpenTask, onOpenStage, slot }: FeatureStageAreaProps) {
  const stage: FeatureStage | undefined = selectedId
    ? (run.stages ?? []).find(s => stageNodeId(s) === selectedId)
    : undefined;
  if (!stage) {
    return (
      <div data-testid="feature-stage-area">
        <EmptyState compact icon={<Workflow size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />} title="Этап не выбран" subtitle="Выберите этап на графе — здесь появятся его задачи." />
      </div>
    );
  }
  const tasks = stageTasksOf(run, stage);
  const status = stageStatusLabel(stage)?.text ?? 'готово';
  return (
    <div
      data-testid="feature-stage-area"
      data-stage-id={selectedId ?? undefined}
      style={{
        display: 'flex', flexDirection: 'column', gap: SP.md, padding: SP.md,
        borderRadius: R.lg, background: C.bgWhite, border: `1px solid ${C.borderLight}`,
        fontFamily: FONT.sans,
      }}
    >
      <div style={{ display: 'flex', alignItems: 'baseline', gap: SP.sm, flexWrap: 'wrap' }}>
        <span data-testid="feature-stage-area-title" style={{ fontSize: FS.md, fontWeight: 600, color: C.textHeading }}>
          {nodeTitle(stage)}
        </span>
        <span style={{ fontSize: FS.xs, color: C.textMuted }}>{status}</span>
        {onOpenStage && tasks.length > 0 && (
          <Button size="sm" variant="primary" dataTestId="feature-stage-area-open"
            style={{ marginLeft: 'auto', ...(isMobile ? { minHeight: 40 } : undefined) }} onClick={onOpenStage}>
            Открыть этап →
          </Button>
        )}
      </div>
      {tasks.length === 0 ? (
        <div data-testid="feature-stage-area-empty" style={{ fontSize: FS.sm, color: C.textSecondary }}>
          У этапа пока нет задач.
        </div>
      ) : (
        tasks.map(t => <TaskRow key={t.id} task={t} isMobile={isMobile} onOpenTask={onOpenTask} />)
      )}
      {slot}
    </div>
  );
}
