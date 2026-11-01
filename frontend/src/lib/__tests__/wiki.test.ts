// Тесты стора вики (S5a): дерево разделов раскладывается по четырём видам и сирота
// остаётся помеченной; notes_changed перечитывает дерево и открытый каталог; отказы
// контракта различаются по статусу (401/404 без кода) и по коду (409 {code,message});
// мутации идут по адресам контракта и перечитывают открытое; последний запрос побеждает
// на всех трёх слотах (доработка M1); смена владельца очищает снимок и не принимает
// ответ прежнего (доработка M2). Разбор по требованию (S7b-0): адрес/метод/тело запроса,
// разбор 200/202 в типизированный результат и отказ по каждому коду; тексты новых кодов —
// в тесте фичи (features/notes/wiki/__tests__/wikiLabels.test.ts): дизайн-линт запрещает
// прямой импорт внутренних модулей фичи отсюда. Доработка S7b-0: analyze-status идёт live
// (офлайн-кэш ему вреден), список отбора чатов — адрес и разбор полей записи списка.
// OPEN-1: запрос открыть страницу — контракт очереди (живёт до взятия, последний побеждает);
// реакция смонтированных экранов — в рендер-тестах WikiTab.test и NotesPage.test.
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
import { readStoredUserId, request } from '../offline';
import {
  WikiError,
  __test,
  adoptWikiPage,
  analyzeWikiChat,
  consumeOpenWikiPage,
  deleteWikiSection,
  ensureWikiLoaded,
  getWikiCatalogSnapshot,
  getWikiPageSnapshot,
  getWikiTreeSnapshot,
  groupSectionsByKind,
  initializeWiki,
  ingestWikiNote,
  loadWikiCatalog,
  loadWikiChatAnalyzeStatus,
  loadWikiChats,
  loadWikiPage,
  loadWikiTree,
  parseSectionKey,
  requestOpenWikiPage,
} from '../wiki';

type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;
const userId = vi.mocked(readStoredUserId);

// Отложенный ответ: тест сам решает, когда резолвится запрос (пробы перекрывающихся
// запросов: первый намеренно резолвится позже второго)
function deferred<T = unknown>(): { promise: Promise<T>; resolve: (v: T) => void } {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>(r => { resolve = r; });
  return { promise, resolve };
}

const PROJECT_ID = '11121314-1516-1718-191a-1b1c1d1e1f10';
const ORPHAN_ID = '21222324-2526-2728-292a-2b2c2d2e2f20';
const SPHERE_ID = '31323334-3536-3738-393a-3b3c3d3e3f30';
const ORPHAN_KEY = `projects/${ORPHAN_ID}`;
const ORPHAN_SECTION_URL = `/wiki/section?section=${encodeURIComponent(ORPHAN_KEY)}`;

// Дерево как его отдаёт GET /wiki/tree: живые имена своих разделов, сирота — с
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

// Для проб порядка ответов и смены владельца — отличимые от прежних снимки:
// «свежее» дерево (initialized, без разделов), пустой каталог того же ключа, страницы A/B.
const TREE_FRESH = { initialized: true, sections: [] };
const CATALOG_EMPTY = { key: 'common', name: null, orphan: false, pages: [] };
const PAGE_A = { pageKey: 'A.md', content: 'страница A' };
const PAGE_B = { pageKey: 'B.md', content: 'страница B' };

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
  __test.reset();
  vi.mocked(onMessage).mockClear();
  req.mockReset();
  // Каждый тест начинает с одного владельца; смену делает тот тест, который её проверяет
  userId.mockReturnValue('u1');
});

afterEach(() => {
  vi.useRealTimers();
});

