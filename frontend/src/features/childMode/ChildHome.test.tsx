// KM-F5: главная ребёнка — обычный день и первый день (правила не согласованы).
// Окружение — node + linkedom, как в taskCardAssignee.test: проверяются элементы,
// порядок плиток и вызовы колбэков пропсов; цвета/тени (CSSOM-значения) — не здесь.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { ChildStatus } from './types';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
// Мобильная ветка включается переключателем ниже: useIsMobile читает window.matchMedia при mount.
let mobile = false;
Object.assign(win, {
  matchMedia: () => ({
    matches: mobile, addEventListener: () => {}, removeEventListener: () => {},
    addListener: () => {}, removeListener: () => {},
  }),
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
vi.stubGlobal('localStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
vi.stubGlobal('sessionStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const { ChildHome, CHILD_SUBJECTS } = await import('./ChildHome');
import type { ChildLastLesson, ChildSubjectId } from './ChildHome';

const status = (over: Partial<ChildStatus> = {}): ChildStatus => ({
  state: 'ok', space: 'lessons', minutesLeft: 40,
  windowEndsAt: '2026-11-17T19:00:00+03:00', nextWindowAt: null,
  warnMinutes: 5, maxAttachmentBytes: 10_000_000,
  attachmentTypes: ['image/png'], rulesAgreedAt: '2026-11-10T12:00:00+03:00', dailyMinutes: 60, windows: [], minutesToday: 0, ...over,
});

const lastLesson: ChildLastLesson = { subjectId: 'math', title: 'Сравнение дробей', progress: 'Шаг 2 из 3' };

// Моки типуются как сами колбэки пропсов: createElement(ChildHome, props) проверяется
// типом Props, «голый» vi.fn под него не подходит.
type Handlers = {
  onOpenSubject: ((id: ChildSubjectId) => void) & ReturnType<typeof vi.fn>;
  onResume: (() => void) & ReturnType<typeof vi.fn>;
  onStartRules: (() => void) & ReturnType<typeof vi.fn>;
};

function handlers(): Handlers {
  return {
    onOpenSubject: vi.fn() as unknown as Handlers['onOpenSubject'],
    onResume: vi.fn() as unknown as Handlers['onResume'],
    onStartRules: vi.fn() as unknown as Handlers['onStartRules'],
  };
}

async function render(over: Partial<ChildStatus> = {}, h: Handlers = handlers(), last: ChildLastLesson | null = lastLesson) {
  mobile = false;
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  const props = { childName: 'Лиза', status: status(over), lastLesson: last, ...h };
  await act(async () => { root.render(createElement(ChildHome, props)); });
  await act(async () => { await Promise.resolve(); });
  return { host: host as HTMLElement, h };
}

// Мобильная ветка: тот же рендер при matchMedia.matches = true.
async function renderMobile(h: Handlers = handlers()) {
  mobile = true;
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  await act(async () => { root.render(createElement(ChildHome, { childName: 'Лиза', status: status(), lastLesson, ...h })); });
  await act(async () => { await Promise.resolve(); });
  return { host: host as HTMLElement, h };
}

const buttons = (host: HTMLElement) => Array.from(host.querySelectorAll('button'));
// Глубочайший узел с текстом (последний в документном порядке): кликать надо сам
// контрол, а не его родителя-обёртку.
const byText = (host: HTMLElement, t: string) =>
  Array.from(host.querySelectorAll('*')).filter((el) => (el.textContent ?? '').includes(t)).pop() as HTMLElement | undefined;
const subjectButtons = (host: HTMLElement) =>
  buttons(host).filter((b) => CHILD_SUBJECTS.some((s) => (b.textContent ?? '').includes(s.label)));

async function click(el: HTMLElement) {
  await act(async () => { el.click(); });
}

beforeEach(() => { mobile = false; });

describe('ChildHome: обычный день', () => {
  it('приветствие, строка про окно (часы из windowEndsAt как таймзона ребёнка) и карточка «Вчера остановились тут»', async () => {
    const { host } = await render();
    const html = host.innerHTML;
    // Интерполированные строки («Привет, {childName}») React разделяет маркерами
    // «<!-- -->» — поэтому по частям, а не одной строкой.
    expect(html).toContain('Привет,');
    expect(html).toContain('Лиза');
    expect(html).toContain('Уроки открыты до');
    expect(html).toContain('19:00');
    expect(html).toContain('Вчера остановились тут');
    expect(html).toContain('Математика');
    expect(html).toContain('Сравнение дробей');
    expect(html).toContain('Шаг 2 из 3');
  });

  it('три плитки предметов в порядке CHILD_SUBJECTS; клик по «Математике» → onOpenSubject(math)', async () => {
    const { host, h } = await render();
    // Порядок — по id предмета: текст плитки включает глиф («×÷Математика»), сравнивать строки нельзя.
    const order = subjectButtons(host).map((b) => CHILD_SUBJECTS.find((s) => (b.textContent ?? '').includes(s.label))!.id);
    expect(order).toEqual(CHILD_SUBJECTS.map((s) => s.id));
    const math = subjectButtons(host)[0];
    await click(math);
    expect(h.onOpenSubject).toHaveBeenCalledTimes(1);
    expect(h.onOpenSubject).toHaveBeenCalledWith('math' satisfies ChildSubjectId);
  });

  it('«Студия» неактивна: бейдж «скоро», клик по ней не зовёт ни один колбэк', async () => {
    const { host, h } = await render();
    const studio = byText(host, 'Студия')!;
    expect(studio).toBeTruthy();
    expect(studio.parentElement?.textContent ?? '').toContain('скоро');
    await click(studio);
    expect(h.onOpenSubject).not.toHaveBeenCalled();
    expect(h.onResume).not.toHaveBeenCalled();
    expect(h.onStartRules).not.toHaveBeenCalled();
  });

  it('«Продолжить» в карточке → onResume; без lastLesson карточки нет', async () => {
    const { host, h } = await render();
    await click(byText(host, 'Продолжить')!);
    expect(h.onResume).toHaveBeenCalledTimes(1);

    const bare = await render({}, handlers(), null);
    expect(bare.host.innerHTML).not.toContain('Вчера остановились тут');
    expect(bare.host.innerHTML).not.toContain('Продолжить');
  });
});

describe('ChildHome: rules_pending', () => {
  it('плиток предметов нет; «Начать вместе с папой» зовёт onStartRules ровно 1 раз', async () => {
    const { host, h } = await render({ state: 'rules_pending', windowEndsAt: null, rulesAgreedAt: null });
    expect(subjectButtons(host)).toHaveLength(0);
    expect(host.innerHTML).not.toContain('Математика');
    expect(host.innerHTML).toContain('Сначала договоримся о правилах');
    await click(byText(host, 'Начать вместе с папой')!);
    expect(h.onStartRules).toHaveBeenCalledTimes(1);
    expect(h.onOpenSubject).not.toHaveBeenCalled();
  });
});

describe('ChildHome: мобила (360)', () => {
  it('плитки в одну колонку; тач-цель каждой плитки ≥ 40px', async () => {
    const { host } = await renderMobile();
    const subjects = subjectButtons(host);
    expect(subjects).toHaveLength(3);
    const grid = subjects[0].parentElement!;
    expect(grid.style.gridTemplateColumns).toBe('1fr');
    for (const b of subjects) {
      expect(Number.parseFloat(b.style.minHeight), `тач-цель «${b.textContent}»`).toBeGreaterThanOrEqual(40);
    }
  });
});
