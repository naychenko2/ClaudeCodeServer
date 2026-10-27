import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// F2c-f: потолок пассивного кэша на пути записи МИМО счётчика.
//
// Проблема (замер QA3): потолок 200 записей на владельца ограничивал
// установившееся состояние, а не пик. Обслуживание тикало от записей, прошедших
// через request(), а листинги дерева писал снапшот прямо в IDB — мимо счётчика.
// Сразу после авто-снапшота диапазон владельца стоял на 957 записей (залп
// ~700–790 листингов по 55 проектам), после прохода обслуживания — ровно 180.
//
// Доработка после ревью R-F2c-f (три находки major):
//   1. инвариант «в сторе не больше 200» держался только внутри одной вкладки
//      и сбивался гонкой (запись, начатая во время ужима, выпадала из оценки);
//   2. тест залпа проверял финальное состояние, а не пик, и число записей было
//      кратно 100 — периодический проход попадал ровно на потолок;
//   3. request() ждал и проход обслуживания, и запись в стор.
//
// Проба: снапшот пишет листинги МИМО request() (как это делает боевой код) —
// после прогона диапазон владельца не превышает потолок, и два снапшота подряд
// не растят его сверх потолка.
//
// Мутации:
//   - снять учёт листингов в счётчике диапазона (writePassiveRangeEntry
//     перестаёт звать ensureRangeBudget) → целевой тест краснеет;
//   - снять `_rangeRecords++` → краснеет ТЕСТ ПИКА ЗАЛПА;
//   - вернуть в write-back устаревший захват pending → краснеет ТЕСТ ГОНКИ.
//
// idb заменён Map-хранилищем с теми же диапазонными операциями, что у настоящего:
// диапазон, который offline.ts передаёт в стор, — часть проверки. Стор считает
// пик размера диапазона владельца (максимум по всем записям и удалениям).

const fake = vi.hoisted(() => {
  const store = new Map<string, { data: unknown; savedAt: number; usedAt?: number; bytes?: number }>();
  const calls = { count: 0, cursor: 0 };
  const inRange = (k: string, lo: string, hi: string) => k >= lo && k <= hi;
  const ownerSize = () => [...store.keys()].filter(k => k.startsWith('offline:A:')).length;
  // Пик диапазона владельца: максимум размера по всем операциям со стором
  const state = { peak: 0, gate: null as Promise<void> | null, gateOpen: null as (() => void) | null, countGate: null as Promise<void> | null };
  const track = () => { state.peak = Math.max(state.peak, ownerSize()); };
  return {
    store, calls, state,
    idbGet: async (k: string) => store.get(k),
    idbSet: async (k: string, v: { data: unknown; savedAt: number; usedAt?: number; bytes?: number }) => { store.set(k, v); track(); return k; },
    idbDelete: async (k: string) => { store.delete(k); track(); return undefined; },
    idbKeys: async () => [...store.keys()],
    idbCountRange: async (lo: string, hi: string) => {
      calls.count++;
      // Гейт на подсчёт: держим проход на месте, чтобы он не пересчитал оценку
      if (state.countGate) await state.countGate;
      return [...store.keys()].filter(k => inRange(k, lo, hi)).length;
    },
    idbRangeStamps: async (lo: string, hi: string) => {
      calls.cursor++;
      // Гейт: держим проход внутри сканирования диапазона, чтобы тест мог
      // начать записи ПОСЛЕДУЮЩИЕ шагу ужима.
      if (state.gate) {
        const g = state.gate;
        const open = state.gateOpen;
        state.gateOpen = null;
        open?.();
        await g;
      }
      return [...store.entries()].filter(([k]) => inRange(k, lo, hi))
        .map(([key, v]) => ({ key, savedAt: v.savedAt, usedAt: v.usedAt, bytes: v.bytes }));
    },
  };
});
vi.mock('../idb', () => ({
  idbGet: fake.idbGet, idbSet: fake.idbSet, idbDelete: fake.idbDelete, idbKeys: fake.idbKeys,
  idbCountRange: fake.idbCountRange, idbRangeStamps: fake.idbRangeStamps,
}));

