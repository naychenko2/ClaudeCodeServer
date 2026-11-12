// S2a-F7 (доработка по ревью R-S2a-10c): фронт знает три машинных кода отказа (S2a-10b)
// и опознаёт их во всех формах доставки: REST body.code, голый префикс хба «код: …» (S2a-10c)
// и РЕАЛЬНАЯ обёртка клиента SignalR «… invoking '<метод>' on the server. HubException: код: …»
// — в ней код не префикс. Окружение node без jsdom: разбор — чистыми функциями, «Стоп»,
// «Не доставлять», ответ на вопрос и решение плана гоняются настоящим useSession на минимальных
// моках (эффекты не монтируем — нужны только возвращённые колбэки и сообщения «сервера» через
// onMessage); проводка компонентов (ChatPanel, PendingMessageView) — сверкой исходников
// (образец chatAuthorOnly.test.ts): их поведение живёт в компонентах, не в useSession.
import { readFileSync } from 'node:fs';
import { describe, it, expect, vi } from 'vitest';
import type { ChatItem } from '../../../../types';
import { denialCodeOf, denialTextOf, isNotChatAuthorError } from '../participantGate';

// Ошибка как её выставляет lib/offline: message = body.error, status и body прикреплены
const rest = (status: number, code: string) =>
  Object.assign(new Error('серверный текст'), { status, body: { error: 'серверный текст', code } });

// Ошибка как её выставляет клиент SignalR при отказе метода хаба (@microsoft/signalr
// собирает Error из error-строки HubException и оборачивает её в эту форму — R-S2a-10c)
const wrap = (method: string, code: string, serverText: string) =>
  new Error(`An unexpected error occurred invoking '${method}' on the server. HubException: ${code}: ${serverText}`);

describe('denialTextOf: коды из REST body.code (S2a-10b)', () => {
  it('три кода — тексты клиента, равные серверным', () => {
    expect(denialTextOf(rest(403, 'not_chat_author'))).toBe('Только автор хода или владелец проекта');
    expect(denialTextOf(rest(403, 'owner_settings'))).toBe('Настройки чата выбирает владелец');
    expect(denialTextOf(rest(403, 'not_sender'))).toBe('Это сообщение отправил другой участник');
  });
  it('незнакомый код, не-403, отказ без кода — null: вызывающий держит прежний текст', () => {
    expect(denialTextOf(rest(403, 'project_scope'))).toBeNull();
    expect(denialTextOf(rest(409, 'not_sender'))).toBeNull();
    expect(denialTextOf(rest(404, 'not_sender'))).toBeNull();
    expect(denialTextOf(new Error('Что-то не так'))).toBeNull();
    expect(denialTextOf(null)).toBeNull();
    expect(denialTextOf(undefined)).toBeNull();
  });
});

describe('denialTextOf: обёртка клиента SignalR (R-S2a-10c, доказано пробой)', () => {
  it('код внутри «HubException: <код>:» — текст клиента, для всех трёх кодов', () => {
    expect(denialTextOf(wrap('Interrupt', 'not_chat_author', 'Только автор хода или владелец проекта')))
      .toBe('Только автор хода или владелец проекта');
    expect(denialTextOf(wrap('SetMode', 'owner_settings', 'Настройки чата выбирает владелец')))
      .toBe('Настройки чата выбирает владелец');
    expect(denialTextOf(wrap('AnswerQuestion', 'not_sender', 'Это сообщение отправил другой участник')))
      .toBe('Это сообщение отправил другой участник');
  });
  it('обёртка без кода («Доступ запрещён» сегодня) — null, как прежний общий путь', () => {
    expect(denialTextOf(new Error("An unexpected error occurred invoking 'JoinSession' on the server. HubException: Доступ запрещён")))
      .toBeNull();
  });
});

