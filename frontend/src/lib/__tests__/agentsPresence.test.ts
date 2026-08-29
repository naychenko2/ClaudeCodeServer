// Тесты стора присутствия фоновых агентов: снимок при первом подписчике, realtime-переходы
// поверх него и стабильность ссылки (список чатов не должен ререндериться вхолостую).
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// SignalR мокаем целиком: стор подписывается на onMessage/onReconnected, соединения в тестах нет
vi.mock('../signalr', () => ({
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
}));

vi.mock('../api', () => ({
  api: { chats: { agentsPresence: vi.fn(() => Promise.resolve({ agents: [], commands: [] })) } },
}));

import type { ServerMessage } from '../../types';
import { api } from '../api';
import { onMessage } from '../signalr';
import {
  __resetAgentsPresence, subscribeAgentsPresence, agentsPresenceSnapshot, bgCommandsPresenceSnapshot,
  bgWorkPresenceSnapshot,
} from '../agentsPresence';

// Обработчик, который стор передал в onMessage при старте
function handler(): (msg: ServerMessage) => void {
  const calls = vi.mocked(onMessage).mock.calls;
  return calls[calls.length - 1][0] as (msg: ServerMessage) => void;
}

const presence = (sessionId: string, active: boolean, command = false) =>
  ({ type: 'bg_agents_presence', sessionId, active, command }) as unknown as ServerMessage;

beforeEach(() => {
  __resetAgentsPresence();
  vi.mocked(api.chats.agentsPresence).mockResolvedValue({ agents: [], commands: [] });
});

afterEach(() => {
  vi.clearAllMocks();
});

