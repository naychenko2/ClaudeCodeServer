import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import {
  serverHistoryNewer, PERSISTED_KINDS, applyServerMessage, normalizeHistory, initialChatState,
  type ChatState,
} from './chatReducer';
import type { ChatItem, ServerMessage } from '../types';

// Сверка «история сервера новее живой ленты?» — единственный гейт, через который
// перезагруженная история попадает в ленту. Ошибочное «не новее» означает, что
// оборванный посреди хода ответ залипает: ровно так терялись ответы, когда
// пользователь уходил в другой чат, не дождавшись конца хода.

const SID = 's1';

// Живая лента: прогоняем события через тот же редьюсер, что и в рантайме
const feed = (state: ChatState, ...msgs: Array<Partial<ServerMessage> & { type: string }>): ChatState =>
  msgs.reduce((s, m) => applyServerMessage(s, { sessionId: SID, ...m } as ServerMessage), state);

// Снимок истории с сервера: stored-сообщения, как их отдаёт GET /history
const snapshot = (...stored: unknown[]) => normalizeHistory(stored);

// Один завершённый ход в обоих представлениях — стартовая точка «лента = история»
const TURN1_STORED = [
  { kind: 'user_message', text: 'первый вопрос' },
  { kind: 'text', text: 'первый ответ' },
  { kind: 'result', subtype: 'success', durationMs: 1, numTurns: 1 },
];
const afterTurn1 = (): ChatState => feed(initialChatState(),
  { type: 'user_message', text: 'первый вопрос' },
  { type: 'text_delta', text: 'первый ответ' },
  { type: 'result', subtype: 'success', durationMs: 1, numTurns: 1 },
);

describe('возврат в чат посреди идущего хода', () => {
  // Пользователь ушёл, когда ассистент написал только начало ответа. Пока он был
  // в другом чате, события шли мимо (вне SignalR-группы). Вернулся — ход ещё идёт,
  // снимок истории обязан подтянуть накопленный текст, иначе в ответе будет дыра.
  const startSecondTurn = (state: ChatState) => feed(state,
    { type: 'user_message', text: 'второй вопрос' },
    { type: 'session_started', model: 'opus', mode: 'default' },
    { type: 'text_delta', text: 'Начало отве' },
  );

  const serverMidTurn = () => snapshot(
    ...TURN1_STORED,
    { kind: 'user_message', text: 'второй вопрос' },
    { kind: 'session_started', model: 'opus', mode: 'default' },
    { kind: 'text', text: 'Начало ответа, который дописался пока вкладка была в другом чате' },
  );

  it('обычный чат: снимок сервера новее — дописанный хвост подтянется', () => {
    const client = startSecondTurn(afterTurn1());
    expect(serverHistoryNewer(serverMidTurn(), client.items)).toBe(true);
  });

  // Регрессия. Эти события существуют только в ленте вкладки — бэкенд их в history.json
  // не пишет. Пока они учитывались в сверке длин, лента навсегда оказывалась «длиннее»
  // серверной, снимок отвергался, и текст ответа обрывался на полуслове.
  const liveOnlyEvents: Array<[string, Partial<ServerMessage> & { type: string }]> = [
    ['provider_limit', { type: 'provider_limit', providers: [] }],
    ['provider_switched', { type: 'provider_switched', label: 'GLM', auto: false }],
    ['git_turn_commit', { type: 'git_turn_commit', projectId: 'p1', sha: 'abc123', subject: 'фикс' }],
    ['permission_request', { type: 'permission_request', requestId: 'r1', toolName: 'Bash', toolInput: {} }],
    ['session_ended', { type: 'exited', code: 0 }],
    ['truncated', { type: 'truncated' }],
    ['redacted_thinking', { type: 'redacted_thinking' }],
    ['team_planning_done', {
      type: 'team_planning', start: false, success: true,
      subtaskCount: 2, waveCount: 1, elapsedMs: 1000, route: null, failure: null,
      promptChars: 0, responseChars: 0,
    }],
  ];

  it.each(liveOnlyEvents)('%s в ленте не блокирует снимок', (_name, event) => {
    const client = startSecondTurn(feed(afterTurn1(), event));
    expect(serverHistoryNewer(serverMidTurn(), client.items)).toBe(true);
  });

  it('лента ушла вперёд дельтами — снимок не применяем, чтобы не откатить свежее', () => {
    const client = feed(startSecondTurn(afterTurn1()),
      { type: 'text_delta', text: 'та + хвост, которого в снимке ещё нет' });
    const stale = snapshot(
      ...TURN1_STORED,
      { kind: 'user_message', text: 'второй вопрос' },
      { kind: 'session_started', model: 'opus', mode: 'default' },
      { kind: 'text', text: 'Начало отве' },
    );
    expect(serverHistoryNewer(stale, client.items)).toBe(false);
  });

  it('та же история — не новее (иначе цикл перезагрузок)', () => {
    const items = snapshot(...TURN1_STORED);
    expect(serverHistoryNewer(items, items)).toBe(false);
  });
});

