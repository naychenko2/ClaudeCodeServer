// KM-F5: главная ребёнка — обычный день и первый день (правила не согласованы).
// Окружение — node + linkedom, как в taskCardAssignee.test: проверяются элементы,
// порядок плиток и вызовы колбэков пропсов; цвета/тени (CSSOM-значения) — не здесь.
// Т3e: проп subjects обязателен — фолбэка волны 1 больше нет, плитки всегда из пропа.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { ChildStatus, ChildSubjectColor, ChildSubjectTutor, ChildSubjectView } from './types';

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

const { ChildHome } = await import('./ChildHome');
import type { ChildLastLesson } from './ChildHome';

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
  // id в колбэке — серверный ключ предмета (строка).
  onOpenSubject: ((id: string) => void) & ReturnType<typeof vi.fn>;
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

// Плитки всегда с сервера (форма — как у GET /api/child/subjects). Порядок фикстуры
// намеренно произвольный: порядок плиток берётся из пропа.
const tutorOf = (personaId: string, name: string, hasAvatar: boolean): ChildSubjectTutor => ({ personaId, name, hasAvatar });
const SERVER_SUBJECTS: ChildSubjectView[] = [
  { id: 'history', label: 'История', glyph: 'Ии', color: 'lilac' as ChildSubjectColor, tutor: tutorOf('p-hist', 'Летописец Пётр', false) },
  { id: 'english', label: 'Английский', glyph: 'Ab', color: 'success', tutor: tutorOf('p-en', 'Мисс Пиппа', false) },
  { id: 'math', label: 'Математика', glyph: '×÷', color: 'info', tutor: tutorOf('p-math', 'Сова Ульяна', true) },
  { id: 'russian', label: 'Русский язык', glyph: 'Аа', color: 'rose', tutor: tutorOf('p-ru', 'Кот Буквоед', false) },
  { id: 'chess', label: 'Шахматы', glyph: '♞', color: 'accent', tutor: tutorOf('p-chess', 'Гроссмейстер Ёж', false) },
  { id: 'biology', label: 'Окружающий мир', glyph: '', color: 'warning', tutor: null },
];

async function render(
  over: Partial<ChildStatus> = {}, h: Handlers = handlers(), last: ChildLastLesson | null = lastLesson,
  subjects: ChildSubjectView[] = SERVER_SUBJECTS,
) {
  mobile = false;
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  const props = { childName: 'Лиза', status: status(over), subjects, lastLesson: last, ...h };
  await act(async () => { root.render(createElement(ChildHome, props)); });
  await act(async () => { await Promise.resolve(); });
  return { host: host as HTMLElement, h };
}

// Мобильная ветка: тот же рендер при matchMedia.matches = true.
async function renderMobile(h: Handlers = handlers(), subjects: ChildSubjectView[] = SERVER_SUBJECTS) {
  mobile = true;
  const host = doc.createElement('div');
  doc.getElementById('root')!.appendChild(host);
  const root = createRoot(host as unknown as HTMLElement);
  await act(async () => { root.render(createElement(ChildHome, { childName: 'Лиза', status: status(), subjects, lastLesson, ...h })); });
  await act(async () => { await Promise.resolve(); });
  return { host: host as HTMLElement, h };
}

const buttons = (host: HTMLElement) => Array.from(host.querySelectorAll('button'));
// Глубочайший узел с текстом (последний в документном порядке): кликать надо сам
// контрол, а не его родителя-обёртку.
const byText = (host: HTMLElement, t: string) =>
  Array.from(host.querySelectorAll('*')).filter((el) => (el.textContent ?? '').includes(t)).pop() as HTMLElement | undefined;
const serverTileButtons = (host: HTMLElement) =>
  buttons(host).filter((b) => SERVER_SUBJECTS.some((s) => (b.textContent ?? '').includes(s.label)));
const tileOf = (host: HTMLElement, id: string) => {
  const s = SERVER_SUBJECTS.find((x) => x.id === id)!;
  return serverTileButtons(host).find((b) => (b.textContent ?? '').includes(s.label))!;
};
// Первый span плитки — её глиф (SubjectGlyph), дальше идёт блок метки
const glyphOf = (tile: HTMLElement) => tile.querySelector('span') as HTMLElement;

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

