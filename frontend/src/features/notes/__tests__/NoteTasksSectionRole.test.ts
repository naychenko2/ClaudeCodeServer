// NoteTasksSection: задачи заметки по роли (S2c-F4f). Точка права — contentRights.ts (S2c-F4a),
// дверь 'note.tasks': у читателя и РЕДАКТОРА все три действия (чекбокс-отметка, «В задачи»,
// срок с дейт-пикера) ПРЯЧУТСЯ (0 узлов; на бэке promote/toggle/set-due закрыты на Write),
// список остаётся только для чтения: текст задачи и read-only статус «в задачах» видны.
// Владелец, проект без myRole, 'personal' (проекта нет по построению) — все три есть.
// Проект-источник даёт NoteView (useAllProjects по id == note.source, 'personal' → undefined)
// — тест монтирует NoteView целиком, чтобы проверка включала и эту связку.
// Мутация: добавить 'note.tasks' в EDITOR_DOORS (contentRights.ts) — тест редактора краснеет.
// Окружение — по образцу NoteViewRole.test.ts: node + fakeDom + spyOn на api.
import { act, createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { NoteView } from '../NoteView';
import { api } from '../../../lib/api';
import { setOnline } from '../../../lib/offline';
import { invalidateProjectsCache } from '../../projects/useAllProjects';
import type { NoteDetail, NoteTask, Project, ProjectRoleView } from '../../../types';
import { installFakeDom, mount, findNodeByAttr, textNodes, uninstallFakeDom } from '../../../components/chat/__tests__/fakeDom';

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

vi.mock('../NoteEditor', () => ({
  NoteEditor: () => createElement('div', { 'data-testid': 'note-editor' }),
}));
vi.mock('../../../lib/toast', () => ({ showToast: () => {} }));

const NOTE = (source: string): NoteDetail => ({
  id: 'n1', title: 'Заметка', source, sourceLabel: source === 'personal' ? 'Личный' : 'Проект', path: 'Заметка.md',
  content: 'text', tags: [], links: [], backlinks: [], unlinkedMentions: [],
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-11-01T08:00:00Z',
});

// Две строки-чекбокса: обычная (без срока и задачи) и уже промоутнутая со сроком —
// вторая держит read-only статус «в задачах» и чип даты (их видимость — тоже предмет теста)
const TASKS: NoteTask[] = [
  { line: 3, text: 'Купить молоко', done: false, due: null, taskId: null },
  { line: 7, text: 'Позвонить в сервис', done: true, due: '2026-11-05', taskId: 't1', taskStatus: 'open' },
];

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
  store.kv.clear(); store.notes.clear(); store.outbox.clear();
  setOnline(true);
  vi.spyOn(api.notes, 'list').mockResolvedValue([] as never);
  vi.spyOn(api.notes, 'folders').mockResolvedValue([] as never);
  vi.spyOn(api.notes, 'tasks').mockResolvedValue(TASKS as never);
  vi.spyOn(api.notes, 'annotations').mockResolvedValue([] as never);
  vi.spyOn(api.notes, 'backlinks').mockResolvedValue([] as never);
  vi.spyOn(api.personas, 'list').mockResolvedValue([] as never);
  vi.spyOn(api.notes, 'sources').mockResolvedValue([] as never);
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

// Смонтировать заметку источника source; projects — состав кэша проектов (роль — в нём)
async function mountView(source: string, projects: Project[]) {
  installFakeDom();
  vi.spyOn(api.notes, 'get').mockResolvedValue(NOTE(source) as never);
  vi.spyOn(api.projects, 'list').mockResolvedValue(projects as never);
  invalidateProjectsCache();
  const r = mount(createElement(NoteView, {
    noteId: 'n1', existingTitles: new Set<string>(), onWikilink: () => {},
    onSelectNote: vi.fn(), onDeleted: vi.fn(),
  }));
  root = r.root; container = r.container;
  await flush();
}

const hasText = (t: string) => textNodes(container).some(n => n === t);
const icon = (label: string) => findNodeByAttr(container, 'aria-label', label);

// Три контрола записи секции: чекбокс (aria-label), «В задачи» (текст кнопки),
// дейт-пикер (текст чипа «📅 срок» у задачи без срока)
const writeControls = () => ({
  checkbox: icon('Отметить выполненной'),
  promote: hasText('В задачи'),
  due: hasText('📅 срок'),
});

describe('NoteTasksSection: задачи заметки по роли (S2c-F4f)', () => {
  beforeEach(setupMocks);
  afterEach(teardown);

  it('читатель: чекбокс, «В задачи» и «Срок» — 0 узлов; текст и статус «в задачах» видны', async () => {
    await mountView('p1', [projectWith('reader')]);
    const w = writeControls();
    expect(w.checkbox, 'чекбокса нет').toBeNull();
    expect(w.promote, 'кнопки «В задачи» нет').toBe(false);
    expect(w.due, 'чипа «📅 срок» нет').toBe(false);
    expect(icon('Снять отметку'), 'чекбокса выполненной задачи тоже нет').toBeNull();
    expect(icon('Убрать срок'), 'кнопки «Убрать срок» нет').toBeNull();
    expect(hasText('📅 2026-11-05'), 'чипа даты существующего срока тоже нет').toBe(false);
    // список только для чтения: текст обеих задач и read-only статус промоута — на месте
    expect(hasText('Купить молоко'), 'текст задачи виден').toBe(true);
    expect(hasText('Позвонить в сервис'), 'текст второй задачи виден').toBe(true);
    expect(hasText('в задачах'), 'read-only статус «в задачах» остаётся').toBe(true);
  });

  it('редактор: те же три контрола — 0 узлов (дверь note.tasks редактору закрыта)', async () => {
    await mountView('p1', [projectWith('editor')]);
    const w = writeControls();
    expect(w.checkbox, 'чекбокса нет').toBeNull();
    expect(w.promote, 'кнопки «В задачи» нет').toBe(false);
    expect(w.due, 'чипа «📅 срок» нет').toBe(false);
    expect(hasText('Купить молоко'), 'текст задачи виден').toBe(true);
  });

  it('владелец: чекбокс, «В задачи» и «Срок» — все есть', async () => {
    await mountView('p1', [projectWith('owner')]);
    const w = writeControls();
    expect(w.checkbox, 'чекбокс есть').not.toBeNull();
    expect(w.promote, 'кнопка «В задачи» есть').toBe(true);
    expect(w.due, 'чип «📅 срок» есть').toBe(true);
    expect(icon('Снять отметку'), 'чекбокс выполненной задачи есть').not.toBeNull();
    expect(icon('Убрать срок'), 'кнопка «Убрать срок» есть').not.toBeNull();
    expect(hasText('📅 2026-11-05'), 'чип существующего срока есть').toBe(true);
  });

  it('проект без myRole и проект не в кэше: как раньше', async () => {
    await mountView('p1', [projectWith(undefined)]);
    const w = writeControls();
    expect(w.checkbox, 'чекбокс есть').not.toBeNull();
    expect(w.promote, 'кнопка «В задачи» есть').toBe(true);
    expect(w.due, 'чип «📅 срок» есть').toBe(true);
    teardown(); setupMocks();
    await mountView('p1', []);
    expect(writeControls().checkbox, 'без кэша — чекбокс есть').not.toBeNull();
  });

  it("'personal': проекта нет — все три есть, даже при читателе в кэше", async () => {
    await mountView('personal', [projectWith('reader')]);
    const w = writeControls();
    expect(w.checkbox, 'чекбокс есть').not.toBeNull();
    expect(w.promote, 'кнопка «В задачи» есть').toBe(true);
    expect(w.due, 'чип «📅 срок» есть').toBe(true);
  });
});
