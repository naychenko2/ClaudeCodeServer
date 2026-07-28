import { describe, it, expect } from 'vitest';
import { groupChats, groupByTags, sortChatsFlat } from '../chatGroups';
import { GROUP_COLORS } from '../design';
import type { ProjectTag, Session } from '../../types';

// Фабрика минимальной сессии: важны только id/updatedAt/isPinned/tags
function mk(id: string, over: Partial<Session> = {}): Session {
  return {
    id,
    mode: 'auto',
    status: 'finished',
    messageCount: 0,
    createdAt: '2026-07-20T10:00:00Z',
    updatedAt: '2026-07-20T10:00:00Z',
    origin: 'manual',
    ...over,
  } as Session;
}

// Даты подбираются от «сегодня» прогона, чтобы попасть в нужные секции
function daysAgo(n: number, h = 12): string {
  const d = new Date();
  d.setDate(d.getDate() - n);
  d.setHours(h, 0, 0, 0);
  return d.toISOString();
}

const REGISTRY: ProjectTag[] = [
  { name: 'Работа', order: 0, color: GROUP_COLORS[0] },
  { name: 'Идеи', order: 1, color: GROUP_COLORS[1] },
];

describe('groupChats с sortOrder', () => {
  const chats = [
    mk('today1', { updatedAt: daysAgo(0, 10) }),
    mk('today2', { updatedAt: daysAgo(0, 18) }),
    mk('yesterday', { updatedAt: daysAgo(1) }),
    mk('old3', { updatedAt: daysAgo(3) }),
    mk('old5', { updatedAt: daysAgo(5) }),
    mk('pinnedOld', { updatedAt: daysAgo(9), isPinned: true }),
  ];

  it('newest: Закреплённые → Сегодня → Вчера → дни от свежих, внутри свежие сверху', () => {
    const groups = groupChats(chats, 'newest');
    expect(groups.map(g => g.title)).toEqual([
      'Закреплённые', 'Сегодня', expect.stringContaining('Вчера'),
      expect.any(String), expect.any(String),
    ]);
    expect(groups[1].items.map(c => c.id)).toEqual(['today2', 'today1']);
    // старые дни — от свежего к старому
    expect(groups[3].items[0].id).toBe('old3');
    expect(groups[4].items[0].id).toBe('old5');
  });

  it('oldest: секция Закреплённые остаётся первой, порядок секций и внутри обращается', () => {
    const groups = groupChats(chats, 'oldest');
    expect(groups[0].title).toBe('Закреплённые');
    // старые дни сверху → вчера → сегодня внизу
    expect(groups[groups.length - 1].title).toBe('Сегодня');
    expect(groups[groups.length - 2].title).toContain('Вчера');
    expect(groups[1].items[0].id).toBe('old5');
    expect(groups[2].items[0].id).toBe('old3');
    // внутри дня — старые сверху
    expect(groups[groups.length - 1].items.map(c => c.id)).toEqual(['today1', 'today2']);
  });
});

describe('groupByTags с sortOrder', () => {
  const chats = [
    mk('a', { updatedAt: daysAgo(0, 10), tags: ['Работа'] }),
    mk('b', { updatedAt: daysAgo(0, 18), tags: ['Работа', 'Идеи'] }),
    mk('c', { updatedAt: daysAgo(1), tags: ['Сирота'] }),
    mk('d', { updatedAt: daysAgo(2) }),
  ];

  it('порядок секций реестровый, чат с двумя тегами дублируется, хвост «Без тегов»', () => {
    const groups = groupByTags(chats, REGISTRY, 'newest');
    expect(groups.map(g => g.tag)).toEqual(['Работа', 'Идеи', 'Сирота', null]);
    expect(groups[0].items.map(c => c.id)).toEqual(['b', 'a']);
    expect(groups[1].items.map(c => c.id)).toEqual(['b']);
    expect(groups[3].items.map(c => c.id)).toEqual(['d']);
  });

  it('oldest обращает только порядок внутри секций, порядок секций не меняется', () => {
    const groups = groupByTags(chats, REGISTRY, 'oldest');
    expect(groups.map(g => g.tag)).toEqual(['Работа', 'Идеи', 'Сирота', null]);
    expect(groups[0].items.map(c => c.id)).toEqual(['a', 'b']);
  });
});

describe('sortChatsFlat', () => {
  it('pinned всегда первые, дальше — по направлению sortOrder', () => {
    const chats = [
      mk('new', { updatedAt: daysAgo(0) }),
      mk('old', { updatedAt: daysAgo(4) }),
      mk('pinnedOldest', { updatedAt: daysAgo(8), isPinned: true }),
    ];
    expect(sortChatsFlat(chats, 'newest').map(c => c.id)).toEqual(['pinnedOldest', 'new', 'old']);
    expect(sortChatsFlat(chats, 'oldest').map(c => c.id)).toEqual(['pinnedOldest', 'old', 'new']);
  });
});
