// Строка «Предгейт» в карточке шага/доработки: зелёный — «зелёный по <sha7>, <HH:MM>»,
// красный — «красный (серия N)», без записи и «нечего проверять» — «не прогонялся»,
// у ролей не step|rework строки нет. Рендер через linkedom.
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
const { PregateLine } = await import('../PregateLine');

function render(task: Pick<Task, 'role' | 'pregate'>) {
  const host = doc.createElement('div');
  doc.body.appendChild(host);
  const root = createRoot(host);
  act(() => { root.render(createElement(PregateLine, { task })); });
  return host;
}

// Отметка без Z — локальное время: HH:MM ожидаем в любой TZ прогона
const green = { sha: 'd9ddd429f1c0', green: true, summary: '', at: '2026-10-25T14:32:00', redStreak: 0, redKey: null, nothingApplicable: false };
const red2 = { sha: 'd9ddd429f1c0', green: false, summary: '✗ build: error CS0246', at: '2026-10-25T15:10:00', redStreak: 2, redKey: 'build', nothingApplicable: false };

describe('PregateLine', () => {
  it('зелёный: «зелёный по <sha7>, <HH:MM>»', () => {
    const el = render({ role: 'step', pregate: green });
    expect(el.querySelectorAll('[data-testid="pregate-line"]').length).toBe(1);
    expect(el.textContent).toBe('Предгейт: зелёный по d9ddd42, 14:32');
  });

  it('красный: «красный (серия N)» по redStreak, та же строка у rework', () => {
    expect(render({ role: 'step', pregate: red2 }).textContent).toBe('Предгейт: красный (серия 2)');
    expect(render({ role: 'rework', pregate: { ...red2, redStreak: 1 } }).textContent).toBe('Предгейт: красный (серия 1)');
  });

  it('без записи: «не прогонялся»', () => {
    expect(render({ role: 'step' }).textContent).toBe('Предгейт: не прогонялся');
    expect(render({ role: 'step', pregate: null }).textContent).toBe('Предгейт: не прогонялся');
  });

  it('«нечего проверять» (NothingApplicable): тоже «не прогонялся»', () => {
    const nothing = { ...green, green: false, nothingApplicable: true };
    expect(render({ role: 'step', pregate: nothing }).textContent).toBe('Предгейт: не прогонялся');
  });

  it('у ролей не step|rework строки нет', () => {
    expect(render({}).innerHTML).toBe('');
    expect(render({ role: 'feature', pregate: green }).innerHTML).toBe('');
    expect(render({ role: 'review', pregate: red2 }).innerHTML).toBe('');
  });
});
