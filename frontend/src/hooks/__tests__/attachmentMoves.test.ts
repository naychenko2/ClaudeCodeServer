import { describe, it, expect, vi, beforeEach } from 'vitest';
import type { ChatItem } from '../../types';

// Карта переноса вложений (S2d-6b-4): бэк при смене видимости чата переносит его файлы
// и возвращает в ответе PUT /visibility attachmentMoves {старый путь: новый путь}.
// Здесь — применение карты к ленте ОДНОЙ сессии в модульном сторе useSession.
// Шапка моков (api, signalr) и импорт через await import — как в useSession.test.ts:
// хук здесь не гоняется, но модуль useSession при импорте тянет эти модули.

const m = vi.hoisted(() => ({
  getHistory: vi.fn<(sid: string) => Promise<unknown[]>>(async () => []),
  getHistoryTail: vi.fn<(sid: string, q: { after: number; fp?: string }) => Promise<unknown>>(async () => []),
  joinSession: vi.fn<(sid: string) => Promise<void>>(async () => { }),
  leaveSession: vi.fn<(sid: string) => Promise<void>>(async () => { }),
  sendMessage: vi.fn<() => Promise<'started' | 'queued' | 'queued-preempted'>>(async () => 'started'),
  preemptForPending: vi.fn<(sid: string) => Promise<void>>(async () => { }),
  messageHandlers: [] as Array<(msg: unknown) => void>,
  reconnectHandlers: [] as Array<() => Promise<void> | void>,
}));

vi.mock('../../lib/api', () => ({
  api: {
    chats: {
      getHistory: (sid: string) => m.getHistory(sid),
      getHistoryTail: (sid: string, q: { after: number; fp?: string }) => m.getHistoryTail(sid, q),
    },
    sessions: {
      getHistory: (_projectId: string, sid: string) => m.getHistory(sid),
      getHistoryTail: (_projectId: string, sid: string, q: { after: number; fp?: string }) => m.getHistoryTail(sid, q),
      cancelPending: vi.fn(async () => { }),
      preemptForPending: (sid: string) => m.preemptForPending(sid),
    },
  },
}));

vi.mock('../../lib/signalr', () => ({
  joinSession: (sid: string) => m.joinSession(sid),
  joinProject: vi.fn(async () => { }),
  leaveSession: (sid: string) => m.leaveSession(sid),
  onMessage: (h: (msg: unknown) => void) => { m.messageHandlers.push(h); return () => { }; },
  onReconnected: (h: () => Promise<void> | void) => { m.reconnectHandlers.push(h); return () => { }; },
  sendMessage: (...args: unknown[]) => m.sendMessage(...(args as [])),
  respondPermission: vi.fn(async () => { }),
  interruptSession: vi.fn(async () => { }),
  compactSession: vi.fn(async () => { }),
  answerQuestion: vi.fn(async () => { }),
  respondPlan: vi.fn(async () => { }),
  respondTeamPlan: vi.fn(async () => { }),
  respondTeamEscalation: vi.fn(async () => { }),
  setMode: vi.fn(async () => { }),
}));

const { remapPaths, remapSessionAttachedPaths, __test } = await import('../useSession');
const { initialChatState } = await import('../../lib/chatReducer');
// ChatVisibilityError — из НАСТОЯЩЕГО features/chat/api (lib/api выше замокан под
// нужды useSession; здесь проверяется текст отказа, а не транспорт)
const { ChatVisibilityError } = await vi.importActual<typeof import('../../features/chat/api')>('../../features/chat/api');

describe('remapPaths: карта переноса', () => {
  it('путь из карты заменён, чужой путь как есть, порядок тот же', () => {
    const moves = { 'data/uploads/a.png': 'data/private/a.png', 'data/uploads/b.txt': 'data/private/b.txt' };
    expect(remapPaths(['data/uploads/b.txt', 'keep/c.png', 'data/uploads/a.png'], moves)).toEqual([
      'data/private/b.txt',
      'keep/c.png',
      'data/private/a.png',
    ]);
  });
});

describe('remapSessionAttachedPaths: лента одной сессии', () => {
  const { _store } = __test;

  // Модульный стор переживает тесты: чистим перед каждым, как в useSession.test.ts
  beforeEach(() => {
    _store.clear();
  });

  const seed = (sid: string, items: ChatItem[]) => {
    _store.set(sid, { ...initialChatState(), isJoined: false, isHistoryLoading: false, items });
  };

  it('меняет attachedPaths у user_message только в s1; s2 идемпотентность и чужая сессия', () => {
    const moves = { 'data/uploads/a.png': 'data/private/a.png' };
    seed('s1', [
      { kind: 'user_message', text: 'раз', attachedPaths: ['data/uploads/a.png', 'keep/c.png'], ts: 1 },
      { kind: 'text', text: 'ответ' }, // не user_message — не трогается вовсе
      { kind: 'user_message', text: 'два' }, // без attachedPaths — не трогается
    ]);
    seed('s2', [
      { kind: 'user_message', text: 'чужой чат', attachedPaths: ['data/uploads/a.png'], ts: 2 },
    ]);
    const s2Before = structuredClone(_store.get('s2')!.items);

    remapSessionAttachedPaths('s1', moves);
    expect(_store.get('s1')!.items[0]).toMatchObject({ attachedPaths: ['data/private/a.png', 'keep/c.png'] });
    expect(_store.get('s1')!.items[1]).toEqual({ kind: 'text', text: 'ответ' });
    expect(_store.get('s1')!.items[2]).toEqual({ kind: 'user_message', text: 'два' });
    // лента сессии B — равна исходной
    expect(_store.get('s2')!.items).toEqual(s2Before);

    // повторный вызов с той же картой — без изменений (идемпотентно)
    const s1After = structuredClone(_store.get('s1')!.items);
    remapSessionAttachedPaths('s1', moves);
    expect(_store.get('s1')!.items).toEqual(s1After);

    // нет сессии в сторе — ничего не делать: запись не заводим
    remapSessionAttachedPaths('ghost', moves);
    expect(_store.has('ghost')).toBe(false);
  });
});

describe('ChatVisibilityError: отказ переноса вложений', () => {
  it('текст attachments_move_failed — дословно с бэка (ChatsController.cs:350)', () => {
    const e = new ChatVisibilityError(409, { code: 'attachments_move_failed' });
    expect(e.message).toBe('Не удалось перенести вложения чата — видимость не изменена');
    expect(e.code).toBe('attachments_move_failed');
    expect(e.status).toBe(409);
  });
});
