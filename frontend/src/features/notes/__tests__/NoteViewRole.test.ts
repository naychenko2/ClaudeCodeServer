// NoteView: контролы записи по роли (S2c-F4e). Точка права — contentRights.ts (S2c-F4a);
// здесь — связка can(дверь): проект источника берётся из кэша useAllProjects по
// id == note.source, у 'personal' проекта нет. Закрытая дверь ПРЯЧЕТ (0 узлов), не отключает:
//   • читатель — нет «Править», «Удалить», «Переместить…», «В избранное»;
//   • редактор — «Править», «Удалить», «Переместить…» есть, но в выборе переноса нет других
//     источников (дверь note.moveSource закрыта), а у владельца они есть;
//   • владелец, проект без myRole, 'personal' — как раньше.
// Мутация: в openMove снять проверку can('note.moveSource') — тест редактора краснеет.
// Окружение — по образцу NoteViewSaveConflict.test.ts: node + fakeDom + spyOn на api.
import { act, createElement } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { NoteView } from '../NoteView';
import { api } from '../../../lib/api';
import { setOnline } from '../../../lib/offline';
import { invalidateProjectsCache } from '../../projects/useAllProjects';
import type { NoteDetail, Project, ProjectRoleView } from '../../../types';
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

vi.mock('../NoteEditor', () => ({
  NoteEditor: () => createElement('div', { 'data-testid': 'note-editor' }),
}));
vi.mock('../../../lib/toast', () => ({ showToast: () => {} }));

const NOTE = (source: string): NoteDetail => ({
  id: 'n1', title: 'Заметка', source, sourceLabel: source === 'personal' ? 'Личный' : 'Проект', path: 'Заметка.md',
  content: 'text', tags: [], links: [], backlinks: [], unlinkedMentions: [],
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-11-01T08:00:00Z',
});

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
  vi.spyOn(api.notes, 'tasks').mockResolvedValue([] as never);
  vi.spyOn(api.notes, 'annotations').mockResolvedValue([] as never);
  vi.spyOn(api.notes, 'backlinks').mockResolvedValue([] as never);
  vi.spyOn(api.personas, 'list').mockResolvedValue([] as never);
  // Источники переноса: свой и чужой — выбор рисуется, только если их больше одного
  vi.spyOn(api.notes, 'sources').mockResolvedValue([
    { key: 'p1', label: 'Свой источник' },
    { key: 'other', label: 'Чужой источник' },
  ] as never);
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
  // fakeDom не знает <select>: React при инициализации читает node.options.length. Выбор
  // источника в диалоге переноса — предмет теста, поэтому даём select пустой options
  // (тексты <option> остаются обычными дочерними узлами и читаются из body).
  const doc = (globalThis as any).document;
  const origCreate = doc.createElement.bind(doc);
  doc.createElement = (tag: string) => {
    const el = origCreate(tag);
    // Свежий fakeDom (master) сам отдаёт options у select геттером — подменяем, только если его нет
    if (tag === 'select' && !('options' in el)) el.options = [];
    return el;
  };
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
const bodyText = () => textNodes((globalThis as any).document.body).join('\n');
// IconButton несёт подпись в aria-label (как в NotesListRole.test.ts)
const icon = (label: string) => findNodeByAttr(container, 'aria-label', label);

async function openMoveDialog() {
  const btn = icon('Переместить…');
  expect(btn, 'кнопка «Переместить…» в тулбаре').not.toBeNull();
  await act(async () => { (reactProps(btn as never) as any).onClick(); await flush(); });
}

describe('NoteView: контролы записи по роли (S2c-F4e)', () => {
  beforeEach(setupMocks);
  afterEach(teardown);

  it('читатель: «Править», «Удалить», «Переместить…», «В избранное» — 0 узлов', async () => {
    await mountView('p1', [projectWith('reader')]);
    expect(hasText('Править')).toBe(false);
    expect(icon('Удалить')).toBeNull();
    expect(icon('Переместить…')).toBeNull();
    expect(icon('В избранное')).toBeNull();
    // чтение остаётся
    expect(icon('Скопировать Markdown (Shift — с форматированием)')).not.toBeNull();
  });

  it('редактор: «Править» и «Удалить» есть; в выборе переноса нет других источников', async () => {
    await mountView('p1', [projectWith('editor')]);
    expect(hasText('Править')).toBe(true);
    expect(icon('Удалить')).not.toBeNull();
    await openMoveDialog();
    expect(bodyText(), 'диалог переноса открыт').toContain('Переместить заметку');
    expect(bodyText(), 'чужой источник редактору не предлагается').not.toContain('Чужой источник');
  });

  it('владелец: всё есть, в выборе переноса другие источники доступны', async () => {
    await mountView('p1', [projectWith('owner')]);
    expect(hasText('Править')).toBe(true);
    expect(icon('Удалить')).not.toBeNull();
    await openMoveDialog();
    expect(bodyText()).toContain('Чужой источник');
  });

  it('проект без myRole и проект не в кэше: как раньше', async () => {
    await mountView('p1', [projectWith(undefined)]);
    expect(hasText('Править')).toBe(true);
    expect(icon('Удалить')).not.toBeNull();
    teardown(); setupMocks();
    await mountView('p1', []);
    expect(hasText('Править')).toBe(true);
    expect(icon('Переместить…')).not.toBeNull();
  });

  it("'personal': проекта нет — всё разрешено, даже при читателе в кэше", async () => {
    await mountView('personal', [projectWith('reader')]);
    expect(hasText('Править')).toBe(true);
    expect(icon('Удалить')).not.toBeNull();
    await openMoveDialog();
    expect(bodyText()).toContain('Чужой источник');
  });
});
