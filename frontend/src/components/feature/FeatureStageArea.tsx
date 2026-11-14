// Область выбранного этапа под графом процесса (C3b, содержимое — F11). Выбор живёт у страницы и
// привязан к featureId: смена фичи сбрасывает его на defaultSelected (resolveSelectedStage — чистая
// функция, отдельного эффекта сброса нет, чужой выбор не переживает переход).
// Состав: заголовок (serif) + чип линии + тег состояния + тег «N возврат», подпись исполнителя и
// шаблона, плитки KPI, «Задачи ветки», заметка о параллельной ветке. Задачи и статусы — из FeatureRun
// как есть; сводку (KPI, исполнитель, вердикты, соседняя ветка) область лениво берёт из
// stageSummary экрана этапа по первой задаче этапа: пока не пришла или отказала — рисуется только то,
// что есть в run (без KPI и подписи). Раскрытая задача показывает «Приёмку» и «Итог» (FeatureStepDetails).
// Что ждёт человека, область берёт из данных сводки, а не из примера макета (противоречие №3).

import { useCallback, useEffect, useState, type ReactNode } from 'react';
import { Workflow } from 'lucide-react';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { Button } from '../ui/Button';
import { Badge, type BadgeTone } from '../ui/Badge';
import { EmptyState } from '../ui/EmptyState';
import { api } from '../../lib/api';
import { plural } from '../../lib/plural';
import { laneWord } from '../../lib/roleLabel';
import { formatDuration } from '../../lib/chatGroups';
import { stageStatusLabel } from './FeatureTimelineBits';
import { taskStatusLabel } from './FeatureLabels';
import { nodeTitle } from './FeatureProcessGraph';
import { stageNodeId, defaultSelected } from './FeatureProcessGraphModel';
import { allFeatureTasks } from './FeatureTasksTab';
import { FeatureStepDetails } from './FeatureStepDetails';
import { FeatureStageKpi } from './FeatureStageKpi';
import { FeatureStageQuestions } from './FeatureStageQuestions';
import type { FeatureRun, FeatureStage, FeatureTask } from '../../types/featureRun';
import type { StageQuestion, StageSummary, StageSummaryTask } from '../../types/featureStage';

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


// Строка задачи ветки, нормализованная из сводки (приоритет) или из задачи run (фолбэк без сводки).
interface BranchRow {
  id: string;
  stepRef: string | null;
  title: string;
  done: boolean;
  statusText: string;
  executor: string | null;
  minutes: number | null;
  verdict: string | null;
  returns: number;
  acceptance?: FeatureTask['acceptance'];
  evidence?: FeatureTask['evidence'];
}

const VERDICT_TEXT: Record<string, string> = { accepted: 'принято', returned: 'доработка после ревью' };

// Подпись «Исполнитель · N мин · принято/доработка после ревью»: только то, что есть.
export function branchRowCaption(r: Pick<BranchRow, 'executor' | 'minutes' | 'verdict'>): string {
  const parts: string[] = [];
  if (r.executor) parts.push(r.executor);
  if (r.minutes != null) parts.push(formatDuration(r.minutes));
  if (r.verdict && VERDICT_TEXT[r.verdict]) parts.push(VERDICT_TEXT[r.verdict]);
  return parts.join(' · ');
}

function branchRows(tasks: FeatureTask[], sum: StageSummary | null): BranchRow[] {
  const byId = new Map(tasks.map(t => [t.id, t]));
  if (sum?.tasks && sum.tasks.length > 0) {
    return sum.tasks.map((r: StageSummaryTask) => {
      const t = byId.get(r.taskId);
      return {
        id: r.taskId, stepRef: r.stepRef, title: r.title, done: r.state === 'done',
        statusText: t ? taskStatusLabel(t.status) : r.state,
        executor: r.executor?.name ?? null, minutes: r.minutes, verdict: r.verdict, returns: r.returns,
        acceptance: t?.acceptance, evidence: t?.evidence,
      };
    });
  }
  return tasks.map(t => ({
    id: t.id, stepRef: t.stepRef ?? null, title: t.title, done: t.status === 'done',
    statusText: taskStatusLabel(t.status), executor: null, minutes: null, verdict: null, returns: 0,
    acceptance: t.acceptance, evidence: t.evidence,
  }));
}

