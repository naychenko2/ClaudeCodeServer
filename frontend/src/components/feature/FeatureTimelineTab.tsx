import { useMemo, useState } from 'react';
import { Info } from 'lucide-react';
import { C, R, FS, FONT } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { Tooltip } from '../ui/Tooltip';
import {
  stageName, stageStatusLabel, stageBoundsLabel, stageDurationLabel,
  stageDotStyle, stageLineStyle, buildTaskMap, stageStatusBadge, ExecutorRow,
} from './FeatureTimelineBits';
import type { FeatureRun, FeatureStage, StageKey } from '../../types/featureRun';

// Вкладка «Хронология» (волна 3, Ф2a, макет features-overview-3-final §5): поясняющая
// строка + вертикальный список 7 этапов в порядке жизненного цикла (cut → plan → prod →
// review → qa → final → merge). У каждого: точка/линия, имя, состояние, границы, «≈»,
// исполнители и плашки «ждёт вас · N мин» / «стоит · N мин». Один раскрытый этап за раз;
// текущий (active) открыт по умолчанию. Шаги/витки/сводка/график — не здесь (Ф2b/Ф7/Ф8).

// Поясняющая строка (макет §5): Info 13, 11 textSecondary, bgCard, borderLight, R.md, поля 6 10.
function TimelineNote() {
  return (
    <div
      data-testid="timeline-note"
      style={{
        display: 'flex', gap: 8, alignItems: 'flex-start',
        background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.md,
        padding: '6px 10px', marginBottom: 14, fontFamily: FONT.sans,
        fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.45,
      }}
    >
      <Info size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} aria-hidden style={{ flexShrink: 0, marginTop: 1 }} />
      <span>
        Порядок этапов, их состояние и кто работал — точные. Длительность со знаком «≈» — по
        задачам этапа. Ревью идёт по шагам, поэтому пересекается с производством.
      </span>
    </div>
  );
}

