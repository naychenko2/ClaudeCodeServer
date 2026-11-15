// Шапка экрана этапа (F12, макет stage-screen.html S3–S12): «Этап · Шаги» + чип линии + тег
// «доработка / в работе» + «↺ N возврат»; h2 «B3 · исполнение по этапу»; строка исполнителя
// (аватар, «Имя · Должность», «Модель · молчит N мин → действие»); чипы задач «B1 ✓ B2 ✓ B3 ●»;
// полоса из пяти плиток. Всё рисуется по данным выбранной задачи (stageSummary), фронт только
// подписывает: «доработка» — по роли задачи, а не по умолчанию (противоречие §2.3 п.2 макета).

import { C, FONT, FS, R, SP, TB } from '../../lib/design';
import { Badge } from '../ui/Badge';
import { Button } from '../ui/Button';
import { plural } from '../../lib/plural';
import { laneWord } from '../../lib/roleLabel';
import { formatDuration } from '../../lib/chatGroups';
import { personaInitials } from '../../lib/personas';
import { formatTokens } from './FeatureStatsFormat';
import type { StageScreen, StageSummaryExecutor, StageSummaryKpi, StageSummaryTask } from '../../types/featureStage';

const SILENCE_ACTION: Record<string, string> = {
  notify: 'только сообщить', nudge: 'напомнить исполнителю', stop: 'остановить и сообщить',
};
const NO_MEASURE = 'нет замера';

// Тег состояния: доработка — по роли задачи; иначе «в работе» только у идущей задачи.
export function stageStateTag(s: Pick<StageScreen, 'role' | 'status'>): string | null {
  if (s.role === 'rework') return 'доработка';
  return s.status === 'inProgress' ? 'в работе' : null;
}

// «Модель · молчит 45 мин → только сообщить»; «(своя ячейка этапа)» — когда модель задана этапу.
export function executorModelLine(ex: StageSummaryExecutor): string {
  const parts: string[] = [];
  if (ex.modelLabel) parts.push(`${ex.modelLabel}${ex.modelIsOwnCell ? ' (своя ячейка этапа)' : ''}`);
  if (ex.silenceMinutes) {
    const act = ex.silenceAction ? SILENCE_ACTION[ex.silenceAction] : null;
    parts.push(`молчит ${ex.silenceMinutes} мин${act ? ` → ${act}` : ''}`);
  }
  return parts.join(' · ');
}

// Пометка задачи в чипе: готово ✓, идёт/ждёт ●, не начата — без пометки.
export function taskMark(state: string | null | undefined): string {
  if (state === 'done') return ' ✓';
  if (state === 'inProgress' || state === 'waiting') return ' ●';
  return '';
}

export interface KpiPlate { key: string; label: string; value: string }

// Пять плиток макета; «≈» у легаси-журнала, «нет замера» вместо ложных нулей.
export function kpiPlates(kpi: StageSummaryKpi): KpiPlate[] {
  const a = kpi.approx ? '≈ ' : '';
  const dur = (m: number) => (kpi.approx && m <= 0 ? NO_MEASURE : `${a}${formatDuration(m)}`);
  const tokens = kpi.tokens ? kpi.tokens.input + kpi.tokens.output : null;
  return [
    { key: 'total', label: 'Всего', value: dur(kpi.totalMinutes) },
    { key: 'exec', label: 'Исполнитель', value: dur(kpi.execMinutes) },
    { key: 'review', label: 'Ревью', value: dur(kpi.reviewMinutes) },
    { key: 'waited', label: 'Ждало вас', value: dur(kpi.waitedHumanMinutes) },
    { key: 'tokens', label: 'Токены', value: tokens == null ? NO_MEASURE : `${a}${formatTokens(tokens)}` },
  ];
}