describe('denialTextOf: голый префикс сообщения хаба (S2a-10c) — без регрессии', () => {
  it('«код: текст» — текст клиента, что бы ни стояло после двоеточия', () => {
    expect(denialTextOf(new Error('not_chat_author: Только автор хода или владелец'))).toBe('Только автор хода или владелец проекта');
    expect(denialTextOf(new Error('not_chat_author:'))).toBe('Только автор хода или владелец проекта');
  });
  it('хба-текст без кода («Доступ запрещён» сегодня) — null, как прежний общий путь', () => {
    expect(denialTextOf(new Error('Доступ запрещён'))).toBeNull();
  });
});

describe('denialCodeOf / isNotChatAuthorError: без регрессии', () => {
  it('denialCodeOf различает коды, isNotChatAuthorError — только свой', () => {
    expect(denialCodeOf(rest(403, 'not_sender'))).toBe('not_sender');
    expect(denialCodeOf(wrap('Interrupt', 'not_chat_author', 'Только автор хода или владелец проекта'))).toBe('not_chat_author');
    expect(isNotChatAuthorError(rest(403, 'not_chat_author'))).toBe(true);
    expect(isNotChatAuthorError(rest(403, 'owner_settings'))).toBe(false);
    expect(isNotChatAuthorError(rest(409, 'not_chat_author'))).toBe(false);
    expect(isNotChatAuthorError(new Error('x'))).toBe(false);
    expect(isNotChatAuthorError(null)).toBe(false);
  });
});

// --- useSession на минимальных моках: эффекты не запускаются (useEffect — noop),
// в сторе работает lazy-init; тесты зовут только возвращённые колбэки хука.
// onMessage capture-ится: тест играет сервера (именно так ставятся isWaiting и promptSuggestion) ---
const m = vi.hoisted(() => ({
  cancelPending: vi.fn<(sid: string, id: string) => Promise<void>>(async () => { }),
  interruptSession: vi.fn<(sid: string) => Promise<void>>(async () => { }),
  answerQuestion: vi.fn<(sid: string, toolUseId: string, answerText: string) => Promise<void>>(async () => { }),
  respondPlan: vi.fn<(sid: string, requestId: string, approve: boolean, feedback?: string) => Promise<void>>(async () => { }),
  // Обработчики сообщений хаба — через них тест играет сервера (как в useSession.test.ts)
  messageHandlers: [] as Array<(msg: unknown) => void>,
}));

vi.mock('react', () => ({
  useState: (init: unknown) => [typeof init === 'function' ? (init as () => unknown)() : init, () => { }],
  useCallback: (fn: unknown) => fn,
  useMemo: (fn: () => unknown) => fn(),
  useRef: (v: unknown) => ({ current: v }),
  useEffect: () => { },
}));

vi.mock('../../../../lib/api', () => ({
  api: {
    chats: { getHistory: async () => [], getHistoryTail: async () => [], get: vi.fn(async () => ({})) },
    sessions: {
      getHistory: async () => [],
      getHistoryTail: async () => [],
      cancelPending: (sid: string, id: string) => m.cancelPending(sid, id),
      preemptForPending: vi.fn(async () => { }),
    },
  },
}));

vi.mock('../../../../lib/signalr', () => ({
  joinSession: vi.fn(async () => { }),
  leaveSession: vi.fn(async () => { }),
  onMessage: (h: (msg: unknown) => void) => { m.messageHandlers.push(h); return () => { }; },
  onReconnected: () => () => { },
  sendMessage: vi.fn(async () => 'started'),
  respondPermission: vi.fn(async () => { }),
  interruptSession: (sid: string) => m.interruptSession(sid),
  compactSession: vi.fn(async () => { }),
  answerQuestion: (sid: string, toolUseId: string, answerText: string) => m.answerQuestion(sid, toolUseId, answerText),
  respondPlan: (sid: string, requestId: string, approve: boolean, feedback?: string) => m.respondPlan(sid, requestId, approve, feedback),
  respondTeamPlan: vi.fn(async () => { }),
  respondTeamEscalation: vi.fn(async () => { }),
  setMode: vi.fn(async () => { }),
}));

