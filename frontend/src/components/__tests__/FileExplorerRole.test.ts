// FileExplorer: контролы записи по роли (S2c-F4b). Точка права — contentRights.ts (S2c-F4a);
// здесь — только связка can(маршрут) && canWriteContent(дверь). Закрытая дверь ПРЯЧЕТ
// (0 узлов в DOM), не отключена:
//   • читатель — ни «Новый», ни «Переименовать»/«Удалить» (ни у файла, ни у папки);
//   • редактор — «Новый» с «Файл»/«Папка»/«Загрузить файлы», «Удалить» у файла есть,
//     у папки — нет (дверь 'dir.delete' закрыта на бэке — закрыта и здесь);
//   • владелец — «Удалить» есть и у папки;
//   • контроль без myRole (обычный проект/старый бэк) — как владелец.
// Мутация «снять && canWriteContent у canDelete» красит читателя и «папку редактора»;
// «canDeleteEntry = canDelete без dir-ветки» красит владелецa-папку и «папку редактора».
//
// Окружение node (без jsdom), по образцу components/FileViewer.test.ts: fakeDom mount +
// spyOn(api.files,'list') + мок useOnline + мок signalr (ensureGit/ensureNotesLoaded строят
// через них соединения) + мок api.git.status/api.notes.* (их load-вызовы на маунте) +
// заглушка DOMRect (меню строит якорь через new DOMRect). Список — 1 файл + 1 папка.
// Меню (ui/Menu с anchor) — портал в document.body: его содержимое искать надо там,
// дерево — в container.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FileExplorer } from '../FileExplorer';
import { api } from '../../lib/api';
import type { Project, FileEntry, ProjectRoleView } from '../../types';
import { installFakeDom, uninstallFakeDom, mount, textNodes, reactProps } from '../chat/__tests__/fakeDom';

vi.mock('../../lib/toast', () => ({ showToast: () => {} }));
// В node navigator.onLine не определён → «офлайн», а в офлайне контролы записи не рисуются вовсе
vi.mock('../../hooks/useOnline', () => ({ useOnline: () => true }));
// signalr: ensureGit/ensureNotesLoaded (wireRealtime, joinUserGroup) строят соединение и
// читают localStorage — в node без сервера/хранилища это падает. Все подписки — no-op.
vi.mock('../../lib/signalr', () => ({
  getConnection: () => ({ start: async () => {}, on: () => {}, stop: async () => {} }),
  onMessage: () => () => {},
  onPreviewMessage: () => () => {},
  onFilesChanged: () => () => {},
  onGitStatusChanged: () => () => {},
  onLocalModelMessage: () => () => {},
  onReconnected: () => () => {},
  joinUser: async () => {},
}));

const FILE: FileEntry = { name: 'a.txt', path: 'a.txt', isDirectory: false, modified: '2026-01-01T00:00:00Z', isModified: false };
const DIR: FileEntry = { name: 'docs', path: 'docs', isDirectory: true, modified: '2026-01-01T00:00:00Z', isModified: false };

function projectWith(role?: ProjectRoleView): Project {
  return {
    id: 'p1', name: 'Проект', rootPath: '/home/an/proj',
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-02T00:00:00Z',
    myRole: role,
  };
}

function setupMocks() {
  vi.useFakeTimers();
  // localStorage/sessionStorage — node их не несёт; loadSortMode/loadSyncMarks/joinUserGroup их читают
  const store: Record<string, string> = {};
  (globalThis as any).localStorage = {
    getItem: (k: string) => (k in store ? store[k] : null),
    setItem: (k: string, v: string) => { store[k] = String(v); },
    removeItem: (k: string) => { delete store[k]; },
    clear: () => { for (const k of Object.keys(store)) delete store[k]; },
    key: () => null, get length() { return Object.keys(store).length; },
  };
  (globalThis as any).sessionStorage = (globalThis as any).localStorage;
  // Дерево: 1 файл + 1 папка в корне; остальные вызовы — no-op/ошибка (их результатов тест не видит)
  vi.spyOn(api.files, 'list').mockResolvedValue([FILE, DIR]);
  vi.spyOn(api.git, 'status').mockRejectedValue(new Error('offline'));
  vi.spyOn(api.notes, 'list').mockResolvedValue([] as never);
  vi.spyOn(api.notes, 'folders').mockResolvedValue([] as never);
}

function teardown() {
  vi.useRealTimers();
  uninstallFakeDom();
  vi.restoreAllMocks();
}

// Заглушка DOMRect: контекстное меню строит якорь как new DOMRect(x, y, w, 0) — в node его нет
function installDomRectStub() {
  if (!(globalThis as any).DOMRect) {
    (globalThis as any).DOMRect = class {
      x = 0; y = 0; width = 0; height = 0;
      constructor(x = 0, y = 0, width = 0, height = 0) { this.x = x; this.y = y; this.width = width; this.height = height; }
    };
  }
}

// Смонтировать панель и дождаться загрузки корня (api.files.list — resolved-промис).
async function mountTree(role?: ProjectRoleView) {
  installFakeDom();
  installDomRectStub();
  // window.location — node его не несёт; первый рендер до загрузки корня рисует
  // FilesRootEmptyState, который читает window.location.origin (подсказка WebDAV)
  if (!(globalThis as any).window.location) (globalThis as any).window.location = { origin: 'http://localhost:5000' };
  const { root, container } = mount(createElement(FileExplorer, { project: projectWith(role), onOpenFile: () => {} }));
  await act(async () => { await vi.advanceTimersByTimeAsync(100); });
  return { root, container };
}

