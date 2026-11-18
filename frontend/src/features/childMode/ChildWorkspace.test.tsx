// KM-F10: детская оболочка — какой экран выбирается по состоянию статуса, ключ чата урока,
// отсутствие взрослых слоёв. Окружение — node + linkedom; тяжёлые дети подменены заглушками.

import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, createElement } from 'react';
import { createRoot } from 'react-dom/client';
import { parseHTML } from 'linkedom';
import type { ChildStatus } from './types';

const { window: win, document: doc } = parseHTML('<!doctype html><html><body><div id="root"></div></body></html>');
Object.assign(win, {
  innerWidth: 1280,
  matchMedia: () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {}, addListener: () => {}, removeListener: () => {} }),
});
vi.stubGlobal('window', win);
vi.stubGlobal('document', doc);
vi.stubGlobal('navigator', { language: 'ru-RU', onLine: true, userAgent: 'node' });
vi.stubGlobal('HTMLElement', win.HTMLElement);
vi.stubGlobal('Node', win.Node);
vi.stubGlobal('Event', win.Event);
vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} });
const store: Record<string, string> = {};
vi.stubGlobal('localStorage', { getItem: (k: string) => store[k] ?? null, setItem: (k: string, v: string) => { store[k] = v; }, removeItem: (k: string) => { delete store[k]; } });
vi.stubGlobal('sessionStorage', { getItem: () => null, setItem: () => {}, removeItem: () => {} });
(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const h = vi.hoisted(() => ({
  status: null as unknown,
  offline: false,
  mounts: [] as string[],
  childMode: vi.fn(),
  api: {
    spaceChats: vi.fn(),
    spaceChat: vi.fn(),
    getSubjects: vi.fn(),
    rulesStart: vi.fn(),
  },
}));

vi.mock('../../lib/personas', () => ({ setPersonasChildMode: (on: boolean) => h.childMode(on) }));
vi.mock('./useChildStatus', () => ({ useChildStatus: () => ({ status: h.status, offline: h.offline }) }));
vi.mock('./api', () => ({ childApi: h.api, ChildAttachmentRejectedError: class extends Error {}, tutorAvatarUrl: (id: string) => `/api/personas/${id}/avatar` }));
vi.mock('./ChildLessonChat', async () => {
  const React = await import('react');
  return {
    // Записываем, что реально приехало из родителя: озвучка, предмет (id или '-'), closed
    ChildLessonChat: (p: { sessionId: string; initialVoiceMode?: boolean; subject?: { id: string } | null; subjectClosed?: boolean }) => {
      React.useEffect(() => { h.mounts.push(`${p.sessionId}:${p.initialVoiceMode}:${p.subject?.id ?? '-'}:${!!p.subjectClosed}`); }, []);
      return React.createElement('div', { 'data-testid': 'lesson-chat' }, p.sessionId);
    },
  };
});
vi.mock('./ChildHintsPanel', async () => {
  const React = await import('react');
  return { ChildHintsPanel: () => React.createElement('div', { 'data-testid': 'hints' }) };
});
vi.mock('../../components/ui/PageCanvas', async () => {
  const React = await import('react');
  return {
    PageCanvas: (p: { project?: { icon?: { color?: string } }; children: unknown }) =>
      React.createElement('div', { 'data-canvas': p.project?.icon?.color ?? 'neutral' }, p.children as never),
  };
});

vi.mock('./ChildTimeUpScreen', async () => {
  const React = await import('react');
  return { ChildTimeUpScreen: (p: { status: { state: string }; minutesToday: number | null }) => React.createElement('div', { 'data-testid': 'timeup', 'data-state': p.status.state, 'data-minutes': String(p.minutesToday) }) };
});

const { ChildWorkspace } = await import('./ChildWorkspace');

const status = (over: Partial<ChildStatus> = {}): ChildStatus => ({
  state: 'ok', space: 'lessons', minutesLeft: 40,
  windowEndsAt: '2026-11-17T19:00:00+03:00', nextWindowAt: null,
  warnMinutes: 5, maxAttachmentBytes: 10_000_000,
  attachmentTypes: ['image/png'], rulesAgreedAt: '2026-11-10T12:00:00+03:00',
  dailyMinutes: 60, windows: [], minutesToday: 0, ...over,
});

const auth = { serverUrl: '', token: 't', username: 'liza', displayName: 'Лиза' };

async function mount() {
  const root = createRoot(doc.getElementById('root')!);
  await act(async () => { root.render(createElement(ChildWorkspace, { auth, onLogout: () => {} })); });
  await act(async () => {});
  return root;
}
const click = async (el: Element) => { await act(async () => { el.dispatchEvent(new win.Event('click', { bubbles: true })); }); await act(async () => {}); };
// Строка-заголовок группы «Старые уроки» (Д2): кнопка SidebarSection с aria-expanded и счётчиком
const oldLessonsToggle = () => Array.from(doc.querySelectorAll('button')).find(b => /Старые уроки/.test(b.textContent ?? ''));

// Ответы сервера — в форме ручек: список чатов с subject/subjectClosed/voiceMode (Т3c),
// предметы — только включённые, в порядке профиля (И5)
const SERVER_CHATS = [
  { sessionId: 's1', name: 'Дроби', updatedAt: '2026-11-17T10:00:00Z', subject: 'russian', subjectClosed: false, voiceMode: true },
  { sessionId: 's2', name: 'Уравнения', updatedAt: '2026-11-16T10:00:00Z', subject: 'english', subjectClosed: false, voiceMode: false },
];
const SERVER_SUBJECTS = [
  { id: 'math', label: 'Математика', glyph: '×÷', color: 'info' as const, tutor: null },
  { id: 'english', label: 'Английский', glyph: 'Ab', color: 'success' as const, tutor: null },
  { id: 'russian', label: 'Русский язык', glyph: 'Аа', color: 'rose' as const, tutor: null },
];

beforeEach(() => {
  doc.getElementById('root')!.innerHTML = '';
  h.status = status(); h.offline = false; h.mounts.length = 0;
  h.api.spaceChats.mockReset().mockResolvedValue({ chats: SERVER_CHATS });
  h.api.getSubjects.mockReset().mockResolvedValue(SERVER_SUBJECTS);
  h.api.spaceChat.mockReset().mockResolvedValue({ sessionId: 's3' });
  h.api.rulesStart.mockReset().mockResolvedValue({ sessionId: 'r1' });
  for (const k of Object.keys(store)) delete store[k];
});

describe('ChildWorkspace', () => {
  it('time_up показывает экран «время вышло» и не грузит уроки и предметы', async () => {
    h.status = status({ state: 'time_up' });
    await mount();
    expect(doc.querySelector('[data-testid="lesson-chat"]')).toBeNull();
    expect(h.api.spaceChats).not.toHaveBeenCalled();
    expect(h.api.getSubjects).not.toHaveBeenCalled();
    expect(doc.querySelector('[data-canvas]')!.getAttribute('data-canvas')).toBe('neutral');
  });

  it('warn — плашка про оставшиеся минуты и «Хорошо» её скрывает, экран «время вышло» не открывается', async () => {
    h.status = status({ state: 'warn', minutesLeft: 5 });
    await mount();
    expect(doc.body.textContent).toContain('Осталось 5 минут');
    const ok = Array.from(doc.querySelectorAll('button')).find(b => b.textContent === 'Хорошо')!;
    await click(ok);
    expect(doc.body.textContent).not.toContain('Осталось 5 минут');
  });

  it('offline — плашка «Нет связи»', async () => {
    h.offline = true;
    await mount();
    expect(doc.body.textContent).toContain('Нет связи');
  });

  it('offline без статуса — плашка «Нет связи» всё равно видна', async () => {
    h.status = null;
    h.offline = true;
    await mount();
    expect(doc.body.textContent).toContain('Нет связи');
  });

  it('rules_pending: точка на «Правила», старт правил идёт через rulesStart', async () => {
    h.status = status({ state: 'rules_pending', rulesAgreedAt: null });
    await mount();
    expect(doc.querySelector('[data-testid="rules-dot"]')).not.toBeNull();
    expect(h.api.spaceChats).not.toHaveBeenCalled();
    const start = Array.from(doc.querySelectorAll('button')).find(b => /Начать вместе с папой/.test(b.textContent ?? ''))!;
    await click(start);
    expect(h.api.rulesStart).toHaveBeenCalledTimes(1);
  });

  it('правила показывают настоящий лимит и окна профиля, а не minutesLeft', async () => {
    h.status = status({ state: 'rules_pending', rulesAgreedAt: null, minutesLeft: 7, dailyMinutes: 40, windows: [{ from: '16:00', to: '18:00' }] });
    await mount();
    const start = Array.from(doc.querySelectorAll('button')).find(b => /Начать вместе с папой/.test(b.textContent ?? ''))!;
    await click(start);
    // модалка предыдущего теста остаётся порталом в body — берём последнюю «Дальше», свою
    const next = Array.from(doc.querySelectorAll('button')).filter(b => /^Дальше$/.test((b.textContent ?? '').trim())).pop()!;
    await click(next);
    const text = doc.body.textContent ?? '';
    expect(text).toContain('40 минут');
    expect(text).toMatch(/16:00/);
    expect(text).toMatch(/18:00/);
    expect(text).not.toContain('7 минут');
  });

  it('time_up: minutesToday=12 уходит на экран итога', async () => {
    h.status = status({ state: 'time_up', minutesToday: 12 });
    await mount();
    expect(doc.querySelector('[data-testid="timeup"]')?.getAttribute('data-minutes')).toBe('12');
  });

  it('time_up: minutesToday=null не превращается в 0', async () => {
    h.status = status({ state: 'time_up', minutesToday: null });
    await mount();
    expect(doc.querySelector('[data-testid="timeup"]')?.getAttribute('data-minutes')).toBe('null');
  });

  it('урок открывается с предметом и озвучкой из списка чатов; холст красится цветом предмета', async () => {
    await mount();
    const resume = Array.from(doc.querySelectorAll('button')).find(b => /Вчера|Продолжить|Дроби/.test(b.textContent ?? ''));
    expect(resume).toBeTruthy();
    await click(resume!);
    // s1: subject=russian, voiceMode=true — и то, и другое приехало из поля сервера, не из клиента
    expect(h.mounts).toEqual(['s1:true:russian:false']);
    expect(doc.querySelector('[data-canvas]')!.getAttribute('data-canvas')).toBe('purple');
    const row = Array.from(doc.querySelectorAll('button')).find(b => b.textContent?.includes('Уравнения'))!;
    await click(row);
    // смена урока — новый экземпляр чата, а не переиспользование
    expect(h.mounts).toEqual(['s1:true:russian:false', 's2:false:english:false']);
    expect(doc.querySelector('[data-canvas]')!.getAttribute('data-canvas')).toBe('green');
  });

  it('закрытый чат открывается без предмета и с subjectClosed; новый урок для него не предлагается', async () => {
    h.api.spaceChats.mockResolvedValue({ chats: [
      { sessionId: 's1', name: 'Пирамиды', updatedAt: '2026-11-17T10:00:00Z', subject: 'history', subjectClosed: true, voiceMode: false },
    ] });
    h.api.getSubjects.mockResolvedValue([{ id: 'math', label: 'Математика', glyph: '×÷', color: 'info', tutor: null }]);
    await mount();
    // closed-чат — в группе «Старые уроки» (Д2): сайдбар открывается с выбранным предметом,
    // группа раскрывается по своей строке — только тогда её строки доступны для клика
    const tile = Array.from(doc.querySelectorAll('button')).find(b => b.textContent?.includes('Математика'))!;
    await click(tile);
    await click(oldLessonsToggle()!);
    const row = Array.from(doc.querySelectorAll('button')).find(b => /Пирамиды/.test(b.textContent ?? ''))!;
    await click(row);
    expect(h.mounts).toEqual(['s1:false:-:true']);
    // closed-чата нет в живых предметах — метки нет и холст не окрашен
    expect(doc.querySelector('[data-canvas]')!.getAttribute('data-canvas')).toBe('neutral');
    // «Новый урок» в сайдбаре — только для живого предмета
    expect(Array.from(doc.querySelectorAll('button')).some(b => b.textContent === 'Новый урок')).toBe(false);
  });

  it('закрытые чаты — в свёрнутой группе «Старые уроки» ниже обычных; пометки «закрыт» в строках нет', async () => {
    h.api.spaceChats.mockResolvedValue({ chats: [
      { sessionId: 's1', name: 'Дроби', updatedAt: '2026-11-17T10:00:00Z', subject: 'russian', subjectClosed: false, voiceMode: false },
      { sessionId: 's2', name: 'Старый урок', updatedAt: '2026-11-16T10:00:00Z', subject: 'history', subjectClosed: true, voiceMode: false },
      { sessionId: 's3', name: 'Таблицы', updatedAt: '2026-11-15T10:00:00Z', subject: null, subjectClosed: true, voiceMode: false },
    ] });
    await mount();
    const tile = Array.from(doc.querySelectorAll('button')).find(b => b.textContent?.includes('Математика'))!;
    await click(tile);

    const toggle = oldLessonsToggle();
    expect(toggle, 'группа «Старые уроки» есть').toBeTruthy();
    expect(toggle!.getAttribute('aria-expanded'), 'свёрнута по умолчанию').toBe('false');
    // заголовок группы — с числом закрытых чатов
    expect(toggle!.textContent).toContain('Старые уроки');
    expect(toggle!.textContent).toContain('2');

    // группа идёт ниже обычных строк; в самих строках пометки «закрыт» нет — её говорит заголовок
    const btns = Array.from(doc.querySelectorAll('button'));
    const live = btns.findIndex(b => b.textContent?.startsWith('Дроби'));
    expect(live, 'обычный урок остался в общем списке').toBeGreaterThanOrEqual(0);
    expect(btns.indexOf(toggle!), 'группа ниже обычных строк').toBeGreaterThan(live);
    expect(doc.body.textContent).not.toContain('закрыт');

    // раскрытие по клику; чат группы открывается как раньше — без предмета, subjectClosed=true
    await click(toggle!);
    expect(toggle!.getAttribute('aria-expanded')).toBe('true');
    const row = Array.from(doc.querySelectorAll('button')).find(b => b.textContent === 'Старый урок')!;
    await click(row);
    expect(h.mounts).toEqual(['s2:false:-:true']);
  });

  it('нет закрытых чатов — группы «Старые уроки» нет', async () => {
    await mount();
    const tile = Array.from(doc.querySelectorAll('button')).find(b => b.textContent?.includes('Математика'))!;
    await click(tile);
    expect(oldLessonsToggle(), 'раздела нет').toBeUndefined();
    expect(doc.body.textContent).not.toContain('Старые уроки');
  });

  it('новый урок шлёт {subject} на сервер', async () => {
    await mount();
    const tile = Array.from(doc.querySelectorAll('button')).find(b => b.textContent?.includes('Математика'))!;
    await click(tile);
    const start = Array.from(doc.querySelectorAll('button')).find(b => b.textContent === 'Начать урок')!;
    await click(start);
    expect(h.api.spaceChat).toHaveBeenCalledWith('lessons', 'math');
  });

  it('отказ по предмету — спокойный текст сервера, без технических кодов', async () => {
    h.api.spaceChat.mockRejectedValue({ body: { code: 'subject_disabled', message: 'Этот предмет закрыт.' } });
    await mount();
    const tile = Array.from(doc.querySelectorAll('button')).find(b => b.textContent?.includes('Математика'))!;
    await click(tile);
    const start = Array.from(doc.querySelectorAll('button')).find(b => b.textContent === 'Начать урок')!;
    await click(start);
    expect(doc.body.textContent).toContain('Этот предмет закрыт.');
    expect(doc.body.textContent).not.toContain('subject_disabled');
  });

  it('двойной клик «Начать урок»: второй игнорируется, пока первый в полёте', async () => {
    let resolveChat: (v: { sessionId: string }) => void;
    h.api.spaceChat.mockImplementation(() => new Promise(r => { resolveChat = r; }));
    await mount();
    const tile = Array.from(doc.querySelectorAll('button')).find(b => b.textContent?.includes('Математика'))!;
    await click(tile);
    const start = Array.from(doc.querySelectorAll('button')).find(b => b.textContent === 'Начать урок')!;
    await click(start);
    await click(start);
    expect(h.api.spaceChat).toHaveBeenCalledTimes(1);
    await act(async () => { resolveChat!({ sessionId: 's3' }); });
  });

  it.each([
    ['ok', { timeup: false, text: null }],
    ['warn', { timeup: false, text: 'Осталось 5 минут' }],
    ['rules_pending', { timeup: false, text: 'Начать вместе с папой' }],
    ['time_up', { timeup: true, text: null }],
    ['out_of_window', { timeup: true, text: null }],
    ['budget_exhausted', { timeup: true, text: null }],
  ] as const)('state=%s: экран «время вышло» — только для time_up/out_of_window/budget_exhausted', async (state, exp) => {
    h.status = status({ state, minutesLeft: 5, rulesAgreedAt: state === 'rules_pending' ? null : '2026-11-10T12:00:00+03:00' });
    await mount();
    expect(!!doc.querySelector('[data-testid="timeup"]')).toBe(exp.timeup);
    if (exp.timeup) expect(doc.querySelector('[data-testid="timeup"]')!.getAttribute('data-state')).toBe(state);
    if (exp.text) expect(doc.body.textContent).toContain(exp.text);
  });

  it('взрослых слоёв нет: ни хаб-табов, ни поиска, ни уведомлений, ни дока проектов', async () => {
    await mount();
    const text = doc.body.textContent ?? '';
    for (const word of ['Проекты', 'Календарь', 'Заметки', 'Персоны', 'Уведомления', 'Поиск']) expect(text).not.toContain(word);
    expect(doc.querySelector('[data-rail-item]')).toBeNull();
  });

  it('флаг детского режима персон живёт, пока оболочка смонтирована, и гаснет при размонтировании', async () => {
    h.childMode.mockClear();
    const root = await mount();
    expect(h.childMode).toHaveBeenLastCalledWith(true);
    await act(async () => { root.unmount(); });
    expect(h.childMode).toHaveBeenLastCalledWith(false);
  });
});
