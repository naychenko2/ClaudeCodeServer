// Тесты агрегатов активности: точка проекта в рельсе воркспейса и номерок чата в доке
// стены. Отдельный интерес — чат с живыми ФОНОВЫМИ агентами: сервер отдаёт его в recent
// (статус active, «живым» не считается нигде), и без домешивания присутствия обе точки
// остались бы немыми, пока агенты работают.
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

vi.mock('../signalr', () => ({
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
}));

vi.mock('../api', () => ({
  api: {
    home: { summary: vi.fn() },
    chats: { agentsPresence: vi.fn(() => Promise.resolve({ agents: [] as string[], commands: [] as string[] })) },
  },
}));

// Прочитанность — из localStorage; в тестах её нет, все чаты «прочитаны»
vi.mock('../chatReadState', () => ({
  hasUnread: vi.fn(() => false),
  subscribeReadState: vi.fn(() => () => {}),
}));

import type { HomeSessionInfo } from '../../types';
import { api } from '../api';
import { __resetAgentsPresence } from '../agentsPresence';
import {
  __subscribeActivity, __projectAggSnapshot, __chatAggSnapshot, __resetProjectActivity,
} from '../projectActivity';

function chat(id: string, status: string, projectId = 'p1'): HomeSessionInfo {
  return {
    id, projectId, status, updatedAt: new Date('2026-08-22T12:00:00Z').toISOString(),
  } as unknown as HomeSessionInfo;
}

function summary(active: HomeSessionInfo[], recent: HomeSessionInfo[]) {
  vi.mocked(api.home.summary).mockResolvedValue({ active, recent } as never);
}

beforeEach(() => {
  __resetProjectActivity();
  __resetAgentsPresence();
  vi.mocked(api.chats.agentsPresence).mockResolvedValue({ agents: [], commands: [] });
});

afterEach(() => {
  __resetProjectActivity();
  vi.clearAllMocks();
});

describe('projectActivity: живые фоновые агенты', () => {
  it('чат из recent с живым фоном зажигает точку проекта', async () => {
    summary([], [chat('c1', 'active')]);
    vi.mocked(api.chats.agentsPresence).mockResolvedValue({ agents: ['c1'], commands: [] });

    __subscribeActivity(() => {});
    await vi.waitFor(() => expect(__projectAggSnapshot().get('p1')?.status).toBe('working'));
  });

  it('чат из recent с живым фоном даёт номерок «работает» в доке стены', async () => {
    summary([], [chat('c1', 'active')]);
    vi.mocked(api.chats.agentsPresence).mockResolvedValue({ agents: ['c1'], commands: [] });

    __subscribeActivity(() => {});
    await vi.waitFor(() => expect(__chatAggSnapshot().get('c1')).toBe('working'));
  });

  it('архивный чат не попадает в номерок дока стены, даже если у него живые агенты', async () => {
    // Шаг 4 плана v4: aggregateChats пропускает архивный чат — он скрыт в обычном
    // списке, точка дока стены на нём смотрелась бы как сломанная навигация.
    // Готовое поле archived с бэка; без сравнения updatedAt/archivedAt на фронте.
    const archived = chat('c1', 'active') as HomeSessionInfo & { archived?: boolean };
    archived.archived = true;
    summary([], [archived]);
    vi.mocked(api.chats.agentsPresence).mockResolvedValue(['c1']);

    __subscribeActivity(() => {});
    await vi.waitFor(() => expect(vi.mocked(api.home.summary)).toHaveBeenCalled());
    await new Promise(r => setTimeout(r, 20));

    expect(__chatAggSnapshot().has('c1')).toBe(false);
  });

  it('архивный чат не считается непрочитанным в номерке дока стены', async () => {
    // Тот же инвариант через ветку hasUnread: archived=true — чат скрыт,
    // точка unread в доке стены на нём врёт
    const archived = chat('c1', 'finished') as HomeSessionInfo & { archived?: boolean };
    archived.archived = true;
    summary([], [archived]);

    __subscribeActivity(() => {});
    await vi.waitFor(() => expect(vi.mocked(api.home.summary)).toHaveBeenCalled());
    await new Promise(r => setTimeout(r, 20));

    expect(__chatAggSnapshot().has('c1')).toBe(false);
  });

  it('без живого фона тихий чат точку не зажигает', async () => {
    summary([], [chat('c1', 'active')]);

    __subscribeActivity(() => {});
    await vi.waitFor(() => expect(vi.mocked(api.home.summary)).toHaveBeenCalled());
    // Дать шанс возможному пересчёту от стора присутствия
    await new Promise(r => setTimeout(r, 20));

    expect(__projectAggSnapshot().has('p1')).toBe(false);
    expect(__chatAggSnapshot().has('c1')).toBe(false);
  });

  it('ожидание человека важнее работающего фона', async () => {
    // waiting — «брось дело, нужен ответ»: фон работает сам и перебивать не должен
    summary([chat('c2', 'waiting')], [chat('c1', 'active')]);
    vi.mocked(api.chats.agentsPresence).mockResolvedValue({ agents: ['c1'], commands: [] });

    __subscribeActivity(() => {});
    await vi.waitFor(() => expect(__projectAggSnapshot().get('p1')?.status).toBe('waiting'));
  });
});
