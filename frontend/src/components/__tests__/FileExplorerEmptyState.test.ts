// Пустое состояние дерева файлов: у ПРОЕКТА — «Проект пуст» и подсказка про
// структуру проекта, у ЛИЧНОЙ панели (папка «Личного») — «Личное пусто» и подсказка
// про папку «Личного». Слово «проект» личному чату показывать нельзя: проекта у
// человека нет.
//
// Тексты берутся из lib/fileRootLabels.ts (единый источник подписей), здесь проверяем,
// что FileExplorer подставляет ЛИЧНЫЕ подписи в личной панели и ПРОЕКТНЫЕ — в проекте.
//
// Окружение node (без jsdom), по образцу FileExplorerRootLabels.test.ts: fakeDom mount +
// мок api.files.list (пустой корень) + мок useOnline + мок signalr + заглушка DOMRect.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { FileExplorer } from '../FileExplorer';
import { api } from '../../lib/api';
import type { Project } from '../../types';
import { installFakeDom, uninstallFakeDom, mount, textNodes } from '../chat/__tests__/fakeDom';
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
  // Корень ПУСТ — иначе рисуется дерево, а не пустое состояние
  vi.spyOn(api.files, 'list').mockResolvedValue([] as never);
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

// Тексты пустого состояния — из одного узла: заголовок и подсказка лежат рядом,
// но проверяем каждый отдельно, чтобы «краснело» на конкретном тексте
function texts(scope: unknown): string[] {
  return textNodes(scope as never);
}

describe('пустое состояние дерева файлов: проект против личной панели', () => {
  beforeEach(setupMocks);
  afterEach(teardown);

  it('личная панель: «Личное пусто», слова «проект» нет', async () => {
    const { root, container } = await mountTree(personalFilesProject('c1'));
    const t = texts(container);
    expect(t, 'заголовок пустого состояния личной панели').toContain('Личное пусто');
    expect(t, 'слова «Проект пуст» в личной панели быть не должно').not.toContain('Проект пуст');
    expect(t, 'подсказки «Файлы и структура проекта» в личной панели быть не должно').not.toContain('Файлы и структура проекта');
    expect(t, 'подсказка про папку «Личного»').toContain('Файлы и структура Личного');
    await act(async () => { root.unmount(); });
  });

  it('проект: тексты прежние — «Проект пуст» и подсказка про структуру проекта', async () => {
    const { root, container } = await mountTree(projectOf());
    const t = texts(container);
    expect(t, 'заголовок пустого состояния проекта').toContain('Проект пуст');
    expect(t, 'подсказка проекта').toContain('Файлы и структура проекта');
    expect(t, 'слова «Личное пусто» в проекте быть не должно').not.toContain('Личное пусто');
    await act(async () => { root.unmount(); });
  });
});
