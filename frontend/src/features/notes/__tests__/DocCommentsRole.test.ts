// DocComments: точки записи комментариев по роли (S2c-F4g). Точка права — contentRights.ts (S2c-F4a);
// здесь — связка can(дверь): проект области берётся из кэша useAllProjects по id == scope,
// у 'personal' проекта нет. Чтение комментариев читателю открыто (F-8): список комментариев
// и их ответы остаются виден, закрытое ПРЯЧЕТ (0 узлов):
//   • читатель — нет «Решён», «Удалить», «Ответить» (без ответов), «Комментировать»,
//     «Перепривязать», подсказки первого использования; карточка комментария и «Открыть
//     заметку» — есть; «Ответы · N» с самими ответами — есть (это чтение);
//   • редактор — все точки «note.comment» есть (и поле «Ответить…» с «Отправить»);
//   • владелец, проект без myRole, не в кэше, 'personal' — как раньше.
// Мутация: снять canComment в гейте «Ответить» — тест читателя краснеет; откат — зелёный.
// Окружение — по образцу NoteViewRole.test.ts: node + fakeDom + spyOn на api.
import { act, createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DocCommentedMarkdown } from '../DocComments';
import { api } from '../../../lib/api';
import { invalidateProjectsCache } from '../../projects/useAllProjects';
import type { DocAnnotation, NoteReply, Project, ProjectRoleView } from '../../../types';
import { installFakeDom, mount, findNodeByAttr, reactProps, textNodes, uninstallFakeDom } from '../../../components/chat/__tests__/fakeDom';

vi.mock('../../../lib/signalr', () => ({
  getConnection: () => ({ start: async () => {}, on: () => {}, stop: async () => {} }),
  onMessage: () => () => {},
  onPreviewMessage: () => () => {},
  onFilesChanged: () => () => {},
  onGitStatusChanged: () => () => {},
  onLocalModelMessage: () => () => {},
  onReconnected: () => () => {},
  joinUser: async () => {},
}));

const { store } = vi.hoisted(() => ({
  store: { kv: new Map<string, any>(), notes: new Map<string, any>(), outbox: new Map<number, any>() },
}));
vi.mock('../../../lib/idb', () => ({
  idbGet: async (k: string) => store.kv.get(k),
  idbSet: async (k: string, v: unknown) => { store.kv.set(k, v); return 1; },
  idbDelete: async (k: string) => { store.kv.delete(k); return undefined; },
  noteContentGet: async (lk: string) => store.notes.get(lk),
  noteContentPut: async (r: { localKey: string }) => { store.notes.set(r.localKey, r); return 1; },
  noteContentDelete: async (lk: string) => { store.notes.delete(lk); return undefined; },
  noteContentAll: async () => [...store.notes.values()],
  notesOutboxAll: async () => [...store.outbox.values()],
  notesOutboxPut: async (op: { opId: number }) => { store.outbox.set(op.opId, op); return op.opId; },
  notesOutboxDelete: async (id: number) => { store.outbox.delete(id); return undefined; },
  idbCountRange: async () => 0, idbRangeStamps: async () => [], idbKeys: async () => [], idbClear: async () => {},
  outboxPut: async () => 1, outboxGet: async () => undefined, outboxDelete: async () => undefined, outboxAll: async () => [],
}));

const DOC = 'Текст абзаца.';

const ANN = (over: Partial<DocAnnotation> = {}): DocAnnotation => ({
  noteId: 'a1', title: 'Комментарий', status: 'open', state: 'exact',
  start: 0, end: 10, quote: 'текст абзаца', excerpt: '', tags: [],
  updatedAt: '2026-11-01T00:00:00Z', replies: 0, ...over,
});

const REPLY: NoteReply = {
  noteId: 'r1', title: 'Ответ', excerpt: 'ответ первый',
  createdAt: '2026-11-01T00:00:00Z', tags: [],
};

function projectWith(role?: ProjectRoleView): Project {
  return {
    id: 'p1', name: 'Проект', rootPath: '/home/an/proj',
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-11-02T00:00:00Z',
    myRole: role,
  };
}

let root: { unmount: () => void } | null = null;
let container: any = null;

