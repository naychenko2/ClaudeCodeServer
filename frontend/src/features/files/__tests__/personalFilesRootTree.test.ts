// Личная панель «Файлы» после Х-9 (442cd8ec0, дефект 44718bbf): бэк больше НЕ вставляет
// виртуальную папку «Заметки» в корень «Личного» (FileService.List с withNotesRoot: false,
// PersonalFilesController.List), и путь notes/… там 404. Закрепление: панель личного чата
// при таком листинге показывает корневые подпапки как раньше — среди них нет «Заметок».
// Фронт сам «Заметки» не синтезирует: строка «Заметки» в дереве — только за notes-записью
// в ответе (FileExplorer.isNotesRoot), поэтому отсутствие записи в ответе = отсутствие строки.
//
// Цепка настоящая (тот же мок lib/offline, что в personalFiles.test.ts): PersonalFilesPanel →
// FileExplorer.loadDir → api.files.list → features/files/api (псевдо-id personal-files:…
// переключает базу на личный маршрут) → request(). Адрес в тесте — личный корень чата,
// не /projects/…; сам id→маршрут уже закреплен personalFiles.test.ts.
// Мутация «вернуть вставку «Заметки» в листинг личного корня» (добавить в ROOT запись
// { path: 'notes' }) красит тест на строке «Заметки».
//
// Окружение node (без jsdom), по образцу components/__tests__/FileExplorerRole.test.ts:
// fakeDom + мок signalr (onFilesChanged подписка на маунте) + мок useOnline (в node
// navigator.onLine не определён → «офлайн») + мок toast + localStorage (loadSortMode его
// читает) + заглушка DOMRect. ensureGit для personal-id сам пропускается (lib/git.ts:147),
// ensureNotesLoaded гейтится подсистемой 'notes' (выключена), loadSyncMarks/
// loadDownloadedSet — гейтом по isPersonalFilesId (Х-13): их вызовов нет.

import { act, createElement } from 'react';
import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { PersonalFilesPanel } from '../PersonalFilesPanel';
import type { FileEntry } from '../../../types';
import { installFakeDom, uninstallFakeDom, mount, textNodes } from '../../../components/chat/__tests__/fakeDom';

const { request } = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('../../../lib/offline', async (importOriginal) => {
  const orig = await importOriginal<typeof import('../../../lib/offline')>();
  return { ...orig, request };
});
vi.mock('../../../lib/toast', () => ({ showToast: () => {} }));
vi.mock('../../../hooks/useOnline', () => ({ useOnline: () => true }));
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

const dir = (name: string): FileEntry =>
  ({ name, path: name, isDirectory: true, modified: '2026-01-01T00:00:00Z', isModified: false });

// Корень «Личного» после Х-9: медиальные папки + пользовательская, БЕЗ notes/ —
// ровно то, что отдаёт PersonalFilesController.List (withNotesRoot: false)
const ROOT: FileEntry[] = [dir('images'), dir('audio'), dir('video'), dir('docs')];

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
  if (!(globalThis as any).DOMRect) {
    (globalThis as any).DOMRect = class {
      x = 0; y = 0; width = 0; height = 0;
      constructor(x = 0, y = 0, width = 0, height = 0) { this.x = x; this.y = y; this.width = width; this.height = height; }
    };
  }
  request.mockReset();
  // Листинг личного корня — ROOT; прочие пути (их тут нет) — пусто
  request.mockImplementation((url: string) => Promise.resolve(
    String(url).startsWith('/personal/chats/s1/files?path=') ? ROOT : []));
}

function teardown() {
  vi.useRealTimers();
  uninstallFakeDom();
  vi.restoreAllMocks();
}

async function mountPanel() {
  installFakeDom();
  // window.location — node его не несёт; первый рендер до загрузки корня рисует
  // FilesRootEmptyState, который читает window.location.origin (подсказка WebDAV)
  if (!(globalThis as any).window.location) (globalThis as any).window.location = { origin: 'http://localhost:5000' };
  const { root, container } = mount(createElement(PersonalFilesPanel, { sessionId: 's1' }));
  await act(async () => { await vi.advanceTimersByTimeAsync(100); });
  return { root, container };
}

const hasText = (scope: unknown, text: string): boolean => textNodes(scope as never).includes(text);

describe('личная панель «Файлы»: корень без «Заметок» (после Х-9)', () => {
  beforeEach(setupMocks);
  afterEach(teardown);

  it('панель личного чата показывает корневые подпапки и запросит личный корень; «Заметок» среди них нет', async () => {
    const { root, container } = await mountPanel();
    // Панель спросила ровно личный корень чата: псевдо-id переключил файловую базу.
    // После Х-13 (дефект 6cdd1cf5) личная панель не ходит на проектные маршруты вовсе:
    // loadSyncMarks/loadDownloadedSet гейтятся по isPersonalFilesId — прежний безусловный
    // вызов давал лишний 404 /projects/personal-files:s1/sync на каждый маунт
    // (снять гейт в FileExplorer.tsx → следующая проверка краснеет на этом запросе).
    const urls = request.mock.calls.map(c => String(c[0]));
    expect(urls, 'листинг ушёл на личный маршрут').toContain('/personal/chats/s1/files?path=');
    expect(urls.filter(u => u.includes('/files')), 'файловый запрос — только личный').toEqual(
      ['/personal/chats/s1/files?path=']);
    expect(urls.some(u => u.includes('/projects/')), 'запросов на проектные маршруты нет').toBe(false);
    // Корневые подпапки из ответа бэка видны строками дерева — панель не сломана
    for (const name of ['images', 'audio', 'video', 'docs']) {
      expect(hasText(container, name), `папка «${name}» в дереве`).toBe(true);
    }
    // Виртуальных «Заметок» в личном корне больше нет — их нет и в панели
    expect(hasText(container, 'Заметки'), '«Заметки» в личном корне нет').toBe(false);
    await act(async () => { root.unmount(); });
  });
});
