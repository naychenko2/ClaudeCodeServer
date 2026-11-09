import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// F2c-d (доработка 3): путь инкрементального ре-синка проекта (doSyncProjectFiles).
//
// Тот же класс дефекта, что и в снапшоте (см. syncEvictionDuringSnapshot.test.ts):
// вытеснение класса содержимого проходит ПОСРЕДИ прогона и сносит часть только что
// скачанных файлов. Финальная запись mtime не должна ставить «свежий» mtime снесённому
// пути — иначе авто-докачка сочтёт файл скачанным (mtime совпал) и содержимое пропало
// бы из офлайна навсегда.
//
// Путь doSyncProjectFiles (sync.ts) шёл через applyMtimeChanges — без фильтра путей,
// снесённых вытеснением в этом прогоне, — и ни один тест этого не закреплял: тот же
// дефект вернулся бы на этом пути без красного теста.
//
// Мутация: в doSyncProjectFiles заменить applyRunMtimeChanges на applyMtimeChanges
// → целевой тест краснеет.

const fake = vi.hoisted(() => {
  const store = new Map<string, { data: unknown; savedAt: number; usedAt?: number; bytes?: number }>();
  const inRange = (k: string, lo: string, hi: string) => k >= lo && k <= hi;
  return {
    store,
    idbGet: async (k: string) => store.get(k),
    idbSet: async (k: string, v: { data: unknown; savedAt: number; usedAt?: number; bytes?: number }) => { store.set(k, v); return k; },
    idbDelete: async (k: string) => { store.delete(k); return undefined; },
    idbKeys: async () => [...store.keys()],
    idbCountRange: async (lo: string, hi: string) => [...store.keys()].filter(k => inRange(k, lo, hi)).length,
    idbRangeStamps: async (lo: string, hi: string) => [...store.entries()].filter(([k]) => inRange(k, lo, hi))
      .map(([key, v]) => ({ key, savedAt: v.savedAt, usedAt: v.usedAt, bytes: v.bytes })),
  };
});
vi.mock('../idb', () => ({
  idbGet: fake.idbGet, idbSet: fake.idbSet, idbDelete: fake.idbDelete, idbKeys: fake.idbKeys,
  idbCountRange: fake.idbCountRange, idbRangeStamps: fake.idbRangeStamps,
}));

const apiMock = vi.hoisted(() => ({
  listAll: vi.fn(async () => []),
  projectsList: vi.fn(async () => [{ id: 'p1' }]),
  sessionsList: vi.fn(async () => []),
  syncList: vi.fn(async () => []),
  tree: vi.fn(async () => [] as unknown[]),
  getContent: vi.fn(async (_projectId: string, _path: string) => ({ content: 'x' })),
  offline: null as null | typeof import('../offline'),
}));
vi.mock('../api', () => ({
  api: {
    tasks: { listAll: apiMock.listAll },
    projects: { list: apiMock.projectsList },
    sessions: { list: apiMock.sessionsList, getHistory: vi.fn(async () => []) },
    sync: { list: apiMock.syncList },
    files: {
      tree: apiMock.tree,
      // Скачивание идёт через НАСТОЯЩИЙ request(): только так содержимое попадает
      // в класс содержимого и вытеснение случается посреди прогона.
      getContent: (projectId: string, path: string) => {
        apiMock.getContent(projectId, path);
        return apiMock.offline!.request(`/projects/${projectId}/files/content?path=${encodeURIComponent(path)}`);
      },
    },
  },
}));
vi.mock('../subsystems', () => ({ isSubsystemEnabled: () => false }));
vi.mock('../notesOffline', () => ({ warmNote: vi.fn(), drainNotesOutbox: vi.fn() }));
vi.mock('../taskOutbox', () => ({ drainTaskOutbox: vi.fn() }));
vi.mock('../tasks', () => ({}));

function storageStub(init: Record<string, string> = {}) {
  const m = new Map(Object.entries(init));
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, v); },
    removeItem: (k: string) => { m.delete(k); },
  };
}

const FILE_COUNT = 300;
const BODY = 'x'.repeat(1000);
const CONTENT_KEY = (path: string) => `offlinefile:A:p1:k::${encodeURIComponent(path)}`;
const MTIMES = 'synced-mtimes:p1';

const entry = (path: string, modified: string) => ({
  name: path, path, isDirectory: false, modified, isModified: false, synced: 'direct' as const,
});