export function StageHead({ s, stageTitle }: { s: StageScreen; stageTitle?: string }) {
  const sum = s.stageSummary;
  const lane = laneWord(s.stage?.lane ?? null);
  const tag = stageStateTag(s);
  const returns = sum?.returns ?? 0;
  return (
    <div data-testid="stage-head" style={{ display: 'flex', flexDirection: 'column', gap: SP.sm, minWidth: 0 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap' }}>
        <span data-testid="stage-head-eyebrow" style={{ fontSize: FS.xs, color: C.textMuted }}>
          {`Этап${stageTitle ? ` · ${stageTitle}` : ''}`}
        </span>
        {lane && <span data-testid="stage-head-lane"><Badge size="xs" tone="neutral">{lane}</Badge></span>}
        {tag && <span data-testid="stage-head-tag"><Badge size="xs" tone={tag === 'доработка' ? 'warning' : 'accent'} dot>{tag}</Badge></span>}
        {returns > 0 && (
          <span data-testid="stage-head-returns">
            <Badge size="xs" tone="warning">{`↺ ${returns} ${plural(returns, 'возврат', 'возврата', 'возвратов')}`}</Badge>
          </span>
        )}
      </div>
      <h2 data-testid="stage-screen-title" style={{ margin: 0, fontFamily: FONT.serif, fontSize: FS.xl, fontWeight: 600, color: C.textHeading, overflowWrap: 'anywhere' }}>
        {s.stepRef ? `${s.stepRef} · ` : ''}{s.title}
      </h2>
      {sum?.executor && <ExecutorLine ex={sum.executor} />}
    </div>
  );
}

function ExecutorLine({ ex }: { ex: StageSummaryExecutor }) {
  const model = executorModelLine(ex);
  return (
    <div data-testid="stage-head-executor" style={{ display: 'flex', alignItems: 'center', gap: SP.sm, minWidth: 0 }}>
      <span aria-hidden style={{
        flexShrink: 0, width: 32, height: 32, borderRadius: R.full, display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
        background: C.bgInset, border: `1px solid ${C.borderLight}`, fontSize: FS.xs, fontWeight: 600, color: C.textSecondary,
      }}>{personaInitials(ex.name)}</span>
      <span style={{ display: 'flex', flexDirection: 'column', minWidth: 0 }}>
        <span data-testid="stage-head-executor-name" style={{ fontSize: FS.sm, color: C.textHeading, overflowWrap: 'anywhere' }}>
          {`${ex.name}${ex.roleTitle ? ` · ${ex.roleTitle}` : ''}`}
        </span>
        {model && <span data-testid="stage-head-executor-model" style={{ fontSize: FS.xs, color: C.textMuted, overflowWrap: 'anywhere' }}>{model}</span>}
      </span>
    </div>
  );
}

export function StagePlates({ kpi }: { kpi: StageSummaryKpi }) {
  return (
    <div data-testid="stage-plates" style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(110px, 1fr))', gap: SP.sm }}>
      {kpiPlates(kpi).map(p => (
        <div key={p.key} data-testid={`stage-plate-${p.key}`} style={{
          display: 'flex', flexDirection: 'column', gap: SP.xs, padding: SP.sm, minWidth: 0,
          borderRadius: R.md, background: C.bgInset, border: `1px solid ${C.borderLight}`,
        }}>
          <span style={{ fontSize: FS.xs, color: C.textMuted }}>{p.label}</span>
          <span data-testid={`stage-plate-${p.key}-value`} style={{ fontSize: FS.md, fontWeight: 600, color: C.textHeading, overflowWrap: 'anywhere' }}>{p.value}</span>
        </div>
      ))}
    </div>
  );
}

export interface PickerTask { id: string; title: string; stepRef?: string | null }

// Переключатель задач этапа чипами; пометки — из stageSummary.tasks (по taskId), если пришли.
export function StageTaskPicker({ tasks, activeId, marks, isMobile, onPick }: {
  tasks: PickerTask[]; activeId: string | null; marks: StageSummaryTask[] | null; isMobile: boolean; onPick: (id: string) => void;
}) {
  return (
    <div data-testid="stage-screen-picker" role="group" aria-label="Задача этапа" style={{ display: 'flex', flexWrap: 'wrap', gap: SP.xs }}>
      {tasks.map(t => (
        <Button key={t.id} size="xs" variant={t.id === activeId ? 'primary' : 'ghost'}
          style={isMobile ? { minHeight: TB.iconHitMobile } : undefined}
          dataTestId={`stage-screen-pick-${t.id}`}
          onClick={() => onPick(t.id)}>
          {(t.stepRef || t.title) + taskMark(marks?.find(m => m.taskId === t.id)?.state)}
        </Button>
      ))}
    </div>
  );
}
