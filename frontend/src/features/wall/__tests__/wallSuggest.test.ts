// Тесты подбора чатов для стены: два разряда поводов (живые и свежие), отбросы,
// лимит мест и то, что addChatsToWall не переполняет набор. Время задаётся явно —
// «сейчас» в тестах не течёт.
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// SignalR мокаем целиком: стор дёргает joinProject/joinUser/onMessage/onReconnected
vi.mock('../../../lib/signalr', () => ({
  joinProject: vi.fn(() => Promise.resolve()),
  joinUser: vi.fn(() => Promise.resolve()),
  onMessage: vi.fn(),
  onReconnected: vi.fn(() => () => {}),
}));

vi.mock('../../../lib/api', () => ({
  api: {
    wall: {
      get: vi.fn(() => Promise.resolve({ chats: [] })),
      put: vi.fn((ids: string[]) => Promise.resolve({ chats: ids.map(id => chat(id)) })),
      candidates: vi.fn(() => Promise.resolve([] as Session[])),
    },
    projects: { list: vi.fn(() => Promise.resolve([])) },
  },
}));

import type { Session } from '../../../types';
import { api } from '../../../lib/api';
import { chatsForWall, loadChatsForWall, addChatsToWall } from '../wallSuggest';
import { addChat, getWallState, MAX_CHATS, __resetWallForTests } from '../wallStore';

const NOW = Date.parse('2026-08-06T12:00:00Z');
const HOUR = 3600_000;
const DAY = 24 * HOUR;

// Кандидат: по умолчанию завершённый чат с перепиской, тронутый час назад —
// это штатное состояние живого чата между ходами
function chat(id: string, over: Partial<Session> = {}): Session {
  return {
    id,
    name: `chat-${id}`,
    status: 'finished',
    messageCount: 5,
    updatedAt: new Date(NOW - HOUR).toISOString(),
    ...over,
  } as unknown as Session;
}

const pick = (list: Session[], limit = MAX_CHATS, taken = new Set<string>()) =>
  chatsForWall(list, { taken, limit, now: NOW });

beforeEach(() => {
  __resetWallForTests();
});

afterEach(() => {
  vi.clearAllMocks();
});

describe('chatsForWall — разряды поводов', () => {
  it('живые (ход и ожидание ответа) идут раньше просто свежих', () => {
    const out = pick([
      chat('fresh'),
      chat('working', { status: 'working' }),
      chat('waiting', { status: 'waiting' }),
    ]);
    expect(out.map(c => c.session.id)).toEqual(['waiting', 'working', 'fresh']);
  });

  it('помечает живым только тот, где идёт ход или ждут ответа', () => {
    const out = pick([chat('working', { status: 'working' }), chat('fresh')]);
    expect(out.map(c => c.live)).toEqual([true, false]);
  });

  it('завершённый чат за сегодня — годный повод (это штатное состояние живого чата)', () => {
    const out = pick([chat('today', { status: 'finished', updatedAt: new Date(NOW - 3 * HOUR).toISOString() })]);
    expect(out.map(c => c.session.id)).toEqual(['today']);
    expect(out[0].live).toBe(false);
  });

  it('внутри разряда свежий выше — порядок не зависит от порядка бэка', () => {
    const out = pick([
      chat('old', { updatedAt: new Date(NOW - 5 * HOUR).toISOString() }),
      chat('new', { updatedAt: new Date(NOW - HOUR).toISOString() }),
    ]);
    expect(out.map(c => c.session.id)).toEqual(['new', 'old']);
  });

  it('идущий ход остаётся поводом даже со старой датой', () => {
    const out = pick([chat('hot', { status: 'working', updatedAt: new Date(NOW - 10 * DAY).toISOString() })]);
    expect(out.map(c => c.session.id)).toEqual(['hot']);
  });
});

describe('chatsForWall — отбросы', () => {
  it('не предлагает то, что уже стоит колонкой', () => {
    const out = pick([chat('a'), chat('b')], MAX_CHATS, new Set(['a']));
    expect(out.map(c => c.session.id)).toEqual(['b']);
  });

  it('не предлагает мусор: оборванные, упавшие, пустые и временные', () => {
    const out = pick([
      chat('orphan', { status: 'orphaned' }),
      chat('broken', { status: 'error' }),
      chat('empty', { messageCount: 0 }),
      chat('temp', { expiresAfterMinutes: 60 }),
      chat('ok'),
    ]);
    expect(out.map(c => c.session.id)).toEqual(['ok']);
  });

  it('не предлагает архив: завершённый чат старше суток', () => {
    const out = pick([chat('stale', { updatedAt: new Date(NOW - 2 * DAY).toISOString() })]);
    expect(out).toEqual([]);
  });

  it('битая дата не роняет подбор и не притворяется свежестью', () => {
    const out = pick([chat('bad', { updatedAt: 'не дата' }), chat('good')]);
    expect(out.map(c => c.session.id)).toEqual(['good']);
  });

  it('соблюдает лимит мест и молчит, когда мест нет', () => {
    const list = [chat('a', { status: 'waiting' }), chat('b'), chat('c')];
    expect(pick(list, 2).map(c => c.session.id)).toEqual(['a', 'b']);
    expect(pick(list, 0)).toEqual([]);
  });
});

describe('loadChatsForWall / addChatsToWall', () => {
  it('молчит, когда мест на стене не осталось', async () => {
    for (let i = 0; i < MAX_CHATS; i++) addChat(chat(`taken-${i}`));
    vi.mocked(api.wall.candidates).mockResolvedValue([chat('hot', { status: 'waiting' })]);

    expect(await loadChatsForWall()).toEqual([]);
    // Кандидатов даже не запрашиваем: предлагать всё равно некуда
    expect(api.wall.candidates).not.toHaveBeenCalled();
  });

  it('молчит, когда бэк недоступен', async () => {
    vi.mocked(api.wall.candidates).mockRejectedValue(new Error('офлайн'));
    expect(await loadChatsForWall()).toEqual([]);
  });

  it('ставит чаты колонками и не переполняет набор', async () => {
    // Одно место занято, значит встать могут только MAX_CHATS - 1
    addChat(chat('already'));
    const many = Array.from({ length: MAX_CHATS + 3 }, (_, i) => chat(`hot-${i}`, { status: 'waiting' }));
    vi.mocked(api.wall.candidates).mockResolvedValue(many);

    const added = await addChatsToWall();

    expect(added).toBe(MAX_CHATS - 1);
    expect(getWallState().chats).toHaveLength(MAX_CHATS);
  });

  it('не дублирует чат, который уже стоит колонкой', async () => {
    addChat(chat('hot', { status: 'waiting' }));
    vi.mocked(api.wall.candidates).mockResolvedValue([chat('hot', { status: 'waiting' })]);

    expect(await addChatsToWall()).toBe(0);
    expect(getWallState().chats).toHaveLength(1);
  });
});