const { useSession } = await import('../../../../hooks/useSession');

// Сервер шлёт событие в сессию (именно так живой клиент получает карточки и статусы)
const emit = (msg: { sessionId: string; type: string } & Record<string, unknown>) =>
  m.messageHandlers.forEach(h => h(msg));

// Стор модуля живой — у каждого теста свой sid, чтение состояния — повторный вызов хука
let _sid = 0;
const nextSid = () => `denial_${++_sid}`;

const askItem = (sid: string, toolUseId: string) =>
  useSession(sid).items.find((i): i is Extract<ChatItem, { kind: 'ask_question' }> =>
    i.kind === 'ask_question' && i.toolUseId === toolUseId);
const planItem = (sid: string, requestId: string) =>
  useSession(sid).items.find((i): i is Extract<ChatItem, { kind: 'plan_review' }> =>
    i.kind === 'plan_review' && i.requestId === requestId);

describe('«Стоп» в useSession: отказ хаба по коду (S2a-10c, доработка ревью F7)', () => {
  it('отказан по коду (реальная обёртка): оптимистичная отметка откатана, причина — в ленте', async () => {
    const sid = nextSid();
    m.interruptSession.mockRejectedValueOnce(wrap('Interrupt', 'not_chat_author', 'Только автор хода или владелец проекта'));
    const chat = useSession(sid);
    await chat.interrupt();
    const items = useSession(sid).items;
    expect(items.some(i => i.kind === 'interrupted')).toBe(false);
    expect(items.at(-1)).toMatchObject({ kind: 'error', text: 'Только автор хода или владелец проекта', canRetry: false });
  });
  it('отказ по коду НЕ меняет состояние хода: isWaiting и promptSuggestion возвращены', async () => {
    const sid = nextSid();
    const chat = useSession(sid);
    // Ход ждёт ответа человека: карточка открыта (isWaiting=true), подсказка задана
    emit({ type: 'ask_question', sessionId: sid, toolUseId: 't1', input: {} });
    emit({ type: 'prompt_suggestion', sessionId: sid, text: 'продолжить по плану' });
    m.interruptSession.mockRejectedValueOnce(wrap('Interrupt', 'not_chat_author', 'Только автор хода или владелец проекта'));
    await chat.interrupt();
    const st = useSession(sid);
    // Сервер ход не остановил — состояние хода должно быть ровно как до «Стоп»
    expect(st.isWaiting).toBe(true);
    expect(st.promptSuggestion).toBe('продолжить по плану');
    expect(st.items.some(i => i.kind === 'interrupted')).toBe(false);
    expect(st.items.at(-1)).toMatchObject({ kind: 'error', text: 'Только автор хода или владелец проекта' });
  });
  it('лента уехала за отметку: она снята по своему времени, текст отказа на месте', async () => {
    const sid = nextSid();
    const chat = useSession(sid);
    m.interruptSession.mockImplementationOnce(async () => {
      // Хвост прерываемого хода долетел ПОСЛЕ отметки — она больше не последний элемент
      emit({ type: 'text_delta', sessionId: sid, text: 'хвост хода' });
      throw wrap('Interrupt', 'not_chat_author', 'Только автор хода или владелец проекта');
    });
    await chat.interrupt();
    const st = useSession(sid);
    expect(st.items.some(i => i.kind === 'interrupted')).toBe(false);
    expect(st.items.some(i => i.kind === 'text')).toBe(true);
    expect(st.items.at(-1)).toMatchObject({ kind: 'error', text: 'Только автор хода или владелец проекта' });
  });
  it('оборвалось на сети (без кода): отметка и молчание — как при живом прерывании', async () => {
    const sid = nextSid();
    m.interruptSession.mockRejectedValueOnce(new Error('SignalR disconnected while waiting'));
    const chat = useSession(sid);
    await chat.interrupt();
    const items = useSession(sid).items;
    expect(items.at(-1)).toEqual({ kind: 'interrupted', ts: expect.any(Number) });
    expect(items.some(i => i.kind === 'error')).toBe(false);
  });
  it('успешный «Стоп» — отметка остаётся (без регрессии)', async () => {
    const sid = nextSid();
    const chat = useSession(sid);
    await chat.interrupt();
    expect(useSession(sid).items.at(-1)).toEqual({ kind: 'interrupted', ts: expect.any(Number) });
  });
});

