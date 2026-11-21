// Х-W4-78 (дефект fab7e418, факт 2): контракт детской панели уроков.
//
// Проверяемое:
// 1. ОДИН источник на один список: текущие уроки — штатная ручка списка чатов
//    (api.sessions.list → через listProjectSessions), «Старые уроки» — детская ручка
//    spaceChats, только subjectClosed. Не-закрытый элемент spaceChats в текущий список
//    не попадает; архивный чат проекта — тем более (у ребёнка архива нет).
// 2. Действия по И11: «открыть урок» (клик строки → onSelectSession) и «Новый урок»
//    (одна точка childApi.createLesson; двойной тап — один вызов; отказ — серверный
//    текст в тосте). Действий взрослого списка в DOM нет.
// 3. Строка «Уроки видит: …» (решение 86) — в панели; пустых кураторов нет вовсе.
// 4. «Старые уроки» — свёрнуты по умолчанию (Д2); их строки — не кнопки (проект
//    закрытого предмета ребёнку не виден), подписаны subjectLabel.
// 5. Realtime: chat_deleted убирает строку; активный урок помечен; созданный урок,
//    которого нет в ответе списка, добавляется сразу.
//
// Транспорт мокнут на двух точках источников: lib/api (штатная ручка) и ./api
// (childApi). signalr — перехват handlers: realtime проверяется настоящим событием,
// не прямым setState. Кнопки зовутся через reactProps (тот же путь, что ChildRulesOnboarding.test).
// Окружение — общий fake-dom хелпер.

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { act, createElement } from 'react';
import type { Session } from '../../types';
import type { ChildChatListItem } from './types';
import { mount, installFakeDom, uninstallFakeDom, findNodeByAttr, reactProps, textNodes } from '../../components/chat/__tests__/fakeDom';

const { store } = vi.hoisted(() => {
  (globalThis as any).self = globalThis;
  const store = new Map<string, string>();
  (globalThis as any).localStorage = {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => { store.set(k, String(v)); },
    removeItem: (k: string) => { store.delete(k); },
    clear: () => store.clear(),
    key: () => null,
    get length() { return store.size; },
  } as Storage;
  (globalThis as any).sessionStorage = {
    getItem: () => null, setItem: () => {}, removeItem: () => {}, clear: () => {},
    key: () => null, get length() { return 0; },
  } as Storage;
  return { store };
});

// ── штатная ручка списка чатов: единственный источник текущего списка ──
const src = vi.hoisted(() => ({
  sessions: [] as Session[],
  sessionCalls: [] as string[],
  spaceChats: [] as ChildChatListItem[],
  spaceCalls: [] as string[],
  createLesson: [] as string[],
  createResult: Promise.resolve({ sessionId: 'new1' }) as Promise<{ sessionId: string }>,
  toasts: [] as string[],
  msgHandlers: new Set<(m: { type: string; sessionId?: string }) => void>(),
}));

vi.mock('../../lib/api', () => ({
  api: {
    sessions: {
      list: (pid: string) => { src.sessionCalls.push(pid); return Promise.resolve(src.sessions); },
    },
  },
  setGitSessionContext: () => {},
}));

// ── детская ручка: spaceChats и createLesson (тот же контракт, что у живого api.ts) ──
vi.mock('./api', () => ({
  childApi: {
    spaceChats: (space: string) => { src.spaceCalls.push(space); return Promise.resolve({ chats: src.spaceChats }); },
    createLesson: (subject: string) => { src.createLesson.push(subject); return src.createResult; },
  },
  // Consumer'ы './api' в графе импортов этой области (ChildLessonHeader → tutorAvatarUrl)
  // в рендере панели не работают, но импорт существует — мок отдаёт и его.
  tutorAvatarUrl: () => '',
}));

// ── signalr: перехват handlers — realtime-путь проверяется настоящим событием ──
vi.mock('../../lib/signalr', () => ({
  onMessage: (fn: (m: { type: string; sessionId?: string }) => void) => { src.msgHandlers.add(fn); return () => { src.msgHandlers.delete(fn); }; },
  onReconnected: () => () => {},
}));

vi.mock('../../lib/toast', () => ({
  showToast: (_t: string, msg: string) => { src.toasts.push(msg); },
}));