const apiMock = vi.hoisted(() => ({
  listAll: vi.fn(async () => []),
  projectsList: vi.fn(async () => [] as { id: string }[]),
  sessionsList: vi.fn(async () => []),
  syncList: vi.fn(async () => []),
  tree: vi.fn(async (_projectId: string) => [] as unknown[]),
  getContent: vi.fn(async () => ({ content: 'x' })),
  offline: null as null | typeof import('../offline'),
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
    get length() { return m.size; },
    key: (i: number) => [...m.keys()][i] ?? null,
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, v); },
    removeItem: (k: string) => { m.delete(k); },
  };
}

const PROJECTS = ['p1', 'p2', 'p3'];
// Папок в проекте: листингов на проект = 1 (корень) + DIRS
const DIRS = 80;

const treeFor = () => {
  const entries: unknown[] = [];
  for (let i = 0; i < DIRS; i++) {
    entries.push({ name: `d${i}`, path: `d${i}`, isDirectory: true, modified: 'M1', isModified: false });
    entries.push({ name: `f${i}.txt`, path: `d${i}/f${i}.txt`, isDirectory: false, modified: 'M1', isModified: false });
  }
  return entries;
};

// Записи общего класса пассивного кэша владельца A (листинги дерева лежат тут)
const ownKeys = () => [...fake.store.keys()].filter(k => k.startsWith('offline:A:'));

// Заполнить диапазон владельца мимо учёта модуля (как будто он уже полон)
const seedOwn = (n: number) => {
  for (let i = 0; i < n; i++) fake.store.set(`offline:A:/seed/${i}`, { data: { i }, savedAt: 1000 + i, usedAt: 1000 + i });
};

// Гейт на сканирование диапазона: проход замирает внутри idbRangeStamps
function gateScan() {
  let open!: () => void;
  let release!: () => void;
  const hit = new Promise<void>(r => { open = r; });
  fake.state.gate = new Promise<void>(r => { release = r; });
  fake.state.gateOpen = open;
  return {
    hit,
    release: () => { fake.state.gate = null; release(); },
  };
}

// Гейт на подсчёт диапазона: проход замирает перед пересчётом оценки
function gateCount() {
  let release!: () => void;
  fake.state.countGate = new Promise<void>(r => { release = r; });
  return { release: () => { fake.state.countGate = null; release(); } };
}

