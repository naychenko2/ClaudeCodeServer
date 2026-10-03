// «В контекст ▾» (ADR-023, 2к-2): серая без основного, обычная при одной роли, со стрелкой при нескольких,
// плашка или переключатель, когда объект уже в контексте. Окружение node — рисуем в строку.
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';

vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 1440, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} }),
}));
vi.mock('../../lib/offline', () => ({ request: vi.fn() }));
vi.mock('../../lib/signalr', () => ({ onMessage: () => () => {}, onReconnected: () => () => {} }));

const { ContextAddButton, NO_PRIMARY_REASON, NO_ROLE_REASON } = await import('./ContextAddButton');
const { __applyChatContext, __resetChatContextStore } = await import('../../lib/chatContext/store');
const { registerSubsystem } = await import('../../lib/subsystems/registryCore');

const S = 's1';
const item = { addedAt: '', version: null, thumb: null, missing: false, by: 'human' as const };
const primary = { ...item, id: 'p1', kind: 'image', ref: { threadId: 't1' }, label: 'hero.png', role: null };
const file = { kind: 'project-file', ref: { path: 'assets/a.png' } };
const inCtx = { ...item, id: 'r1', kind: 'project-file', ref: { path: 'assets/a.png' }, label: 'a.png', role: 'style', usedBy: [] as string[] };

const kindApi = (roles: { role: string; label: string }[]) => ({ kinds: ['image'], refRoles: () => roles });
const install = (roles: { role: string; label: string }[]) => registerSubsystem({
  key: 'add-button-test', title: 't', order: 1, noPill: true, core: true, slots: { 'context-kind': [{ name: 'image', action: kindApi(roles) as never }] },
});
const html = (toggle = false) => renderToStaticMarkup(createElement(ContextAddButton, { sessionId: S, projectId: 'p', candidate: file, toggle }));
const put = (p: typeof primary | null, refs: typeof inCtx[] = []) => __applyChatContext(S, { revision: 1, primary: p, refs });

beforeEach(() => { __resetChatContextStore(); install([{ role: 'style', label: 'Как образец стиля' }]); });

describe('«В контекст ▾»', () => {
  it('без основного объекта серая, причина — в подсказке', () => {
    put(null);
    const h = html();
    expect(h).toContain('data-context-add="off"');
    expect(h).toContain(NO_PRIMARY_REASON);
    expect(h).toContain('disabled');
  });

  it('основной не берёт такой референс — серая с другой причиной', () => {
    install([]);
    put(primary);
    expect(html()).toContain(NO_ROLE_REASON);
  });

  it('одна роль — обычная кнопка без стрелки (вопроса не будет)', () => {
    put(primary);
    const h = html();
    expect(h).toContain('data-context-add="add"');
    expect(h).toContain('Положить в контекст');
    expect(h).not.toContain('lucide-chevron-down');
  });

  it('несколько ролей — стрелка меню и подсказка про выбор роли', () => {
    install([{ role: 'style', label: 'Как образец стиля' }, { role: 'object', label: 'Как объект' }]);
    put(primary);
    const h = html();
    expect(h).toContain('lucide-chevron-down');
    expect(h).toContain('Выбрать роль');
  });

  it('уже в контексте: плашка с ролью; с toggle — кнопка «В контексте» со снятием', () => {
    put(primary, [inCtx]);
    expect(html()).toContain('В контексте · образец стиля');
    const t = html(true);
    expect(t).toContain('Убрать из контекста');
    expect(t).not.toContain('образец стиля');
  });
});
