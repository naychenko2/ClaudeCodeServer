import { describe, it, expect, vi, beforeEach } from 'vitest';

// drainTaskOutbox: reload стора («канонизация») нужен только после реального дренажа.
// Пустая очередь на каждом монтировании компонента задач не должна тянуть весь список.

const h = vi.hoisted(() => ({
  store: new Map<string, unknown>(),
  create: vi.fn(),
  update: vi.fn(),
  del: vi.fn(),
}));

vi.mock('../idb', () => ({
  outboxAll: vi.fn(async () => [...h.store.values()]),
  outboxGet: vi.fn(async (id: string) => h.store.get(id)),
  outboxPut: vi.fn(async (op: { taskId: string }) => { h.store.set(op.taskId, op); return op.taskId; }),
  outboxDelete: vi.fn(async (id: string) => { h.store.delete(id); }),
}));
vi.mock('../offline', () => ({
  isOnline: () => true,
  OfflineError: class OfflineError extends Error {},
}));
vi.mock('../api', () => ({
  api: { tasks: { create: h.create, update: h.update, delete: h.del } },
}));
// taskOutbox берёт правила дефектов из tasks.ts, а тот тянет signalr/тему — тут не нужны
vi.mock('../tasks', () => ({
  ensureNotClosedAtCreate: vi.fn(),
  ensureVerificationOnClose: vi.fn(),
}));

import { configureOutbox, drainTaskOutbox, type OutboxOp } from '../taskOutbox';
import type { Task, UpdateTaskDto } from '../../types';

const hooks = { upsert: vi.fn(), remove: vi.fn(), reload: vi.fn(async () => {}) };

function updateOp(taskId: string): OutboxOp {
  return { taskId, seq: 1, kind: 'update', dto: { title: 'X' } as UpdateTaskDto, attempts: 0, enqueuedAt: 0 };
}

describe('drainTaskOutbox — когда перезагружать стор', () => {
  beforeEach(() => {
    h.store.clear();
    vi.clearAllMocks();
    configureOutbox(hooks);
  });

  it('пустая очередь → reload не вызывается', async () => {
    await drainTaskOutbox();
    expect(hooks.reload).not.toHaveBeenCalled();
    expect(h.update).not.toHaveBeenCalled();
  });

  it('одна применённая операция → reload ровно один раз, очередь пуста', async () => {
    h.store.set('t1', updateOp('t1'));
    h.update.mockResolvedValue({ id: 't1' } as Task);
    await drainTaskOutbox();
    expect(h.update).toHaveBeenCalledTimes(1);
    expect(hooks.upsert).toHaveBeenCalledTimes(1);
    expect(hooks.reload).toHaveBeenCalledTimes(1);
    expect(h.store.size).toBe(0);
  });

  it('операция отклонена сервером (4xx) и снята → reload тоже вызывается', async () => {
    h.store.set('t1', updateOp('t1'));
    h.update.mockRejectedValue(Object.assign(new Error('bad'), { status: 400 }));
    vi.spyOn(console, 'warn').mockImplementation(() => {});
    await drainTaskOutbox();
    expect(h.store.size).toBe(0);
    expect(hooks.reload).toHaveBeenCalledTimes(1);
  });

  it('одна применена, другая осталась на повтор (5xx) → очередь непуста, reload не вызывается', async () => {
    h.store.set('t1', updateOp('t1'));
    h.store.set('t2', { ...updateOp('t2'), seq: 2 });
    h.update.mockImplementation(async (id: string) => {
      if (id === 't2') throw Object.assign(new Error('boom'), { status: 500 });
      return { id } as Task;
    });
    await drainTaskOutbox();
    expect(h.store.has('t2')).toBe(true);
    expect(hooks.reload).not.toHaveBeenCalled();
  });

  it('5xx (повтор с backoff) → ничего не применено, reload не вызывается', async () => {
    h.store.set('t1', updateOp('t1'));
    h.update.mockRejectedValue(Object.assign(new Error('boom'), { status: 500 }));
    await drainTaskOutbox();
    expect(h.store.size).toBe(1);
    expect(hooks.reload).not.toHaveBeenCalled();
  });
});