function TaskRow({ row, isMobile, onOpenTask }: { row: BranchRow; isMobile: boolean; onOpenTask?: (id: string) => void }) {
  const [open, setOpen] = useState(false);
  const touch = isMobile ? { minHeight: 40 } : undefined;
  const caption = branchRowCaption(row);
  return (
    <div data-testid={`stage-task-${row.id}`} style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <span data-testid={`stage-task-mark-${row.id}`} aria-hidden="true"
          style={{ fontSize: FS.sm, color: row.done ? C.success : C.accent, flexShrink: 0 }}>
          {row.done ? '✓' : '●'}
        </span>
        <span style={{ flex: '1 1 160px', minWidth: 0, fontSize: FS.base, color: C.textHeading, overflowWrap: 'anywhere' }}>
          {row.stepRef ? `${row.stepRef} · ` : ''}{row.title}
        </span>
        {row.returns > 0 && (
          <span data-testid={`stage-task-returns-${row.id}`} title="Возвраты на доработку"
            style={{ fontSize: FS.xs, color: C.warningText }}>↺{row.returns}</span>
        )}
        <span data-testid={`stage-task-status-${row.id}`} style={{ fontSize: FS.xs, color: C.textMuted }}>
          {row.statusText}
        </span>
        <Button size="xs" variant="ghost" style={touch} dataTestId={`stage-task-toggle-${row.id}`}
          ariaLabel={`${open ? 'Скрыть' : 'Показать'} приёмку и итог задачи «${row.title}»`}
          onClick={() => setOpen(v => !v)}>
          {open ? 'Скрыть' : 'Приёмка и итог'}
        </Button>
        {onOpenTask && (
          <Button size="xs" variant="ghost" style={touch} onClick={() => onOpenTask(row.id)}>Задача</Button>
        )}
      </div>
      {caption && (
        <span data-testid={`stage-task-caption-${row.id}`} style={{ fontSize: FS.xs, color: C.textMuted, overflowWrap: 'anywhere' }}>
          {caption}
        </span>
      )}
      {open && (
        <FeatureStepDetails taskId={row.id} acceptance={row.acceptance} evidence={row.evidence} isMobile={isMobile} />
      )}
    </div>
  );
}

// Задачи этапа в порядке stage.taskIds (общий источник для области и экрана этапа).
export function stageTasksOf(run: FeatureRun, stage: FeatureStage): FeatureTask[] {
  const byId = new Map(allFeatureTasks(run).map(t => [t.id, t]));
  return (stage.taskIds ?? []).map(id => byId.get(id)).filter((t): t is FeatureTask => !!t);
}

// Сводка этапа по первой его задаче. Состояние помечено taskId, ответ отменённого запроса не
// применяется; отказ — null (область живёт без KPI, ошибок наружу не бросает).
function useStageSummary(taskId: string | null): { summary: StageSummary | null; questions: StageQuestion[]; reload: () => void } {
  const [loaded, setLoaded] = useState<{ taskId: string; summary: StageSummary | null; questions: StageQuestion[] } | null>(null);
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    if (!taskId) return;
    const controller = new AbortController();
    const { signal } = controller;
    Promise.resolve()
      .then(() => api.tasks.stage(taskId, signal))
      .then(screen => { if (!signal.aborted) setLoaded({ taskId, summary: screen?.stageSummary ?? null, questions: screen?.questions ?? [] }); })
      .catch(() => { if (!signal.aborted) setLoaded({ taskId, summary: null, questions: [] }); });
    return () => controller.abort();
  }, [taskId, attempt]);
  const reload = useCallback(() => setAttempt(n => n + 1), []);
  const cur = loaded && loaded.taskId === taskId ? loaded : null;
  return { summary: cur?.summary ?? null, questions: cur?.questions ?? [], reload };
}