describe('serverHistoryNewer: базовые случаи', () => {
  const userMsg = (text: string): ChatItem => ({ kind: 'user_message', text });
  const text = (t: string): ChatItem => ({ kind: 'text', text: t });
  const result = (): ChatItem => ({ kind: 'result', subtype: 'success', durationMs: 1, numTurns: 1 });

  it('история длиннее живой ленты — новее', () => {
    expect(serverHistoryNewer(
      [userMsg('в'), text('ответ целиком'), result()],
      [userMsg('в'), text('отв')],
    )).toBe(true);
  });

  it('при равной длине новее тот, у кого последний текст длиннее', () => {
    expect(serverHistoryNewer([userMsg('в'), text('ответ целиком')], [userMsg('в'), text('отв')])).toBe(true);
  });

  it('ответа нет ни там, ни там — не новее', () => {
    expect(serverHistoryNewer([userMsg('в')], [userMsg('в')])).toBe(false);
  });
});

// Сторож: белый список персистируемых видов на фронте обязан совпадать с тем, что
// бэкенд реально пишет в history.json. Разъехались — сверка длин врёт.
describe('PERSISTED_KINDS ↔ StoredMessage.cs', () => {
  it('совпадает с дискриминаторами StoredMessage', () => {
    const here = path.dirname(fileURLToPath(import.meta.url));
    const storedPath = path.resolve(here, '..', '..', '..', 'backend',
      'ClaudeHomeServer', 'Protocol', 'StoredMessage.cs');
    const src = readFileSync(storedPath, 'utf8');
    const discriminators = [...src.matchAll(/JsonDerivedType\(typeof\([^)]+\),\s*"([a-z_]+)"\)/g)]
      .map(m => m[1]);

    expect(discriminators.length).toBeGreaterThan(10); // регулярка жива, файл найден

    // workflow_progress персистится, но собственным элементом ленты не становится:
    // normalizeHistory вливает его в карточку родительского tool_use
    const expected = discriminators.filter(k => k !== 'workflow_progress');

    expect([...PERSISTED_KINDS].sort()).toEqual([...expected].sort());
  });
});

// Регрессия: BroadcastSessionMessageAsync рассылает «доклад» о делегированной задаче
// И в session-группу, И в project_/user_-группу SignalR — клиент открытого чата состоит
// в обеих и получает одно и то же событие дважды (карточка дублируется до перезагрузки
// истории). Сервер ставит один и тот же timestamp в оба прохода — applyServerMessage
// дедупит по хвосту ленты (timestamp + текст, + personaId у guest_text).
describe('applyServerMessage: дедуп эха «доклада» о делегированной задаче', () => {
  it('повторный guest_text с тем же timestamp+текстом не добавляет второй элемент', () => {
    const once = feed(initialChatState(),
      { type: 'guest_text', text: 'Отчёт готов', personaId: 'p1', timestamp: 1000 });
    expect(once.items).toHaveLength(1);

    const twice = applyServerMessage(once, { sessionId: SID, type: 'guest_text', text: 'Отчёт готов', personaId: 'p1', timestamp: 1000 } as ServerMessage);
    expect(twice.items).toHaveLength(1);
    expect(twice).toBe(once); // та же ссылка — подписчики не будятся
  });

  it('повторный user_message с viaAgent (senderChatName) не добавляет второй элемент', () => {
    const once = feed(initialChatState(),
      { type: 'user_message', text: 'Отчёт готов', senderChatName: 'Задача: починить билд', auto: true, timestamp: 2000 });
    expect(once.items).toHaveLength(1);

    const twice = applyServerMessage(once, { sessionId: SID, type: 'user_message', text: 'Отчёт готов', senderChatName: 'Задача: починить билд', auto: true, timestamp: 2000 } as ServerMessage);
    expect(twice.items).toHaveLength(1);
    expect(twice).toBe(once);
  });

  it('два guest_text с одинаковым текстом, но разными timestamp — добавляются оба', () => {
    const state = feed(initialChatState(),
      { type: 'guest_text', text: 'Отчёт готов', personaId: 'p1', timestamp: 1000 },
      { type: 'guest_text', text: 'Отчёт готов', personaId: 'p1', timestamp: 1001 },
    );
    expect(state.items).toHaveLength(2);
  });

  it('обычные user_message пользователя (без timestamp/senderChatName) не дедупятся — два подряд легитимны', () => {
    const state = feed(initialChatState(),
      { type: 'user_message', text: 'привет' },
      { type: 'user_message', text: 'привет' },
    );
    expect(state.items).toHaveLength(2);
  });
});