describe('ответ и план в useSession: отказ по коду откатывает отметку (S2a-10c)', () => {
  it('ответ на вопрос отклонён по коду (бэк гейтит AnswerQuestion): карточка снова открыта', async () => {
    const sid = nextSid();
    const chat = useSession(sid);
    emit({ type: 'ask_question', sessionId: sid, toolUseId: 't1', input: {} });
    m.answerQuestion.mockRejectedValueOnce(wrap('AnswerQuestion', 'not_chat_author', 'Только автор хода или владелец проекта'));
    await expect(chat.answerQuestion('t1', JSON.stringify({ answers: { 'вопрос': 'ответ' } }))).rejects.toThrow('HubException');
    const item = askItem(sid, 't1');
    expect(item?.resolved).toBe(false);
    expect(item?.answers).toBeUndefined();
  });
  it('сетевой сбой ответа — отметка держится, как прежде (поправит снимок сервера)', async () => {
    const sid = nextSid();
    const chat = useSession(sid);
    emit({ type: 'ask_question', sessionId: sid, toolUseId: 't2', input: {} });
    m.answerQuestion.mockRejectedValueOnce(new Error('SignalR disconnected while waiting'));
    await expect(chat.answerQuestion('t2', '{}')).rejects.toThrow('disconnected');
    expect(askItem(sid, 't2')?.resolved).toBe(true);
  });
  it('решение по плану отклонено по коду: карточка снова открыта', async () => {
    const sid = nextSid();
    const chat = useSession(sid);
    emit({ type: 'plan_review', sessionId: sid, requestId: 'p1', plan: 'шаги' });
    m.respondPlan.mockRejectedValueOnce(wrap('RespondPlan', 'not_chat_author', 'Только автор хода или владелец проекта'));
    await expect(chat.respondPlan('p1', true)).rejects.toThrow('HubException');
    const item = planItem(sid, 'p1');
    expect(item?.resolved).toBe(false);
    expect(item?.approved).toBeUndefined();
  });
});

describe('«Не доставлять» в useSession: отказ по коду (S2a-10b)', () => {
  it('отказ по коду поднимается наверх — строка очереди откатит отметку и покажет текст', async () => {
    const sid = nextSid();
    m.cancelPending.mockRejectedValueOnce(rest(403, 'not_sender'));
    const chat = useSession(sid);
    await expect(chat.cancelPending('msg-1')).rejects.toThrow('серверный текст');
  });
  it('сетевой сбой — молчит, как раньше (снимок от сервера поправит)', async () => {
    const sid = nextSid();
    m.cancelPending.mockRejectedValueOnce(new Error('fetch failed'));
    const chat = useSession(sid);
    await chat.cancelPending('msg-2');
  });
});

describe('проводка: смена модели и строка очереди', () => {
  const chatPanel = readFileSync(new URL('../../../../components/ChatPanel.tsx', import.meta.url), 'utf8');
  const row = readFileSync(new URL('../../../../components/chat/PendingMessageView.tsx', import.meta.url), 'utf8');
  it('смена модели: знакомый код — текст клиента, незнакомый — прежний путь', () => {
    expect(chatPanel).toContain("showToast('Модель', denialTextOf(err) ??");
  });
  it('строка очереди: на отказе отметка откатана и причина показана', () => {
    expect(row).toContain('setCancelling(false)');
    expect(row).toContain("denialTextOf(e) ?? 'Не удалось отменить сообщение'");
  });
});