describe('agentsPresence', () => {
  it('первый подписчик снимает снимок с сервера', async () => {
    vi.mocked(api.chats.agentsPresence).mockResolvedValue({ agents: ['a', 'b'], commands: ['c'] });
    subscribeAgentsPresence(() => {});
    await vi.waitFor(() => expect(agentsPresenceSnapshot().size).toBe(2));

    expect(agentsPresenceSnapshot().has('a')).toBe(true);
    expect(agentsPresenceSnapshot().has('b')).toBe(true);
    expect(bgCommandsPresenceSnapshot().has('c')).toBe(true);
  });

  it('событие включает и выключает присутствие конкретного чата', async () => {
    subscribeAgentsPresence(() => {});
    await vi.waitFor(() => expect(vi.mocked(onMessage)).toHaveBeenCalled());

    handler()(presence('a', true));
    expect(agentsPresenceSnapshot().has('a')).toBe(true);

    handler()(presence('a', false));
    expect(agentsPresenceSnapshot().has('a')).toBe(false);
  });

  it('повторное событие с тем же значением не пересоздаёт множество', async () => {
    // useSyncExternalStore сравнивает снимок по ссылке: новый Set того же состава
    // перерисовал бы весь список чатов на каждом лишнем событии
    subscribeAgentsPresence(() => {});
    await vi.waitFor(() => expect(vi.mocked(onMessage)).toHaveBeenCalled());

    handler()(presence('a', true));
    const first = agentsPresenceSnapshot();
    handler()(presence('a', true));

    expect(agentsPresenceSnapshot()).toBe(first);
  });

  it('удаление чата снимает его присутствие', async () => {
    subscribeAgentsPresence(() => {});
    await vi.waitFor(() => expect(vi.mocked(onMessage)).toHaveBeenCalled());
    handler()(presence('a', true));

    handler()({ type: 'chat_deleted', sessionId: 'a' } as unknown as ServerMessage);

    expect(agentsPresenceSnapshot().has('a')).toBe(false);
  });

  it('фоновая команда учитывается отдельно от агентов', async () => {
    // Дев-сервер в фоне — не агент: значок в строке имени свой, а вот подсветка общая
    subscribeAgentsPresence(() => {});
    await vi.waitFor(() => expect(vi.mocked(onMessage)).toHaveBeenCalled());

    handler()(presence('a', false, true));

    expect(agentsPresenceSnapshot().has('a')).toBe(false);
    expect(bgCommandsPresenceSnapshot().has('a')).toBe(true);
    expect(bgWorkPresenceSnapshot().has('a')).toBe(true);
  });

  it('объединение сводит оба вида фона и гаснет вместе с последним', async () => {
    // Подсветка (плитка чата, точки рельсы и стены) читает именно объединение:
    // пока живёт хоть один вид фона, чат обязан выглядеть работающим
    subscribeAgentsPresence(() => {});
    await vi.waitFor(() => expect(vi.mocked(onMessage)).toHaveBeenCalled());

    handler()(presence('a', true, true));
    expect(bgWorkPresenceSnapshot().has('a')).toBe(true);

    handler()(presence('a', false, true)); // агент отработал, дев-сервер живёт
    expect(bgWorkPresenceSnapshot().has('a')).toBe(true);

    handler()(presence('a', false, false));
    expect(bgWorkPresenceSnapshot().has('a')).toBe(false);
  });

  it('чат с обоими видами фона входит в объединение один раз', async () => {
    // Пересчёт объединения сравнивает СОСТАВ: наивная сверка по сумме размеров
    // считала бы такой чат за два и пересоздавала Set на каждом чужом событии
    subscribeAgentsPresence(() => {});
    await vi.waitFor(() => expect(vi.mocked(onMessage)).toHaveBeenCalled());
    handler()(presence('a', true, true));

    const first = bgWorkPresenceSnapshot();
    expect(first.size).toBe(1);

    handler()(presence('b', false, false)); // событие про чужой чат состава не меняет
    expect(bgWorkPresenceSnapshot()).toBe(first);
  });

  it('снимок с сервера наполняет объединение обоими видами', async () => {
    vi.mocked(api.chats.agentsPresence).mockResolvedValue({ agents: ['a'], commands: ['b'] });
    subscribeAgentsPresence(() => {});

    await vi.waitFor(() => expect(bgWorkPresenceSnapshot().size).toBe(2));
    expect(bgWorkPresenceSnapshot().has('a')).toBe(true);
    expect(bgWorkPresenceSnapshot().has('b')).toBe(true);
  });

  it('конец агента не гасит фоновую команду того же чата', async () => {
    // Боевой случай: агент отработал за минуту, дев-сервер живёт дальше часами
    subscribeAgentsPresence(() => {});
    await vi.waitFor(() => expect(vi.mocked(onMessage)).toHaveBeenCalled());
    handler()(presence('a', true, true));

    handler()(presence('a', false, true));

    expect(agentsPresenceSnapshot().has('a')).toBe(false);
    expect(bgCommandsPresenceSnapshot().has('a')).toBe(true);
  });

  it('смена обоих видов разом уведомляет подписчика один раз', async () => {
    const fn = vi.fn();
    subscribeAgentsPresence(fn);
    await vi.waitFor(() => expect(vi.mocked(onMessage)).toHaveBeenCalled());
    fn.mockClear();

    handler()(presence('a', true, true));

    expect(fn).toHaveBeenCalledTimes(1);
  });

  it('удаление чата снимает и фоновую команду', async () => {
    subscribeAgentsPresence(() => {});
    await vi.waitFor(() => expect(vi.mocked(onMessage)).toHaveBeenCalled());
    handler()(presence('a', false, true));

    handler()({ type: 'chat_deleted', sessionId: 'a' } as unknown as ServerMessage);

    expect(bgCommandsPresenceSnapshot().has('a')).toBe(false);
    expect(bgWorkPresenceSnapshot().has('a')).toBe(false);
  });

  it('подписчик получает уведомление о смене', async () => {
    const fn = vi.fn();
    subscribeAgentsPresence(fn);
    await vi.waitFor(() => expect(vi.mocked(onMessage)).toHaveBeenCalled());
    fn.mockClear();

    handler()(presence('a', true));

    expect(fn).toHaveBeenCalledTimes(1);
  });
});