const body = () => (globalThis as any).document.body as never;

function hasText(scope: unknown, text: string): boolean {
  return textNodes(scope as never).includes(text);
}

// Найти текстовый узел (по подстроке) и подняться до предка с нужным React-пропом
function findAncestorWithProp(scope: unknown, text: string, prop: string): unknown {
  let hit: { parentNode: unknown } | null = null;
  const walk = (n: { nodeType?: number; nodeValue?: string; childNodes?: unknown[] }) => {
    if (hit) return;
    if (n.nodeType === 3 && (n.nodeValue ?? '').includes(text)) { hit = n as never; return; }
    for (const c of n.childNodes ?? []) walk(c as never);
  };
  walk(scope as never);
  let el: { getAttribute?: unknown; parentNode?: unknown } | null = hit as never;
  while (el && !((reactProps(el as never) ?? {})[prop])) el = el.parentNode ?? null;
  return el;
}

// Открыть контекстное меню строки (TreeRow держит onContextMenu на корне строки)
async function openContextMenu(container: unknown, name: string) {
  const row = findAncestorWithProp(container, name, 'onContextMenu');
  expect(row, `строка «${name}» с onContextMenu`).not.toBeNull();
  await act(async () => {
    const onContextMenu = (reactProps(row as never) ?? {}).onContextMenu as (e: unknown) => void;
    onContextMenu({ preventDefault: () => {}, stopPropagation: () => {}, clientX: 10, clientY: 10 });
  });
}

// Открыть меню «Новый» (кнопка в шапке панели; onClick читает currentTarget.getBoundingClientRect)
async function openCreateMenu(container: unknown) {
  const btn = findAncestorWithProp(container, 'Новый', 'onClick');
  expect(btn, 'кнопка «Новый»').not.toBeNull();
  await act(async () => {
    const onClick = (reactProps(btn as never) ?? {}).onClick as (e: unknown) => void;
    onClick({ currentTarget: btn });
  });
}

describe('FileExplorer: контролы записи по роли (S2c-F4b)', () => {
  beforeEach(setupMocks);
  afterEach(teardown);

  // Дерево при любом результате видно — проверка не про «пустая панель», а про пункты записи
  it('дерево видно всем ролям: строки «a.txt» и «docs» в DOM', async () => {
    const { root, container } = await mountTree('reader');
    expect(hasText(container, 'a.txt'), 'файл в дереве').toBe(true);
    expect(hasText(container, 'docs'), 'папка в дереве').toBe(true);
    await act(async () => { root.unmount(); });
  });

  it('читатель: нет «Новый»; в меню файла и папки нет «Переименовать»/«Удалить»', async () => {
    const { root, container } = await mountTree('reader');
    expect(hasText(container, 'Новый'), 'кнопка «Новый» скрыта').toBe(false);
    await openContextMenu(container, 'a.txt');
    expect(hasText(body(), 'Переименовать'), 'переименование файла скрыто').toBe(false);
    expect(hasText(body(), 'Удалить'), 'удаление файла скрыто').toBe(false);
    await openContextMenu(container, 'docs');
    expect(hasText(body(), 'Переименовать'), 'переименование папки скрыто').toBe(false);
    expect(hasText(body(), 'Удалить'), 'удаление папки скрыто').toBe(false);
    await act(async () => { root.unmount(); });
  });

  it('редактор: «Новый» с «Файл»/«Папка»/«Загрузить файлы»; «Удалить» у файла есть, у папки — нет', async () => {
    const { root, container } = await mountTree('editor');
    expect(hasText(container, 'Новый'), 'кнопка «Новый» есть').toBe(true);
    await openCreateMenu(container);
    expect(hasText(body(), 'Файл'), 'пункт «Файл»').toBe(true);
    expect(hasText(body(), 'Папка'), 'пункт «Папка»').toBe(true);
    expect(hasText(body(), 'Загрузить файлы'), 'пункт «Загрузить файлы»').toBe(true);
    await openContextMenu(container, 'a.txt');
    expect(hasText(body(), 'Переименовать'), 'переименование файла открыто').toBe(true);
    expect(hasText(body(), 'Удалить'), 'удаление файла открыто редактору').toBe(true);
    await openContextMenu(container, 'docs');
    expect(hasText(body(), 'Переименовать'), 'переименование папки открыто').toBe(true);
    expect(hasText(body(), 'Удалить'), 'удаление папки закрыто редактору (door dir.delete)').toBe(false);
    await act(async () => { root.unmount(); });
  });

  it('владелец: «Удалить» есть и у папки', async () => {
    const { root, container } = await mountTree('owner');
    await openContextMenu(container, 'docs');
    expect(hasText(body(), 'Удалить'), 'удаление папки открыто владельцу').toBe(true);
    await act(async () => { root.unmount(); });
  });

  // Контроль: без myRole (обычный проект/старый бэк) — как владелец: не «всегда прятать»
  it('без myRole — как раньше: «Удалить» у папки есть', async () => {
    const { root, container } = await mountTree(undefined);
    await openContextMenu(container, 'docs');
    expect(hasText(body(), 'Удалить'), 'обычный проект — удаление папки на месте').toBe(true);
    await act(async () => { root.unmount(); });
  });
});
