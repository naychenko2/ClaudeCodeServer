// Тесты стора вики (S5a): дерево разделов раскладывается по четырём видам и сирота
// остаётся помеченной; notes_changed перечитывает дерево и открытый каталог; отказы
// контракта различаются по статусу (401/404 без кода) и по коду (409 {code,message});
// мутации идут по адресам контракта и перечитывают открытое.
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

vi.mock('../signalr', () => ({
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
  joinUser: vi.fn(() => Promise.resolve()),
}));

vi.mock('../offline', () => ({
  request: vi.fn(),
  readStoredUserId: vi.fn(() => 'u1'),
}));

import type { ServerMessage } from '../../types';
import { onMessage } from '../signalr';
import { request } from '../offline';
import {
  WikiError,
  adoptWikiPage,
  deleteWikiSection,
  ensureWikiLoaded,
  getWikiCatalogSnapshot,
  getWikiTreeSnapshot,
  groupSectionsByKind,
  initializeWiki,
  loadWikiCatalog,
  loadWikiPage,
  loadWikiTree,
  parseSectionKey,
  resetWikiStore,
} from '../wiki';

type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;

const PROJECT_ID = '11121314-1516-1718-191a-1b1c1d1e1f10';
const ORPHAN_ID = '21222324-2526-2728-292a-2b2c2d2e2f20';
const SPHERE_ID = '31323334-3536-3738-393a-3b3c3d3e3f30';
const ORPHAN_KEY = `projects/${ORPHAN_ID}`;
const ORPHAN_SECTION_URL = `/api/wiki/section?section=${encodeURIComponent(ORPHAN_KEY)}`;

// Дерево как его отдаёт GET /api/wiki/tree: живые имена своих разделов, сирота — с
// name=null и orphan=true, раздел сферы — sphereReserved (место зарезервировано).
const TREE = {
  initialized: false,
  sections: [
    { key: 'personal', name: null, orphan: false, local: false, sphereReserved: false, pageCount: 3 },
    { key: 'common', name: null, orphan: false, local: false, sphereReserved: false, pageCount: 1 },
    { key: `projects/${PROJECT_ID}`, name: 'AI Home', orphan: false, local: false, sphereReserved: false, pageCount: 2 },
    { key: ORPHAN_KEY, name: null, orphan: true, local: false, sphereReserved: false, pageCount: 4 },
    { key: `spheres/${SPHERE_ID}`, name: 'Дом', orphan: false, local: false, sphereReserved: true, pageCount: 0 },
  ],
};

const CATALOG_COMMON = {
  key: 'common',
  name: null,
  orphan: false,
  // проекций _index/_log в каталоге нет — бэк их исключает (хвост ревью S2f, п. а)
  pages: [
    { pageKey: 'Общее/релизы.md', title: 'Релизы', summary: 'что и когда выкатывали', type: 'concept', status: 'confirmed', sourcesCount: 2, updated: '2026-10-20T00:00:00Z' },
  ],
};

const CATALOG_ORPHAN = { key: ORPHAN_KEY, name: null, orphan: true, pages: [] };

// Ответы по «метод + URL» (у DELETE и GET раздела один адрес): значение-Error бросается,
// остальное отдаётся как ответ; неоговорённый запрос — падение теста, а не тихая пустота.
function serve(routes: Record<string, unknown>): void {
  req.mockImplementation(async (url: string, init?: RequestInit) => {
    const key = `${((init?.method ?? 'GET').toUpperCase())} ${url}`;
    if (!(key in routes)) throw new Error(`неожиданный запрос: ${key}`);
    const value = routes[key];
    if (value instanceof Error) throw value;
    return value;
  });
}

// Обработчик, который стор передал в onMessage при wireRealtime
function handler(): (msg: ServerMessage) => void {
  const calls = vi.mocked(onMessage).mock.calls;
  return calls[calls.length - 1][0] as (msg: ServerMessage) => void;
}

const httpError = (status: number, body?: unknown): Error =>
  Object.assign(new Error(`HTTP ${status}`), { status, body });

async function rejection(fn: () => Promise<unknown>): Promise<WikiError> {
  try {
    await fn();
  } catch (e) {
    return e as WikiError;
  }
  throw new Error('ожидался отказ по контракту, а запрос прошёл');
}

const sentBody = (index: number): Record<string, unknown> =>
  JSON.parse(((req.mock.calls[index][1] as RequestInit).body ?? '') as string);

const urlsFrom = (from: number): string[] => req.mock.calls.slice(from).map((c: unknown[]) => c[0] as string);

