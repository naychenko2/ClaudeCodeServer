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
// W4-1: перечитка после мутации — только про открытую страницу (счётчик подписки,
// тестовый шов __test.openPage): 404 перечитки снимает страницу из стора и не выходит
// наружу; закрытая страница не поднимает поколение и не отбрасывает ответ новой;
// неформный 200 списка чатов — ошибка, а не тихая пустота, и код wiki_triage_invalid
// узнаётся. OPEN-1: запрос открыть страницу — контракт очереди (живёт до взятия,
// последний побеждает); реакция смонтированных экранов — в рендер-тестах
// WikiTab.test и NotesPage.test. Ф1 («Ночной разбор», контракты NA-2/N1b): PUT настроек
// шлёт только изменённые поля; правка оптимистична, отказ откатывает её и приходит
// результатом (WikiError с кодом), а не исключением; слот настроек — последний запрос
// побеждает; lastNight: форма доходит, null и отсутствие поля — одно «итога не было»;
// пометка чата — транспорт по адресу/методу/телу, 400 без кода различим только по статусу.
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
  getWikiSettingsSnapshot,
  groupSectionsByKind,
  initializeWiki,
  ingestWikiNote,
  loadWikiCatalog,
  loadWikiChatAnalyzeStatus,
  loadWikiChats,
  loadWikiPage,
  loadWikiSettings,
  loadWikiTree,
  parseSectionKey,
  putWikiNightly,
  requestOpenWikiPage,
  retractWikiSource,
  setWikiChatMark,
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

// ─── Перечитка открытой страницы после мутации (W4-1) ───
// Открытой страницу делает счётчик подписки экрана — в тесте его поднимает шов
// __test.openPage (те же операции, что у useWikiPage). Свойства:
// — 404 перечитки (страницу снес откат) снимает страницу из стора и не выходит
//   необработанным исключением наружу;
// — закрытая за время мутации страница не перечитывается: её перечитка подняла бы
//   поколение слота и отбросила ответ только что открытой другой страницы.
// Мутации (вывод — в отчёте задачи):
// (1) вернуть в reReadOpenPage тихий .catch (без снятия по ключу) →
//     «reReadOpenPage404_ЧиститСтраницуВСторе» краснеет (снимок остаётся прежним);
// (2) снять в adoptWikiPage сверку открытого ключа (перечитывать безусловно) →
//     «Adopt_ПользовательОткрылДругуюСтраницу_ЕёОтветНеОтброшен» краснеет
//     (запрос-исключение всё равно поднимает _pageGen, ответ B отбрасывается).

const RETRACT_W41_OK = {
  revertedPackages: [], unlinkedPackages: [], remainingPackages: [],
  removedChronicles: [], failed: [], markedSkip: false,
};

describe('стор вики: перечитка открытой страницы после мутации (W4-1)', () => {
  it('reReadOpenPage404_ЧиститСтраницуВСторе', async () => {
    serve({
      'GET /wiki/tree': TREE,
      'GET /wiki/page?section=common&page=A.md': PAGE_A,
    });
    await ensureWikiLoaded();
    await loadWikiPage('common', 'A.md');
    __test.openPage('common', 'A.md'); // экран страницы подписан — она открыта
    expect(getWikiPageSnapshot('common', 'A.md')).toEqual(PAGE_A);

    // откат по источнику снес страницу: её перечитка даёт 404
    serve({
      'POST /wiki/packages/retract': RETRACT_W41_OK,
      'GET /wiki/page?section=common&page=A.md': httpError(404),
      'GET /wiki/tree': TREE,
    });
    // 404 перечитки — не необработанное исключение наружу: откат отдаёт свой результат
    const res = await retractWikiSource('note:n1');
    expect(res).toEqual(RETRACT_W41_OK);
    // содержимое до отката под открытым экраном не остаётся: страница снята по ключу
    expect(getWikiPageSnapshot('common', 'A.md')).toBeNull();
  });

  it('Adopt_ПользовательОткрылДругуюСтраницу_ЕёОтветНеОтброшен', async () => {
    serve({
      'GET /wiki/tree': TREE,
      'GET /wiki/page?section=common&page=A.md': PAGE_A,
    });
    await ensureWikiLoaded();
    await loadWikiPage('common', 'A.md');
    const closeA = __test.openPage('common', 'A.md');

    const adopt = deferred<{ status: string }>();
    const pageB = deferred<typeof PAGE_B>();
    serve({
      'POST /wiki/adopt': adopt.promise,
      'GET /wiki/page?section=common&page=B.md': pageB.promise,
      'GET /wiki/tree': TREE,
    });
    const pAdopt = adoptWikiPage('common', 'A.md');

    // за время POST человек закрыл A и открыл B: запрос B — последний в слоте страниц
    closeA();
    const pB = loadWikiPage('common', 'B.md');

    adopt.resolve({ status: 'applied' });
    await pAdopt;
    // перечитки закрытой A не было: ни одного нового запроса её страницы
    expect(req.mock.calls.filter((c: unknown[]) => c[0] === '/wiki/page?section=common&page=A.md')).toHaveLength(1);
    pageB.resolve(PAGE_B);
    await pB;
    // ответ B не отброшен как устаревший и не висит на спиннере: стор на B
    expect(getWikiPageSnapshot('common', 'B.md')).toEqual(PAGE_B);
    // ответ перечитки A после её закрытия в стор не пишет — прежняя страница не возвращается
    expect(getWikiPageSnapshot('common', 'A.md')).toBeNull();
  });
});

