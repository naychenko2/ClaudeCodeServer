// S2a-F7: фронт знает три машинных кода отказа (S2a-10b) — не только not_chat_author:
// разбор из REST body.code И из префикса сообщения хаба «not_chat_author: …» (S2a-10c).
// Окружение node без jsdom: разбор — чистыми функциями, «Стоп» и «Не доставлять» гоняются
// настоящим useSession на минимальных моках (эффекты не монтируем — нужны только возвращённые
// колбэки), проводка модели и строки очереди — сверкой исходников (образец chatAuthorOnly.test.ts).
import { readFileSync } from 'node:fs';
import { describe, it, expect, vi } from 'vitest';
import { denialCodeOf, denialTextOf, isNotChatAuthorError } from '../participantGate';

// Ошибка как её выставляет lib/offline: message = body.error, status и body прикреплены
const rest = (status: number, code: string) =>
  Object.assign(new Error('серверный текст'), { status, body: { error: 'серверный текст', code } });

describe('denialTextOf: коды из REST body.code (S2a-10b)', () => {
  it('три кода — тексты клиента', () => {
    expect(denialTextOf(rest(403, 'not_chat_author'))).toBe('Только автор хода или владелец проекта');
    expect(denialTextOf(rest(403, 'owner_settings'))).toBe('Это решает владелец проекта');
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

describe('denialTextOf: префикс сообщения хаба (S2a-10c)', () => {
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
    expect(isNotChatAuthorError(rest(403, 'not_chat_author'))).toBe(true);
    expect(isNotChatAuthorError(rest(403, 'owner_settings'))).toBe(false);
    expect(isNotChatAuthorError(rest(409, 'not_chat_author'))).toBe(false);
    expect(isNotChatAuthorError(new Error('x'))).toBe(false);
    expect(isNotChatAuthorError(null)).toBe(false);
  });
});

// --- useSession на минимальных моках: эффекты не запускаются (useEffect — noop),
// в сторе работает lazy-init; тесты зовут только возвращённые колбэки хука ---
const m = vi.hoisted(() => ({
  cancelPending: vi.fn<(sid: string, id: string) => Promise<void>>(async () => { }),
  interruptSession: vi.fn<(sid: string) => Promise<void>>(async () => { }),
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
  onMessage: () => () => { },
  onReconnected: () => () => { },
  sendMessage: vi.fn(async () => 'started'),
  respondPermission: vi.fn(async () => { }),
  interruptSession: (sid: string) => m.interruptSession(sid),
  compactSession: vi.fn(async () => { }),
  answerQuestion: vi.fn(async () => { }),
  respondPlan: vi.fn(async () => { }),
  respondTeamPlan: vi.fn(async () => { }),
  respondTeamEscalation: vi.fn(async () => { }),
  setMode: vi.fn(async () => { }),
}));

const { useSession } = await import('../../../../hooks/useSession');

// Стор модуля живой — у каждого теста свой sid, чтение состояния — повторный вызов хука
let _sid = 0;
const nextSid = () => `denial_${++_sid}`;

describe('«Стоп» в useSession: отказ хаба по коду (S2a-10c)', () => {
  it('отказан по коду: оптимистичная отметка откатана, причина — в ленте', async () => {
    const sid = nextSid();
    m.interruptSession.mockRejectedValueOnce(new Error('not_chat_author: Только автор хода или владелец'));
    const chat = useSession(sid);
    await chat.interrupt();
    const items = useSession(sid).items;
    expect(items.some(i => i.kind === 'interrupted')).toBe(false);
    expect(items.at(-1)).toMatchObject({ kind: 'error', text: 'Только автор хода или владелец проекта', canRetry: false });
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
