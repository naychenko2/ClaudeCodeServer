// Симуляция паузы планирования (dev-only, #/team-plan-sim): проигрывает wire-события
// режима «Командная реализация» через настоящий chatReducer и показывает, как ведёт
// себя плашка «Команда готовит план…» в ленте. Кнопки повторяют ходы бэкенда:
// вход в стадию planning → карточка плана (стадия confirming) ИЛИ карточка отказа
// (стадия остаётся planning) → повторное планирование после отказа.
import { useReducer } from 'react';
import type { ChatState } from '../lib/chatReducer';
import { applyServerMessage, initialChatState } from '../lib/chatReducer';
import { teamPlanningIndicatorVisible } from '../lib/teamImplement';
import { TeamPlanningIndicator } from '../components/chat/TeamPlanningIndicator';
import type { ServerMessage } from '../types';
import { C, FS, FONT, R, SP } from '../lib/design';

const BUDGET = {
  tasksUsed: 0, wavesUsed: 0, runsUsed: 0, retriesUsed: 0, wakeupsUsed: 0,
  maxTasks: 6, maxWaves: 4, maxRuns: 20, maxRetries: 3, maxWakeups: 3,
};

// Те же поля, что у wire-событий бэкенда (Protocol/ServerMessage.cs)
const MSG = {
  planning: {
    type: 'team_implement', active: true, stage: 'planning', waveNumber: 0,
    autoWaves: true, coordinatorPersonaId: 'p-coord', plannerPersonaId: 'p-plan',
    executorPersonaIds: ['p-exec'], budget: BUDGET, planCardId: null, modeLocked: true,
  },
  confirming: {
    type: 'team_implement', active: true, stage: 'confirming', waveNumber: 0,
    autoWaves: true, coordinatorPersonaId: 'p-coord', plannerPersonaId: 'p-plan',
    executorPersonaIds: ['p-exec'], budget: BUDGET, planCardId: 'plan-1', modeLocked: false,
  },
  planCard: {
    type: 'team_plan', planId: 'plan-1', resolved: false, approved: null,
    plan: {
      id: 'plan-1', request: 'Показать, что штаб готовит план', summary: 'Индикатор паузы планирования',
      createdAt: '2026-08-04T12:00:00Z', waveCount: 1, executorCount: 1,
      subtasks: [], version: 1, assumptions: [], changes: [],
    },
  },
  planFailed: {
    type: 'team_escalation', escalationId: 'esc-1', kind: 'productDecision',
    title: 'План не построился: планировщик не уложился во время',
    details: 'Попробуйте повторить планирование.',
    actions: [{ id: 'retryPlan', label: 'Повторить планирование' }],
    taskId: null, wave: 0, resolved: false, chosenActionId: null,
  },
  planFailedResolved: {
    type: 'team_escalation', escalationId: 'esc-1', kind: 'productDecision',
    title: 'План не построился: планировщик не уложился во время',
    details: 'Попробуйте повторить планирование.',
    actions: [{ id: 'retryPlan', label: 'Повторить планирование' }],
    taskId: null, wave: 0, resolved: true, chosenActionId: 'retryPlan',
  },
} as const;

function SimButton({ label, onClick, disabled }: { label: string; onClick: () => void; disabled?: boolean }) {
  return (
    <button
      data-testid={`sim-${label}`}
      onClick={onClick}
      disabled={disabled}
      style={{
        padding: '7px 12px', borderRadius: R.lg, border: `1px solid ${C.border}`,
        background: C.bgWhite, color: C.textHeading, cursor: disabled ? 'default' : 'pointer',
        fontFamily: FONT.sans, fontSize: FS.sm, opacity: disabled ? 0.5 : 1,
      }}
    >
      {label}
    </button>
  );
}

// Служебный экшен сброса ленты симуляции — не wire-событие, в прод-код не попадает
const RESET = { type: '__sim_reset' } as unknown as ServerMessage;