// ─── Факт снятой страницы (W4-5, дефект 3f3449e4) ───
// 404 перечитки оставляет в сторе факт «эту страницу снес откат» (_pageGoneKey,
// шов __test.pageGone): отсутствие снимка само по себе не различает «страница
// грузится» и «страницу снес откат», а экрану возврата к каталогу нужен именно
// этот факт. Свойства:
// — 404 перечитки отмечает снятую страницу; отметка принадлежит только её ключу;
// — перечитка той же страницы снимает отметку: до ответа состояние — «грузится»;
// — поздний успешный ответ снятой страницы не возвращает её в стор (сверка поколения).
// Мутации (вывод — в отчёте задачи):
// (1) убрать _pageGoneKey = page.cacheKey в reReadOpenPage → «ОтмечаетСнятуюСтраницу»
//     краснеет (факта нет — экран остался бы на скелетоне);
// (2) убрать снятие отметки в начале loadWikiPage → «ПеречиткаТойЖеСнимаетОтметку»
//     краснеет (открытая заново страница считалась бы снятой).

describe('стор вики: факт снятой страницы (W4-5, дефект 3f3449e4)', () => {
  it('W45_404Перечитки_ОтмечаетСнятуюСтраницу_ПеречиткаТойЖеСнимаетОтметку', async () => {
    serve({ 'GET /wiki/tree': TREE, 'GET /wiki/page?section=common&page=A.md': PAGE_A });
    await ensureWikiLoaded();
    await loadWikiPage('common', 'A.md');
    __test.openPage('common', 'A.md'); // экран подписан — страница открыта
    serve({
      'POST /wiki/packages/retract': RETRACT_W41_OK,
      'GET /wiki/page?section=common&page=A.md': httpError(404),
      'GET /wiki/tree': TREE,
    });
    await retractWikiSource('note:n1');
    // страница снята — и в сторе остался факт: экран по нему вернётся к каталогу
    expect(getWikiPageSnapshot('common', 'A.md')).toBeNull();
    expect(__test.pageGone('common', 'A.md')).toBe(true);
    // факт принадлежит только её ключу: другая страница не «снята», она «грузится»
    expect(__test.pageGone('common', 'B.md')).toBe(false);
    // повторное открытие той же страницы: начало чтения снимает отметку — до ответа
    // состояние «грузится», и экран не закрывает то, что только что открыл
    serve({ 'GET /wiki/page?section=common&page=A.md': PAGE_A });
    await loadWikiPage('common', 'A.md');
    expect(__test.pageGone('common', 'A.md')).toBe(false);
    expect(getWikiPageSnapshot('common', 'A.md')).toEqual(PAGE_A);
  });

  it('W45_ПозднийУспешныйОтветСнятойСтраницы_ЕёНеВозвращает', async () => {
    serve({ 'GET /wiki/tree': TREE, 'GET /wiki/page?section=common&page=A.md': PAGE_A });
    await ensureWikiLoaded();
    await loadWikiPage('common', 'A.md'); // gen1: страница в сторе, экран подписан
    __test.openPage('common', 'A.md');
    // пользователь перечитывает A (gen2 — её ответ придёт позже), и за это время
    // откат перечитывает ту же страницу (gen3) и даёт 404: страница снята
    const late = deferred();
    let pageCalls = 0;
    req.mockImplementation(async (url: string) => {
      if (url === '/wiki/tree') return TREE;
      if (url === '/wiki/packages/retract') return RETRACT_W41_OK;
      if (url === '/wiki/page?section=common&page=A.md') {
        pageCalls += 1;
        if (pageCalls === 1) return late.promise;
        if (pageCalls === 2) throw httpError(404);
      }
      throw new Error(`неожиданный запрос: ${url}`);
    });
    const pLate = loadWikiPage('common', 'A.md');
    await retractWikiSource('note:n1');
    expect(__test.pageGone('common', 'A.md')).toBe(true);
    late.resolve(PAGE_A);
    await pLate;
    // устаревший ответ снятой страницы её не воскрешает: сверка поколения (M1)
    expect(getWikiPageSnapshot('common', 'A.md')).toBeNull();
    expect(__test.pageGone('common', 'A.md')).toBe(true);
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

  it('loadWikiChats_КодWikiTriageInvalid_ЭтоWikiErrorСКодом', async () => {
    // 409 «файл разбора чатов не читается» (WikiChatsController.cs:108,152) — код контракта
    // узнаётся: экран отбора может назвать причину, а не общий отказ
    serve({ 'GET /wiki/chats': httpError(409, { code: 'wiki_triage_invalid', message: 'файл разбора чатов не читается' }) });
    const e = await rejection(() => loadWikiChats());
    expect(e).toBeInstanceOf(WikiError);
    expect(e.status).toBe(409);
    expect(e.code).toBe('wiki_triage_invalid');
  });

  it('loadWikiChats_Неформный200_Бросает', async () => {
    // Неформный 200 — не тихая пустота экрана отбора: пустой список бэк говорит items:[]
    // (List контроллера отдаёт пустой массив — WikiChatsController.cs:110,133), а тело
    // вне формы — ответ вне контракта, как в analyzeWikiChat
    for (const body of [{}, { items: null }, null]) {
      serve({ 'GET /wiki/chats': body });
      const e = await rejection(() => loadWikiChats());
      expect(e).toBeInstanceOf(WikiError);
      expect(e.status).toBe(0);
      expect(e.message).toContain('неожиданный ответ');
    }
  });
});

// ─── «Ночной разбор»: настройки и итог (Ф1; контракты NA-2 и N7c) ───
// Настройки как их отдаёт GET /api/wiki/settings: ночной блок целый всегда, с умолчаниями
// (WikiSettingsController.cs:212-223); lastNight появится с N7c — до него поля нет вовсе.
// Свойства стора: правка оптимистична (до ответа), отказ откатывает её и приходит
// результатом, слот настроек — последний запрос побеждает, ответ чужого владельца не пишется.
// Мутации (вывод — в отчёте задачи):
// (1) убрать сверку поколения в успехе putWikiNightly → «ДваБыстрыхPUT» краснеет
//     (устаревший ответ затирает состояние);
// (2) убрать оптимистичную запись в начале putWikiNightly → «ТумблерПрименяетсяОптимистично»
//     краснеет (до ответа состояние прежнее);
// (3) убрать откат в ветке отказа → «ОтказPUTОткатывает» краснеет (оптимистичное остаётся).

const SETTINGS = {
  autoApplyEnabled: false,
  classes: [],
  nightly: {
    enabled: false, windowStartHour: 3, windowEndHour: 6,
    maxPackagesPerNight: 20, maxPackagesPerSection: 6, maxChatsPerSection: 10,
    inputTokenBudget: 700000,
  },
};
const SETTINGS_ON = { ...SETTINGS, nightly: { ...SETTINGS.nightly, enabled: true } };
const SETTINGS_WS2 = { ...SETTINGS, nightly: { ...SETTINGS.nightly, windowStartHour: 2 } };
const SETTINGS_WS4 = { ...SETTINGS, nightly: { ...SETTINGS.nightly, windowStartHour: 4 } };
const LAST_NIGHT = {
  date: '2026-11-01', selected: 4, analyzed: 3, applied: 2, toInbox: 1,
  estTokens: 180000, outcome: 'completed',
};

describe('вики: настройки «Ночного разбора» (F1)', () => {
  it('putWikiNightly шлёт только изменённые поля ночного блока', async () => {
    serve({ 'GET /wiki/settings': SETTINGS, 'PUT /wiki/settings': SETTINGS_ON });
    await loadWikiSettings();
    const r = await putWikiNightly({ enabled: true });
    expect(r.ok).toBe(true);
    expect(req.mock.calls[1][0]).toBe('/wiki/settings');
    expect((req.mock.calls[1][1] as RequestInit).method).toBe('PUT');
    // частичный блок: ни неизменённых полей, ни null-«не менять» — на бэке «не менять»
    // это отсутствие поля (NightlyRequest, WikiSettingsController.cs:281-297)
    expect(sentBody(1)).toEqual({ nightly: { enabled: true } });
  });

  it('putWikiNightly_ТумблерПрименяетсяОптимистично_ОтветПодтверждаетСервернымЦелым', async () => {
    serve({ 'GET /wiki/settings': SETTINGS });
    await loadWikiSettings();
    const put = deferred();
    serve({ 'PUT /wiki/settings': put.promise });
    const p = putWikiNightly({ enabled: true });
    // до ответа: новое значение уже в сторе — UI тумблера не ждёт сети
    expect(getWikiSettingsSnapshot()!.nightly.enabled).toBe(true);
    put.resolve(SETTINGS_ON);
    const r = await p;
    expect(r.ok).toBe(true);
    // подтверждение — серверное целое, а не сохранённый патч
    expect(getWikiSettingsSnapshot()!.nightly).toEqual(SETTINGS_ON.nightly);
  });

  it('putWikiNightly_ДваБыстрыхPUT_ВСтореСостояниеПоследнегоОтвет', async () => {
    serve({ 'GET /wiki/settings': SETTINGS });
    await loadWikiSettings();
    // первый PUT резолвится намеренно позже второго: его ответ — устаревший
    const slow = deferred();
    serve({ 'PUT /wiki/settings': slow.promise });
    const p1 = putWikiNightly({ windowStartHour: 2 });
    serve({ 'PUT /wiki/settings': SETTINGS_WS4 });
    const r2 = await putWikiNightly({ windowStartHour: 4 });
    expect(r2.ok).toBe(true);
    expect(getWikiSettingsSnapshot()!.nightly.windowStartHour).toBe(4);
    slow.resolve(SETTINGS_WS2);
    await p1;
    // поздний ответ раннего запроса не затирает состояние: сверка поколения (M1)
    expect(getWikiSettingsSnapshot()!.nightly).toEqual(SETTINGS_WS4.nightly);
  });

  it('putWikiNightly_ОтказPUTОткатываетОптимистичное_ОшибкаПриходитРезультатом', async () => {
    serve({ 'GET /wiki/settings': SETTINGS });
    await loadWikiSettings();
    for (const [status, code] of [
      [400, 'wiki_nightly'],
      [500, 'wiki_settings_write_failed'],
    ] as Array<[number, string]>) {
      serve({ 'PUT /wiki/settings': httpError(status, { code, message: 'отказ по контракту' }) });
      // исключение наружу не летит: отказ контракта — значение результата
      const r = await putWikiNightly({ enabled: true });
      if (r.ok) throw new Error('ожидался отказ по контракту, а PUT прошёл');
      expect(r.error).toBeInstanceOf(WikiError);
      expect(r.error.status).toBe(status);
      expect(r.error.code).toBe(code);
      expect(r.error.message).toBe('отказ по контракту');
      // оптимистичное откатано: в сторе то, что было до запроса
      expect(getWikiSettingsSnapshot()!.nightly).toEqual(SETTINGS.nightly);
    }
  });

  it('lastNight: форма доходит до вызывающего; null и отсутствие поля — одно «итога не было»', async () => {
    serve({ 'GET /wiki/settings': { ...SETTINGS, lastNight: LAST_NIGHT } });
    expect((await loadWikiSettings()).lastNight).toEqual(LAST_NIGHT);
    serve({ 'GET /wiki/settings': { ...SETTINGS, lastNight: null } });
    expect((await loadWikiSettings()).lastNight).toBeNull();
    // бэк до N7c: поля нет вовсе — то же «итога не было», а не ошибка
    serve({ 'GET /wiki/settings': SETTINGS });
    expect((await loadWikiSettings()).lastNight).toBeNull();
  });

  it('putWikiNightly_ОтветPUTМолчитОбИтогеНочи_ОнСохраняетсяОтПоследнегоЧтения', async () => {
    // lastNight несёт только GET (N7c); ответ PUT — настройки без него. Молча обнулять
    // прочитанный итог нечем: поле сохраняется от последнего чтения.
    serve({ 'GET /wiki/settings': { ...SETTINGS, lastNight: LAST_NIGHT } });
    await loadWikiSettings();
    serve({ 'PUT /wiki/settings': SETTINGS_ON });
    const r = await putWikiNightly({ enabled: true });
    expect(r.ok).toBe(true);
    expect(getWikiSettingsSnapshot()!.nightly).toEqual(SETTINGS_ON.nightly);
    expect(getWikiSettingsSnapshot()!.lastNight).toEqual(LAST_NIGHT);
  });

  it('неформный 200 настроек — не тихие умолчания, а ошибка', async () => {
    // пустой ночный блок для фронта — не «ночь выключена»: бэк обязан дать целое
    // (WikiSettingsController.cs:212-223); тело вне формы — ответ вне контракта
    for (const body of [{}, { nightly: null }, null]) {
      serve({ 'GET /wiki/settings': body });
      const e = await rejection(() => loadWikiSettings());
      expect(e).toBeInstanceOf(WikiError);
      expect(e.status).toBe(0);
      expect(e.message).toContain('неожиданный ответ');
    }
  });

  it('настройки: ответ прежнего владельца в чужой снимок не пишется', async () => {
    const slow = deferred();
    serve({ 'GET /wiki/settings': slow.promise });
    const p = loadWikiSettings(); // запрос прежнего владельца — резолвится после смены
    userId.mockReturnValue('u2');
    slow.resolve(SETTINGS);
    await p;
    expect(getWikiSettingsSnapshot()).toBeNull();
  });
});

// ─── Пометки чата (Ф1; контракт N1b) ───
// Транспорт чистый: адрес/метод/тело, состояния стора не трогает, ничего не перечитывает.
// 400 бывает и с кодом (значение вне WikiMarks — wiki_mark_invalid, WikiChatsController.cs:
// 207-211), и без кода (неразбираемое тело — ValidationProblemDetails биндинга ASP.NET):
// полагаться на code здесь нельзя, только на статус.

const MARK_URL = '/wiki/chats/s1/mark';

describe('вики: пометки чата (N1b)', () => {
  it('setWikiChatMark идёт по адресу, методу и телу; снятие — {mark:null}', async () => {
    serve({ [`PUT ${MARK_URL}`]: { mark: 'important' } });
    expect(await setWikiChatMark('s1', 'important')).toBe('important');
    expect(req.mock.calls[0][0]).toBe(MARK_URL);
    expect((req.mock.calls[0][1] as RequestInit).method).toBe('PUT');
    expect(sentBody(0)).toEqual({ mark: 'important' });

    serve({ [`PUT ${MARK_URL}`]: { mark: null } });
    expect(await setWikiChatMark('s1', null)).toBeNull();
    expect(sentBody(1)).toEqual({ mark: null });

    // транспорт: ни одного лишнего запроса (список перечитывает экран, не стор)
    expect(urlsFrom(0)).toEqual([MARK_URL, MARK_URL]);
  });

  it('отказы пометки: 404 и 409 — WikiError с кодом; 400 без кода — различим только по статусу', async () => {
    const cases: Array<[number, string | null, unknown]> = [
      [404, 'not_found', { code: 'not_found', message: 'чат не существует' }],
      [409, 'wiki_llm_wiki_off', { code: 'wiki_llm_wiki_off', message: 'пометка не сохранена: функция выключена' }],
      [400, null, undefined], // 400 без поля code (сбой биндинга тела) — на код здесь не опираемся
    ];
    for (const [status, code, body] of cases) {
      serve({ [`PUT ${MARK_URL}`]: httpError(status, body) });
      const e = await rejection(() => setWikiChatMark('s1', 'skip'));
      expect(e).toBeInstanceOf(WikiError);
      expect(e.status).toBe(status);
      expect(e.code).toBe(code);
    }
  });

  it('список отбора несёт mark: important/skip доходят; отсутствие поля — пометки нет', async () => {
    const base = {
      name: null, zone: 'gray', score: null, signals: null, reason: null,
      nothingNewStreak: 0, triagedAt: '2026-11-01T12:00:00+00:00',
      running: false, cursor: 0, chroniclePath: null,
    };
    serve({
      'GET /wiki/chats': {
        items: [
          { ...base, sessionId: 's1', mark: 'skip' },
          { ...base, sessionId: 's2', mark: null },
          { ...base, sessionId: 's3' }, // бэк без поля (до N1b) — то же «пометки нет»
        ],
        counts: {},
      },
    });
    const items = await loadWikiChats();
    expect(items[0].mark).toBe('skip');
    expect(items[1].mark).toBeNull();
    expect(items[2].mark).toBeUndefined();
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