beforeEach(() => {
  vi.useFakeTimers();
  resetWikiStore();
  vi.mocked(onMessage).mockClear();
  req.mockReset();
});

afterEach(() => {
  vi.useRealTimers();
});

describe('стор вики: дерево разделов', () => {
  it('разделы раскладываются по четырём видам; сирота помечена и остаётся в своей группе', async () => {
    serve({ 'GET /api/wiki/tree': TREE });
    await ensureWikiLoaded();

    const tree = getWikiTreeSnapshot();
    expect(tree).not.toBeNull();
    // «Вики не заведена» — состояние данных, а не ошибка
    expect(tree!.initialized).toBe(false);

    const byKind = groupSectionsByKind(tree!.sections);
    expect(byKind.personal.map(s => s.key)).toEqual(['personal']);
    expect(byKind.common.map(s => s.key)).toEqual(['common']);
    expect(byKind.project.map(s => s.key)).toEqual([`projects/${PROJECT_ID}`, ORPHAN_KEY]);
    expect(byKind.sphere.map(s => s.key)).toEqual([`spheres/${SPHERE_ID}`]);

    const own = byKind.project.find(s => s.key === `projects/${PROJECT_ID}`)!;
    expect(own.name).toBe('AI Home');
    expect(own.orphan).toBe(false);

    const orphan = byKind.project.find(s => s.key === ORPHAN_KEY)!;
    expect(orphan.orphan).toBe(true);
    expect(orphan.name).toBeNull();

    // раздел сферы: место есть, запись — за следующей фичей (ADR-036 §7)
    expect(byKind.sphere[0].sphereReserved).toBe(true);
  });

  it('ключ раздела разбирается как на бэке; физическое имя папки — не ключ', () => {
    expect(parseSectionKey('common')).toEqual({ kind: 'common', id: null });
    expect(parseSectionKey('personal')).toEqual({ kind: 'personal', id: null });
    expect(parseSectionKey(`projects/${PROJECT_ID}`)).toEqual({ kind: 'project', id: PROJECT_ID });
    expect(parseSectionKey(`spheres/${SPHERE_ID}`)).toEqual({ kind: 'sphere', id: SPHERE_ID });
    // fail-closed: чужая строка не принадлежит ни одному разделу
    expect(parseSectionKey('Проекты/p1')).toBeNull();
    expect(parseSectionKey('projects/')).toBeNull();
    expect(parseSectionKey('projects/a/b')).toBeNull();
    expect(parseSectionKey('')).toBeNull();
  });
});

describe('стор вики: realtime', () => {
  it('notes_changed перечитывает дерево и уже открытый каталог раздела', async () => {
    serve({ 'GET /api/wiki/tree': TREE, 'GET /api/wiki/section?section=common': CATALOG_COMMON });
    await ensureWikiLoaded();
    await loadWikiCatalog('common');
    const before = req.mock.calls.length;

    // поля `wiki` у сообщения сегодня нет (ServerMessage.cs:485) — тест отдаёт его, как
    // если бэк добавит следующим: реакция стора от этого не зависит
    handler()({ type: 'notes_changed', action: 'updated', noteId: 'n1', wiki: true } as unknown as ServerMessage);
    await vi.advanceTimersByTimeAsync(0);

    const urls = urlsFrom(before);
    expect(urls).toContain('/api/wiki/tree');
    expect(urls).toContain('/api/wiki/section?section=common');
    // каталог, которого никто не открывал, сам себя не перечитывает
    expect(urls.filter(u => u.startsWith('/api/wiki/section'))).toHaveLength(1);
  });

  it('чужое событие (не notes_changed) вики не перечитывает', async () => {
    serve({ 'GET /api/wiki/tree': TREE });
    await ensureWikiLoaded();
    const before = req.mock.calls.length;

    handler()({ type: 'task_changed', action: 'updated', task: { id: 't1' } } as unknown as ServerMessage);
    await vi.advanceTimersByTimeAsync(0);

    expect(urlsFrom(before)).toEqual([]);
  });
});