describe('ChildHome: плитки с сервера', () => {
  it('шесть предметов — шесть плиток в порядке пропа', async () => {
    const { host } = await render({}, handlers(), null, SERVER_SUBJECTS);
    const tiles = serverTileButtons(host);
    expect(tiles).toHaveLength(6);
    const order = tiles.map((b) => SERVER_SUBJECTS.find((s) => (b.textContent ?? '').includes(s.label))!.id);
    expect(order).toEqual(SERVER_SUBJECTS.map((s) => s.id));
  });

  it('клик по плитке зовёт onOpenSubject с серверным id', async () => {
    const { host, h } = await render({}, handlers(), null, SERVER_SUBJECTS);
    await click(tileOf(host, 'chess'));
    expect(h.onOpenSubject).toHaveBeenCalledTimes(1);
    expect(h.onOpenSubject).toHaveBeenCalledWith('chess');
  });

  it('на плитке — имя репетитора; с hasAvatar=true знак — картинка ручки /avatar', async () => {
    const { host } = await render({}, handlers(), null, SERVER_SUBJECTS);
    const math = tileOf(host, 'math');
    expect(math.textContent ?? '').toContain('Сова Ульяна');
    expect((math.querySelector('img')?.getAttribute('src') ?? '')).toContain('/api/personas/p-math/avatar');
  });

  it('hasAvatar=false — инициалы имени, картинки нет', async () => {
    const { host } = await render({}, handlers(), null, SERVER_SUBJECTS);
    const ru = tileOf(host, 'russian');
    expect(ru.querySelector('img')).toBeNull();
    expect(ru.textContent ?? '').toContain('КБ');
  });

  it('tutor=null — строки репетитора нет: только глиф и метка', async () => {
    const { host } = await render({}, handlers(), null, SERVER_SUBJECTS);
    const bio = tileOf(host, 'biology');
    expect(bio.querySelector('img')).toBeNull();
    // глиф + обёртка метки + метка (3 span): обёртки строки репетитора нет — с репетитором их 5
    expect(bio.querySelectorAll('span')).toHaveLength(3);
    // пустой глиф с сервера — «?» (плитка без знака не читается)
    expect(glyphOf(bio).textContent).toBe('?');
  });

  it('неизвестный ключ цвета — пара info (фолбэк палитры)', async () => {
    const { host } = await render({}, handlers(), null, SERVER_SUBJECTS);
    expect(glyphOf(tileOf(host, 'history')).getAttribute('style') ?? '').toContain('var(--c-info-bg)');
  });

  it('карточка «Вчера остановились тут» — из серверного списка: метка, репетитор и шаг', async () => {
    const last: ChildLastLesson = { subjectId: 'chess', title: 'Конь и пешки', progress: 'Шаг 2 из 3' };
    const { host } = await render({}, handlers(), last, SERVER_SUBJECTS);
    const text = host.textContent ?? '';
    expect(text).toContain('Вчера остановились тут');
    expect(text).toContain('Шахматы · Конь и пешки');
    expect(text).toContain('Гроссмейстер Ёж · Шаг 2 из 3');
  });

  it('чата по закрытому/удалённому предмету: карточка есть — без метки предмета, в нейтральном цвете (Т3e)', async () => {
    const { host, h } = await render({}, handlers(), { subjectId: 'nope', title: 'Урок без предмета' }, SERVER_SUBJECTS);
    const label = byText(host, 'Вчера остановились тут')!;
    expect(label).toBeTruthy();
    // подпись → блок текста → строка flex → карточка (Island)
    const card = label.parentElement!.parentElement!.parentElement!;
    const text = card.textContent ?? '';
    expect(text).toContain('Урок без предмета');
    // метки живых предметов в карточке нет
    for (const s of SERVER_SUBJECTS) expect(text).not.toContain(s.label);
    // квадрат нейтральный: C.bgInset/C.textSecondary, не палитра предмета
    const glyph = card.querySelector('span') as HTMLElement;
    expect(glyph.getAttribute('style') ?? '').toContain('var(--c-bg-inset)');
    expect(glyph.getAttribute('style') ?? '').toContain('var(--c-text-secondary)');
    // строки репетитора и аватара нет
    expect(card.querySelector('img')).toBeNull();
    // «Продолжить» зовёт onResume: для закрытого/удалённого предмета сервер даёт
    // subjectClosed=true (ChildChatsController.cs:102) — чат откроется только для чтения
    await click(byText(card, 'Продолжить')!);
    expect(h.onResume).toHaveBeenCalledTimes(1);
  });

  it('пустой список предметов — ни плиток, ни подписи секции', async () => {
    const { host } = await render({}, handlers(), null, []);
    expect(serverTileButtons(host)).toHaveLength(0);
    expect(host.textContent ?? '').not.toContain('Предметы');
  });
});

describe('ChildHome: rules_pending', () => {
  it('плиток предметов нет; «Начать вместе с папой» зовёт onStartRules ровно 1 раз', async () => {
    const { host, h } = await render({ state: 'rules_pending', windowEndsAt: null, rulesAgreedAt: null });
    expect(serverTileButtons(host)).toHaveLength(0);
    expect(host.innerHTML).not.toContain('Математика');
    expect(host.innerHTML).toContain('Сначала договоримся о правилах');
    await click(byText(host, 'Начать вместе с папой')!);
    expect(h.onStartRules).toHaveBeenCalledTimes(1);
    expect(h.onOpenSubject).not.toHaveBeenCalled();
  });
});

describe('ChildHome: мобила (360)', () => {
  it('мобила на шести серверных предметах: сетка не ломается — одна колонка, тач-цель ≥ 40px', async () => {
    const { host } = await renderMobile(handlers(), SERVER_SUBJECTS);
    const tiles = serverTileButtons(host);
    expect(tiles).toHaveLength(6);
    expect(tiles[0].parentElement!.style.gridTemplateColumns).toBe('1fr');
    for (const b of tiles) {
      expect(Number.parseFloat(b.style.minHeight), `тач-цель «${b.textContent}»`).toBeGreaterThanOrEqual(40);
    }
  });
});
