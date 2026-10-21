import { useEffect, useMemo, useRef, useState } from 'react';
import { Info } from 'lucide-react';
import { useFeatureStageFocus, clearFeatureStageFocus } from '../../lib/featurePage';
import { EMPTY_NAV_MARKS, buildNavMarks, type NavMarks } from '../../lib/featureNavMarks';
import { C, R, FS, SP, FONT } from '../../lib/design';
import { ICON_SIZE, ICON_STROKE } from '../ui/icons';
import { Tooltip } from '../ui/Tooltip';
import {
  stageName, stageStatusLabel, stageBoundsLabel, stageDurationLabel,
  stageDotStyle, stageLineStyle, buildTaskMap, stageStatusBadge, ExecutorRow,
  StepRow, stepModel,
} from './FeatureTimelineBits';
import type { FeatureRun, FeatureStage, FeatureStepChain, StageKey } from '../../types/featureRun';

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
        display: 'flex', gap: SP.sm, alignItems: 'flex-start',
        background: C.bgCard, border: `1px solid ${C.borderLight}`, borderRadius: R.md,
        padding: `${SP.xs}px ${SP.sm}px`, marginBottom: SP.md, fontFamily: FONT.sans,
        fontSize: FS.xs, color: C.textSecondary, lineHeight: 1.45,
      }}
    >
      <Info size={ICON_SIZE.xs} strokeWidth={ICON_STROKE} aria-hidden style={{ flexShrink: 0, marginTop: SP.xxs }} />
      <span>
        Порядок этапов, их состояние и кто работал — точные. Длительность со знаком «≈» — по
        задачам этапа. Ревью идёт по шагам, поэтому пересекается с производством.
      </span>
    </div>
  );
}

