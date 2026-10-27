import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// F2c-d: вытеснение ПОСРЕДИ снапшота не должно терять синхронизированные файлы.
//
// Сценарий ревьюера: 300 синхронизированных файлов, вытеснение каждые 100 записей.
// Старый код: дифф mtime собирался по каждому скачанному файлу, а в конце прогона
// applyMtimeChanges записывал mtime ВСЕМ путям из диффа — в том числе тем, чьё
// содержимое уже вытеснили. Итог: mtime «свежий», содержимого нет, отметка «скачано»
// снята — авто-докачка файл больше не трогает, пока он не изменится на диске.
//
// Тест воспроизводит именно этот порядок: содержимое пишется через НАСТОЯЩИЙ путь
// request() → passiveWrite (иначе вытеснения не было бы вовсе), а скачивание идёт
// одним снапшотом на 300 файлов.
//
// Мутация: снять фильтр «в дифф идут только пути, чьё содержимое лежит в сторе»
// (sync.ts, финальный applyMtimeChanges) → целевой тест краснеет.

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
      // в класс содержимого и вытеснение случается посреди снапшота.
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
// Тело ответа сервера: JSON с содержимым. Длина фиксирована — по ней считается вес.
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

// Файлы, чьё содержимое лежит в сторе.
const storedPaths = () => [...fake.store.keys()]
  .filter(k => k.startsWith('offlinefile:A:p1:'))
  .map(k => decodeURIComponent(k.slice('offlinefile:A:p1:k::'.length)));

// Пути с записанным mtime.
const mtimePaths = () => Object.keys((fake.store.get(MTIMES)?.data ?? {}) as Record<string, string>);

// Бюджет класса содержимого на 50 файлов: при 300 скачиваниях проход вытеснения
// (он идёт каждые 100 записей) сносит самые давние файлы прогона — ровно тот порядок,
// который воспроизводил ревьюер («300 файлов, вытеснение каждые 100 записей»).
function setTightBudget() {
  const fileBytes = JSON.stringify({ content: BODY }).length;
  offline.setFileClassBudget({ maxBytes: 50 * fileBytes, freeMaxBytes: 50 * fileBytes, maxRecords: 100_000 });
}

describe('вытеснение посреди снапшота', () => {
  it('после второго снапшота потерянных файлов (mtime свежий, содержимого нет) НОЛЬ', async () => {
    const tree = Array.from({ length: FILE_COUNT }, (_, i) => entry(`f${i}.txt`, 'M1'));
    apiMock.tree.mockResolvedValue(tree);
    setTightBudget();

    await sync.runOfflineSnapshot();

    // Вытеснение правда было и было ПОСРЕДИ прогона (иначе тест прошёл бы вхолостую):
    //  - все 300 файлов скачались (содержимое писалось в стор),
    //  - но в сторе осталось сильно меньше — проход вытеснения отнёс самые давние.
    expect(fetchMock).toHaveBeenCalledTimes(FILE_COUNT);
    const storedAfterFirst = storedPaths();
    expect(storedAfterFirst.length).toBeLessThan(FILE_COUNT);
    expect(storedAfterFirst.length).toBeGreaterThan(0);

    // Помеченные к синхронизации файлы лежат в защищённой части класса ('k'):
    // ни одной записи в непомеченной части ('f') быть не должно
    expect([...fake.store.keys()].some(k => k.startsWith('offlinefile:A:p1:f::'))).toBe(false);

    // Инвариант: у каждого пути с записанным mtime содержимое лежит в сторе
    const lostAfterFirst = mtimePaths().filter(p => !fake.store.has(CONTENT_KEY(p)));
    expect(lostAfterFirst).toEqual([]);

    // Второй снапшот: файлы без содержимого докачиваются заново (mtime у них сброшен)
    apiMock.getContent.mockClear();
    await sync.runOfflineSnapshot();

    const lostAfterSecond = mtimePaths().filter(p => !fake.store.has(CONTENT_KEY(p)));
    expect(lostAfterSecond).toEqual([]);
    // Докачивание действительно было: снапшот не прошёл вхолостую по всем 300 файлам
    expect(apiMock.getContent).toHaveBeenCalled();
  });

  it('вытесненный посреди прогона файл не остаётся «свежим» — авто-докачка его вернёт', async () => {
    const tree = Array.from({ length: FILE_COUNT }, (_, i) => entry(`f${i}.txt`, 'M1'));
    apiMock.tree.mockResolvedValue(tree);
    setTightBudget();

    await sync.runOfflineSnapshot();
    const stored = new Set(storedPaths());
    const missing = Array.from({ length: FILE_COUNT }, (_, i) => `f${i}.txt`).filter(p => !stored.has(p));
    // Вытеснение прошло несколько раз (каждые 100 записей) — снесена большая часть
    expect(missing.length).toBeGreaterThan(FILE_COUNT / 2);

    // У вытесненных файлов mtime сброшен слушателем вытеснения
    const mtimes = (fake.store.get(MTIMES)?.data ?? {}) as Record<string, string>;
    for (const p of missing) expect(mtimes[p]).toBeUndefined();

    // Второй снапшот докачивает именно их
    apiMock.getContent.mockClear();
    await sync.runOfflineSnapshot();
    for (const p of missing) {
      expect(apiMock.getContent).toHaveBeenCalledWith('p1', p);
    }
  });
});