import { ChildLessonsPanel } from './ChildLessonsPanel';

// Тип узла — тот, что отдаёт хелпер (FNode не экспортирован — берём из сигнатуры)
type Node = NonNullable<ReturnType<typeof findNodeByAttr>>;

const iso = (msAgo: number): string => new Date(Date.now() - msAgo).toISOString();

const session = (over: Partial<Session> = {}): Session => ({
  id: 'l1', name: 'Дроби', projectId: 'p1', mode: 'default', status: 'active',
  messageCount: 2, createdAt: iso(86_400_000), updatedAt: iso(3_600_000), ...over,
} as Session);

const closedItem = (over: Partial<ChildChatListItem> = {}): ChildChatListItem => ({
  sessionId: 'c1', name: 'Десятичные дроби', updatedAt: iso(3 * 86_400_000),
  subject: 'math-old', subjectClosed: true, subjectLabel: 'Старая математика', voiceMode: false, ...over,
});

const walk = (n: Node, cb: (n: Node) => void): void => { cb(n); for (const c of n.childNodes) walk(c, cb); };
const rowsWithText = (container: Node, testid: string, text: string): Node[] => {
  const out: Node[] = [];
  walk(container, (n) => {
    if (n.nodeType === 1 && n.getAttribute('data-testid') === testid && textNodes(n).join('').includes(text)) out.push(n);
  });
  return out;
};
// Клик — через props узла (тот же путь, что событие кнопки; React держит их под
// __reactProps$ — ChildRulesOnboarding.test.tsx:381)
const click = async (n: Node) => { await act(async () => { (reactProps(n) as { onClick?: () => void })?.onClick?.(); }); };

async function mountPanel(props: Parameters<typeof ChildLessonsPanel>[0]) {
  const { root, container } = mount(createElement(ChildLessonsPanel, props));
  // макротаск: цепочки промисов обоих источников (list → cache → setState) разрешены
  await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
  return { root, container: container as Node };
}