// Строка этапа: точка + линия + заголовок (имя · состояние · «≈») + границы + (раскрыто)
// карточка с исполнителями и плашкой состояния. Клик по заголовку — раскрыть/схлопнуть.
function StageRow({ stage, isLast, expanded, onToggle, taskMap, now, featureId, isMobile = false }: {
  stage: FeatureStage; isLast: boolean; expanded: boolean;
  onToggle: () => void; taskMap: Map<string, import('../../types/featureRun').FeatureTask>;
  now: number; featureId: string; isMobile?: boolean;
}) {
  const status = stageStatusLabel(stage);
  const bounds = stageBoundsLabel(stage);
  const duration = stageDurationLabel(stage.approxMinutes);
  const badge = stageStatusBadge(stage, taskMap, now);
  const nameMuted = stage.state === 'pending' || stage.state === 'absent';
  const line = stageLineStyle(stage, isLast);

  const statusColor =
    status?.tone === 'info' ? C.info :
    status?.tone === 'danger' ? C.dangerText :
    C.textMuted;

  return (
    <div data-testid={`timeline-stage-${stage.key}`} style={{ position: 'relative', paddingLeft: 28, paddingBottom: 16 }}>
      {/* Точка этапа (14) */}
      <span style={{ ...stageDotStyle(stage), position: 'absolute', left: 2, top: 6 }} />
      {/* Линия к следующему этапу */}
      {line && <span style={line} />}
      {/* Заголовок: имя · состояние · «≈» (кликабельный) */}
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={expanded}
        data-testid={`timeline-stage-header-${stage.key}`}
        style={{
          display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap',
          background: 'transparent', border: 'none', cursor: 'pointer',
          padding: '4px 0', minHeight: isMobile ? 40 : 26,
          fontFamily: FONT.sans, width: '100%', textAlign: 'left',
        }}
      >
        <span style={{ fontSize: FS.md, fontWeight: nameMuted ? 600 : 700, color: nameMuted ? C.textMuted : C.textHeading }}>
          {stageName(stage.key)}
        </span>
        {status && (
          <span style={{ fontSize: FS.xs, fontWeight: 600, color: statusColor, whiteSpace: 'nowrap' }}>
            {status.text}
          </span>
        )}
        {duration && (
          <Tooltip
            content="Приблизительно: сумма работы задач этапа — от старта исполнителя до сдачи. Ожидание и простой внутри не вычитаются, параллельные задачи складываются."
          >
            <span
              data-testid="timeline-stage-duration"
              style={{
                marginLeft: 'auto', fontFamily: FONT.mono, fontSize: FS.xs,
                color: C.textSecondary, whiteSpace: 'nowrap',
                padding: '2px 4px', borderRadius: R.sm, cursor: 'help',
                minHeight: isMobile ? 40 : undefined, display: 'inline-flex', alignItems: 'center',
              }}
            >
              {duration}
            </span>
          </Tooltip>
        )}
      </button>
      {/* Границы: «dd.mm · ЧЧ:ММ – ЧЧ:ММ» / «с dd.mm · ЧЧ:ММ» */}
      {bounds && (
        <div data-testid="timeline-stage-bounds" style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, marginTop: 1 }}>
          {bounds}
        </div>
      )}
      {/* Раскрыто: карточка с плашкой состояния и исполнителями */}
      {expanded && (
        <div
          data-testid={`timeline-stage-body-${stage.key}`}
          style={{
            background: C.bgCard,
            border: `1px solid ${badge?.tone === 'danger' ? C.dangerBorder : badge?.tone === 'info' ? C.info : C.borderLight}`,
            borderRadius: R.lg, padding: '8px 10px 10px', marginTop: 6,
          }}
        >
          {badge && (
            <div
              data-testid="timeline-stage-badge"
              style={{
                display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap',
                background: badge.tone === 'info' ? C.infoBg : C.dangerBg,
                borderRadius: R.md, padding: '6px 10px', marginBottom: 6,
                fontFamily: FONT.sans, fontSize: FS.sm,
              }}
            >
              <span style={{ fontWeight: 700, color: badge.tone === 'danger' ? C.dangerText : C.textHeading }}>
                {badge.text}
              </span>
            </div>
          )}
          {stage.executors.length > 0 ? (
            stage.executors.map((x, i) => (
              <ExecutorRow
                key={`${x.taskId}-${i}`}
                x={x}
                task={x.taskId ? taskMap.get(x.taskId) : undefined}
                now={now}
                featureId={featureId}
                isMobile={isMobile}
              />
            ))
          ) : (
            <div data-testid="timeline-stage-no-executors" style={{ fontSize: FS.sm, color: C.textMuted }}>
              Исполнителей нет
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// Вкладка «Хронология»: поясняющая строка + список 7 этапов. `now` — фиксированный
// момент (Date.now на рендер) для минут «идёт/ждёт/стоит»; стабильен в пределах кадра.
export function FeatureTimelineTab({ run, isMobile = false }: { run: FeatureRun; isMobile?: boolean }) {
  const stages = run.stages ?? [];
  const taskMap = useMemo(() => buildTaskMap(run), [run]);
  const now = useMemo(() => Date.now(), []);

  // Текущий этап: первый active (в порядке жизненного цикла); его открыто по умолчанию.
  const currentKey = useMemo<StageKey | null>(() => {
    const active = stages.find(s => s.state === 'active');
    return active ? active.key : null;
  }, [stages]);

  // Один раскрытый этап за раз; null — все схлопнуты.
  const [expanded, setExpanded] = useState<StageKey | null>(currentKey);
  const toggle = (key: StageKey) => setExpanded(cur => (cur === key ? null : key));

  return (
    <div data-testid="feature-timeline-tab" style={{ fontFamily: FONT.sans }}>
      <TimelineNote />
      {stages.map((stage, i) => (
        <StageRow
          key={stage.key}
          stage={stage}
          isLast={i === stages.length - 1}
          expanded={expanded === stage.key}
          onToggle={() => toggle(stage.key)}
          taskMap={taskMap}
          now={now}
          featureId={run.featureId}
          isMobile={isMobile}
        />
      ))}
    </div>
  );
}