export function TeamPlanSimPage() {
  const [state, dispatch] = useReducer(
    (s: ChatState, msg: ServerMessage): ChatState =>
      msg === RESET ? initialChatState() : applyServerMessage(s, msg),
    undefined,
    initialChatState,
  );

  // Карточки плана/отказа рисуем заглушками: полная отрисовка тянет персоны и контексты,
  // а в симуляции важен ход индикатора. Реальны здесь редьюсер, предикат и сама плашка.
  const showIndicator = teamPlanningIndicatorVisible(
    state.teamImplement && state.teamImplement.active ? state.teamImplement : null,
    state.items,
  );
  const stage = state.teamImplement?.stage ?? null;

  return (
    <div style={{ minHeight: '100vh', background: C.bgMain, padding: SP.xl, fontFamily: FONT.sans }}>
      <div style={{ maxWidth: 720, margin: '0 auto', display: 'flex', flexDirection: 'column', gap: SP.lg }}>
        <div>
          <div style={{ fontSize: FS.lg, fontWeight: 700, color: C.textHeading }}>
            Симуляция паузы планирования
          </div>
          <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: 2 }}>
            События идут через настоящий chatReducer. Стадия режима:
            {' '}<b data-testid="sim-stage">{stage ?? '—'}</b>
          </div>
        </div>

        <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
          <SimButton label="start-planning" onClick={() => dispatch(MSG.planning as unknown as ServerMessage)} />
          <SimButton label="plan-ready" onClick={() => {
            dispatch(MSG.confirming as unknown as ServerMessage);
            dispatch(MSG.planCard as unknown as ServerMessage);
          }} />
          <SimButton label="plan-failed" onClick={() => dispatch(MSG.planFailed as unknown as ServerMessage)} />
          <SimButton label="retry-planning" onClick={() => {
            dispatch(MSG.planFailedResolved as unknown as ServerMessage);
            dispatch(MSG.planning as unknown as ServerMessage);
          }} />
          <SimButton label="reset" onClick={() => dispatch(RESET)} />
        </div>

        {/* Лента */}
        <div style={{
          display: 'flex', flexDirection: 'column', gap: SP.sm,
          background: C.bgPanel, border: `1px solid ${C.border}`,
          borderRadius: R.xl, padding: SP.lg, minHeight: 200,
        }} data-testid="sim-feed">
          <div style={{
            alignSelf: 'flex-end', maxWidth: '70%', padding: '8px 12px',
            background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
            fontSize: FS.base, color: C.textPrimary,
          }}>
            Покажите, что штаб готовит план — в ленте сейчас тихо
          </div>

          {state.items.map((it, i) => {
            if (it.kind === 'team_plan') {
              return (
                <div key={`plan-${i}`} data-testid="sim-plan-card" style={{
                  border: `1px solid ${C.border}`, borderLeft: `4px solid ${C.accent}`,
                  borderRadius: R.xl, padding: '10px 14px', background: C.bgCard,
                  fontSize: FS.base, color: C.textHeading, fontWeight: 600,
                }}>
                  План: {it.plan.summary} (карточка плана)
                </div>
              );
            }
            if (it.kind === 'team_escalation') {
              return (
                <div key={`esc-${i}`} data-testid="sim-escalation-card" style={{
                  border: `1px solid ${C.border}`, borderLeft: `4px solid ${C.warning}`,
                  borderRadius: R.xl, padding: '10px 14px', background: C.bgCard,
                  fontSize: FS.base,
                }}>
                  <span style={{ color: C.textHeading, fontWeight: 600 }}>{it.escalation.title}</span>
                  {' '}
                  <span style={{ color: C.textMuted, fontSize: FS.sm }}>
                    {it.escalation.resolved ? '(решено)' : '(ждёт решения)'}
                  </span>
                </div>
              );
            }
            return null;
          })}

          {showIndicator && <TeamPlanningIndicator />}
        </div>
      </div>
    </div>
  );
}