describe('ChildLessonsPanel: детская панель уроков (Х-W4-78)', () => {
  beforeEach(() => {
    installFakeDom();
    (globalThis as any).window.matchMedia = () => ({ matches: false, addEventListener: () => {}, removeEventListener: () => {} });
    store.clear();
    // isArchived — готовый bool с бэка, на фронте не пересчитывается (types/index.ts:393-401);
    // архивный урок в детском списке быть не должен
    src.sessions = [session(), session({ id: 'lArch', name: 'Архивный урок', isArchived: true })];
    src.spaceChats = [
      closedItem(),
      closedItem({ sessionId: 'c2', name: 'Живой из другой ручки', subject: 'phys', subjectClosed: false, subjectLabel: 'Физика' }),
    ];
    src.sessionCalls.length = 0; src.spaceCalls.length = 0; src.createLesson.length = 0; src.toasts.length = 0;
    src.msgHandlers.clear();
    src.createResult = Promise.resolve({ sessionId: 'new1' });
  });
  afterEach(() => { uninstallFakeDom(); vi.restoreAllMocks(); });

  it('текущие уроки — из штатной ручки; «Старые» — только subjectClosed из spaceChats', async () => {
    const { root, container } = await mountPanel({ projectId: 'p1', subjectId: 'math', onSelectSession: () => {}, onLessonCreated: () => {} });
    try {
      // оба источника спросаны ровно по одному разу, каждый — своей ручкой
      expect(src.sessionCalls, 'штатная ручка списка не спросана').toEqual(['p1']);
      expect(src.spaceCalls, 'детская ручка не спросана').toEqual(['lessons']);
      // текущий урок проекта — в списке
      expect(rowsWithText(container, 'child-lesson-row', 'Дроби').length, 'строки текущего урока нет').toBe(1);
      // архивный — нет: у ребёнка архива нет (И11)
      expect(rowsWithText(container, 'child-lesson-row', 'Архивный урок').length, 'архивного урока быть не должно').toBe(0);
      // не-закрытый элемент spaceChats — не в текущем списке: один источник на один список
      expect(rowsWithText(container, 'child-lesson-row', 'Живой из другой ручки').length, 'чужого элемента spaceChats в текущем списке быть не должно').toBe(0);
      // «Старые уроки» — свёрнуты по умолчанию (Д2): строки закрытого пока нет
      expect(rowsWithText(container, 'child-lesson-closed-row', 'Десятичные дроби').length, 'старый урок показан без раскрытия').toBe(0);
      const toggle = findNodeByAttr(container, 'data-testid', 'child-closed-toggle');
      expect(toggle, 'счётчика «Старые уроки» нет').not.toBeNull();
      expect(textNodes(toggle!).join(''), 'счётчик закрытых').toContain('Старые уроки · 1');
      await click(toggle!);
      const closedRow = rowsWithText(container, 'child-lesson-closed-row', 'Десятичные дроби')[0];
      expect(closedRow, 'после раскрытия строки закрытого урока нет').toBeTruthy();
      // подписан subjectLabel (ХВ2-Б1: предмета уже нет в GET /api/child/subjects)
      expect(textNodes(closedRow).join(''), 'подписи предмета у закрытой строки нет').toContain('Старая математика');
      // закрытый урок — не кнопка: его проект ребёнку не виден (ProjectsController.cs:192)
      expect(closedRow.getAttribute('role'), 'строка закрытого урока не должна быть кнопкой').not.toBe('button');
    } finally { root.unmount(); }
  });

  it('«Уроки видит: …» — в панели (решение 86); пустых кураторов нет вовсе', async () => {
    const withCurators = await mountPanel({ projectId: 'p1', subjectId: 'math', viewers: ['Папа'], onSelectSession: () => {}, onLessonCreated: () => {} });
    try {
      const line = findNodeByAttr(withCurators.container, 'data-testid', 'child-curators-line');
      expect(line, 'строки кураторов в панели нет').not.toBeNull();
      expect(textNodes(line!).join('').trim(), 'текст строки кураторов').toBe('Уроки видит Папа');
    } finally { withCurators.root.unmount(); }
    const noCurators = await mountPanel({ projectId: 'p1', subjectId: 'math', viewers: [], onSelectSession: () => {}, onLessonCreated: () => {} });
    try {
      expect(findNodeByAttr(noCurators.container, 'data-testid', 'child-curators-line'), 'пустой строки кураторов быть не должно').toBeNull();
    } finally { noCurators.root.unmount(); }
  });

  it('«Новый урок»: двойной тап — один вызов детской точки; отказ — серверный текст как есть', async () => {
    const created: string[] = [];
    const { root, container } = await mountPanel({ projectId: 'p1', subjectId: 'math', onSelectSession: () => {}, onLessonCreated: (id) => { created.push(id); } });
    try {
      const btn = findNodeByAttr(container, 'data-testid', 'child-new-lesson');
      expect(btn, 'кнопки «Новый урок» нет').not.toBeNull();
      // первый вызов держим в полёте: без этого второй тап пришёл бы уже после
      // снятия creating и создал бы второй урок (тот же урок, что в заметке про
      // state-guard кнопки: два act() с уже решённым промисом не проверяют блок)
      let resolveCreate: (v: { sessionId: string }) => void = () => {};
      src.createResult = new Promise((r) => { resolveCreate = r; });
      await click(btn!);
      await click(btn!); // второй тап, пока первый в полёте: creating держит блок
      resolveCreate({ sessionId: 'new1' });
      await act(async () => { await new Promise((r) => setTimeout(r, 0)); });
      expect(src.createLesson, 'createLesson позван не один раз').toEqual(['math']);
      expect(created, 'id созданного урока не передан владельцу').toEqual(['new1']);
    } finally { root.unmount(); }

    // отказ по состоянию: текст сервера (ADR-041, «Общие правила») — в тосте, без технического
    const refused = await mountPanel({ projectId: 'p1', subjectId: 'math', onSelectSession: () => {}, onLessonCreated: () => {} });
    try {
      src.createResult = Promise.reject({ body: { message: 'На сегодня уроки закончились. Отдохни и возвращайся завтра.' } });
      const btn = findNodeByAttr(refused.container, 'data-testid', 'child-new-lesson');
      await click(btn!);
      expect(src.toasts, 'текста отказа в тосте нет').toContain('На сегодня уроки закончились. Отдохни и возвращайся завтра.');
    } finally { refused.root.unmount(); }
  });

  it('без предмета «Нового урока» нет; действий взрослого списка в панели нет вовсе', async () => {
    const { root, container } = await mountPanel({ projectId: 'p1', subjectId: null, onSelectSession: () => {}, onLessonCreated: () => {} });
    try {
      const btn = findNodeByAttr(container, 'data-testid', 'child-new-lesson');
      expect(btn, 'кнопки нет вовсе').not.toBeNull();
      // выключенное состояние читаем по props кнопки: React ставит disabled свойством,
      // а не атрибутом — getAttribute здесь молчал бы всегда
      expect((reactProps(btn!) as { disabled?: boolean }).disabled, 'без предмета кнопка должна быть выключена').toBe(true);
      expect(src.createLesson.length, 'createLesson без предмета зваться не должен').toBe(0);
      // ни тулбара фильтров, ни карточных действий штатного списка (И11): в DOM нет
      // ни одной кнопки с signature-подписями SessionList
      let adultBtns = 0;
      walk(container, (n) => { if (n.nodeType === 1 && n.nodeName === 'BUTTON' && (n.getAttribute('title') ?? '').match(/Фильтр|Архив|Показать ещё|Новый чат/)) adultBtns += 1; });
      expect(adultBtns, 'взрослых контролов списка в детской панели быть не должно').toBe(0);
    } finally { root.unmount(); }
  });

  it('клик строки открывает урок (onSelectSession)', async () => {
    const opened: Session[] = [];
    const { root, container } = await mountPanel({ projectId: 'p1', subjectId: 'math', onSelectSession: (s) => { opened.push(s); }, onLessonCreated: () => {} });
    try {
      const row = rowsWithText(container, 'child-lesson-row', 'Дроби')[0];
      expect(row, 'строки урока нет').toBeTruthy();
      expect(row.getAttribute('role'), 'строка текущего урока должна быть кнопкой').toBe('button');
      await click(row);
      expect(opened.map(s => s.id), 'клик строки не открыл урок').toEqual(['l1']);
    } finally { root.unmount(); }
  });

  it('activeSession: подсветка строки и немедленная добавка созданного урока; chat_deleted убирает строку', async () => {
    const active = session();
    const { root, container } = await mountPanel({ projectId: 'p1', subjectId: 'math', activeSession: active, onSelectSession: () => {}, onLessonCreated: () => {} });
    try {
      const row = rowsWithText(container, 'child-lesson-row', 'Дроби')[0];
      expect(row, 'строки активного урока нет').toBeTruthy();
      expect(row.getAttribute('aria-current'), 'активный урок не помечен').toBe('true');
      // realtime: удаление — сразу, без перечитывания
      await act(async () => { for (const fn of [...src.msgHandlers]) fn({ type: 'chat_deleted', sessionId: 'l1' }); });
      expect(rowsWithText(container, 'child-lesson-row', 'Дроби').length, 'удалённый урок остался в списке').toBe(0);
    } finally { root.unmount(); }

    // созданный урок: его id приходит в activeSession после того, как список уже прочитан
    // (pending-канал открывает его, список этого проекта его ещё не знает) — строка
    // добавляется сразу, без перечитывания
    src.sessions = [session({ id: 'lOld', name: 'Прежний урок' })];
    const fresh = await mountPanel({ projectId: 'p1', subjectId: 'math', onSelectSession: () => {}, onLessonCreated: () => {} });
    try {
      await act(async () => {
        fresh.root.render(createElement(ChildLessonsPanel, {
          projectId: 'p1', subjectId: 'math', activeSession: session({ id: 'lNew', name: 'Новый урок', updatedAt: iso(1000) }),
          onSelectSession: () => {}, onLessonCreated: () => {},
        }));
        await new Promise((r) => setTimeout(r, 0));
      });
      expect(rowsWithText(fresh.container, 'child-lesson-row', 'Новый урок').length, 'созданный урок не добавлен сразу').toBe(1);
      expect(rowsWithText(fresh.container, 'child-lesson-row', 'Прежний урок').length, 'прежний урок пропал из списка').toBe(1);
    } finally { fresh.root.unmount(); }
  });
});
