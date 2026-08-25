// Симуляция паузы планирования (dev-only, #/team-plan-sim): проигрывает wire-события
// режима «Командная реализация» через настоящий chatReducer и показывает, как ведёт
// себя полоса «Практика ждёт вашего решения» и карточка «Готовит план…» в ленте.
//
// Кнопки соответствуют ходам бэкенда: вход в стадию planning → карточка плана
// (стадия confirming) ИЛИ карточка отказа (стадия остаётся planning) → повторное
// планирование. Плюс сценарии под правки командной реализации: открытые
// эскалации (полоса с мягкой подсветкой), планировщик с персоной и без (бэкенд
// старый/новый), ход координатора (staffNote/auto гасятся, реплики персоны
// чата остаются).
import { useMemo, useReducer } from 'react';
import type { ChatItem, Persona } from '../types';
import type { ChatState } from '../lib/chatReducer';
import { applyServerMessage, initialChatState } from '../lib/chatReducer';
import {
  resolvePlannerPersonaId, teamPlanningIndicatorVisible,
} from '../lib/teamImplement';
import { TeamPlanningIndicator } from '../components/chat/TeamPlanningIndicator';
import { EscalationStickyBanner, findOpenEscalations } from '../components/chat/EscalationStickyBanner';
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
  planningNewPlanner: {
    // После правки бэка: тот же стади-планинг + plannerPersonaId уже заполнен
    type: 'team_implement', active: true, stage: 'planning', waveNumber: 0,
    autoWaves: true, coordinatorPersonaId: 'p-coord', plannerPersonaId: 'p-planner-2',
    executorPersonaIds: ['p-exec'], budget: BUDGET, planCardId: null, modeLocked: true,
  },
  confirming: {
    type: 'team_implement', active: true, stage: 'confirming', waveNumber: 0,
    autoWaves: true, coordinatorPersonaId: 'p-coord', plannerPersonaId: 'p-plan',
    executorPersonaIds: ['p-exec'], budget: BUDGET, planCardId: 'plan-1', modeLocked: false,
  },
  // Транзитное событие team_planning (новое поле personaId). Симулирует бэкенд,
  // протащивший ResolvePlanner в BroadcastTeamPlanningStartedAsync
  planningStartedNew: {
    type: 'team_planning', start: true, success: false,
    subtaskCount: 0, waveCount: 0, elapsedMs: 0,
    route: 'planner-2', failure: null, promptChars: 0, responseChars: 0,
    personaId: 'p-planner-2',
  },
  // Старый бэкенд: событие без personaId. Фолбэк resolvePlannerPersonaId должен
  // достать plannerPersonaId из team_implement.plannerPersonaId
  planningStartedLegacy: {
    type: 'team_planning', start: true, success: false,
    subtaskCount: 0, waveCount: 0, elapsedMs: 0,
    route: 'planner-legacy', failure: null, promptChars: 0, responseChars: 0,
  },
  planningDone: {
    type: 'team_planning', start: false, success: true,
    subtaskCount: 5, waveCount: 2, elapsedMs: 46_000,
    route: 'planner-2', failure: null, promptChars: 0, responseChars: 0,
    personaId: 'p-planner-2',
  },
  planCard: {
    type: 'team_plan', planId: 'plan-1', resolved: false, approved: null,
    plan: {
      id: 'plan-1', request: 'Показать, что штаб готовит план', summary: 'Индикатор паузы планирования',
      createdAt: '2026-08-04T12:00:00Z', waveCount: 1, executorCount: 1,
      subtasks: [], version: 1, assumptions: [], changes: [],
    },
  },
  escalationOpen: {
    type: 'team_escalation', escalationId: 'esc-1', kind: 'productDecision',
    title: 'План не построился: планировщик не уложился во время',
    details: 'Попробуйте повторить планирование.',
    actions: [{ id: 'retryPlan', label: 'Повторить планирование' }],
    taskId: null, wave: 0, resolved: false, chosenActionId: null,
  },
  escalationResolved: {
    type: 'team_escalation', escalationId: 'esc-1', kind: 'productDecision',
    title: 'План не построился: планировщик не уложился во время',
    details: 'Попробуйте повторить планирование.',
    actions: [{ id: 'retryPlan', label: 'Повторить планирование' }],
    taskId: null, wave: 0, resolved: true, chosenActionId: 'retryPlan',
  },
  // Вторая/третья открытые карточки — для проверки счётчика остальных
  escalationBlocker: {
    type: 'team_escalation', escalationId: 'esc-2', kind: 'blocker',
    title: 'Исполнитель «Катя» уперлась в типизацию',
    details: 'Не понимает, какой формат у Result<T, E> в нашем коде.',
    actions: [{ id: 'explain', label: 'Объяснить подробнее' }],
    taskId: 'task-7', wave: 1, resolved: false, chosenActionId: null,
  },
  escalationBudget: {
    type: 'team_escalation', escalationId: 'esc-3', kind: 'budgetExhausted',
    title: 'Бюджет итерации исчерпан: задачи 12 из 12, волны 4 из 4',
    details: 'Продолжать нечем — поднимите бюджет или завершите итерацию.',
    actions: [
      { id: 'extend', label: 'Поднять бюджет' },
      { id: 'finish', label: 'Завершить итерацию' },
    ],
    taskId: null, wave: 4, resolved: false, chosenActionId: null,
  },
  // Ход координатора: служебные триггеры + ответ репликой персоны чата.
  // Это ЧАТ-ИТЕМЫ, а не wire-события: отдаём их служебным «emit_item», который
  // добавляет ChatItem в items напрямую (мимо applyServerMessage)
  coordStaffTrigger: {
    kind: 'user_message',
    text: 'Ответ на карточку передан координатору',
    staffNote: 'Ответ на карточку передан координатору',
  },
  coordAutoTrigger: {
    kind: 'user_message',
    text: '[Режим «Командная реализация»] Волна 2 закрыта: 3 из 3 готовы',
    auto: true,
  },
  coordReply: {
    kind: 'text',
    text: 'Разобрал доклады волны 2 — задачи прошли проверку. Готовлю следующую волну.',
    personaId: 'p-coord',
  },
  askQuestion: {
    kind: 'ask_question', toolUseId: 'q-1',
    input: { question: 'Какой HTTP-клиент использовать?', options: ['fetch', 'axios'] },
    resolved: false,
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

// Служебные экшены симулятора — не wire-события, в прод-код не попадают
type SimAction =
  | { kind: 'reset' }
  | { kind: 'emit'; item: ChatItem }
  | { kind: 'wire'; msg: ServerMessage };
const RESET: SimAction = { kind: 'reset' };
const emitItem = (item: ChatItem): SimAction => ({ kind: 'emit', item });
const wireMsg = (msg: ServerMessage): SimAction => ({ kind: 'wire', msg });

// Заглушки персон для проверки резолва. В симуляции не подтягиваем реальный стор —
// реальная персона подтянется в живом чате; здесь важно, что резолв вернул правильный id.
// Полный набор полей Persona не заполняем — это dev-симуляция, минимально нужны
// id/name/role/handle/avatar, остальное (ownerId/scope/memoryEnabled/…) для рендера
// TeamPlanningIndicator не используется
const SIM_PERSONAS: Record<string, Persona> = {
  'p-planner-2': {
    id: 'p-planner-2', name: 'Соня Планировщик', role: 'Планировщик',
    handle: 'planner', avatar: { color: 'mint', initials: 'СП' },
  } as unknown as Persona,
  'p-plan': {
    id: 'p-plan', name: 'Лёша План', role: 'Планировщик',
    handle: 'plan', avatar: { color: 'blue', initials: 'ЛП' },
  } as unknown as Persona,
};

export function TeamPlanSimPage() {
  // Свой редьюсер поверх chatReducer: и wire-события (ServerMessage), и «выпустить
  // ChatItem в ленту» для имитации хода координатора (ChatItem не приходит из wire)
  const [state, dispatch] = useReducer(
    (s: ChatState, action: SimAction): ChatState => {
      if (action.kind === 'reset') return initialChatState();
      if (action.kind === 'emit') return { ...s, items: [...s.items, action.item] };
      // Обычный wire-путь
      return applyServerMessage(s, action.msg);
    },
    undefined,
    initialChatState,
  );

  // Тот же предикат, что в ChatPanel — источник правды для видимости плашки
  const showIndicator = teamPlanningIndicatorVisible(
    state.teamImplement && state.teamImplement.active ? state.teamImplement : null,
    state.items,
    state.teamPlanning ?? undefined,
  );

  // Резолв персоны планировщика — единая точка, та же, что в ChatPanel.
  // Приоритет: personaId события → plannerPersonaId стадии → координатор → чат
  const plannerId = resolvePlannerPersonaId(
    state.teamImplement && state.teamImplement.active ? state.teamImplement : null,
    state.teamPlanning?.personaId ?? null,
    'p-coord',
  );
  // dev-only: SIM_PERSONAS неполон по полям Persona, компоненту нужно только id/name/role/avatar;
  // приведение локальное, чтобы TeamPlanningIndicator принял значение
  const plannerPersona = plannerId
    ? (SIM_PERSONAS[plannerId] as unknown as Persona | undefined) ?? null
    : null;

  // Полоса «Практика ждёт вашего решения» — открытые team_escalation из ленты
  const openEscalations = useMemo(() => findOpenEscalations(state.items), [state.items]);
  const topEscalation = openEscalations[openEscalations.length - 1] ?? null;

  const stage = state.teamImplement?.stage ?? null;
  const coordTurnHidden = state.items.filter(it =>
    it.kind === 'user_message' && (it.staffNote || it.auto)).length;

  return (
    <div style={{ minHeight: '100vh', background: C.bgMain, padding: SP.xl, fontFamily: FONT.sans }}>
      <div style={{ maxWidth: 720, margin: '0 auto', display: 'flex', flexDirection: 'column', gap: SP.lg }}>
        <div>
          <div style={{ fontSize: FS.lg, fontWeight: 700, color: C.textHeading }}>
            Симуляция: командная реализация
          </div>
          <div style={{ fontSize: FS.sm, color: C.textSecondary, marginTop: 2 }}>
            Стадия режима:{' '}
            <b data-testid="sim-stage">{stage ?? '—'}</b>
            {' · '}скрыто служебных (⚑/auto):{' '}
            <b data-testid="sim-suppressed-count">{coordTurnHidden}</b>
            {' · '}открытых эскалаций:{' '}
            <b data-testid="sim-open-escalations">{openEscalations.length}</b>
          </div>
        </div>

        <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
          <SimButton label="start-planning" onClick={() => dispatch(wireMsg(MSG.planning as unknown as ServerMessage))} />
          <SimButton label="plan-ready" onClick={() => {
            dispatch(wireMsg(MSG.confirming as unknown as ServerMessage));
            dispatch(wireMsg(MSG.planCard as unknown as ServerMessage));
          }} />
          <SimButton label="plan-failed" onClick={() => dispatch(wireMsg(MSG.escalationOpen as unknown as ServerMessage))} />
          <SimButton label="escalation-resolve" onClick={() => dispatch(wireMsg(MSG.escalationResolved as unknown as ServerMessage))} />
          <SimButton label="add-second-escalation" onClick={() => dispatch(wireMsg(MSG.escalationBlocker as unknown as ServerMessage))} />
          <SimButton label="add-third-escalation" onClick={() => dispatch(wireMsg(MSG.escalationBudget as unknown as ServerMessage))} />
          <SimButton label="reset" onClick={() => dispatch(RESET)} />
        </div>

        <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
          <SimButton label="planning-started-new" onClick={() => {
            // Как делает бэкенд: team_implement (стадия planning) + team_planning (transient)
            dispatch(wireMsg(MSG.planningNewPlanner as unknown as ServerMessage));
            dispatch(wireMsg(MSG.planningStartedNew as unknown as ServerMessage));
          }} />
          <SimButton label="planning-started-legacy" onClick={() => {
            // Старый бэкенд: personaId не приходит, фолбэк на team_implement.plannerPersonaId
            dispatch(wireMsg(MSG.planningNewPlanner as unknown as ServerMessage));
            dispatch(wireMsg(MSG.planningStartedLegacy as unknown as ServerMessage));
          }} />
          <SimButton label="planning-done" onClick={() => {
            dispatch(wireMsg(MSG.planningDone as unknown as ServerMessage));
            dispatch(wireMsg(MSG.confirming as unknown as ServerMessage));
            dispatch(wireMsg(MSG.planCard as unknown as ServerMessage));
          }} />
        </div>

        <div style={{ display: 'flex', gap: SP.sm, flexWrap: 'wrap' }}>
          {/* Ход координатора: имитируем последовательность из реального хода —
              служебные триггеры и реплика персоны чата. В ChatPanel staffNote/auto
              гасятся suppressed-набором; в симуляции они лежат в items и просто
              не рендерятся, а счётчик sim-suppressed-count показывает их число */}
          <SimButton label="coord-turn" onClick={() => {
            dispatch(emitItem(MSG.coordStaffTrigger as ChatItem));
            dispatch(emitItem(MSG.coordAutoTrigger as ChatItem));
            dispatch(emitItem(MSG.coordReply as ChatItem));
          }} />
          <SimButton label="ask-question" onClick={() => dispatch(emitItem(MSG.askQuestion as ChatItem))} />
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
                <div key={`plan-${i}`} data-feed-index={i} data-testid="sim-plan-card" style={{
                  border: `1px solid ${C.border}`, borderLeft: `4px solid ${C.accent}`,
                  borderRadius: R.xl, padding: '10px 14px', background: C.bgCard,
                  fontSize: FS.base, color: C.textHeading, fontWeight: 600,
                }}>
                  План: {it.plan.summary} (карточка плана — должна быть видна)
                </div>
              );
            }
            if (it.kind === 'team_escalation') {
              return (
                <div key={`esc-${i}`} data-feed-index={i} data-testid="sim-escalation-card" style={{
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
            if (it.kind === 'ask_question') {
              return (
                <div key={`ask-${i}`} data-feed-index={i} data-testid="sim-ask-question" style={{
                  border: `1px solid ${C.border}`, borderLeft: `4px solid ${C.accent}`,
                  borderRadius: R.xl, padding: '10px 14px', background: C.bgCard,
                  fontSize: FS.base, color: C.textHeading,
                }}>
                  Вопрос человеку (ask_question — должен быть виден отдельным элементом)
                </div>
              );
            }
            if (it.kind === 'text') {
              return (
                <div key={`text-${i}`} data-feed-index={i} data-testid="sim-coord-reply" style={{
                  alignSelf: 'flex-start', maxWidth: '75%', padding: '8px 12px',
                  background: C.bgWhite, border: `1px solid ${C.border}`, borderRadius: R.xl,
                  fontSize: FS.base, color: C.textPrimary,
                }}>
                  <span style={{ fontSize: FS.xs, color: C.textMuted }}>{it.personaId ?? '—'} →</span>{' '}
                  {it.text}
                </div>
              );
            }
            return null;
          })}

          {showIndicator && <TeamPlanningIndicator persona={plannerPersona} />}
        </div>

        {/* Закреплённая полоса над композером + сам «композер» */}
        <div style={{
          display: 'flex', flexDirection: 'column', gap: SP.sm,
          background: C.bgPanel, border: `1px solid ${C.border}`,
          borderRadius: R.xl, padding: SP.lg,
        }}>
          <div style={{ fontSize: FS.sm, color: C.textSecondary }}>
            Зона над композером (полоса эскалации видна, пока есть открытая team_escalation)
          </div>
          {topEscalation ? (
            <EscalationStickyBanner
              top={topEscalation}
              others={openEscalations.length - 1}
              onJump={() => {/* в симуляторе scrollIntoView недоступен */}}
            />
          ) : (
            <div data-testid="sim-banner-empty" style={{
              fontSize: FS.sm, color: C.textMuted, fontStyle: 'italic',
            }}>
              (полосы нет — открытых карточек нет)
            </div>
          )}
          <div style={{
            padding: '10px 14px', background: C.bgWhite, border: `1px solid ${C.border}`,
            borderRadius: R.lg, fontSize: FS.sm, color: C.textMuted,
          }}>
            [Композер — пустой, как в живом чате]
          </div>
        </div>
      </div>
    </div>
  );
}