const jsonResponse = (data: unknown) => ({
  ok: true, status: 200, statusText: 'OK',
  json: async () => data,
  text: async () => JSON.stringify(data),
  headers: new Headers(),
});

let offline: typeof import('../offline');
let sync: typeof import('../sync');
let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(async () => {
  vi.resetModules();
  fake.store.clear();
  apiMock.tree.mockReset();
  apiMock.getContent.mockClear();
  fetchMock = vi.fn(async () => jsonResponse({ content: BODY }));
  vi.stubGlobal('fetch', fetchMock);
  vi.stubGlobal('navigator', { onLine: true });
  vi.stubGlobal('window', { addEventListener: vi.fn(), dispatchEvent: () => true });
  vi.stubGlobal('localStorage', storageStub({ cc_token: 't', cc_user_id: 'A' }));
  vi.stubGlobal('sessionStorage', storageStub());
  offline = await import('../offline');
  apiMock.offline = offline;
  sync = await import('../sync');
});
afterEach(() => { vi.unstubAllGlobals(); });

// Пути, чьё содержимое лежит в сторе.
const storedPaths = () => new Set([...fake.store.keys()]
  .filter(k => k.startsWith('offlinefile:A:p1:'))
  .map(k => decodeURIComponent(k.slice('offlinefile:A:p1:k::'.length))));

// Пути с записанным mtime.
const mtimePaths = () => Object.keys((fake.store.get(MTIMES)?.data ?? {}) as Record<string, string>);

// Пути, у которых mtime есть, а содержимого нет — «свежий, но пустой» (MAJOR-1).
const lostPaths = () => mtimePaths().filter(p => !fake.store.has(CONTENT_KEY(p)));

const tick = (ms = 0) => new Promise(r => setTimeout(r, ms));

// Бюджет класса содержимого на 50 файлов: при 300 скачиваниях проход вытеснения
// (он идёт каждые 100 записей) сносит самые давние файлы прогона.
function setTightBudget() {
  const fileBytes = JSON.stringify({ content: BODY }).length;
  offline.setFileClassBudget({ maxBytes: 50 * fileBytes, freeMaxBytes: 50 * fileBytes, maxRecords: 100_000 });
}

describe('вытеснение посреди инкрементального ре-синка проекта', () => {
  it('после второго прогона потерянных файлов (mtime свежий, содержимого нет) НОЛЬ', async () => {
    const tree = Array.from({ length: FILE_COUNT }, (_, i) => entry(`f${i}.txt`, 'M1'));
    apiMock.tree.mockResolvedValue(tree);
    setTightBudget();

    await sync.syncProjectFiles('p1');

    // Вытеснение правда было и было ПОСРЕДИ прогона: файлов скачалось 300, а в сторе
    // осталось сильно меньше (иначе тест прошёл бы вхолостую).
    expect(fetchMock).toHaveBeenCalledTimes(FILE_COUNT);
    const storedAfterFirst = storedPaths();
    expect(storedAfterFirst.size).toBeLessThan(FILE_COUNT);
    expect(storedAfterFirst.size).toBeGreaterThan(0);
    const missingAfterFirst = Array.from({ length: FILE_COUNT }, (_, i) => `f${i}.txt`)
      .filter(p => !storedAfterFirst.has(p));
    expect(missingAfterFirst.length).toBeGreaterThan(FILE_COUNT / 2);

    // Помеченные к синхронизации файлы лежат в защищённой части класса ('k').
    expect([...fake.store.keys()].some(k => k.startsWith('offlinefile:A:p1:f::'))).toBe(false);

    // Слушатель вытеснения асинхронен: даём ему дописать сброс mtime перед проверкой.
    await tick();
    await tick();

    // Инвариант: у каждого пути с записанным mtime содержимое лежит в сторе.
    // С вытеснением посреди прогона это нарушается, если финальная запись mtime не
    // вычёркивает пути, снесённые вытеснением (applyRunMtimeChanges).
    expect(lostPaths()).toEqual([]);

    // Второй прогон: файлы без содержимого докачиваются заново (mtime у них сброшен).
    apiMock.getContent.mockClear();
    await sync.syncProjectFiles('p1');
    await tick();
    await tick();

    expect(lostPaths()).toEqual([]);
    // Докачивание действительно было: не вытесненный файл заново не качался,
    // а вытесненные — качались.
    expect(apiMock.getContent).toHaveBeenCalledWith('p1', missingAfterFirst[0]);
  });
});
