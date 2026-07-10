import { describe, it, expect } from 'vitest';
import { mergeOps, applyUpdateLocally, buildLocalTask, type OutboxOp } from '../taskOutbox';
import type { CreateTaskDto, Task, UpdateTaskDto } from '../../types';

// Заглушка операции очереди с заданными kind/dto
function op(kind: OutboxOp['kind'], dto: CreateTaskDto | UpdateTaskDto, projectId?: string | null): OutboxOp {
  return { taskId: 't1', seq: 1, kind, dto, projectId, attempts: 0, enqueuedAt: 0 };
}

describe('mergeOps — коалесинг офлайн-очереди задач', () => {
  it('create + update → один create со слитыми полями', () => {
    const prev = op('create', { title: 'A', priority: 'low' } as CreateTaskDto, 'p1');
    const r = mergeOps(prev, { kind: 'update', dto: { priority: 'high', description: 'д' } });
    expect(r).not.toBe('drop');
    if (r === 'drop') return;
    expect(r.kind).toBe('create');
    expect(r.projectId).toBe('p1');
    expect((r.dto as CreateTaskDto).title).toBe('A');
    expect((r.dto as CreateTaskDto).priority).toBe('high');
    expect((r.dto as CreateTaskDto).description).toBe('д');
  });

  it('create + update с "" очищает поле в create-dto', () => {
    const prev = op('create', { title: 'A', dueDate: '2026-07-10' } as CreateTaskDto);
    const r = mergeOps(prev, { kind: 'update', dto: { dueDate: '' } });
    if (r === 'drop') throw new Error('не drop');
    expect((r.dto as CreateTaskDto).dueDate).toBeUndefined();
  });

  it('create + update с recurrence none убирает повтор', () => {
    const prev = op('create', { title: 'A', recurrence: { type: 'daily', interval: 1 } } as CreateTaskDto);
    const r = mergeOps(prev, { kind: 'update', dto: { recurrence: { type: 'none', interval: 1 } } });
    if (r === 'drop') throw new Error('не drop');
    expect((r.dto as CreateTaskDto).recurrence).toBeUndefined();
  });

  it('create + delete → drop (задача не дойдёт до сервера)', () => {
    const prev = op('create', { title: 'A' } as CreateTaskDto);
    expect(mergeOps(prev, { kind: 'delete', dto: {} })).toBe('drop');
  });

  it('update + update → слияние, позднее перекрывает', () => {
    const prev = op('update', { title: 'X', priority: 'low' } as UpdateTaskDto);
    const r = mergeOps(prev, { kind: 'update', dto: { priority: 'urgent' } });
    if (r === 'drop') throw new Error('не drop');
    expect(r.kind).toBe('update');
    expect((r.dto as UpdateTaskDto).title).toBe('X');
    expect((r.dto as UpdateTaskDto).priority).toBe('urgent');
  });

  it('update + delete → delete', () => {
    const prev = op('update', { title: 'X' } as UpdateTaskDto);
    const r = mergeOps(prev, { kind: 'delete', dto: {} });
    if (r === 'drop') throw new Error('не drop');
    expect(r.kind).toBe('delete');
  });

  it('delete терминален — последующий update не меняет операцию', () => {
    const prev = op('delete', {});
    const r = mergeOps(prev, { kind: 'update', dto: { title: 'Z' } });
    if (r === 'drop') throw new Error('не drop');
    expect(r.kind).toBe('delete');
  });
});

describe('applyUpdateLocally — зеркало семантики TaskManager.Update', () => {
  const base: Task = buildLocalTask('t1', null, { title: 'T', priority: 'medium' }, 1000);

  it('пустая строка dueDate очищает, undefined не трогает', () => {
    const withDue = applyUpdateLocally(base, { dueDate: '2026-07-10', dueTime: '14:00' });
    expect(withDue.dueDate).toBe('2026-07-10');
    const cleared = applyUpdateLocally(withDue, { dueTime: '' });
    expect(cleared.dueTime).toBeUndefined();
    expect(cleared.dueDate).toBe('2026-07-10');   // не менялось
  });

  it('reminderMinutes < 0 убирает напоминание', () => {
    const r = applyUpdateLocally(base, { reminderMinutes: -1 });
    expect(r.reminderMinutes).toBeUndefined();
  });

  it('смена срока сбрасывает reminderSentAt', () => {
    const withSent: Task = { ...base, dueDate: '2026-07-10', reminderSentAt: '2026-07-09T00:00:00Z' };
    const r = applyUpdateLocally(withSent, { dueDate: '2026-07-11' });
    expect(r.reminderSentAt).toBeUndefined();
  });

  it('смена статуса без columnId сбрасывает колонку', () => {
    const withCol: Task = { ...base, columnId: 'col-x', status: 'todo' };
    const r = applyUpdateLocally(withCol, { status: 'inProgress' });
    expect(r.columnId).toBeUndefined();
  });

  it('recurrence none убирает повтор', () => {
    const withRec: Task = { ...base, recurrence: { type: 'daily', interval: 1 } };
    const r = applyUpdateLocally(withRec, { recurrence: { type: 'none', interval: 1 } });
    expect(r.recurrence).toBeUndefined();
  });
});
