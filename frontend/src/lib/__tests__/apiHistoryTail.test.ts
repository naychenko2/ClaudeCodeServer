import { describe, it, expect, vi, beforeEach } from 'vitest';

// api.ts берёт request/readStoredToken из offline.ts — мокаем, чтобы проверить,
// какие options уходят в request из getHistoryTail (вес ответа, live, chatScope).
const { request, readStoredToken } = vi.hoisted(() => ({
  request: vi.fn(),
  readStoredToken: vi.fn(() => 'tok'),
}));
vi.mock('../offline', () => ({ request, readStoredToken }));

import { api } from '../api';

type Opts = { live?: boolean; chatScope?: string; onBytes?: (n: number) => void };
const lastOpts = () => (request.mock.calls[0] as [string, Opts])[1];

describe('getHistoryTail — прокладка onBytes, live и chatScope', () => {
  beforeEach(() => {
    request.mockReset();
    request.mockResolvedValue({});
  });

  it('api.sessions.getHistoryTail передаёт тот же onBytes в request', async () => {
    const onBytes = vi.fn();
    await api.sessions.getHistoryTail('p1', 's1', { after: -1 }, onBytes);
    expect(lastOpts().onBytes).toBe(onBytes);
  });

  it('api.chats.getHistoryTail передаёт тот же onBytes в request', async () => {
    const onBytes = vi.fn();
    await api.chats.getHistoryTail('c1', { after: -1 }, onBytes);
    expect(lastOpts().onBytes).toBe(onBytes);
  });

  it('без onBytes в request уходит undefined', async () => {
    await api.chats.getHistoryTail('c1', { after: -1 });
    expect(lastOpts().onBytes).toBeUndefined();
  });

  it.each([
    ['sessions', () => api.sessions.getHistoryTail('p1', 's1', { after: -1 }), 's1'],
    ['chats', () => api.chats.getHistoryTail('c1', { after: -1 }), 'c1'],
  ])('%s: after=-1 → live:false и chatScope по id', async (_n, call, sid) => {
    await call();
    expect(lastOpts().live).toBe(false);
    expect(lastOpts().chatScope).toBe(sid);
  });

  it.each([
    ['sessions', () => api.sessions.getHistoryTail('p1', 's1', { after: 5 })],
    ['chats', () => api.chats.getHistoryTail('c1', { after: 0 })],
  ])('%s: after>=0 → live:true и chatScope undefined', async (_n, call) => {
    await call();
    expect(lastOpts().live).toBe(true);
    expect(lastOpts().chatScope).toBeUndefined();
  });
});
