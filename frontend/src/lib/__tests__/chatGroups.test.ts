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

  it('created-*: порядок по createdAt, а не по активности', () => {
    const chats = [
      // активен сегодня, создан давно — по активности первый, по созданию последний
      mk('oldBorn', { createdAt: daysAgo(6), updatedAt: daysAgo(0) }),
      mk('newBorn', { createdAt: daysAgo(1), updatedAt: daysAgo(1) }),
    ];
    expect(sortChatsFlat(chats, 'newest').map(c => c.id)).toEqual(['oldBorn', 'newBorn']);
    expect(sortChatsFlat(chats, 'created-newest').map(c => c.id)).toEqual(['newBorn', 'oldBorn']);
    expect(sortChatsFlat(chats, 'created-oldest').map(c => c.id)).toEqual(['oldBorn', 'newBorn']);
  });
});

// Сортировка по времени создания: и порядок, и СЕКЦИИ дней считаются по createdAt —
// «Сегодня» означает «создан сегодня», сколько бы раз чат ни оживал потом
describe('groupChats: сортировка по созданию', () => {
  const chats = [
    mk('oldBornActiveToday', { createdAt: daysAgo(4), updatedAt: daysAgo(0, 18) }),
    mk('bornToday', { createdAt: daysAgo(0, 9), updatedAt: daysAgo(0, 9) }),
    mk('bornYesterday', { createdAt: daysAgo(1), updatedAt: daysAgo(1) }),
  ];

  it('чат, созданный давно и активный сегодня, попадает в секцию своего дня создания', () => {
    const groups = groupChats(chats, 'created-newest');
    expect(groups.map(g => g.title)).toEqual([
      'Сегодня', expect.stringContaining('Вчера'), expect.any(String),
    ]);
    expect(groups[0].items.map(c => c.id)).toEqual(['bornToday']);
    expect(groups[2].items.map(c => c.id)).toEqual(['oldBornActiveToday']);
    // По активности тот же чат живёт в «Сегодня» — оси расходятся осознанно
    expect(groupChats(chats, 'newest')[0].items.map(c => c.id))
      .toEqual(['oldBornActiveToday', 'bornToday']);
  });

  it('created-oldest: порядок секций обращается — старые дни сверху, «Сегодня» внизу', () => {
    const groups = groupChats(chats, 'created-oldest');
    expect(groups[groups.length - 1].title).toBe('Сегодня');
    expect(groups[0].items.map(c => c.id)).toEqual(['oldBornActiveToday']);
  });

  it('секция «Закреплённые» первая во всех четырёх режимах', () => {
    const withPin = [...chats, mk('pin', { createdAt: daysAgo(7), updatedAt: daysAgo(7), isPinned: true })];
    for (const order of ['newest', 'oldest', 'created-newest', 'created-oldest'] as const) {
      const groups = groupChats(withPin, order);
      expect(groups[0].title).toBe('Закреплённые');
      expect(groups[0].items.map(c => c.id)).toEqual(['pin']);
    }
  });
});

describe('groupByTags: сортировка по созданию', () => {
  it('внутри секции — по createdAt, порядок секций реестровый', () => {
    const chats = [
      mk('oldBorn', { createdAt: daysAgo(5), updatedAt: daysAgo(0), tags: ['Работа'] }),
      mk('newBorn', { createdAt: daysAgo(1), updatedAt: daysAgo(1), tags: ['Работа'] }),
    ];
    const groups = groupByTags(chats, REGISTRY, 'created-newest');
    expect(groups.map(g => g.tag)).toEqual(['Работа']);
    expect(groups[0].items.map(c => c.id)).toEqual(['newBorn', 'oldBorn']);
    expect(groupByTags(chats, REGISTRY, 'created-oldest')[0].items.map(c => c.id))
      .toEqual(['oldBorn', 'newBorn']);
  });
});