describe('стор вики: дерево разделов', () => {
  it('разделы раскладываются по четырём видам; сирота помечена и остаётся в своей группе', async () => {
    serve({ 'GET /wiki/tree': TREE });
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
    serve({ 'GET /wiki/tree': TREE, 'GET /wiki/section?section=common': CATALOG_COMMON });
    await ensureWikiLoaded();
    await loadWikiCatalog('common');
    const before = req.mock.calls.length;

    // поля `wiki` у сообщения сегодня нет (ServerMessage.cs:485) — тест отдаёт его, как
    // если бэк добавит следующим: реакция стора от этого не зависит
    handler()({ type: 'notes_changed', action: 'updated', noteId: 'n1', wiki: true } as unknown as ServerMessage);
    await vi.advanceTimersByTimeAsync(0);

    const urls = urlsFrom(before);
    expect(urls).toContain('/wiki/tree');
    expect(urls).toContain('/wiki/section?section=common');
    // каталог, которого никто не открывал, сам себя не перечитывает
    expect(urls.filter(u => u.startsWith('/wiki/section'))).toHaveLength(1);
  });

  it('чужое событие (не notes_changed) вики не перечитывает', async () => {
    serve({ 'GET /wiki/tree': TREE });
    await ensureWikiLoaded();
    const before = req.mock.calls.length;

    handler()({ type: 'task_changed', action: 'updated', task: { id: 't1' } } as unknown as ServerMessage);
    await vi.advanceTimersByTimeAsync(0);

    expect(urlsFrom(before)).toEqual([]);
  });
});

describe('стор вики: порядок ответов (последний запрос побеждает)', () => {
  it('дерево: устаревший ответ, пришедший позже свежего, не затирает снимок', async () => {
    const stale = deferred();
    serve({ 'GET /wiki/tree': stale.promise });
    const p1 = loadWikiTree(); // первый запрос — резолвится позже второго
    serve({ 'GET /wiki/tree': TREE_FRESH });
    await loadWikiTree(); // второй запрос — резолвится первым
    expect(getWikiTreeSnapshot()).toEqual(TREE_FRESH);

    stale.resolve(TREE);
    await p1;
    expect(getWikiTreeSnapshot()).toEqual(TREE_FRESH);
  });

  it('каталог раздела: устаревший ответ, пришедший позже свежего, не затирает снимок', async () => {
    const stale = deferred();
    serve({ 'GET /wiki/section?section=common': stale.promise });
    const p1 = loadWikiCatalog('common');
    serve({ 'GET /wiki/section?section=common': CATALOG_EMPTY });
    await loadWikiCatalog('common');
    expect(getWikiCatalogSnapshot('common')).toEqual(CATALOG_EMPTY);

    stale.resolve(CATALOG_COMMON);
    await p1;
    expect(getWikiCatalogSnapshot('common')).toEqual(CATALOG_EMPTY);
  });

  it('страница: ответ ранее кликнутой страницы, пришедший позже, не вытесняет последнюю', async () => {
    // сценарий ревью: клик A, сразу клик B; ответ B приходит раньше — экран обязан остаться на B
    const stale = deferred();
    serve({ 'GET /wiki/page?section=common&page=A.md': stale.promise });
    const p1 = loadWikiPage('common', 'A.md');
    serve({ 'GET /wiki/page?section=common&page=B.md': PAGE_B });
    await loadWikiPage('common', 'B.md');
    expect(getWikiPageSnapshot('common', 'B.md')).toEqual(PAGE_B);

    stale.resolve(PAGE_A);
    await p1;
    expect(getWikiPageSnapshot('common', 'B.md')).toEqual(PAGE_B);
  });
});