const SILENCE_ACTION: Record<string, string> = {
  notify: 'только сообщить', nudge: 'напомнить исполнителю', stop: 'остановить и сообщить',
};

// Подпись под заголовком: «Исполнители из плана · шаблон «…» vN · этап: молчит N мин → только сообщить».
export function stageCaption(sum: StageSummary | null): string | null {
  if (!sum) return null;
  const parts = ['Исполнители из плана'];
  if (sum.templateName) parts.push(`шаблон «${sum.templateName}»${sum.templateVersion ? ` v${sum.templateVersion}` : ''}`);
  const ex = sum.executor;
  if (ex?.silenceMinutes) {
    const act = ex.silenceAction ? SILENCE_ACTION[ex.silenceAction] : null;
    parts.push(`этап: молчит ${ex.silenceMinutes} мин${act ? ` → ${act}` : ''}`);
  }
  return parts.join(' · ');
}

function stateTag(stage: FeatureStage): { text: string; tone: BadgeTone } {
  const label = stageStatusLabel(stage);
  if (!label) return { text: 'готово', tone: 'success' };
  const tone: BadgeTone = label.tone === 'danger' ? 'danger' : label.tone === 'info' ? 'info'
    : stage.state === 'active' ? 'accent' : 'neutral';
  return { text: label.text, tone };
}

export interface FeatureStageAreaProps {
  run: FeatureRun;
  selectedId: string | null;
  isMobile: boolean;
  onOpenTask?: (taskId: string) => void;
  // «Открыть этап →»: переход к отдельному экрану этапа (F10).
  onOpenStage?: () => void;
  // Ссылка заметки о параллельной ветке: выбрать соседний узел графа (id стадии).
  onSelectStage?: (stageId: string) => void;
  // Дополнительное место под областью этапа.
  slot?: ReactNode;
}

interface ViewProps extends Omit<FeatureStageAreaProps, 'run' | 'selectedId'> {
  stage: FeatureStage;
  tasks: FeatureTask[];
  summary: StageSummary | null;
  // Вопросы человеку (из экрана этапа); блок рисуется, только если они есть
  questions?: StageQuestion[];
  onAnswered?: () => void;
}

