import { describe, it, expect } from 'vitest';
import { buildChatTreeRows, collectDescendants, formatGroupCount, splitChatTreeByRoots } from '../chatTree';
import type { Session } from '../../types';

// Фабрика минимальной сессии: важны только id/parentSessionId/updatedAt/origin/isPinned
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

const all = () => true;
const none = new Set<string>();

function build(chats: Session[], opts: {
  isVisible?: (c: Session) => boolean;
  collapsedIds?: Set<string>;
  activeId?: string | null;
} = {}) {
  return buildChatTreeRows(chats, {
    isVisible: opts.isVisible ?? all,
    collapsedIds: opts.collapsedIds ?? none,
    activeId: opts.activeId ?? null,
  });
}

describe('buildChatTreeRows', () => {
  it('без parentSessionId все чаты — корни, связей нет', () => {
    const r = build([mk('a'), mk('b')]);
    expect(r.rows.map(x => x.depth)).toEqual([0, 0]);
    expect(r.rows.every(x => !x.hasChildren)).toBe(true);
    expect(r.renderedCount).toBe(2);
  });

  it('ребёнок идёт под родителем с depth 1, корни — по максимуму активности поддерева', () => {
    const chats = [
      // Родитель сам старый, но его ребёнок свежее второго корня — родитель выше
      mk('parent', { updatedAt: '2026-07-20T10:00:00Z' }),
      mk('other', { updatedAt: '2026-07-21T10:00:00Z' }),
      mk('child', { parentSessionId: 'parent', updatedAt: '2026-07-22T10:00:00Z', origin: 'task' }),
    ];
    const r = build(chats);
    expect(r.rows.map(x => x.chat.id)).toEqual(['parent', 'child', 'other']);
    expect(r.rows[1].depth).toBe(1);
    expect(r.rows[1].isLast).toBe(true);
    expect(r.rows[0].hasChildren).toBe(true);
    expect(r.renderedCount).toBe(3);
  });

  it('дети внутри родителя отсортированы по updatedAt desc', () => {
    const r = build([
      mk('p'),
      mk('old', { parentSessionId: 'p', updatedAt: '2026-07-20T11:00:00Z' }),
      mk('new', { parentSessionId: 'p', updatedAt: '2026-07-21T11:00:00Z' }),
    ]);
    expect(r.rows.map(x => x.chat.id)).toEqual(['p', 'new', 'old']);
    expect(r.rows[1].isLast).toBe(false);
    expect(r.rows[2].isLast).toBe(true);
  });

  it('сирота (родитель не в наборе) — обычный корень', () => {
    const r = build([mk('orphan', { parentSessionId: 'gone' })]);
    expect(r.rows).toHaveLength(1);
    expect(r.rows[0].depth).toBe(0);
    expect(r.rows[0].hasChildren).toBe(false);
  });

  it('цикл ссылок разрывается, оба чата в списке', () => {
    const r = build([
      mk('a', { parentSessionId: 'b' }),
      mk('b', { parentSessionId: 'a' }),
    ]);
    expect(r.rows).toHaveLength(2);
  });

  it('фильтр отсекает и детей видимого родителя (множество как в плоском)', () => {
    const chats = [
      mk('p'),
      mk('c', { parentSessionId: 'p', origin: 'task' }),
    ];
    const r = build(chats, { isVisible: c => c.origin === 'manual' });
    // Ребёнок с origin=task скрыт, хотя его родитель видим — как в плоском списке
    expect(r.rows.map(x => x.chat.id)).toEqual(['p']);
    expect(r.renderedCount).toBe(1);
  });

  it('дети скрытого корня всплывают кандидатами в корни и фильтруются сами', () => {
    const chats = [
      mk('hiddenParent', { origin: 'automation' }),
      mk('c1', { parentSessionId: 'hiddenParent', origin: 'manual' }),
      mk('c2', { parentSessionId: 'hiddenParent', origin: 'automation' }),
    ];
    const r = build(chats, { isVisible: c => c.origin === 'manual' });
    expect(r.rows.map(x => x.chat.id)).toEqual(['c1']);
    expect(r.rows[0].depth).toBe(0);
    expect(r.renderedCount).toBe(1);
  });

  it('скрытый потомок видимого родителя прокалывается: видимый внук поднимается', () => {
    // Регрессия бага: готовый дочерний чат под видимым родителем не должен торчать,
    // а его видимые потомки поднимаются к деду. Множество видимых = как в плоском.
    const chats = [
      mk('p'),
      mk('hidden', { parentSessionId: 'p', origin: 'automation' }),
      mk('grand', { parentSessionId: 'hidden', origin: 'manual' }),
    ];
    const r = build(chats, { isVisible: c => c.origin === 'manual' });
    expect(r.rows.map(x => x.chat.id)).toEqual(['p', 'grand']);
    expect(r.rows[1].depth).toBe(1);
    expect(r.renderedCount).toBe(2);
  });

  it('свёрнутое поддерво остаётся в массиве (для DOM-анимации), счётчик считает всю ветку', () => {
    const chats = [
      mk('p'),
      mk('c1', { parentSessionId: 'p' }),
      mk('c2', { parentSessionId: 'p' }),
      mk('g', { parentSessionId: 'c1' }),
    ];
    const r = build(chats, { collapsedIds: new Set(['p']) });
    // Дети свёрнутого узла НЕ вырезаются из rows: рендер прячет их контейнером
    // grid 0fr↔1fr, чтобы анимировать схлопывание высоты. Раньше их здесь не было.
    expect(r.rows.map(x => x.chat.id)).toEqual(['p', 'c1', 'g', 'c2']);
    expect(r.rows[0].collapsed).toBe(true);
    expect(r.rows[1].collapsed).toBe(false);
    // Внук g тоже спрятан — счётчик обязан его учесть (не 2 прямых ребёнка)
    expect(r.rows[0].groupCount).toBe(3);
    expect(r.rows[0].groupRunningCount).toBe(0);
    // renderedCount — весь лес, collapse не считается «скрыто фильтрами»
    expect(r.renderedCount).toBe(4);
  });

  it('счётчик работающих считает всю ветку и только живые статусы', () => {
    const chats = [
      mk('p'),
      mk('c1', { parentSessionId: 'p', status: 'working' }),
      mk('c2', { parentSessionId: 'p', status: 'active' }),
      mk('g1', { parentSessionId: 'c1', status: 'waiting' }),
      mk('g2', { parentSessionId: 'c1', status: 'starting' }),
      mk('g3', { parentSessionId: 'c2', status: 'finished' }),
    ];
    const r = build(chats, { collapsedIds: new Set(['p']) });

    expect(r.rows[0].groupCount).toBe(5);
    // working + waiting + starting; active и finished — не «в работе»
    expect(r.rows[0].groupRunningCount).toBe(3);
  });

  it('сам узел в свой счётчик не входит', () => {
    const chats = [mk('p', { status: 'working' }), mk('c', { parentSessionId: 'p', status: 'working' })];
    const r = build(chats, { collapsedIds: new Set(['p']) });

    expect(r.rows[0].groupCount).toBe(1);
    expect(r.rows[0].groupRunningCount).toBe(1);
  });

  it('закреплённый корень поднимается выше более активного', () => {
    const r = build([
      mk('fresh', { updatedAt: '2026-07-22T10:00:00Z' }),
      mk('pinned', { updatedAt: '2026-07-20T10:00:00Z', isPinned: true }),
    ]);
    expect(r.rows.map(x => x.chat.id)).toEqual(['pinned', 'fresh']);
  });

  it('sortOrder=oldest: корни по возрастанию maxActivity, pinned всё ещё первые', () => {
    const r = buildChatTreeRows([
      mk('fresh', { updatedAt: '2026-07-22T10:00:00Z' }),
      mk('old', { updatedAt: '2026-07-18T10:00:00Z' }),
      mk('pinnedNew', { updatedAt: '2026-07-21T10:00:00Z', isPinned: true }),
    ], { isVisible: all, collapsedIds: none, activeId: null, sortOrder: 'oldest' });
    expect(r.rows.map(x => x.chat.id)).toEqual(['pinnedNew', 'old', 'fresh']);
  });

  it('sortOrder=oldest: дети внутри родителя по возрастанию updatedAt', () => {
    const r = buildChatTreeRows([
      mk('p'),
      mk('old', { parentSessionId: 'p', updatedAt: '2026-07-20T11:00:00Z' }),
      mk('new', { parentSessionId: 'p', updatedAt: '2026-07-21T11:00:00Z' }),
    ], { isVisible: all, collapsedIds: none, activeId: null, sortOrder: 'oldest' });
    expect(r.rows.map(x => x.chat.id)).toEqual(['p', 'old', 'new']);
  });

  it('maxActivity строки — максимум updatedAt по поддереву (ключ секций корня)', () => {
    const r = build([
      mk('parent', { updatedAt: '2026-07-20T10:00:00Z' }),
      mk('child', { parentSessionId: 'parent', updatedAt: '2026-07-24T10:00:00Z' }),
    ]);
    const by = new Map(r.rows.map(x => [x.chat.id, x]));
    expect(by.get('parent')!.maxActivity).toBe(new Date('2026-07-24T10:00:00Z').getTime());
    expect(by.get('child')!.maxActivity).toBe(new Date('2026-07-24T10:00:00Z').getTime());
  });

  it('путь корень→активный чат подсвечен: seg/elbow у активного, stub у предков', () => {
    const chats = [
      mk('p'),
      mk('a', { parentSessionId: 'p', updatedAt: '2026-07-21T10:00:00Z' }),
      mk('b', { parentSessionId: 'p', updatedAt: '2026-07-20T10:00:00Z' }),
      mk('x', { parentSessionId: 'a' }),
    ];
    const r = build(chats, { activeId: 'x' });
    const by = new Map(r.rows.map(x => [x.chat.id, x]));
    expect(r.rows.map(x => x.chat.id)).toEqual(['p', 'a', 'x', 'b']);
    expect(by.get('p')!.stubAccent).toBe(true);
    expect(by.get('a')!.segAccent).toBe(true);
    expect(by.get('a')!.stubAccent).toBe(true);
    expect(by.get('x')!.segAccent).toBe(true);
    expect(by.get('x')!.elbowAccent).toBe(true);
    // Ось родителя сквозь строку x ведёт к b мимо пути — линия есть, но не accent
    expect(by.get('x')!.ancestors).toEqual([{ show: true, accent: false }]);
    expect(by.get('b')!.segAccent).toBe(false);
  });

  it('сквозная вертикаль предка рисуется в строках глубокого поддерева не-последнего ребёнка', () => {
    const chats = [
      mk('p'),
      mk('a', { parentSessionId: 'p', updatedAt: '2026-07-21T10:00:00Z' }),
      mk('b', { parentSessionId: 'p', updatedAt: '2026-07-20T10:00:00Z' }),
      mk('x', { parentSessionId: 'a' }),
      mk('y', { parentSessionId: 'x' }),
    ];
    const r = build(chats);
    const by = new Map(r.rows.map(x => [x.chat.id, x]));
    // У последнего в ветке (y, depth 3) видимы оси: родительская (p→b, show)
    // и ось a (x — единственный ребёнок a, продолжения нет)
    expect(by.get('y')!.depth).toBe(3);
    expect(by.get('y')!.ancestors.map(l => l.show)).toEqual([true, false]);
  });
});

