// Х-W4-52: контролы записи в полосе контекста ребёнку не показываются. Запись (PUT) детской
// ручки не имеет — «Убрать» и «Указать заново…» дали бы 403 и тост об ошибке. Читать можно:
// чипы и «⋯» со списком остаются. Взрослый видит оба контрола, как раньше.
// Контролы ui-кита подменены записывающими заглушками: проверяем, что рисует полоса, а не Menu.

import { describe, it, expect, beforeAll, beforeEach, afterAll, vi } from 'vitest';
import { act, createElement } from 'react';
import type { SessionContextEntry } from '../../types';
import { clearMe, setMeFromServer } from '../../lib/defaultPersona';
import { mount, installFakeDom, uninstallFakeDom, reactProps } from '../../components/chat/__tests__/fakeDom';
type FNode = ReturnType<typeof mount>['container'];

vi.hoisted(() => { (globalThis as any).self = globalThis; });

const entries: SessionContextEntry[] = [
  { type: 'file', id: 'src/a.ts', addedAt: '', missing: false } as SessionContextEntry,
  { type: 'file', id: 'src/gone.ts', addedAt: '', missing: true } as SessionContextEntry,
];

vi.mock('../../lib/chatContext', () => ({
  loadChatContext: async () => {},
  removeFromChatContext: async () => {},
  replaceChatContextEntry: async () => {},
  useChatContext: () => entries,
  contextKey: (t: string, id: string) => `${t}:${id}`,
}));
vi.mock('../../lib/signalr', () => ({ onMessage: () => () => {}, onReconnected: () => () => {} }));
vi.mock('../../lib/tasks', () => ({ useTasks: () => [] }));
vi.mock('../../components/chat/AttachPicker', () => ({ AttachPicker: () => null }));
vi.mock('../../components/ui', async () => {
  const { createElement: h } = await import('react');
  return {
    IconButton: ({ title, onClick, children }: any) => h('button', { 'data-l': title, onClick }, children),
    Menu: ({ children }: any) => h('div', { 'data-menu': '1' }, children),
    MenuItem: ({ label, onClick, action }: any) => h('div', { 'data-l': label, onClick },
      action ? h('button', { 'data-l': action.title, onClick: action.onClick }) : null),
    Modal: ({ children }: any) => h('div', null, children),
    ModalActions: () => null,
    TextField: () => null,
    FileTypeTile: () => null,
  };
});

const { ChatContextBar } = await import('./ChatContextBar');

const labels = (n: FNode, out: string[] = []): string[] => {
  if (n.nodeType === 1) {
    const l = n.getAttribute('data-l');
    if (l) out.push(l);
  }
  for (const c of n.childNodes) labels(c, out);
  return out;
};
const byLabel = (n: FNode, l: string): FNode | null => {
  let hit: FNode | null = null;
  const walk = (x: FNode) => {
    if (hit) return;
    if (x.nodeType === 1 && x.getAttribute('data-l') === l) { hit = x; return; }
    for (const c of x.childNodes) walk(c);
  };
  walk(n);
  return hit;
};
const click = (n: FNode) => act(() => { (reactProps(n)!.onClick as (e: unknown) => void)({
  stopPropagation() {}, currentTarget: { getBoundingClientRect: () => ({}) },
}); });

// Меню ненайденного материала открывается кликом по его чипу — чип без data-l, ищем по title
const missingChip = (n: FNode): FNode | null => {
  let hit: FNode | null = null;
  const walk = (x: FNode) => {
    if (hit) return;
    if (x.nodeType === 1 && x.getAttribute('title')?.startsWith('Материал не найден')) { hit = x; return; }
    for (const c of x.childNodes) walk(c);
  };
  walk(n);
  return hit;
};

const me = (isChild: boolean) => setMeFromServer({
  userId: 'u', username: 'u', role: isChild ? 'child' : 'user', isChild,
} as never);

const render = () => mount(createElement(ChatContextBar, {
  projectId: 'p', sessionId: 's', variant: 'tabs', active: { type: 'file', id: 'src/a.ts' }, onOpen: () => {},
}));

beforeAll(() => { installFakeDom(); });
afterAll(() => { uninstallFakeDom(); });
beforeEach(() => { clearMe(); });

describe('полоса контекста: контролы записи', () => {
  it('ребёнок: ни крестика, ни «Убрать» в «⋯», ни «Указать заново…» у ненайденного', () => {
    me(true);
    const { container } = render();
    expect(labels(container)).toContain('Все материалы контекста');
    expect(labels(container)).not.toContain('Убрать из контекста');

    click(byLabel(container, 'Все материалы контекста')!);
    const all = labels(container);
    expect(all).toContain('a.ts');
    expect(all).not.toContain('Убрать из контекста');

    click(missingChip(container)!);
    expect(labels(container)).not.toContain('Указать заново…');
  });

  it('взрослый: крестик у активной вкладки, «Убрать» в «⋯» и «Указать заново…» на месте', () => {
    me(false);
    const { container } = render();
    expect(labels(container)).toContain('Убрать из контекста');

    click(byLabel(container, 'Все материалы контекста')!);
    expect(labels(container).filter(l => l === 'Убрать из контекста').length).toBeGreaterThan(1);

    click(missingChip(container)!);
    expect(labels(container)).toContain('Указать заново…');
  });
});
