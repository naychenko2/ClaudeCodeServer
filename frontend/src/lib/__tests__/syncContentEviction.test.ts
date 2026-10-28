import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// Связка «вытеснение содержимого ↔ авто-докачка» (F2c-d).
// Содержимое файлов живёт в отдельном классе ('offlinefile:') со своим бюджетом по весу;
// вытеснение снимает запись содержимого из стора, а mtime-запись лежит вне диапазона
// вытеснения (ключ synced-mtimes:<pid>) и сама вытеснением не трогается: без сброса
// авто-снапшот счёл бы файл свежим (mtime совпал) и больше его не докачал — содержимое
// пропало бы из офлайна навсегда.
//
// idb заменён Map-хранилищем с теми же диапазонными операциями, что у настоящего:
// диапазон, который offline.ts передаёт в стор, — часть проверки (вытеснение бьёт
// по ключам владельца, а не по всему стору).

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
  getContent: vi.fn(async () => ({ content: 'данные' })),
}));
vi.mock('../api', () => ({
  api: {
    tasks: { listAll: apiMock.listAll },
    projects: { list: apiMock.projectsList },
    sessions: { list: apiMock.sessionsList, getHistory: vi.fn(async () => []) },
    sync: { list: apiMock.syncList },
    files: { tree: apiMock.tree, getContent: apiMock.getContent },
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

// Ключ содержимого в новом классе: offlinefile:<owner>:<pid>:<k|f>:<extra>:<путь>.
// Помеченные к синхронизации файлы лежат под 'k', непомеченные — под 'f'.
const CONTENT = (path: string, keep = true) =>
  `offlinefile:A:p1:${keep ? 'k' : 'f'}::${encodeURIComponent(path)}`;
const MTIMES = 'synced-mtimes:p1';

const entry = (path: string, modified: string) => ({
  name: path, path, isDirectory: false, modified, isModified: false, synced: 'direct' as const,
});

let offline: typeof import('../offline');
let sync: typeof import('../sync');

beforeEach(async () => {
  vi.resetModules();
  fake.store.clear();
  apiMock.tree.mockReset();
  apiMock.getContent.mockReset();
  apiMock.getContent.mockResolvedValue({ content: 'данные' });
  vi.stubGlobal('navigator', { onLine: true });
  vi.stubGlobal('window', { addEventListener: vi.fn(), dispatchEvent: () => true });
  vi.stubGlobal('localStorage', storageStub({ cc_token: 't', cc_user_id: 'A' }));
  vi.stubGlobal('sessionStorage', storageStub());
  offline = await import('../offline');
  sync = await import('../sync');
});
afterEach(() => { vi.unstubAllGlobals(); });

describe('вытеснение содержимого и авто-докачка', () => {
  it('вытесненный файл авто-снапшот докачивает заново, а невытесненный — нет', async () => {
    // Проект синхронизирован: оба файла скачаны, mtime записан.
    fake.store.set(MTIMES, { data: { 'f.txt': 'M1', 'keep.txt': 'M1' }, savedAt: 1 });
    fake.store.set(CONTENT('f.txt'), { data: { content: 'f' }, savedAt: 10, usedAt: 1, bytes: 100 });
    fake.store.set(CONTENT('keep.txt'), { data: { content: 'k' }, savedAt: 10, usedAt: 999_999, bytes: 100 });
    // Бюджет класса содержимого — на один файл: более давний (f.txt) вытесняется.
    offline.setFileClassBudget({ maxBytes: 150, freeMaxBytes: 150, maxRecords: 100 });

    await offline.runPassiveMaintenance();

    // Содержимое вытесненного файла действительно снято (иначе тест прошёл бы вхолостую)
    expect(fake.store.has(CONTENT('f.txt'))).toBe(false);
    expect(fake.store.has(CONTENT('keep.txt'))).toBe(true);
    // mtime вытесненного пути сброшен, у живого — нет (иначе лишняя перекачка)
    const mtimesAfterEvict = { ...(fake.store.get(MTIMES)!.data as Record<string, string>) };
    expect('f.txt' in mtimesAfterEvict).toBe(false);
    expect(mtimesAfterEvict['keep.txt']).toBe('M1');

    apiMock.tree.mockResolvedValue([entry('f.txt', 'M1'), entry('keep.txt', 'M1')]);
    await sync.runOfflineSnapshot();

    // Вытесненный файл докачан заново; невытесненный — не перекачивался
    expect(apiMock.getContent).toHaveBeenCalledWith('p1', 'f.txt');
    expect(apiMock.getContent).not.toHaveBeenCalledWith('p1', 'keep.txt');
  });

  it('вытеснение содержимого сбрасывает mtime — авто-снапшот докачивает файл заново', async () => {
    fake.store.set(MTIMES, { data: { 'f.txt': 'M1' }, savedAt: 1 });
    fake.store.set(CONTENT('f.txt'), { data: { content: 'f' }, savedAt: 10, usedAt: 1, bytes: 100 });
    // Бюджет меньше содержимого: проход вытеснит файл из класса содержимого.
    offline.setFileClassBudget({ maxBytes: 50, freeMaxBytes: 50, maxRecords: 100 });
    await offline.runPassiveMaintenance();

    // Содержимое снесено, mtime сброшен слушателем вытеснения
    expect(fake.store.has(CONTENT('f.txt'))).toBe(false);
    const mtimes = { ...(fake.store.get(MTIMES)!.data as Record<string, string>) };
    expect('f.txt' in mtimes).toBe(false);

    // Снапшот докачивает файл заново (mtime сброшен → файл считается новым)
    apiMock.tree.mockResolvedValue([entry('f.txt', 'M1')]);
    await sync.runOfflineSnapshot();
    expect(apiMock.getContent).toHaveBeenCalledWith('p1', 'f.txt');
  });

  it('файл, изменившийся на диске, докачивается как раньше (сброс mtime не мешает)', async () => {
    fake.store.set(MTIMES, { data: { 'f.txt': 'M1' }, savedAt: 1 });
    fake.store.set(CONTENT('f.txt'), { data: { content: 'f' }, savedAt: 10, usedAt: 999_999, bytes: 100 });
    offline.setFileClassBudget({ maxBytes: 1000, freeMaxBytes: 1000, maxRecords: 100 });

    apiMock.tree.mockResolvedValue([entry('f.txt', 'M2')]);
    await sync.runOfflineSnapshot();

    expect(apiMock.getContent).toHaveBeenCalledWith('p1', 'f.txt');
  });

  it('вытеснение содержимого одного проекта не трогает mtime другого', async () => {
    fake.store.set(MTIMES, { data: { 'f.txt': 'M1' }, savedAt: 1 });
    fake.store.set('synced-mtimes:p2', { data: { 'g.txt': 'M1' }, savedAt: 1 });
    fake.store.set(CONTENT('f.txt'), { data: { content: 'f' }, savedAt: 10, usedAt: 1, bytes: 100 });
    offline.setFileClassBudget({ maxBytes: 50, freeMaxBytes: 50, maxRecords: 100 });
    await offline.runPassiveMaintenance();

    // Содержимое p1 вытеснено…
    expect(fake.store.has(CONTENT('f.txt'))).toBe(false);
    // …а mtime чужого проекта не задет
    expect((fake.store.get('synced-mtimes:p2')!.data as Record<string, string>)['g.txt']).toBe('M1');
  });
});