// Связь доклада с задачей — структурное поле сообщения, а не id, выковыренный из текста
// маркера. Ломается в двух местах: живое событие (редьюсер) и снимок истории
// (normalizeHistory). Старые записи поля не несут — карточка обязана деградировать.
describe('доклад о задаче: delegationTaskId доезжает до ленты', () => {
  it('guest_text несёт delegationTaskId в text-элемент', () => {
    const state = feed(initialChatState(),
      { type: 'guest_text', text: 'Отчёт готов', personaId: 'p1', timestamp: 1000, delegationTaskId: 't-42' });
    expect(state.items[0]).toMatchObject({ kind: 'text', delegationTaskId: 't-42' });
  });

  it('user_message несёт delegationTaskId (доклад из чата без персоны)', () => {
    const state = feed(initialChatState(),
      { type: 'user_message', text: 'Отчёт готов', senderChatName: 'Задача: починить билд', auto: true, timestamp: 2000, delegationTaskId: 't-43' });
    expect(state.items[0]).toMatchObject({ kind: 'user_message', delegationTaskId: 't-43' });
  });

  it('история: поле переживает нормализацию для text и user_message', () => {
    const items = snapshot(
      { kind: 'text', text: 'Отчёт готов', personaId: 'p1', timestamp: 1000, delegationTaskId: 't-42' },
      { kind: 'user_message', text: 'Отчёт готов', senderChatName: 'Задача', timestamp: 2000, delegationTaskId: 't-43' },
    );
    expect(items[0]).toMatchObject({ kind: 'text', ts: 1000, delegationTaskId: 't-42' });
    expect(items[1]).toMatchObject({ kind: 'user_message', ts: 2000, delegationTaskId: 't-43' });
  });

  it('совместимость: у старых записей поля нет — undefined, а не мусор', () => {
    const items = snapshot({ kind: 'text', text: 'старая реплика', personaId: 'p1' });
    expect((items[0] as { delegationTaskId?: string }).delegationTaskId).toBeUndefined();

    const live = feed(initialChatState(), { type: 'guest_text', text: 'Отчёт готов', personaId: 'p1' });
    expect((live.items[0] as { delegationTaskId?: string }).delegationTaskId).toBeUndefined();
  });
});

// Э2 КР-наблюдаемости: событие team_wave_pulse эфемерное — пишется в ChatState.
// teamWavePulse, а НЕ в items. Регрессия: счётчик длины ленты (serverHistoryNewer)
// считает ТОЛЬКО persisted kinds, и если бы pulse стал persisted, перезагрузка истории
// навсегда признавалась бы «не новее» живой ленты, и ответ оборванного хода залипал бы
describe('team_wave_pulse: эфемерность', () => {
  const waveState = (over: Partial<Extract<ServerMessage, { type: 'team_implement' }>> = {}) =>
    ({
      type: 'team_implement', active: true, stage: 'wave', waveNumber: 1, plannedWaves: 2,
      autoWaves: true, coordinatorPersonaId: 'p-coord', plannerPersonaId: 'p-plan',
      executorPersonaIds: ['p-1'], budget: null, planCardId: null, modeLocked: false,
      ...over,
    }) as unknown as ServerMessage;

  const wavePulse = (over: Partial<Extract<ServerMessage, { type: 'team_wave_pulse' }>> = {}) =>
    ({
      type: 'team_wave_pulse', stage: 'wave', waveNumber: 1, plannedWaves: 2,
      tasksActive: 2, tasksTotal: 5,
      lastActivityAt: '2026-08-23T11:00:00Z', quietSeconds: 240, liveness: 'alive',
      ...over,
    }) as unknown as ServerMessage;

  it('не добавляет элементов в items', () => {
    const before: ChatState = applyServerMessage(initialChatState(), waveState());
    const after: ChatState = applyServerMessage(before, wavePulse());
    expect(after.items).toEqual(before.items);
  });

  it('обновляет ChatState.teamWavePulse (не путать с persisted)', () => {
    let s: ChatState = applyServerMessage(initialChatState(), waveState());
    expect(s.teamWavePulse).toBeUndefined();
    s = applyServerMessage(s, wavePulse({ liveness: 'stalled', quietSeconds: 2400 }));
    expect(s.teamWavePulse?.liveness).toBe('stalled');
    expect(s.teamWavePulse?.quietSeconds).toBe(2400);
    expect(s.teamWavePulse?.waveNumber).toBe(1);
  });

  it('live-only: сверка «сервер новее?» не считает team_wave_pulse', () => {
    // Без этой проверки вышло бы: пульс удлиняет items, serverHistoryNewer возвращает
    // false (live длиннее), и перезагрузка истории не подтягивает оборванный хвост хода
    const client = feed(initialChatState(), waveState(), wavePulse(), wavePulse());
    const server = snapshot({ kind: 'text', text: 'ответ, дописанный пока вкладка была в другом чате' });
    expect(serverHistoryNewer(server, client.items)).toBe(true);
  });
});
