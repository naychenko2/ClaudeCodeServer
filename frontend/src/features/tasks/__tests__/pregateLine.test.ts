// Строка «Предгейт» в карточке шага/доработки: зелёный — «зелёный по <sha7>, <HH:MM>»,
// красный — «красный (серия N)», без записи и «нечего проверять» — «не прогонялся»,
// у ролей не step|rework строки нет. Рендер через linkedom.
import { describe, it, expect, vi, beforeAll, afterAll } from 'vitest';
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

// Отметка приходит с бэка в UTC с «Z» — как в проде. Зона просмотра фиксируется
// в самом тесте: на CI (TZ=UTC) локальное время и UTC совпадают, и подмена
// «локального» на «UTC» прошла бы незамеченной. Etc/GMT-3 — фиксированное
// смещение +3 без перехода на летнее время.
const TZ_TEST = 'Etc/GMT-3';
const AT_GREEN = '2026-10-25T14:32:00.000Z';
const green = { sha: 'd9ddd429f1c0', green: true, summary: '', at: AT_GREEN, redStreak: 0, redKey: null, nothingApplicable: false };
const red2 = { sha: 'd9ddd429f1c0', green: false, summary: '✗ build: error CS0246', at: '2026-10-25T15:10:00.000Z', redStreak: 2, redKey: 'build', nothingApplicable: false };

// Локальное время той же отметки — из той же строки, а не константой
function localTime(iso: string): string {
  return new Date(iso).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
}

// Тот же момент в UTC — эталон для гарда «локальное ≠ UTC». Считается из той же
// отметки, поэтому гард остаётся живым при любой фикстуре (смена времени/даты
// не превращает проверку в пустую или в ложный красный).
function utcTime(iso: string): string {
  return new Date(iso).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit', timeZone: 'UTC' });
}

describe('PregateLine', () => {
  let originalTz: string | undefined;
  beforeAll(() => {
    originalTz = process.env.TZ;
    process.env.TZ = TZ_TEST;
  });
  afterAll(() => {
    if (originalTz === undefined) delete process.env.TZ;
    else process.env.TZ = originalTz;
  });

  it('зелёный: «зелёный по <sha7>, <HH:MM>» — время в локальной зоне', () => {
    const local = localTime(AT_GREEN);
    // Зона должна отличаться от UTC, иначе проверка «локальное против UTC» пустая
    expect(local).not.toBe(utcTime(AT_GREEN));
    const el = render({ role: 'step', pregate: green });
    expect(el.querySelectorAll('[data-testid="pregate-line"]').length).toBe(1);
    expect(el.textContent).toBe(`Предгейт: зелёный по d9ddd42, ${local}`);
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
