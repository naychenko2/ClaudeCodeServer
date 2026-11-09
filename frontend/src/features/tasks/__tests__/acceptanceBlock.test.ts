// Секция «Критерии приёмки»: пусто — ничего, частично — пометка у критерия без свидетельства,
// полный набор — таблица «как проверено / результат» без пометок. Рендер через linkedom.
import { describe, it, expect, vi } from 'vitest';
import { parseHTML } from 'linkedom';
import type { Task } from '../../../types';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
// Badge читает useIsMobile → matchMedia
(win as unknown as { matchMedia: unknown }).matchMedia = () => ({
  matches: false, addEventListener() {}, removeEventListener() {},
});
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

// Динамический импорт ПОСЛЕ стабов: react-dom читает window при загрузке
const { act, createElement } = await import('react');
const { createRoot } = await import('react-dom/client');
const { AcceptanceBlock } = await import('../AcceptanceBlock');

function render(task: Pick<Task, 'acceptance' | 'evidence'>) {
  const host = doc.createElement('div');
  doc.body.appendChild(host);
  const root = createRoot(host);
  act(() => { root.render(createElement(AcceptanceBlock, { task })); });
  return host;
}

const acc = [{ id: 'a', text: 'Первый' }, { id: 'b', text: 'Второй' }];

describe('AcceptanceBlock', () => {
  it('без критериев ничего не рисует', () => {
    expect(render({}).innerHTML).toBe('');
    expect(render({ acceptance: [], evidence: [{ criterionId: 'a', how: 'x', result: 'y' }] }).innerHTML).toBe('');
  });

  it('частичный набор: пометка только у критерия без свидетельства', () => {
    const el = render({ acceptance: acc, evidence: [{ criterionId: 'a', how: 'vitest', result: 'зелёный' }] });
    expect(el.querySelectorAll('[data-testid="acceptance-row"]').length).toBe(2);
    expect(el.querySelectorAll('[data-testid="acceptance-missing"]').length).toBe(1);
    expect(el.textContent).toContain('vitest');
    expect(el.textContent).toContain('зелёный');
    expect(el.querySelectorAll('[data-testid="acceptance-row"]')[1].textContent).toContain('Нет свидетельства');
  });

  it('полный набор: пометок нет', () => {
    const el = render({
      acceptance: acc,
      evidence: [{ criterionId: 'a', how: 'h1', result: 'r1' }, { criterionId: 'b', how: 'h2', result: 'r2' }],
    });
    expect(el.querySelectorAll('[data-testid="acceptance-missing"]').length).toBe(0);
    expect(el.querySelectorAll('[data-testid="acceptance-evidence"]').length).toBe(2);
  });
});
