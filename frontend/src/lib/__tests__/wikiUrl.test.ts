// Сторож реального URL (дефект B2, QA P1: .qa/llm-wiki-qa-p1-2026-11-01.md).
// lib/wiki.ts передаёт пути в request() БЕЗ /api — request() префиксует их сам
// (offline.ts:31, BASE='/api'), на прово это /api/wiki/…. Мок request() в wiki.test.ts
// двойной префикс не ловит: он сверяет пути до подстановки BASE — поэтому здесь
// НАСТОЯЩИЙ request() с подменённым fetch: каждая экспортированная операция вики
// обязана уйти на /api/wiki/… и нигде не должно появиться /api/api.
// Плюс сито по исходнику wiki.ts: ни одной quoted-литеральной строки с /api/ —
// а значит, и ни одного литерал-аргумента request(/wikiHttp( с ведущим /api/.
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

// idb — мок, как в offline.test.ts: реальный request() только пишет в него GET-кэш
// (и читает при сетевом отказе — его здесь нет). signalr — мок: стор подписывает
// realtime, соедине тесту не нужно.
const { idbGet, idbSet, idbDelete, idbKeys, idbCountRange, idbRangeStamps } = vi.hoisted(() => ({
  idbGet: vi.fn(),
  idbSet: vi.fn(),
  idbDelete: vi.fn(),
  idbKeys: vi.fn(),
  idbCountRange: vi.fn(),
  idbRangeStamps: vi.fn(),
}));
vi.mock('../idb', () => ({ idbGet, idbSet, idbDelete, idbKeys, idbCountRange, idbRangeStamps }));
vi.mock('../signalr', () => ({
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
  joinUser: vi.fn(() => Promise.resolve()),
}));

// Простая замена Web Storage и ответ fetch — по образцу offline.test.ts:11-34
function storageStub(init: Record<string, string> = {}) {
  const m = new Map(Object.entries(init));
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, v); },
    removeItem: (k: string) => { m.delete(k); },
  };
}

function jsonResponse(data: unknown) {
  return {
    ok: true,
    status: 200,
    statusText: 'HTTP 200',
    json: async () => data,
    text: async () => JSON.stringify(data),
  };
}

let fetchMock: ReturnType<typeof vi.fn>;
let urls: string[];
let wiki: typeof import('../wiki');

beforeEach(async () => {
  // Стор вики и состояние offline.ts — модульные: свежий импорт на каждый тест
  vi.resetModules();
  idbGet.mockReset();
  idbSet.mockReset();
  idbSet.mockResolvedValue('key');

  fetchMock = vi.fn();
  urls = [];
  vi.stubGlobal('fetch', fetchMock);
  vi.stubGlobal('navigator', { onLine: true });
  vi.stubGlobal('window', {
    addEventListener: vi.fn(),
    dispatchEvent: () => true,
  });
  vi.stubGlobal('localStorage', storageStub({ cc_token: 'tok', cc_user_id: 'u1' }));
  vi.stubGlobal('sessionStorage', storageStub());

  // настоящий request(): '../offline' НЕ мокаем — подменён только fetch
  wiki = await import('../wiki');
});

afterEach(() => {
  vi.unstubAllGlobals();
});

// Маршрутизация по «метод + URL» (как serve в wiki.test.ts): неоговорённый запрос —
// падение теста, а не тихая пустота.
function serve(routes: Record<string, unknown>): void {
  fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
    urls.push(url);
    const key = `${((init?.method ?? 'GET').toUpperCase())} ${url}`;
    if (!(key in routes)) throw new Error(`неожиданный запрос: ${key}`);
    return jsonResponse(routes[key]);
  });
}

// Инвариант дефекта B2: каждый ушедший fetch — на /api/wiki/… и без /api/api
function assertWikiUrls(): void {
  expect(urls.length).toBeGreaterThan(0);
  for (const url of urls) {
    expect(url.startsWith('/api/wiki/')).toBe(true);
    expect(url).not.toContain('/api/api');
  }
}

// Мутация (п.4 задачи): вернуть '/api' в любом вызове ниже — краснит названный тест
// этого вызова (URL станет /api/api/wiki/…) и сито по исходнику.

const TREE_OK = { initialized: true, sections: [] };

