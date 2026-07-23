import { describe, it, expect } from 'vitest';
import { buildChatTreeRows } from '../chatTree';
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
  isRootVisible?: (c: Session) => boolean;
  collapsedIds?: Set<string>;
  activeId?: string | null;
} = {}) {
  return buildChatTreeRows(chats, {
    isRootVisible: opts.isRootVisible ?? all,
    collapsedIds: opts.collapsedIds ?? none,
    activeId: opts.activeId ?? null,
  });
}

describe('buildChatTreeRows', () => {
  it('без parentSessionId все чаты — корни, связей нет', () => {
    const r = build([mk('a'), mk('b')]);
    expect(r.rows.map(x => x.depth)).toEqual([0, 0]);
    expect(r.linkCount).toBe(0);
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
    expect(r.linkCount).toBe(1);
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
    expect(r.linkCount).toBe(0);
  });

  it('цикл ссылок разрывается, оба чата в списке', () => {
    const r = build([
      mk('a', { parentSessionId: 'b' }),
      mk('b', { parentSessionId: 'a' }),
    ]);
    expect(r.rows).toHaveLength(2);
  });

  it('фильтр применяется к корням, но не к детям видимого родителя', () => {
    const chats = [
      mk('p'),
      mk('c', { parentSessionId: 'p', origin: 'task' }),
    ];
    const r = build(chats, { isRootVisible: c => c.origin === 'manual' });
    // Ребёнок с origin=task показан, хотя фильтр task-чаты прячет
    expect(r.rows.map(x => x.chat.id)).toEqual(['p', 'c']);
    expect(r.renderedCount).toBe(2);
  });

  it('дети скрытого корня всплывают кандидатами в корни и фильтруются сами', () => {
    const chats = [
      mk('hiddenParent', { origin: 'automation' }),
      mk('c1', { parentSessionId: 'hiddenParent', origin: 'manual' }),
      mk('c2', { parentSessionId: 'hiddenParent', origin: 'automation' }),
    ];
    const r = build(chats, { isRootVisible: c => c.origin === 'manual' });
    expect(r.rows.map(x => x.chat.id)).toEqual(['c1']);
    expect(r.rows[0].depth).toBe(0);
    expect(r.renderedCount).toBe(1);
  });

  it('свёрнутое поддерево не рендерится, счётчик прямых детей сохраняется', () => {
    const chats = [
      mk('p'),
      mk('c1', { parentSessionId: 'p' }),
      mk('c2', { parentSessionId: 'p' }),
      mk('g', { parentSessionId: 'c1' }),
    ];
    const r = build(chats, { collapsedIds: new Set(['p']) });
    expect(r.rows.map(x => x.chat.id)).toEqual(['p']);
    expect(r.rows[0].collapsed).toBe(true);
    expect(r.rows[0].childCount).toBe(2);
    // renderedCount — весь лес, collapse не считается «скрыто фильтрами»
    expect(r.renderedCount).toBe(4);
  });

  it('закреплённый корень поднимается выше более активного', () => {
    const r = build([
      mk('fresh', { updatedAt: '2026-07-22T10:00:00Z' }),
      mk('pinned', { updatedAt: '2026-07-20T10:00:00Z', isPinned: true }),
    ]);
    expect(r.rows.map(x => x.chat.id)).toEqual(['pinned', 'fresh']);
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
