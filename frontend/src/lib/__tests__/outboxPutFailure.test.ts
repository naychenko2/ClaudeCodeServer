// Сбой записи в outbox не глотается молча: правка не числится сохранённой
// (_pending не выставляется), пользователь видит тост.
import { describe, it, expect, vi, beforeEach } from 'vitest';

const m = vi.hoisted(() => ({
  outboxPut: vi.fn(), outboxGet: vi.fn(), outboxAll: vi.fn(), outboxDelete: vi.fn(),
  notesOutboxAll: vi.fn(), notesOutboxPut: vi.fn(), notesOutboxDelete: vi.fn(),
  showToast: vi.fn(),
}));
vi.mock('../idb', () => ({
  outboxPut: m.outboxPut, outboxGet: m.outboxGet, outboxAll: m.outboxAll, outboxDelete: m.outboxDelete,
  notesOutboxAll: m.notesOutboxAll, notesOutboxPut: m.notesOutboxPut, notesOutboxDelete: m.notesOutboxDelete,
  idbGet: vi.fn(), idbSet: vi.fn(),
  noteContentGet: vi.fn(), noteContentPut: vi.fn(() => Promise.reject(new Error('quota'))),
  noteContentDelete: vi.fn(), noteContentAll: vi.fn(() => Promise.resolve([])),
}));
vi.mock('../toast', () => ({ showToast: m.showToast }));

import { enqueue, outboxHasPending } from '../taskOutbox';
import { createNoteOffline } from '../notesOffline';
import type { CreateTaskDto } from '../../types';

beforeEach(() => {
  Object.values(m).forEach(f => f.mockReset());
  m.outboxAll.mockResolvedValue([]);
  m.outboxGet.mockResolvedValue(undefined);
  m.notesOutboxAll.mockResolvedValue([]);
});

describe('сбой записи офлайн-правки виден пользователю', () => {
  it('задача: реджект outboxPut → тост, _pending не выставлен', async () => {
    m.outboxPut.mockRejectedValue(new Error('quota'));
    await enqueue('t-fail', 'create', { title: 'A' } as CreateTaskDto);
    expect(m.showToast).toHaveBeenCalledTimes(1);
    expect(outboxHasPending('t-fail')).toBe(false);
  });

  it('задача: успешная запись → без тоста, _pending выставлен', async () => {
    m.outboxPut.mockResolvedValue('t-ok');
    await enqueue('t-ok', 'create', { title: 'A' } as CreateTaskDto);
    expect(m.showToast).not.toHaveBeenCalled();
    expect(outboxHasPending('t-ok')).toBe(true);
  });

  it('заметка: реджект notesOutboxPut → тост; сбой кэша контента не ломает вызов', async () => {
    m.notesOutboxPut.mockRejectedValue(new Error('quota'));
    await expect(createNoteOffline({ title: 'Н' } as never)).resolves.toBeTypeOf('string');
    expect(m.showToast).toHaveBeenCalledTimes(1);
  });
});