describe('URL вики: настоящий request() уходит на /api/wiki/… (дефект B2)', () => {
  it('loadWikiTree → GET /api/wiki/tree', async () => {
    serve({ 'GET /api/wiki/tree': TREE_OK });
    const tree = await wiki.loadWikiTree();
    expect(tree).toEqual(TREE_OK);
    expect(urls).toEqual(['/api/wiki/tree']);
    assertWikiUrls();
  });

  it('loadWikiCatalog → GET /api/wiki/section', async () => {
    serve({ 'GET /api/wiki/section?section=common': { key: 'common', name: null, orphan: false, pages: [] } });
    const catalog = await wiki.loadWikiCatalog('common');
    expect(catalog.key).toBe('common');
    expect(urls).toEqual(['/api/wiki/section?section=common']);
    assertWikiUrls();
  });

  it('loadWikiPage → GET /api/wiki/page', async () => {
    serve({ 'GET /api/wiki/page?section=common&page=x.md': { pageKey: 'x.md', content: 'страница' } });
    const page = await wiki.loadWikiPage('common', 'x.md');
    expect(page.content).toBe('страница');
    expect(urls).toEqual(['/api/wiki/page?section=common&page=x.md']);
    assertWikiUrls();
  });

  it('initializeWiki → POST /api/wiki/initialize (+перечитка дерева)', async () => {
    serve({ 'POST /api/wiki/initialize': { initialized: true }, 'GET /api/wiki/tree': TREE_OK });
    const res = await wiki.initializeWiki('common');
    expect(res).toEqual({ initialized: true });
    expect(urls).toEqual(['/api/wiki/initialize', '/api/wiki/tree']);
    assertWikiUrls();
  });

  it('adoptWikiPage → POST /api/wiki/adopt (+перечитка дерева)', async () => {
    serve({ 'POST /api/wiki/adopt': { status: 'applied' }, 'GET /api/wiki/tree': TREE_OK });
    const res = await wiki.adoptWikiPage('common', 'x.md');
    expect(res).toEqual({ status: 'applied' });
    expect(urls).toEqual(['/api/wiki/adopt', '/api/wiki/tree']);
    assertWikiUrls();
  });

  it('deleteWikiSection → DELETE /api/wiki/section (+перечитка дерева)', async () => {
    serve({ 'DELETE /api/wiki/section?section=common': { deleted: true }, 'GET /api/wiki/tree': TREE_OK });
    const res = await wiki.deleteWikiSection('common');
    expect(res).toEqual({ deleted: true });
    expect(urls).toEqual(['/api/wiki/section?section=common', '/api/wiki/tree']);
    assertWikiUrls();
  });

  it('listWikiPackages → GET /api/wiki/packages?inbox=true', async () => {
    serve({ 'GET /api/wiki/packages?inbox=true': { packages: [], invalidCount: 0 } });
    const res = await wiki.listWikiPackages();
    expect(res).toEqual({ packages: [], invalidCount: 0 });
    expect(urls).toEqual(['/api/wiki/packages?inbox=true']);
    assertWikiUrls();
  });

  it('decideWikiPackage → POST /api/wiki/packages/{id}/decide', async () => {
    serve({ 'POST /api/wiki/packages/p1/decide': { package: { id: 'p1' }, apply: null } });
    const res = await wiki.decideWikiPackage('p1', 0, true, null);
    expect(res.apply).toBeNull();
    expect(urls).toEqual(['/api/wiki/packages/p1/decide']);
    assertWikiUrls();
  });

  it('acceptAllWikiPackage → POST /api/wiki/packages/{id}/accept-all', async () => {
    serve({ 'POST /api/wiki/packages/p1/accept-all': { package: { id: 'p1' }, apply: null } });
    const res = await wiki.acceptAllWikiPackage('p1');
    expect(res.apply).toBeNull();
    expect(urls).toEqual(['/api/wiki/packages/p1/accept-all']);
    assertWikiUrls();
  });

  it('loadWikiJournal → GET /api/wiki/journal', async () => {
    serve({ 'GET /api/wiki/journal': { entries: [], take: 200, linesRead: 0, invalidLines: 0 } });
    const res = await wiki.loadWikiJournal();
    expect(res.entries).toEqual([]);
    expect(urls).toEqual(['/api/wiki/journal']);
    assertWikiUrls();
  });

  it('revertWikiPackage → POST /api/wiki/packages/{id}/revert (+перечитка дерева)', async () => {
    serve({ 'POST /api/wiki/packages/p1/revert': { restoredPages: [] }, 'GET /api/wiki/tree': TREE_OK });
    const res = await wiki.revertWikiPackage('p1');
    expect(res).toEqual({ restoredPages: [] });
    expect(urls).toEqual(['/api/wiki/packages/p1/revert', '/api/wiki/tree']);
    assertWikiUrls();
  });

  it('retractWikiSource → POST /api/wiki/packages/retract (+перечитка дерева)', async () => {
    serve({
      'POST /api/wiki/packages/retract': {
        revertedPackages: [], unlinkedPackages: [], remainingPackages: [],
        removedChronicles: [], failed: [], markedSkip: false,
      },
      'GET /api/wiki/tree': TREE_OK,
    });
    const res = await wiki.retractWikiSource('chat:s1');
    expect(res.markedSkip).toBe(false);
    expect(urls).toEqual(['/api/wiki/packages/retract', '/api/wiki/tree']);
    assertWikiUrls();
  });

  it('getWikiHealth → GET /api/wiki/health/{section}', async () => {
    serve({ 'GET /api/wiki/health/projects/p1': { section: 'projects/p1', content: '' } });
    const res = await wiki.getWikiHealth('projects/p1');
    expect(res.section).toBe('projects/p1');
    expect(urls).toEqual(['/api/wiki/health/projects/p1']);
    assertWikiUrls();
  });

  it('runWikiLint → POST /api/wiki/lint', async () => {
    serve({ 'POST /api/wiki/lint': { ran: true, sectionsOk: 0, sectionsFailed: 0 } });
    const res = await wiki.runWikiLint();
    expect(res.ran).toBe(true);
    expect(urls).toEqual(['/api/wiki/lint']);
    assertWikiUrls();
  });

  it('analyzeWikiChat → POST /api/wiki/chats/{id}/analyze', async () => {
    serve({ 'POST /api/wiki/chats/s1/analyze': { outcome: 'nothing_new', reason: null, packageIds: [] } });
    const res = await wiki.analyzeWikiChat('s1');
    expect(res).toEqual({ outcome: 'nothing_new', reason: null, packageIds: [] });
    expect(urls).toEqual(['/api/wiki/chats/s1/analyze']);
    assertWikiUrls();
  });

  it('loadWikiChatAnalyzeStatus → GET /api/wiki/chats/{id}/analyze-status', async () => {
    serve({ 'GET /api/wiki/chats/s1/analyze-status': { running: false, last: null } });
    const res = await wiki.loadWikiChatAnalyzeStatus('s1');
    expect(res).toEqual({ running: false, last: null });
    expect(urls).toEqual(['/api/wiki/chats/s1/analyze-status']);
    assertWikiUrls();
  });

  it('ingestWikiNote → POST /api/wiki/ingest/note', async () => {
    serve({ 'POST /api/wiki/ingest/note': { outcome: 'awaiting_human', packageId: null, reason: null } });
    const res = await wiki.ingestWikiNote('n1');
    expect(res).toEqual({ outcome: 'awaiting_human', packageId: null, reason: null });
    expect(urls).toEqual(['/api/wiki/ingest/note']);
    assertWikiUrls();
  });

  it('loadWikiChats → GET /api/wiki/chats', async () => {
    serve({ 'GET /api/wiki/chats': { items: [], counts: {} } });
    const items = await wiki.loadWikiChats();
    expect(items).toEqual([]);
    expect(urls).toEqual(['/api/wiki/chats']);
    assertWikiUrls();
  });
});

describe('wiki.ts: сито по исходнику', () => {
  it('в исходнике нет quoted-литералов с /api/ — значит, нет и литерал-аргументов request(/wikiHttp( с ведущим /api/', () => {
    const src = readFileSync(fileURLToPath(new URL('../wiki.ts', import.meta.url)), 'utf8');
    // Сильнее требуемого сита: ловит любую quoted-строку '/api/…' — и в вызове,
    // и в константе, из которой её потом подставят в вызов. Комментарии (без кавычек)
    // не трогает: «GET /api/wiki/…» там — описание запроса на прово, а не путь в коде.
    const bad = src.match(/['"`]\/api\//g);
    expect(bad).toBeNull();
  });
});