function setupMocks() {
  vi.useFakeTimers();
  const ls: Record<string, string> = {};
  (globalThis as any).localStorage = {
    getItem: (k: string) => (k in ls ? ls[k] : null),
    setItem: (k: string, v: string) => { ls[k] = String(v); },
    removeItem: (k: string) => { delete ls[k]; },
    clear: () => { for (const k of Object.keys(ls)) delete ls[k]; },
    key: () => null, get length() { return Object.keys(ls).length; },
  };
  (globalThis as any).sessionStorage = (globalThis as any).localStorage;
  // onMouseUp читает Node.TEXT_NODE — в node-окружении глобала нет, даём его:
  // классом (callable) — vitest's toContain проверяет «x instanceof Node» и на объекте-литерале падает
  (globalThis as any).Node = class Node { static TEXT_NODE = 3; };
  store.kv.clear(); store.notes.clear(); store.outbox.clear();
  vi.spyOn(api.notes, 'annotations').mockResolvedValue([] as never);
  vi.spyOn(api.notes, 'replies').mockResolvedValue([] as never);
  vi.spyOn(api.personas, 'list').mockResolvedValue([] as never);
}

function teardown() {
  act(() => { root?.unmount(); });
  root = null; container = null;
  vi.useRealTimers();
  uninstallFakeDom();
  vi.restoreAllMocks();
}

async function flush() {
  await act(async () => { await vi.advanceTimersByTimeAsync(100); });
}

// Смонтировать документ области scope с комментариями anns; projects — состав кэша проектов (роль — в нём)
async function mountDoc(scope: string, projects: Project[], anns: DocAnnotation[]) {
  installFakeDom();
  vi.spyOn(api.notes, 'annotations').mockResolvedValue(anns as never);
  vi.spyOn(api.projects, 'list').mockResolvedValue(projects as never);
  invalidateProjectsCache();
  const r = mount(createElement(DocCommentedMarkdown, { scope, docPath: 'doc.md', content: DOC }));
  root = r.root; container = r.container;
  await flush();
}

// сравнение с trim: JSX-текст после иконки на той же строке несёт ведущий пробел (« Решён»)
const hasText = (t: string) => textNodes(container).map(n => n.trim()).includes(t);
const hasSub = (t: string) => textNodes(container).join('').includes(t);
const bodyText = () => textNodes((globalThis as any).document.body).join('\n');
const delBtn = () => findNodeByAttr(container, 'title', 'Удалить комментарий');

// Клик по кнопке с текстом t (кнопки треда не несут aria-label — ищем по текстовому узлу)
async function clickText(t: string) {
  const walk = (x: any): any | null => {
    if (x.nodeType === 3 && x.nodeValue === t) return x.parentNode;
    for (const c of x.childNodes) { const h = walk(c); if (h) return h; }
    return null;
  };
  const btn = walk(container);
  expect(btn, `кнопка «${t}»`).not.toBeNull();
  await act(async () => { (reactProps(btn) as any).onClick(); await flush(); });
}

// Выделение текста в документе (onMouseUp компонента) — вход к «Комментировать»/repin
async function simulateSelection() {
  const win = (globalThis as any).window;
  win.getSelection = () => ({
    isCollapsed: false,
    toString: () => 'текст абзаца',
    getRangeAt: () => ({
      // commonAncestorContainer = сам корень документа (contains(root) истинно);
      // startContainer — элементный: ветка TEXT_NODE не берётся, closest у фейка нет
      commonAncestorContainer: container.__docRoot,
      startContainer: { nodeType: 1 },
      getBoundingClientRect: () => ({ left: 100, top: 50, width: 10, height: 10 }),
    }),
  });
  const walk = (x: any): any | null => {
    const p = reactProps(x);
    if (p && typeof p.onMouseUp === 'function') return x;
    for (const c of x.childNodes) { const h = walk(c); if (h) return h; }
    return null;
  };
  const target = walk(container);
  expect(target, 'узел документа с onMouseUp').not.toBeNull();
  container.__docRoot = target;
  await act(async () => {
    (reactProps(target) as any).onMouseUp();
    await vi.advanceTimersByTimeAsync(20);
  });
}

