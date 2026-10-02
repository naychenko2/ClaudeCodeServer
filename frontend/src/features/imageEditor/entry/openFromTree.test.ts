import { describe, expect, it } from 'vitest';
import type { Session } from '../../../types';
import { lastActiveChat } from './openFromTree';

const chat = (id: string, updatedAt: string, extra: Partial<Session> = {}) =>
  ({ id, updatedAt, ...extra }) as Session;

describe('lastActiveChat — куда «Редактировать картинку» из дерева', () => {
  it('самый свежий по updatedAt, даже если он занят ходом', () => {
    const list = [chat('a', '2026-09-27T10:00:00Z'), chat('b', '2026-09-27T12:00:00Z', { status: 'running' } as Partial<Session>), chat('c', '2026-09-27T11:00:00Z')];
    expect(lastActiveChat(list)?.id).toBe('b');
  });

  it('архивные пропускаются', () => {
    const list = [chat('a', '2026-09-27T10:00:00Z'), chat('b', '2026-09-27T12:00:00Z', { isArchived: true })];
    expect(lastActiveChat(list)?.id).toBe('a');
  });

  it('чатов нет — null, вызывающий создаёт новый', () => {
    expect(lastActiveChat([])).toBeNull();
    expect(lastActiveChat([chat('a', '2026-09-27T10:00:00Z', { isArchived: true })])).toBeNull();
  });
});
