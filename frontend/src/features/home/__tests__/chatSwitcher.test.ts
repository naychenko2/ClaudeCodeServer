// Состав шторки мобильного переключателя чатов: порядок строго по времени, закреплённые
// отдельно и без повторов, архивные — мимо
import { describe, it, expect } from 'vitest';
import type { HomeSessionInfo } from '../../../types';
import { switcherSections } from '../chatSwitcher';

function s(id: string, minute: number, extra: Partial<HomeSessionInfo> & { archived?: boolean } = {}): HomeSessionInfo {
  return {
    id, status: 'finished', messageCount: 1, origin: null,
    updatedAt: new Date(Date.UTC(2026, 9, 9, 10, minute)).toISOString(), ...extra,
  } as unknown as HomeSessionInfo;
}

describe('switcherSections', () => {
  it('живые не всплывают: общий порядок — по времени', () => {
    const r = switcherSections({
      active: [s('live', 1, { status: 'working' })],
      recent: [s('fresh', 5), s('old', 0)],
    });
    expect(r.recent.map(x => x.id)).toEqual(['fresh', 'live', 'old']);
  });

  it('закреплённые — своей секцией и в недавних не повторяются', () => {
    const r = switcherSections({
      active: [],
      recent: [s('a', 9), s('pin', 1, { isPinned: true }), s('b', 3)],
    });
    expect(r.pinned.map(x => x.id)).toEqual(['pin']);
    expect(r.recent.map(x => x.id)).toEqual(['a', 'b']);
  });

  it('лимит недавних, архивные и дубли отсекаются', () => {
    const recent = Array.from({ length: 15 }, (_, i) => s(`c${i}`, i));
    const r = switcherSections({
      active: [s('c14', 14, { status: 'working' }), s('arch', 59, { archived: true })],
      recent,
    }, 10);
    expect(r.recent).toHaveLength(10);
    expect(r.recent[0].id).toBe('c14');
    expect(new Set(r.recent.map(x => x.id)).size).toBe(10);
    expect(r.recent.some(x => x.id === 'arch')).toBe(false);
  });
});
