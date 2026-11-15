// Подписи корня дерева файлов: у ПРОЕКТА корень — корень проекта, у ЛИЧНОЙ
// панели (папка «Личного») — сама папка «Личное». Слово «проект» человеку в
// личном чате показывать нельзя: проекта у него нет.
//
// Проверяем на живом дереве FileExplorer: личная панель (псевдо-проект с
// префиксом personal-files:) и проект — тексты разные, и проектные не изменились.
//
// Окружение node (без jsdom), по образцу FileExplorerRole.test.ts: fakeDom mount +
// мок api.files.list + мок useOnline + мок signalr + заглушка DOMRect (меню строит
// якорь через new DOMRect). Меню — портал в document.body, его содержимое искать там.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FileExplorer } from '../FileExplorer';
import { api } from '../../lib/api';
import type { FileEntry, Project } from '../../types';
import { installFakeDom, uninstallFakeDom, mount, textNodes, reactProps } from '../chat/__tests__/fakeDom';
import { personalFilesProject } from '../../features/files/personalFiles';

vi.mock('../../lib/toast', () => ({ showToast: () => {} }));
vi.mock('../../hooks/useOnline', () => ({ useOnline: () => true }));
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
const INNER: FileEntry = { name: 'b.txt', path: 'docs/b.txt', isDirectory: false, modified: '2026-01-01T00:00:00Z', isModified: false };

function projectOf(): Project {
  return {
    id: 'p1', name: 'Проект', rootPath: '/home/an/proj',
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-02T00:00:00Z',
  };
}

function setupMocks() {
  vi.useFakeTimers();
  const store: Record<string, string> = {};
  (globalThis as any).localStorage = {
    getItem: (k: string) => (k in store ? store[k] : null),
    setItem: (k: string, v: string) => { store[k] = String(v); },
    removeItem: (k: string) => { delete store[k]; },
    clear: () => { for (const k of Object.keys(store)) delete store[k]; },
    key: () => null, get length() { return Object.keys(store).length; },
  };
  (globalThis as any).sessionStorage = (globalThis as any).localStorage;
  // В корне — файл и папка; внутри папки — файл (нужен для списка папок переноса:
  // корень появляется в списке, только когда переносим не из него)
  vi.spyOn(api.files, 'list').mockImplementation(async (_id: string, path?: string) =>
    (path === 'docs' ? [INNER] : [FILE, DIR]));
  vi.spyOn(api.git, 'status').mockRejectedValue(new Error('offline'));
  vi.spyOn(api.notes, 'list').mockResolvedValue([] as never);
  vi.spyOn(api.notes, 'folders').mockResolvedValue([] as never);
}

function teardown() {
  vi.useRealTimers();
  uninstallFakeDom();
  vi.restoreAllMocks();
}

function installDomRectStub() {
  if (!(globalThis as any).DOMRect) {
    (globalThis as any).DOMRect = class {
      x = 0; y = 0; width = 0; height = 0;
      constructor(x = 0, y = 0, width = 0, height = 0) { this.x = x; this.y = y; this.width = width; this.height = height; }
    };
  }
}

async function mountTree(project: Project) {
  installFakeDom();
  installDomRectStub();
  if (!(globalThis as any).window.location) (globalThis as any).window.location = { origin: 'http://localhost:5000' };
  const { root, container } = mount(createElement(FileExplorer, { project, onOpenFile: () => {} }));
  await act(async () => { await vi.advanceTimersByTimeAsync(100); });
  return { root, container };
}

const body = () => (globalThis as any).document.body as never;

function hasText(scope: unknown, text: string): boolean {
  return textNodes(scope as never).includes(text);
}

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

// Открыть меню «Новый» (кнопка в шапке панели)
async function openCreateMenu(container: unknown) {
  const btn = findAncestorWithProp(container, 'Новый', 'onClick');
  expect(btn, 'кнопка «Новый»').not.toBeNull();
  await act(async () => {
    const onClick = (reactProps(btn as never) ?? {}).onClick as (e: unknown) => void;
    onClick({ currentTarget: btn });
  });
}

// Клик по строке папки: раскрывает её (внутри подгружается её содержимое)
async function openDir(container: unknown, name: string) {
  const row = findAncestorWithProp(container, name, 'onClick');
  expect(row, `строка «${name}» с onClick`).not.toBeNull();
  await act(async () => {
    const onClick = (reactProps(row as never) ?? {}).onClick as () => void;
    onClick();
    await vi.advanceTimersByTimeAsync(100);
  });
}

// Контекстное меню строки
async function openContextMenu(container: unknown, name: string) {
  const row = findAncestorWithProp(container, name, 'onContextMenu');
  expect(row, `строка «${name}» с onContextMenu`).not.toBeNull();
  await act(async () => {
    const onContextMenu = (reactProps(row as never) ?? {}).onContextMenu as (e: unknown) => void;
    onContextMenu({ preventDefault: () => {}, stopPropagation: () => {}, clientX: 10, clientY: 10 });
  });
}

// Клик по пункту меню (ищем по подписи)
async function clickMenuItem(label: string) {
  const item = findAncestorWithProp(body(), label, 'onClick');
  expect(item, `пункт меню «${label}»`).not.toBeNull();
  await act(async () => {
    const onClick = (reactProps(item as never) ?? {}).onClick as (e: unknown) => void;
    onClick({});
  });
}

describe('дерево файлов: подписи корня', () => {
  beforeEach(setupMocks);
  afterEach(teardown);

  it('личная панель: корень — «Личное», слова «проект» нет', async () => {
    const { root, container } = await mountTree(personalFilesProject('c1'));
    await openCreateMenu(container);
    expect(hasText(body(), 'Личное'), 'подпись корня в меню «Новый»').toBe(true);
    expect(hasText(body(), 'корень проекта'), 'слова «корень проекта» в личной панели быть не должно').toBe(false);
    await act(async () => { root.unmount(); });
  });

  it('личная панель: диалог создания — «В корне Личного»', async () => {
    const { root, container } = await mountTree(personalFilesProject('c1'));
    await openCreateMenu(container);
    await clickMenuItem('Файл');
    expect(hasText(body(), 'В корне Личного'), 'подзаголовок диалога «Новый файл»').toBe(true);
    expect(hasText(body(), 'В корне проекта'), 'слова «В корне проекта» быть не должно').toBe(false);
    await act(async () => { root.unmount(); });
  });

  it('личная панель: в списке папок для переноса корень — «/ (Личное)»', async () => {
    const { root, container } = await mountTree(personalFilesProject('c1'));
    await openDir(container, 'docs');
    await openContextMenu(container, 'b.txt');
    await clickMenuItem('Переместить в…');
    expect(hasText(body(), '/ (Личное)'), 'корень в списке папок').toBe(true);
    expect(hasText(body(), '/ (корень проекта)'), 'слова «корень проекта» быть не должно').toBe(false);
    await act(async () => { root.unmount(); });
  });

  it('проект: тексты прежние — «корень проекта»', async () => {
    const { root, container } = await mountTree(projectOf());
    await openCreateMenu(container);
    expect(hasText(body(), 'корень проекта'), 'подпись корня в меню «Новый»').toBe(true);
    await act(async () => { root.unmount(); });
  });
});
