import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { ReactElement, ReactNode } from 'react';
import type { HomeSessionInfo } from '../../types';

// Компонентный тест шторки без DOM: в окружении тестов (node) нет ни jsdom, ни RTL.
// Хуки React подменены синхронными (useEffect срабатывает сразу), компонент зовётся
// как функция, а из возвращённого дерева берутся onClose модалки и onPick строк —
// ровно те обработчики, которые получили бы крестик/фон и тап по строке.

const effectCleanups: (() => void)[] = [];
vi.mock('react', async importOriginal => {
  const actual = await importOriginal<typeof import('react')>();
  return {
    ...actual,
    useState: (init: unknown) => [typeof init === 'function' ? (init as () => unknown)() : init, () => {}],
    useEffect: (fn: () => void | (() => void)) => { const c = fn(); if (c) effectCleanups.push(c); },
    useMemo: (fn: () => unknown) => fn(),
  };
});

const A: HomeSessionInfo = { id: 'A', projectId: null, name: 'Чат A', status: 'idle', messageCount: 1, updatedAt: '2026-10-09T10:00:00Z', origin: 'user' } as HomeSessionInfo;
const B: HomeSessionInfo = { ...A, id: 'B', name: 'Чат B', updatedAt: '2026-10-09T09:00:00Z' };

vi.mock('./useHomeSummary', () => ({ useHomeSummary: () => ({ data: { active: [], recent: [A, B] }, failed: false }) }));
const openSession = vi.fn();
vi.mock('./SessionRow', () => ({ openSession: (s: HomeSessionInfo) => openSession(s), rowTitle: (s: HomeSessionInfo) => s.name ?? '' }));
vi.mock('../../lib/projectActivity', () => ({
  STATUS_COLOR: {}, STATUS_PULSE: {}, foldChatActivity: () => undefined, useChatActivity: () => new Map(),
}));
vi.mock('../projects/useAllProjects', () => ({ useAllProjects: () => [] }));
vi.mock('../projects/ProjectIcon', () => ({ ProjectIcon: () => null }));
vi.mock('../../components/ui', () => ({ Modal: () => null }));

const { ChatSwitcherSheet } = await import('./ChatSwitcherSheet');
const { HISTORY_FLAG } = await import('./chatSwitcherHistory');

// Подменный window: стек истории и асинхронный back(), как в браузере
function fakeWindow() {
  const target = new EventTarget();
  const entries: unknown[] = [{ screen: 'project', chatId: 'A' }];
  let idx = 0;
  let backCalls = 0;
  const win = {
    history: {
      get state() { return entries[idx]; },
      back() {
        backCalls++;
        setTimeout(() => { if (idx > 0) idx--; target.dispatchEvent(new Event('popstate')); }, 0);
      },
      pushState(s: unknown) { entries.splice(idx + 1); entries.push(s); idx++; },
    },
    location: { href: '#/project/P/chat/A' },
    addEventListener: (type: string, fn: () => void, opts?: AddEventListenerOptions) => target.addEventListener(type, fn, opts),
    removeEventListener: (type: string, fn: () => void) => target.removeEventListener(type, fn),
  };
  return { win, get backCalls() { return backCalls; }, get top() { return entries[idx]; } };
}

type Props = { onClose?: () => void; onPick?: (s: HomeSessionInfo) => void; s?: HomeSessionInfo; children?: ReactNode };

// Все элементы дерева (без рендера вложенных компонентов)
function elements(node: ReactNode): ReactElement<Props>[] {
  if (Array.isArray(node)) return node.flatMap(elements);
  if (!node || typeof node !== 'object' || !('props' in node)) return [];
  const el = node as ReactElement<Props>;
  return [el, ...elements(el.props.children)];
}

let fw: ReturnType<typeof fakeWindow>;
let closed = 0;
function renderSheet() {
  const tree = ChatSwitcherSheet({ currentId: 'A', onClose: () => { closed++; } }) as ReactElement<Props>;
  const all = elements(tree);
  return {
    close: tree.props.onClose!,
    row: (id: string) => all.find(e => e.props.s?.id === id)!.props,
  };
}

beforeEach(() => {
  vi.useFakeTimers();
  fw = fakeWindow();
  vi.stubGlobal('window', fw.win);
  closed = 0;
  openSession.mockClear();
});
afterEach(() => {
  effectCleanups.splice(0).forEach(c => c());
  vi.runAllTimers();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('ChatSwitcherSheet', () => {
  it('открытие кладёт запись шторки', () => {
    renderSheet();
    expect((fw.top as Record<string, unknown>)[HISTORY_FLAG]).toBe(true);
  });

  it('закрытие (и повторное) — один back(), шторка закрыта', () => {
    const v = renderSheet();
    v.close();
    v.close();
    vi.runAllTimers();
    expect(fw.backCalls).toBe(1);
    expect(closed).toBe(1);
    expect((fw.top as Record<string, unknown>)[HISTORY_FLAG]).toBeUndefined();
  });

  it('тап по строке другого чата — один openSession после снятия записи', () => {
    const v = renderSheet();
    v.row('B').onPick!(B);
    v.row('B').onPick!(B); // двойной тап
    expect(openSession).not.toHaveBeenCalled();
    vi.runAllTimers();
    expect(fw.backCalls).toBe(1);
    expect(openSession).toHaveBeenCalledTimes(1);
    expect(openSession).toHaveBeenCalledWith(B);
  });

  it('тап по открытому чату только закрывает шторку', () => {
    const v = renderSheet();
    v.row('A').onPick!(A);
    vi.runAllTimers();
    expect(fw.backCalls).toBe(1);
    expect(openSession).not.toHaveBeenCalled();
    expect(closed).toBe(1);
  });
});