let offline: typeof import('../offline');
let sync: typeof import('../sync');
let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(async () => {
  vi.resetModules();
  fake.store.clear();
  fake.calls.count = 0; fake.calls.cursor = 0;
  fake.state.peak = 0; fake.state.gate = null; fake.state.gateOpen = null; fake.state.countGate = null;
  apiMock.tree.mockImplementation(async () => treeFor());
  apiMock.projectsList.mockResolvedValue(PROJECTS.map(id => ({ id })));
  fetchMock = vi.fn(async () => ({
    ok: true, status: 200, statusText: 'OK',
    json: async () => ({}), text: async () => '{}', headers: new Headers(),
  }));
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

describe('потолок пассивного кэша на пути записи мимо счётчика', () => {
  it('запись мимо request() не обходит потолок: диапазон владельца не выше потолка', async () => {
    const listings = PROJECTS.length * (DIRS + 1);
    expect(listings).toBeGreaterThan(offline.PASSIVE_CACHE_MAX); // залп заведомо больше потолка

    await sync.runOfflineSnapshot();

    // Листинги писались мимо request() — потолок обязан держать и их
    expect(ownKeys().length).toBeLessThanOrEqual(offline.PASSIVE_CACHE_MAX);
  });

  it('два снапшота подряд не растят диапазон сверх потолка', async () => {
    await sync.runOfflineSnapshot();
    const afterFirst = ownKeys().length;
    expect(afterFirst).toBeLessThanOrEqual(offline.PASSIVE_CACHE_MAX);

    await sync.runOfflineSnapshot();
    expect(ownKeys().length).toBeLessThanOrEqual(offline.PASSIVE_CACHE_MAX);
  });

  it('офлайн-дерево продолжает работать: листинги на месте и читаются', async () => {
    await sync.runOfflineSnapshot();

    // Свежий проект (записан последним) — его листинги уцелели
    const last = PROJECTS[PROJECTS.length - 1];
    const rootKey = `offline:A:/projects/${last}/files?path=`;
    expect(fake.store.has(rootKey)).toBe(true);
    const entries = fake.store.get(rootKey)!.data as { path: string; isDirectory: boolean }[];
    expect(Array.isArray(entries)).toBe(true);
    expect(entries.length).toBe(DIRS); // в корне — папки проекта

    // Листинг папки тоже на месте и читается
    const dirKey = `offline:A:/projects/${last}/files?path=d0`;
    expect(fake.store.has(dirKey)).toBe(true);
    expect((fake.store.get(dirKey)!.data as { path: string }[])[0].path).toBe('d0/f0.txt');
  });
});

describe('пик залпа', () => {
  // Залп НЕ кратен EVICT_AFTER_WRITES (100): иначе периодический проход попадает
  // ровно на потолок и проверка ничего не говорит о счётчике.
  it('залп из 237 записей не превышает потолок на пике (не только в конце)', async () => {
    // Диапазон заполнен наполовину — как после снапшота: залп доводит его до потолка
    seedOwn(150);
    const BURST = 237;

    await Promise.all(Array.from({ length: BURST }, (_, i) => offline.request(`/g/${i}`)));
    await offline.flushPassiveWrites();

    // Пик считает стор: максимум размера диапазона по всем записям и удалениям
    expect(fake.state.peak).toBeLessThanOrEqual(offline.PASSIVE_CACHE_MAX);
    expect(ownKeys().length).toBeLessThanOrEqual(offline.PASSIVE_CACHE_MAX);
  });
});

describe('гонка: запись, начатая во время ужима', () => {
  it('запись, начатая во время ужима, не выпадает из оценки', async () => {
    // Диапазон почти полон: до потолка — одна запись
    seedOwn(199);

    // Запись, которая запускает вытеснение
    const first = offline.writePassiveRangeEntry('offline:A:/new', { i: 'first' });
    // Ждём, пока проход дойдёт до сканирования диапазона
    const scan = gateScan();
    await scan.hit;
    // Следующий проход (он чинит оценку пересчётом по стору) держим на гейте:
    // смотрим оценку сразу после ужима, какой она и должна быть.
    const count = gateCount();

    // Пока идёт ужим, стартуют записи — они обязаны попасть в оценку, иначе
    // диапазон уйдёт выше потолка до следующего прохода.
    const during = Array.from({ length: 15 }, (_, i) => offline.writePassiveRangeEntry(`offline:A:/d/${i}`, { i }));
    scan.release();
    await Promise.all([first, ...during]);

    // Оценка не меньше истины: места, занятые записями, начатыми во время ужима,
    // в ней остались
    expect(offline.rangeRecordsEstimate()).toBeGreaterThanOrEqual(ownKeys().length);
    expect(fake.state.peak).toBeLessThanOrEqual(offline.PASSIVE_CACHE_MAX);
    count.release();
  });
});

describe('две вкладки', () => {
  it('общий диапазон не выше потолка, когда пишут две вкладки', async () => {
    // Второй экземпляр модуля — как вторая вкладка: тот же стор, тот же origin
    vi.resetModules();
    const other = await import('../offline');

    for (let i = 0; i < 120; i++) {
      await offline.writePassiveRangeEntry(`offline:A:/a/${i}`, { i });
      await other.writePassiveRangeEntry(`offline:A:/b/${i}`, { i });
      expect(ownKeys().length).toBeLessThanOrEqual(offline.PASSIVE_CACHE_MAX);
    }
  });
});

describe('ответ сети не ждёт обслуживания', () => {
  it('request() возвращается, не дожидаясь прохода обслуживания', async () => {
    // Первый GET после входа запускает проход обслуживания — он не должен
    // держать ответ сети.
    const g = gateScan();

    // Ответ сети должен прийти, пока проход обслуживания ещё стоит в гейте
    const timeout = new Promise<'timeout'>(r => setTimeout(() => r('timeout'), 500));
    const res = await Promise.race([offline.request('/g/first'), timeout]);

    expect(res).not.toBe('timeout');
    expect(res).toEqual({});
    // Проход всё ещё стоит в гейте — ответ сети уже получен
    g.release();
    await offline.flushPassiveWrites();
    expect(ownKeys()).toContain('offline:A:/g/first');
  });
});