// Строка этапа: точка + линия + заголовок (имя · состояние · «≈») + границы + (раскрыто)
// карточка с исполнителями и плашкой состояния; у «Производство» — ещё и шаги (Ф2b).
// Клик по заголовку — раскрыть/схлопнуть.
function StageRow({ stage, isLast, expanded, onToggle, taskMap, now, featureId, featureTitle = null,
  chains, showSteps = false, expandedStep = null, onToggleStep, marks = EMPTY_NAV_MARKS, isMobile = false }: {
  stage: FeatureStage; isLast: boolean; expanded: boolean;
  onToggle: () => void; taskMap: Map<string, import('../../types/featureRun').FeatureTask>;
  now: number; featureId: string; featureTitle?: string | null;
  chains?: FeatureStepChain[]; showSteps?: boolean;
  expandedStep?: string | null; onToggleStep?: (ref: string) => void;
  marks?: NavMarks;
  isMobile?: boolean;
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
    <div data-testid={`timeline-stage-${stage.key}`} style={{ position: 'relative', paddingLeft: SP.xl, paddingBottom: SP.lg }}>
      {/* Точка этапа (14) */}
      <span style={{ ...stageDotStyle(stage), position: 'absolute', left: SP.xxs, top: SP.xs }} />
      {/* Линия к следующему этапу */}
      {line && <span style={line} />}
      {/* Заголовок: имя · состояние · «≈» (кликабельный) */}
      <button
        type="button"
        onClick={onToggle}
        aria-expanded={expanded}
        data-testid={`timeline-stage-header-${stage.key}`}
        style={{
          display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap',
          background: 'transparent', border: 'none', cursor: 'pointer',
          padding: `${SP.xs}px 0`, minHeight: isMobile ? 40 : 26,
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
                padding: `${SP.xxs}px ${SP.xs}px`, borderRadius: R.sm, cursor: 'help',
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
        <div data-testid="timeline-stage-bounds" style={{ fontFamily: FONT.mono, fontSize: FS.xs, color: C.textMuted, marginTop: SP.xxs }}>
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
            borderRadius: R.lg, padding: `${SP.sm}px ${SP.sm}px ${SP.sm}px`, marginTop: SP.xs,
          }}
        >
          {badge && (
            <div
              data-testid="timeline-stage-badge"
              style={{
                display: 'flex', alignItems: 'center', gap: SP.sm, flexWrap: 'wrap',
                background: badge.tone === 'info' ? C.infoBg : C.dangerBg,
                borderRadius: R.md, padding: `${SP.xs}px ${SP.sm}px`, marginBottom: SP.xs,
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
                featureTitle={featureTitle}
                isMobile={isMobile}
              />
            ))
          ) : (
            <div data-testid="timeline-stage-no-executors" style={{ fontSize: FS.sm, color: C.textMuted }}>
              Исполнителей нет
            </div>
          )}
          {/* Ф2b: шаги и витки — только у «Производство» */}
          {showSteps && (
            <div
              data-testid="timeline-steps"
              style={{ borderTop: `1px solid ${C.borderLight}`, marginTop: SP.sm, paddingTop: SP.sm }}
            >
              {(chains && chains.length > 0) ? (
                chains.map(chain => {
                  const ref = chain.stepRef || chain.title;
                  return (
                    <StepRow
                      key={ref}
                      chain={chain}
                      model={stepModel(stage.executors, chain.stepRef)}
                      expanded={expandedStep === ref}
                      onToggle={() => onToggleStep?.(ref)}
                      featureId={featureId}
                      featureTitle={featureTitle}
                      task={chain.taskIds[0] ? taskMap.get(chain.taskIds[0]) : undefined}
                      marks={marks}
                      isMobile={isMobile}
                    />
                  );
                })
              ) : (
                <div data-testid="feature-steps-empty" style={{ fontFamily: FONT.sans }}>
                  <div style={{ fontSize: FS.md, fontWeight: 700, color: C.textHeading, padding: `${SP.xs}px 0` }}>
                    Шагов пока нет
                  </div>
                  <div style={{ fontSize: FS.sm, color: C.textMuted }}>
                    Появятся, когда Егор составит план.
                  </div>
                </div>
              )}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

// Вкладка «Хронология»: поясняющая строка + список 7 этапов. `now` — фиксированный
// момент (Date.now на рендер) для минут «идёт/ждёт/стоит»; стабильен в пределах кадра.
// Ф2b: у «Производство» — шаги (run.stepChains) и их витки; один шаг раскрыт за раз.
export function FeatureTimelineTab({ run, marks: marksProp, isMobile = false }: { run: FeatureRun; marks?: NavMarks; isMobile?: boolean }) {
  const stages = run.stages ?? [];
  const chains = run.stepChains ?? [];
  const taskMap = useMemo(() => buildTaskMap(run), [run]);
  // Пометки «в архиве»/«задача закрыта»: страница даёт полные (с чатами), без пропа —
  // из самого агрегата (исполнители стадий + задачи).
  const marks = useMemo(() => marksProp ?? buildNavMarks(run), [marksProp, run]);
  const now = useMemo(() => Date.now(), []);
  // Название фичи (epic.title) — для «← к фиче {название}» в шапке чата (Ф-pl-b).
  const featureTitle = run.epic?.title ?? null;

  // Текущий этап: первый active (в порядке жизненного цикла); его открыто по умолчанию.
  const currentKey = useMemo<StageKey | null>(() => {
    const active = stages.find(s => s.state === 'active');
    return active ? active.key : null;
  }, [stages]);

  // Один раскрытый этап за раз; null — все схлопнуты.
  const [expanded, setExpanded] = useState<StageKey | null>(currentKey);
  const toggle = (key: StageKey) => setExpanded(cur => (cur === key ? null : key));

  // Ф2b: один раскрытый шаг за раз (ключ = stepRef || title); null — все схлопнуты.
  const [expandedStep, setExpandedStep] = useState<string | null>(null);
  const toggleStep = (ref: string) => setExpandedStep(cur => (cur === ref ? null : ref));

  // Якорь этапа (Ф-pl-b): клик по точке на плашке чата открыл страницу на «Хронологии» —
  // раскрываем/прокручиваем на этот этап. One-shot по seq (тот же приём, что focusFeatureStep):
  // применённый seq запоминаем — ререндер/повторное чтение не сбрасывают ручной выбор.
  const stageFocus = useFeatureStageFocus();
  const stageFocusSeq = useRef(0);
  useEffect(() => {
    if (!stageFocus || stageFocus.seq === stageFocusSeq.current) return;
    stageFocusSeq.current = stageFocus.seq;
    setExpanded(stageFocus.key as StageKey);
    const el = document.querySelector(`[data-testid="timeline-stage-${stageFocus.key}"]`);
    if (el && 'scrollIntoView' in el) {
      (el as HTMLElement).scrollIntoView({ block: 'center', behavior: 'smooth' });
    }
    // M4: якорь прочитан — гасим. Повторный монтаж вкладки (смена вкладки/фичи) старый
    // этап не раскроет, даже если последнее открытие было без stage.
    clearFeatureStageFocus();
  }, [stageFocus]);

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
          featureTitle={featureTitle}
          chains={stage.key === 'prod' ? chains : undefined}
          showSteps={stage.key === 'prod'}
          expandedStep={stage.key === 'prod' ? expandedStep : null}
          onToggleStep={stage.key === 'prod' ? toggleStep : undefined}
          marks={marks}
          isMobile={isMobile}
        />
      ))}
    </div>
  );
}