// Нарезка плоских строк на сегменты по корням — секционирование (дни/теги) в SessionList
describe('splitChatTreeByRoots', () => {
  it('каждый сегмент — корень с его строками-потомками', () => {
    const r = build([
      mk('p1'),
      mk('c1', { parentSessionId: 'p1' }),
      mk('g1', { parentSessionId: 'c1' }),
      mk('p2'),
      mk('c2', { parentSessionId: 'p2' }),
      mk('p3'),
    ]);
    const segs = splitChatTreeByRoots(r.rows);
    expect(segs.map(s => s.map(x => x.chat.id))).toEqual([
      ['p1', 'c1', 'g1'],
      ['p2', 'c2'],
      ['p3'],
    ]);
  });

  it('пустой список строк — пустая нарезка', () => {
    expect(splitChatTreeByRoots([])).toEqual([]);
  });
});

// Бейдж вылезает из своей колонки поверх карточки — длинное число накрыло бы
// точку статуса и начало названия чата
describe('formatGroupCount', () => {
  it('клампит числа больше 99', () => {
    expect(formatGroupCount(0)).toBe('0');
    expect(formatGroupCount(7)).toBe('7');
    expect(formatGroupCount(99)).toBe('99');
    expect(formatGroupCount(100)).toBe('99+');
    expect(formatGroupCount(1284)).toBe('99+');
  });
});