describe('DocComments: точки записи комментариев по роли (S2c-F4g)', () => {
  beforeEach(setupMocks);
  afterEach(teardown);

  it('читатель: «Решён», «Удалить», «Ответить», «Комментировать», «Перепривязать» — 0 узлов; комментарии видны', async () => {
    await mountDoc('p1', [projectWith('reader')], [ANN({ state: 'changed' })]);
    // карточка комментария и её чтение — на месте
    expect(hasText('Комментарий'), 'карточка комментария видна').toBe(true);
    expect(hasText('место изменилось'), 'чип состояния виден').toBe(true);
    expect(hasText('текст абзаца'), 'цитата видна').toBe(true);
    expect(hasText('Открыть заметку'), 'чтение заметки-комментария остаётся').toBe(true);
    // точки записи — прячутся целиком
    expect(hasText('Решён')).toBe(false);
    expect(hasText('Снова открыть')).toBe(false);
    expect(delBtn(), 'кнопка удаления').toBeNull();
    expect(hasText('Ответить')).toBe(false);
    expect(hasText('Перепривязать')).toBe(false);
    // выделение есть — плавающей «Комментировать» нет (для редактора — есть, см. ниже)
    await simulateSelection();
    expect(bodyText()).not.toContain('Комментировать');
  });

  it('читатель: «Ответы · 2» виден, сами ответы читаются, поля записи нет', async () => {
    await mountDoc('p1', [projectWith('reader')], [ANN({ replies: 2 })]);
    expect(hasText('Ответы · 2'), 'тред читателю доступен').toBe(true);
    expect(hasText('Ответить')).toBe(false);
    vi.spyOn(api.notes, 'replies').mockResolvedValue([REPLY] as never);
    await clickText('Ответы · 2');
    expect(hasText('ответ первый'), 'ответ прочитан').toBe(true);
    expect(hasText('Отправить'), 'поля ответа нет').toBe(false);
  });

  it('подсказка первого использования: читателю не обещает запись, редактору — обещает', async () => {
    await mountDoc('p1', [projectWith('reader')], []);
    expect(hasSub('Выделите текст, чтобы оставить комментарий'), 'читателю подсказки нет').toBe(false);
    teardown(); setupMocks();
    await mountDoc('p1', [projectWith('editor')], []);
    expect(hasSub('Выделите текст, чтобы оставить комментарий'), 'редактору подсказка на месте').toBe(true);
  });

  it('редактор: все точки «note.comment» есть, включая поле «Ответить…» и плавающий вход', async () => {
    await mountDoc('p1', [projectWith('editor')], [ANN({ state: 'changed' })]);
    expect(hasText('Решён')).toBe(true);
    expect(delBtn(), 'кнопка удаления').not.toBeNull();
    expect(hasText('Перепривязать')).toBe(true);
    expect(hasText('Ответить'), 'вход в тред').toBe(true);
    await clickText('Ответить');
    expect(hasText('Отправить'), 'поле ответа').toBe(true);
    await simulateSelection();
    expect(bodyText(), 'плавающий вход к комментированию').toContain('Комментировать');
  });

  it('владелец: всё есть', async () => {
    await mountDoc('p1', [projectWith('owner')], [ANN({ state: 'changed' })]);
    expect(hasText('Решён')).toBe(true);
    expect(delBtn()).not.toBeNull();
    expect(hasText('Ответить')).toBe(true);
    expect(hasText('Перепривязать')).toBe(true);
    await simulateSelection();
    expect(bodyText()).toContain('Комментировать');
  });

  it('проект без myRole и проект не в кэше: как раньше', async () => {
    await mountDoc('p1', [projectWith(undefined)], [ANN({ state: 'changed' })]);
    expect(hasText('Решён')).toBe(true);
    expect(delBtn()).not.toBeNull();
    expect(hasText('Ответить')).toBe(true);
    teardown(); setupMocks();
    await mountDoc('p1', [], [ANN({ state: 'changed' })]);
    expect(hasText('Решён')).toBe(true);
    expect(hasText('Перепривязать')).toBe(true);
  });

  it("'personal': проекта нет — всё разрешено, даже при читателе в кэше", async () => {
    await mountDoc('personal', [projectWith('reader')], [ANN({ state: 'changed' })]);
    expect(hasText('Решён')).toBe(true);
    expect(delBtn()).not.toBeNull();
    expect(hasText('Ответить')).toBe(true);
    expect(hasText('Перепривязать')).toBe(true);
    await simulateSelection();
    expect(bodyText()).toContain('Комментировать');
  });
});