describe('стор вики: привязка к владельцу', () => {
  it('смена владельца: снимок прежнего очищается и перечитывается; его ответ не оседает', async () => {
    serve({
      'GET /wiki/tree': TREE,
      'GET /wiki/section?section=common': CATALOG_COMMON,
      'GET /wiki/page?section=common&page=A.md': PAGE_A,
    });
    await ensureWikiLoaded();
    await loadWikiCatalog('common');
    await loadWikiPage('common', 'A.md');
    expect(getWikiTreeSnapshot()).toEqual(TREE);

    const stale = deferred();
    serve({ 'GET /wiki/tree': stale.promise });
    const p1 = loadWikiTree(); // запрос прежнего владельца — резолвится после смены

    userId.mockReturnValue('u2'); // выход A, вход B в той же вкладке
    serve({ 'GET /wiki/tree': TREE_FRESH });
    await ensureWikiLoaded();
    // на экране — вики того, кто вошёл; catalogs/page прежнего не переживают смену
    expect(getWikiTreeSnapshot()).toEqual(TREE_FRESH);
    expect(getWikiCatalogSnapshot('common')).toBeUndefined();
    expect(getWikiPageSnapshot('common', 'A.md')).toBeNull();

    stale.resolve(TREE);
    await p1;
    expect(getWikiTreeSnapshot()).toEqual(TREE_FRESH);
  });

  it('ответ запроса прежнего владельца без новой загрузки в чужой снимок не пишется', async () => {
    const stale = deferred();
    serve({ 'GET /wiki/tree': stale.promise });
    const p1 = loadWikiTree(); // запрос u1

    userId.mockReturnValue('u2'); // сменился владелец, новой загрузки этого слота нет
    stale.resolve(TREE);
    await p1;
    expect(getWikiTreeSnapshot()).toBeNull();
  });
});