// Тело области без загрузки: сводка приходит пропсом (в тестах подставляется напрямую).
export function FeatureStageAreaView({ stage, tasks, summary, questions, onAnswered, isMobile, onOpenTask, onOpenStage, onSelectStage, slot }: ViewProps) {
  const tag = stateTag(stage);
  const lane = laneWord(stage.line ?? stage.lane ?? null);
  const returns = summary?.returns ?? stage.returns ?? 0;
  const caption = stageCaption(summary);
  const ex = summary?.executor;
  const rows = branchRows(tasks, summary);
  const note = summary?.parallelNote ?? null;
  return (
    <div
      data-testid="feature-stage-area"
      data-stage-id={stageNodeId(stage)}
      style={{
        display: 'flex', flexDirection: 'column', gap: SP.md, padding: SP.md,
        borderRadius: R.lg, background: C.bgWhite, border: `1px solid ${C.borderLight}`,
        fontFamily: FONT.sans,
      }}
    >
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <span data-testid="feature-stage-area-title"
          style={{ fontFamily: FONT.serif, fontSize: FS.xl, fontWeight: 600, color: C.textHeading, overflowWrap: 'anywhere' }}>
          {nodeTitle(stage)}
        </span>
        {lane && <span data-testid="feature-stage-area-lane"><Badge size="xs" tone="neutral">{lane}</Badge></span>}
        <span data-testid="feature-stage-area-state"><Badge size="xs" tone={tag.tone} dot>{tag.text}</Badge></span>
        {returns > 0 && (
          <span data-testid="feature-stage-area-returns">
            <Badge size="xs" tone="warning">{returns} {plural(returns, 'возврат', 'возврата', 'возвратов')}</Badge>
          </span>
        )}
        {onOpenStage && tasks.length > 0 && (
          <Button size="sm" variant="primary" dataTestId="feature-stage-area-open"
            style={{ marginLeft: 'auto', ...(isMobile ? { minHeight: 40 } : undefined) }} onClick={onOpenStage}>
            Открыть этап →
          </Button>
        )}
      </div>
      {(caption || ex) && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: SP.xs }}>
          {ex && (
            <span data-testid="feature-stage-area-executor" style={{ fontSize: FS.sm, color: C.textSecondary, overflowWrap: 'anywhere' }}>
              {ex.name}{ex.roleTitle ? ` · ${ex.roleTitle}` : ''}{ex.modelLabel ? ` · ${ex.modelLabel}` : ''}
            </span>
          )}
          {caption && (
            <span data-testid="feature-stage-area-caption" style={{ fontSize: FS.xs, color: C.textMuted, overflowWrap: 'anywhere' }}>
              {caption}
            </span>
          )}
        </div>
      )}
      {summary?.kpi && <FeatureStageKpi kpi={summary.kpi} />}
      <div data-testid="feature-stage-area-branch" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm }}>
        <span style={{ fontSize: FS.xs, fontWeight: 600, color: C.textMuted }}>Задачи ветки</span>
        {rows.length === 0 ? (
          <div data-testid="feature-stage-area-empty" style={{ fontSize: FS.sm, color: C.textSecondary }}>
            У этапа пока нет задач.
          </div>
        ) : (
          rows.map(r => <TaskRow key={r.id} row={r} isMobile={isMobile} onOpenTask={onOpenTask} />)
        )}
      </div>
      {questions && questions.length > 0 && stage.taskIds?.[0] && (
        <FeatureStageQuestions taskId={stage.taskIds[0]} questions={questions} isMobile={isMobile} onAnswered={onAnswered ?? (() => {})} />
      )}
      {note && (
        <div data-testid="feature-stage-area-parallel"
          style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap', fontSize: FS.sm, color: C.textSecondary }}>
          <span style={{ flex: '1 1 200px', minWidth: 0, overflowWrap: 'anywhere' }}>
            Параллельно идёт {note.title}{note.personaName ? ` (${note.personaName})` : ''}, {note.doneCount} из {note.totalCount}.
            {' '}Ревью стартует, когда закончатся обе
          </span>
          {onSelectStage && (
            <Button size="xs" variant="ghost" style={isMobile ? { minHeight: 40 } : undefined}
              dataTestId="feature-stage-area-parallel-link" onClick={() => onSelectStage(note.stageId)}>
              Показать ветку
            </Button>
          )}
        </div>
      )}
      {slot}
    </div>
  );
}

export function FeatureStageArea({ run, selectedId, ...rest }: FeatureStageAreaProps) {
  const stage: FeatureStage | undefined = selectedId
    ? (run.stages ?? []).find(s => stageNodeId(s) === selectedId)
    : undefined;
  const { summary, questions, reload } = useStageSummary(stage?.taskIds?.[0] ?? null);
  if (!stage) {
    return (
      <div data-testid="feature-stage-area">
        <EmptyState compact icon={<Workflow size={ICON_SIZE.lg} strokeWidth={ICON_STROKE} />} title="Этап не выбран"
          subtitle="Здесь будет выбранный этап — исполнитель, задачи этапа, сколько ушло времени и что ждёт вас." />
      </div>
    );
  }
  return <FeatureStageAreaView stage={stage} tasks={stageTasksOf(run, stage)} summary={summary} questions={questions} onAnswered={reload} {...rest} />;
}