describe('стор вики: ошибки контракта', () => {
  it('404 и 401 — без кода (различаем по статусу), 409 — с кодом и сообщением из тела', async () => {
    serve({ 'GET /api/wiki/page?section=common&page=x.md': httpError(404, { error: 'Not Found' }) });
    const notFound = await rejection(() => loadWikiPage('common', 'x.md'));
    expect(notFound).toBeInstanceOf(WikiError);
    expect(notFound.status).toBe(404);
    expect(notFound.code).toBeNull();

    serve({ 'DELETE /api/wiki/section?section=common': httpError(409, { code: 'wiki_not_orphan', message: 'удаляется только раздел-сирота' }) });
    const notOrphan = await rejection(() => deleteWikiSection('common'));
    expect(notOrphan.status).toBe(409);
    expect(notOrphan.code).toBe('wiki_not_orphan');
    expect(notOrphan.message).toBe('удаляется только раздел-сирота');

    serve({ 'GET /api/wiki/tree': httpError(401) });
    const unauthorized = await rejection(() => loadWikiTree());
    expect(unauthorized.status).toBe(401);
    expect(unauthorized.code).toBeNull();
  });

  it('отказ удаления не трогает состояние: каталог сироты остаётся', async () => {
    serve({ 'GET /api/wiki/tree': TREE, [`GET ${ORPHAN_SECTION_URL}`]: CATALOG_ORPHAN });
    await ensureWikiLoaded();
    await loadWikiCatalog(ORPHAN_KEY);
    expect(getWikiCatalogSnapshot(ORPHAN_KEY)).toBeTruthy();

    req.mockImplementation(async (url: string, init?: RequestInit) => {
      const key = `${((init?.method ?? 'GET').toUpperCase())} ${url}`;
      if (key === 'GET /api/wiki/tree') return TREE;
      throw httpError(409, { code: 'wiki_not_orphan', message: 'удаляется только раздел-сирота' });
    });
    const e = await rejection(() => deleteWikiSection(ORPHAN_KEY));
    expect(e).toBeInstanceOf(WikiError);
    expect(getWikiCatalogSnapshot(ORPHAN_KEY)).toBeTruthy();
  });
});

describe('стор вики: операции', () => {
  it('«Завести» и «Принять» идут по адресам контракта и перечитывают открытое', async () => {
    serve({
      'GET /api/wiki/tree': TREE,
      'GET /api/wiki/section?section=common': CATALOG_COMMON,
      'POST /api/wiki/initialize': { initialized: true },
      'POST /api/wiki/adopt': { status: 'applied' },
    });
    await ensureWikiLoaded();
    await loadWikiCatalog('common');
    req.mock.calls.length = 0;

    await initializeWiki(`projects/${PROJECT_ID}`);
    await adoptWikiPage('common', 'Общее/релизы.md');

    const calls = req.mock.calls.map((c: unknown[]) => ({
      url: c[0] as string,
      method: ((c[1] as RequestInit | undefined)?.method ?? 'GET'),
      body: (c[1] as RequestInit | undefined)?.body as string | undefined,
    }));
    const at = (url: string) => calls.findIndex(c => c.url === url);
    expect(at('/api/wiki/initialize')).toBeGreaterThanOrEqual(0);
    expect(calls[at('/api/wiki/initialize')].method).toBe('POST');
    expect(JSON.parse(calls[at('/api/wiki/initialize')].body!)).toEqual({ section: `projects/${PROJECT_ID}` });

    expect(at('/api/wiki/adopt')).toBeGreaterThan(at('/api/wiki/initialize'));
    expect(calls[at('/api/wiki/adopt')].method).toBe('POST');
    expect(JSON.parse(calls[at('/api/wiki/adopt')].body!)).toEqual({ section: 'common', page: 'Общее/релизы.md' });

    // после каждой мутации открытое перечитано: дерево + открытый каталог — по разу на мутацию
    expect(calls.filter(c => c.url === '/api/wiki/tree')).toHaveLength(2);
    expect(calls.filter(c => c.url === '/api/wiki/section?section=common')).toHaveLength(2);
  });

  it('«Завести» без адресата — null в теле; удаление сироты — DELETE по ключу и сброс её каталога', async () => {
    serve({
      'GET /api/wiki/tree': TREE,
      [`GET ${ORPHAN_SECTION_URL}`]: CATALOG_ORPHAN,
      'POST /api/wiki/initialize': { initialized: true },
      [`DELETE ${ORPHAN_SECTION_URL}`]: { deleted: true },
    });
    await ensureWikiLoaded();
    await loadWikiCatalog(ORPHAN_KEY);
    req.mock.calls.length = 0;

    await initializeWiki();
    expect(sentBody(0)).toEqual({ section: null });

    await deleteWikiSection(ORPHAN_KEY);
    const del = req.mock.calls.find(
      (c: unknown[]) => c[0] === ORPHAN_SECTION_URL
        && (c[1] as RequestInit | undefined)?.method === 'DELETE',
    );
    expect(del).toBeTruthy();
    expect(getWikiCatalogSnapshot(ORPHAN_KEY)).toBeUndefined();
  });
});