describe('стор вики: ошибки контракта', () => {
  it('404 и 401 — без кода (различаем по статусу), 409 — с кодом и сообщением из тела', async () => {
    serve({ 'GET /wiki/page?section=common&page=x.md': httpError(404, { error: 'Not Found' }) });
    const notFound = await rejection(() => loadWikiPage('common', 'x.md'));
    expect(notFound).toBeInstanceOf(WikiError);
    expect(notFound.status).toBe(404);
    expect(notFound.code).toBeNull();

    serve({ 'DELETE /wiki/section?section=common': httpError(409, { code: 'wiki_not_orphan', message: 'удаляется только раздел-сирота' }) });
    const notOrphan = await rejection(() => deleteWikiSection('common'));
    expect(notOrphan.status).toBe(409);
    expect(notOrphan.code).toBe('wiki_not_orphan');
    expect(notOrphan.message).toBe('удаляется только раздел-сирота');

    serve({ 'GET /wiki/tree': httpError(401) });
    const unauthorized = await rejection(() => loadWikiTree());
    expect(unauthorized.status).toBe(401);
    expect(unauthorized.code).toBeNull();
  });

  it('отказ удаления не трогает состояние: каталог сироты остаётся', async () => {
    serve({ 'GET /wiki/tree': TREE, [`GET ${ORPHAN_SECTION_URL}`]: CATALOG_ORPHAN });
    await ensureWikiLoaded();
    await loadWikiCatalog(ORPHAN_KEY);
    expect(getWikiCatalogSnapshot(ORPHAN_KEY)).toBeTruthy();

    req.mockImplementation(async (url: string, init?: RequestInit) => {
      const key = `${((init?.method ?? 'GET').toUpperCase())} ${url}`;
      if (key === 'GET /wiki/tree') return TREE;
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
      'GET /wiki/tree': TREE,
      'GET /wiki/section?section=common': CATALOG_COMMON,
      'POST /wiki/initialize': { initialized: true },
      'POST /wiki/adopt': { status: 'applied' },
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
    expect(at('/wiki/initialize')).toBeGreaterThanOrEqual(0);
    expect(calls[at('/wiki/initialize')].method).toBe('POST');
    expect(JSON.parse(calls[at('/wiki/initialize')].body!)).toEqual({ section: `projects/${PROJECT_ID}` });

    expect(at('/wiki/adopt')).toBeGreaterThan(at('/wiki/initialize'));
    expect(calls[at('/wiki/adopt')].method).toBe('POST');
    expect(JSON.parse(calls[at('/wiki/adopt')].body!)).toEqual({ section: 'common', page: 'Общее/релизы.md' });

    // после каждой мутации открытое перечитано: дерево + открытый каталог — по разу на мутацию
    expect(calls.filter(c => c.url === '/wiki/tree')).toHaveLength(2);
    expect(calls.filter(c => c.url === '/wiki/section?section=common')).toHaveLength(2);
  });

  it('«Завести» без адресата — null в теле; удаление сироты — DELETE по ключу и сброс её каталога', async () => {
    serve({
      'GET /wiki/tree': TREE,
      [`GET ${ORPHAN_SECTION_URL}`]: CATALOG_ORPHAN,
      'POST /wiki/initialize': { initialized: true },
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

// ─── Разбор по требованию: чат и заметка (S7b-0) и список отбора чатов (доработка H3) ───
// Транспорт чистый: адрес/метод/тело запроса, разбор 200/202 в типизированный результат,
// отказ по каждому коду контракта. Мутация «202 трактуется как 200» (убрать ветку running
// в analyzeWikiChat) краснит тест «202 — отдельный результат»: тело {running:true} не
// подходит под форму 200, функция бросает вместо возврата результата.

const ANALYZE_URL = '/wiki/chats/s1/analyze';
const STATUS_URL = '/wiki/chats/s1/analyze-status';

describe('вики: разбор чата по требованию (S7b-0)', () => {
  it('analyze идёт по адресу и методу контракта; 200 отдаёт результат как есть', async () => {
    serve({ [`POST ${ANALYZE_URL}`]: { outcome: 'awaiting_human', reason: 'есть спорные', packageIds: ['p1', 'p2'] } });
    const res = await analyzeWikiChat('s1');
    expect(res).toEqual({ outcome: 'awaiting_human', reason: 'есть спорные', packageIds: ['p1', 'p2'] });
    expect(req.mock.calls[0][0]).toBe(ANALYZE_URL);
    expect((req.mock.calls[0][1] as RequestInit).method).toBe('POST');
  });

  it('202 {running:true} — отдельный результат, не Ok-результат', async () => {
    serve({ [`POST ${ANALYZE_URL}`]: { running: true } });
    const res = await analyzeWikiChat('s1');
    expect(res).toEqual({ running: true });
    // полей Ok в результате нет: вызывающий выбирает ветку по их наличию
    expect('outcome' in res).toBe(false);
    expect('packageIds' in res).toBe(false);
  });

  it('200 с null или пропущенными полями нормализуется в заявленную форму', async () => {
    serve({
      [`POST ${ANALYZE_URL}`]: { outcome: 'applied', reason: null, packageIds: [] },
      'POST /wiki/chats/s2/analyze': { outcome: 'up_to_date' },
      'POST /wiki/chats/s3/analyze': { outcome: 'nothing_new', reason: 'нечего добавить', packageIds: null },
      'POST /wiki/chats/s4/analyze': { outcome: 'rejected' },
    });
    expect(await analyzeWikiChat('s1')).toEqual({ outcome: 'applied', reason: null, packageIds: [] });
    expect(await analyzeWikiChat('s2')).toEqual({ outcome: 'up_to_date', reason: null, packageIds: [] });
    expect(await analyzeWikiChat('s3')).toEqual({ outcome: 'nothing_new', reason: 'нечего добавить', packageIds: [] });
    expect(await analyzeWikiChat('s4')).toEqual({ outcome: 'rejected', reason: null, packageIds: [] });
  });

  it('отказы analyze: 404 not_found и 409 по каждому коду — WikiError с кодом', async () => {
    const cases: Array<[number, string]> = [
      [404, 'not_found'],
      [409, 'task_execution'],
      [409, 'marked_skip'],
      [409, 'personal_section'],
      [409, 'wiki_not_initialized'],
    ];
    for (const [status, code] of cases) {
      serve({ [`POST ${ANALYZE_URL}`]: httpError(status, { code, message: 'отказ по контракту' }) });
      const e = await rejection(() => analyzeWikiChat('s1'));
      expect(e).toBeInstanceOf(WikiError);
      expect(e.status).toBe(status);
      expect(e.code).toBe(code);
      expect(e.message).toBe('отказ по контракту');
    }
  });

  it('analyze-status: running и last как есть; last=null остаётся null', async () => {
    serve({ [`GET ${STATUS_URL}`]: { running: true, last: null } });
    expect(await loadWikiChatAnalyzeStatus('s1')).toEqual({ running: true, last: null });

    serve({
      [`GET ${STATUS_URL}`]: {
        running: false,
        last: { outcome: 'nothing_new', reason: 'чат разобран до конца', packageIds: [], finishedAt: '2026-10-31T12:00:00+00:00' },
      },
    });
    expect(await loadWikiChatAnalyzeStatus('s1')).toEqual({
      running: false,
      last: { outcome: 'nothing_new', reason: 'чат разобран до конца', packageIds: [], finishedAt: '2026-10-31T12:00:00+00:00' },
    });
  });

  it('analyze-status уходит с live: true — офлайн-кэш ему ВРЕДЕН (доработка H2)', async () => {
    // под обрывом сети кэшированный GET вернул бы прошлый running:false при живом разборе;
    // live исключает запрос из кэша IndexedDB (offline.ts: useOfflineCache = isGet && !live)
    serve({ [`GET ${STATUS_URL}`]: { running: false, last: null } });
    await loadWikiChatAnalyzeStatus('s1');
    expect(req.mock.calls[0][0]).toBe(STATUS_URL);
    expect((req.mock.calls[0][1] as { live?: boolean } | undefined)?.live).toBe(true);
  });

  it('analyze-status 404 not_found — WikiError с кодом', async () => {
    serve({ [`GET ${STATUS_URL}`]: httpError(404, { code: 'not_found', message: 'чат не существует' }) });
    const e = await rejection(() => loadWikiChatAnalyzeStatus('s1'));
    expect(e).toBeInstanceOf(WikiError);
    expect(e.status).toBe(404);
    expect(e.code).toBe('not_found');
  });
});

describe('вики: «разобрать заметку» (S7b-0)', () => {
  it('ingestWikiNote идёт по адресу, методу и телу; 200 отдаёт результат как есть', async () => {
    serve({ 'POST /wiki/ingest/note': { outcome: 'awaiting_human', packageId: 'p1', reason: 'есть спорные' } });
    const res = await ingestWikiNote('n1');
    expect(res).toEqual({ outcome: 'awaiting_human', packageId: 'p1', reason: 'есть спорные' });
    expect(req.mock.calls[0][0]).toBe('/wiki/ingest/note');
    expect((req.mock.calls[0][1] as RequestInit).method).toBe('POST');
    // без адресата — явный null: модель бэка {NoteId, Target?} (WikiController.cs:575)
    expect(sentBody(0)).toEqual({ noteId: 'n1', target: null });

    serve({ 'POST /wiki/ingest/note': { outcome: 'applied', packageId: 'p2', reason: null } });
    expect(await ingestWikiNote('n1', 'common')).toEqual({ outcome: 'applied', packageId: 'p2', reason: null });
    expect(sentBody(1)).toEqual({ noteId: 'n1', target: 'common' });
  });

  it('отказы ingest: 400 wiki_note_id, 409 по каждому коду, 404 not_found', async () => {
    const cases: Array<[number, string]> = [
      [400, 'wiki_note_id'],
      [409, 'wiki_not_initialized'],
      [409, 'packages_limit'],
      [404, 'not_found'],
    ];
    for (const [status, code] of cases) {
      serve({ 'POST /wiki/ingest/note': httpError(status, { code, message: 'отказ по контракту' }) });
      const e = await rejection(() => ingestWikiNote('n1'));
      expect(e).toBeInstanceOf(WikiError);
      expect(e.status).toBe(status);
      expect(e.code).toBe(code);
    }
  });
});

describe('вики: список отбора чатов (доработка H3)', () => {
  it('loadWikiChats идёт по адресу GET /wiki/chats и отдаёт записи как есть', async () => {
    // запись списка — WikiChatTriageDto (WikiChatsController.cs:246-250): поле за полем
    const ITEM_PROJECT = {
      sessionId: 's1', name: 'Чат про релизы', zone: 'triage', score: 42,
      signals: [{ name: 'mentions_plan', weight: 12 }], reason: 'активная работа',
      nothingNewStreak: 0, triagedAt: '2026-10-31T12:00:00+00:00',
      running: true, cursor: 7,
      chroniclePath: `Проекты/${PROJECT_ID}/Хроники/s1.md`,
    };
    serve({ 'GET /wiki/chats': { items: [ITEM_PROJECT], counts: { triage: 1 } } });
    const items = await loadWikiChats();
    expect(req.mock.calls[0][0]).toBe('/wiki/chats');
    // порядок бэка сохраняется как есть, поля доходят до вызывающего без потерь
    expect(items).toEqual([ITEM_PROJECT]);
  });

  it('не-проектный дом: chroniclePath=null остаётся null, name/signals/score — тоже', async () => {
    // бэк отдаёт null для личного дома (ChroniclePath: не-проектный дом) и для записей
    // без балла — фронт не превращает null в пустую строку/ноль
    serve({
      'GET /wiki/chats': {
        items: [{
          sessionId: 's2', name: null, zone: 'nothing_new', score: null, signals: null,
          reason: 'нечего добавить', nothingNewStreak: 3, triagedAt: '2026-10-31T12:01:00+00:00',
          running: false, cursor: 4, chroniclePath: null,
        }],
        counts: { nothing_new: 1 },
      },
    });
    const [item] = await loadWikiChats();
    expect(item.chroniclePath).toBeNull();
    expect(item.name).toBeNull();
    expect(item.signals).toBeNull();
    expect(item.score).toBeNull();
    expect(item.running).toBe(false);
    expect(item.cursor).toBe(4);
    expect(item.nothingNewStreak).toBe(3);
  });
});

// ─── OPEN-1: запрос открыть страницу извне (шапка чата → экран вики) ───
// Стор — единственное место этого состояния: запрос живёт до того, как его возьмут,
// второй замещает первый. Реакция смонтированных экранов — в рендер-тестах
// (WikiTab.test, NotesPage.test); здесь — контракт самой очереди.

describe('вики: запрос открыть страницу (OPEN-1)', () => {
  it('запрос живёт до взятия; взятие очищает; повторного взятия нет', () => {
    requestOpenWikiPage({ section: 'projects/p1', path: 'Проекты/p1/Хроники/c1.md' });
    expect(consumeOpenWikiPage()).toEqual({ section: 'projects/p1', path: 'Проекты/p1/Хроники/c1.md' });
    expect(consumeOpenWikiPage()).toBeNull();
  });

  it('двойной клик — один запрос: последний замещает первый, дублей нет', () => {
    requestOpenWikiPage({ section: 'projects/p1', path: 'Проекты/p1/Хроники/c1.md' });
    requestOpenWikiPage({ section: 'projects/p2', path: 'Проекты/p2/Хроники/c2.md' });
    expect(consumeOpenWikiPage()).toEqual({ section: 'projects/p2', path: 'Проекты/p2/Хроники/c2.md' });
    expect(consumeOpenWikiPage()).toBeNull();
  });

  it('сброс стора снимает и незабранный запрос (тестовый шов не оставляет состояния)', () => {
    requestOpenWikiPage({ section: 'projects/p1', path: 'a' });
    __test.reset();
    expect(consumeOpenWikiPage()).toBeNull();
  });
});
