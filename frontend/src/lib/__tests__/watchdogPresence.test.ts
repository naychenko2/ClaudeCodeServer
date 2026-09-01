// Тесты стора присутствия сторожей: снимок при первом подписчике, замена состояния целиком
// по событию и стабильность ссылки (список чатов не должен ререндериться вхолостую).
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// SignalR мокаем целиком: стор подписывается на onMessage/onReconnected, соединения в тестах нет
vi.mock('../signalr', () => ({
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
}));

vi.mock('../api', () => ({
  api: { watchdogs: { snapshot: vi.fn(() => Promise.resolve({ sessions: [], projects: [] })) } },
}));

import type { ServerMessage } from '../../types';
import { api } from '../api';
import { onMessage } from '../signalr';
import {
  __resetWatchdogPresence, subscribeWatchdogPresence, watchdogSessionsSnapshot,
  watchdogProjectsSnapshot,
} from '../watchdogPresence';

// Обработчик, который стор передал в onMessage при старте
function handler(): (msg: ServerMessage) => void {
  const calls = vi.mocked(onMessage).mock.calls;
  return calls[calls.length - 1][0] as (msg: ServerMessage) => void;
}

// Событие глобальное: несёт ПОЛНЫЙ состав, sessionId в нём ничего не значит
const changed = (sessions: string[], projects: string[]) =>
  ({ type: 'watchdogs_changed', sessionId: '', sessions, projects }) as unknown as ServerMessage;

beforeEach(() => {
  __resetWatchdogPresence();
  vi.mocked(api.watchdogs.snapshot).mockResolvedValue({ sessions: [], projects: [] });
});

afterEach(() => {
  vi.clearAllMocks();
});

describe('watchdogPresence', () => {
  it('первый подписчик снимает снимок с сервера', async () => {
    vi.mocked(api.watchdogs.snapshot).mockResolvedValue({
      sessions: ['a', 'b'],
      projects: ['p1'],
    });
    subscribeWatchdogPresence(() => {});
    await vi.waitFor(() => expect(watchdogSessionsSnapshot().size).toBe(2));

    expect(watchdogSessionsSnapshot().has('a')).toBe(true);
    expect(watchdogSessionsSnapshot().has('b')).toBe(true);
    expect(watchdogProjectsSnapshot().has('p1')).toBe(true);
  });

  it('событие заменяет состояние целиком: убирает погасших и добавляет новых за один заход', async () => {
    // Payload полный, а не дифф: состав, которого в нём нет, считаем погасшим
    subscribeWatchdogPresence(() => {});
    await vi.waitFor(() => expect(vi.mocked(onMessage)).toHaveBeenCalled());

    handler()(changed(['a', 'b'], ['p1']));
    handler()(changed(['b', 'c'], ['p2']));

    expect(watchdogSessionsSnapshot().has('a')).toBe(false);
    expect(watchdogSessionsSnapshot().has('b')).toBe(true);
    expect(watchdogSessionsSnapshot().has('c')).toBe(true);
    expect(watchdogProjectsSnapshot().has('p1')).toBe(false);
    expect(watchdogProjectsSnapshot().has('p2')).toBe(true);
  });

  it('повторный payload того же состава не пересоздаёт множества', async () => {
    // useSyncExternalStore сравнивает снимок по ссылке: новый Set того же состава
    // перерисовал бы весь список чатов на каждом лишнем событии
    subscribeWatchdogPresence(() => {});
    await vi.waitFor(() => expect(vi.mocked(onMessage)).toHaveBeenCalled());

    handler()(changed(['a'], ['p1']));
    const sessions = watchdogSessionsSnapshot();
    const projects = watchdogProjectsSnapshot();
    handler()(changed(['a'], ['p1']));

    expect(watchdogSessionsSnapshot()).toBe(sessions);
    expect(watchdogProjectsSnapshot()).toBe(projects);
  });

  it('тот же состав не дёргает подписчика вовсе', async () => {
    const fn = vi.fn();
    subscribeWatchdogPresence(fn);
    await vi.waitFor(() => expect(vi.mocked(onMessage)).toHaveBeenCalled());
    handler()(changed(['a'], ['p1']));
    fn.mockClear();

    handler()(changed(['a'], ['p1']));

    expect(fn).not.toHaveBeenCalled();
  });

  it('удаление чата вычитает его из чатов, но проект не трогает', async () => {
    // id проекта по чату клиент не знает: состав проектов пересоберёт ближайшее
    // watchdogs_changed от бэка, который сторож при удалении гасит сам
    subscribeWatchdogPresence(() => {});
    await vi.waitFor(() => expect(vi.mocked(onMessage)).toHaveBeenCalled());
    handler()(changed(['a'], ['p1']));

    handler()({ type: 'chat_deleted', sessionId: 'a' } as unknown as ServerMessage);

    expect(watchdogSessionsSnapshot().has('a')).toBe(false);
    expect(watchdogProjectsSnapshot().has('p1')).toBe(true);
  });

  it('удаление чата без сторожей не пересоздаёт множество', async () => {
    subscribeWatchdogPresence(() => {});
    await vi.waitFor(() => expect(vi.mocked(onMessage)).toHaveBeenCalled());
    handler()(changed(['a'], ['p1']));
    const sessions = watchdogSessionsSnapshot();

    handler()({ type: 'chat_deleted', sessionId: 'b' } as unknown as ServerMessage);

    expect(watchdogSessionsSnapshot()).toBe(sessions);
  });

  it('событие меняет только одно множество — второе сохраняет ссылку', async () => {
    // Потребитель точек рельсы слушает проекты: чужая смена чатов не должна его будить
    subscribeWatchdogPresence(() => {});
    await vi.waitFor(() => expect(vi.mocked(onMessage)).toHaveBeenCalled());
    handler()(changed(['a'], ['p1']));
    const projects = watchdogProjectsSnapshot();

    handler()(changed(['b'], ['p1']));

    expect(watchdogProjectsSnapshot()).toBe(projects);
    expect(watchdogSessionsSnapshot().has('b')).toBe(true);
  });
});