// Запретные цели перетаскивания: вложить чат в собственного потомка = замкнуть кольцо
describe('collectDescendants', () => {
  it('собирает всё поддерево рекурсивно, себя не включает', () => {
    const chats = [
      mk('root'),
      mk('a', { parentSessionId: 'root' }),
      mk('b', { parentSessionId: 'a' }),
      mk('c', { parentSessionId: 'b' }),
      mk('other'),
    ];

    expect(collectDescendants(chats, 'root')).toEqual(new Set(['a', 'b', 'c']));
    expect(collectDescendants(chats, 'b')).toEqual(new Set(['c']));
    expect(collectDescendants(chats, 'c')).toEqual(new Set());
    expect(collectDescendants(chats, 'other')).toEqual(new Set());
  });

  it('не зацикливается на кольце, уже лежащем в данных', () => {
    const chats = [
      mk('a', { parentSessionId: 'b' }),
      mk('b', { parentSessionId: 'a' }),
    ];

    expect(collectDescendants(chats, 'a')).toEqual(new Set(['b']));
  });

  it('игнорирует ссылку чата на самого себя', () => {
    const chats = [mk('a', { parentSessionId: 'a' }), mk('b', { parentSessionId: 'a' })];

    expect(collectDescendants(chats, 'a')).toEqual(new Set(['b']));
  });
});
