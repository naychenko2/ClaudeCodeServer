import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { Session } from '../types';

const list = vi.fn<(projectId: string) => Promise<Session[]>>();
vi.mock('./api', () => ({ api: { sessions: { list: (id: string) => list(id) } } }));

import { listProjectSessions, __test } from './sessionsList';

const s = (id: string) => ({ id, updatedAt: '2026-10-19T10:00:00Z' }) as Session;

// Управляемый ответ: тест сам решает, когда запрос «вернётся»
function deferred<T>() {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>(r => { resolve = r; });
  return { promise, resolve };
}

describe('listProjectSessions', () => {
  beforeEach(() => { list.mockReset(); __test.reset(); vi.useRealTimers(); });

  it('одновременные запросы одного проекта делят один fetch', async () => {
    const d = deferred<Session[]>();
    list.mockReturnValue(d.promise);
    const a = listProjectSessions('p1');
    const b = listProjectSessions('p1');
    const c = listProjectSessions('p1', { maxAgeMs: 10_000 });
    d.resolve([s('a')]);
    expect((await a)[0].id).toBe('a');
    expect(await b).toBe(await a);
    expect(await c).toBe(await a);
    expect(list).toHaveBeenCalledTimes(1);
  });

  it('разные проекты не смешиваются', async () => {
    list.mockImplementation(async id => [s(id)]);
    const [x, y] = await Promise.all([listProjectSessions('p1'), listProjectSessions('p2')]);
    expect(x[0].id).toBe('p1');
    expect(y[0].id).toBe('p2');
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('без maxAgeMs после завершения запроса снова идёт на сервер (опрос всегда свежий)', async () => {
    list.mockResolvedValue([s('a')]);
    await listProjectSessions('p1');
    await listProjectSessions('p1');
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('maxAgeMs: недавний результат отдаётся без запроса, протухший — перечитывается', async () => {
    vi.useFakeTimers();
    list.mockResolvedValue([s('a')]);
    await listProjectSessions('p1');
    await listProjectSessions('p1', { maxAgeMs: 10_000 });
    expect(list).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(10_001);
    await listProjectSessions('p1', { maxAgeMs: 10_000 });
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('mustContain: в кэше нет нужного чата — идём на сервер, а не отвечаем «нет»', async () => {
    list.mockResolvedValueOnce([s('a')]).mockResolvedValueOnce([s('a'), s('new')]);
    await listProjectSessions('p1');
    const got = await listProjectSessions('p1', { maxAgeMs: 10_000, mustContain: 'new' });
    expect(got.map(x => x.id)).toEqual(['a', 'new']);
    expect(list).toHaveBeenCalledTimes(2);
    // Теперь чат в кэше — повторный запрос с тем же условием сервер не трогает
    await listProjectSessions('p1', { maxAgeMs: 10_000, mustContain: 'new' });
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('ошибка запроса не залипает: следующий вызов пробует снова', async () => {
    list.mockRejectedValueOnce(new Error('сеть')).mockResolvedValueOnce([s('a')]);
    await expect(listProjectSessions('p1')).rejects.toThrow('сеть');
    expect((await listProjectSessions('p1'))[0].id).toBe('a');
    expect(list).toHaveBeenCalledTimes(2);
  });

  it('десять переключений внутри проекта с maxAgeMs дают не больше одного запроса', async () => {
    list.mockResolvedValue([s('a'), s('b')]);
    await listProjectSessions('p1'); // вход в проект: список приехал
    list.mockClear();
    for (let i = 0; i < 10; i++) await listProjectSessions('p1', { maxAgeMs: 10_000, mustContain: i % 2 ? 'a' : 'b' });
    expect(list).toHaveBeenCalledTimes(0);
  });
});
