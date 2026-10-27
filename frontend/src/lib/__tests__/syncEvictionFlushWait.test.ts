import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// F2c-d (доработка 3): гонка «вытеснение ↔ финальная запись mtime» на пути снапшота.
//
// Слушатель вытеснения (sync.ts) АСИНХРОНЕН: он ждёт IDB (сверка, что содержимого
// больше нет) и только потом пишет отметку о вытеснении (_evictedAt) и сбрасывает mtime.
// Пока слушатель не отработал, финальная запись mtime проходить не должна: она увидит
// состояние, в котором содержимое уже снесено, и поставит снесённому пути «свежий»
// mtime — авто-докачка сочтёт файл скачанным, и содержимое пропало бы из офлайна
// навсегда (MAJOR-1 из R-F2c-d2).
//
// Механизм ожидания — flushFileEviction(): он ждёт завершения прохода вытеснения
// (вместе с его слушателем) перед финальной записью mtime.
//
// Тест: фейковый IDB держит ответ слушателя (диапазон содержимого проекта) до ручного
// резолва — вытеснение «зависает» в момент, когда снапшот должен писать финальный mtime.
//
// Мутация: снять вызов flushFileEviction() перед финальной записью mtime → целевой
// тест краснеет (финальная запись проходит, пока вытеснение не отработало).

const fake = vi.hoisted(() => {
  const store = new Map<string, { data: unknown; savedAt: number; usedAt?: number; bytes?: number }>();
  const inRange = (k: string, lo: string, hi: string) => k >= lo && k <= hi;
  // Диапазон, который читает слушатель вытеснения (сверка «что осталось в сторе»).
  // Его ответ держим до ручного резолва — слушатель на этом виснет.
  const HELD_LO = 'offlinefile:A:p1:';
  let releaseHeld: () => void = () => {};
  const heldRequested = new Promise<void>(resolve => { releaseHeld = resolve; });
  let heldRequestedFlag = false;
  return {
    store,
    releaseHeld: () => releaseHeld(),
    heldRequested,
    idbGet: async (k: string) => store.get(k),
    idbSet: async (k: string, v: { data: unknown; savedAt: number; usedAt?: number; bytes?: number }) => { store.set(k, v); return k; },
    idbDelete: async (k: string) => { store.delete(k); return undefined; },
    idbKeys: async () => [...store.keys()],
    idbCountRange: async (lo: string, hi: string) => [...store.keys()].filter(k => inRange(k, lo, hi)).length,
    idbRangeStamps: async (lo: string, hi: string) => {
      if (lo === HELD_LO) {
        heldRequestedFlag = true;
        await heldRequested;
      }
      return [...store.entries()].filter(([k]) => inRange(k, lo, hi))
        .map(([key, v]) => ({ key, savedAt: v.savedAt, usedAt: v.usedAt, bytes: v.bytes }));
    },
    heldRequestedNow: () => heldRequestedFlag,
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

const mtimePaths = () => Object.keys((fake.store.get(MTIMES)?.data ?? {}) as Record<string, string>);
const lostPaths = () => mtimePaths().filter(p => !fake.store.has(CONTENT_KEY(p)));

const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));

function setTightBudget() {
  const fileBytes = JSON.stringify({ content: BODY }).length;
  offline.setFileClassBudget({ maxBytes: 50 * fileBytes, freeMaxBytes: 50 * fileBytes, maxRecords: 100_000 });
}

describe('гонка вытеснения и финальной записи mtime', () => {
  it('финальная запись mtime не проходит, пока не отработал слушатель вытеснения', async () => {
    const tree = Array.from({ length: FILE_COUNT }, (_, i) => entry(`f${i}.txt`, 'M1'));
    apiMock.tree.mockResolvedValue(tree);
    setTightBudget();

    // Снапшот идёт в фоне: он «зависнет» на финальной записи mtime, пока слушатель
    // вытеснения ждёт ответа IDB.
    const run = sync.runOfflineSnapshot();

    // Ждём, пока слушатель вытеснения действительно запросил отложенный ответ
    // (иначе тест прошёл бы вхолостую) и пока скачаны все файлы.
    for (let i = 0; i < 300 && (!fake.heldRequestedNow() || fetchMock.mock.calls.length < FILE_COUNT); i++) {
      await sleep(5);
    }
    expect(fake.heldRequestedNow()).toBe(true);
    expect(fetchMock).toHaveBeenCalledTimes(FILE_COUNT);

    // Пока вытеснение не отработало, финальная запись mtime не должна была пройти:
    // она увидела бы содержимое уже снесённым и поставила бы «свежий» mtime.
    await sleep(50);
    expect(fake.store.has(MTIMES)).toBe(false);
    // И на уровне данных: у пути с записанным mtime содержимое лежит в сторе —
    // слушатель вытеснения не успел сбросить mtime снесённых путей, поэтому такая
    // запись означала бы потерю содержимого из офлайна навсегда.
    expect(lostPaths()).toEqual([]);

    // Отпускаем слушателя — снапшот должен доехать.
    fake.releaseHeld();
    await run;
    await sleep(20);

    // Инвариант после прогона: у пути с записанным mtime содержимое лежит в сторе.
    expect(lostPaths()).toEqual([]);
  });
});
