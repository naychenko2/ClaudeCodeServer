// Слой данных вкладки «Стенды» (Ф0): гонка выбора узла в поллере (ответ старого узла
// не должен перезаписать новый снимок), бракование устаревших ответов по поколению,
// отказ fetch с сохранением прежних данных, точные URL ручек hardware/hardwareFarm и
// новые поля контракта (плитка «Плата» с peripheralC, ключи cpu-0/cpu-1/vrm-board,
// сводка фермы с полем farmNodes).
//
// Окружение node (без jsdom): поллер — прямыми вызовами с фейковыми таймерами и
// vi.fn вместо fetch (паттерн hardware.test.ts:104); URL — замена fetch и idb-обёртки
// (паттерн localModelApi.test.ts).

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import type { HardwareFarm, HardwareReport } from './hardwareTypes';
import { createHardwarePoller } from './useLocalModelHardware';

// Промежуток, чтобы все обещания, поставленные в очередь, отработали.
const flush = () => new Promise<void>(r => setTimeout(r, 0));

// === Фикстуры: отчёт узла и сводка фермы (форма — record'ы бэкенда, camelCase) ===

function nodeReport(key: string, title: string, over: Partial<HardwareReport> = {}): HardwareReport {
  return {
    at: 1700000000000,
    status: 'ok',
    level: 'ok',
    gpus: [],
    nodes: [],
    caseFans: [],
    series: [],
    stepMs: 10000,
    node: { key, title },
    uptimeS: 12345,
    cpuLoadPerc: 8,
    ...over,
  };
}

// Отчёт узла с двумя сокетами (ADR-029 «Узел — набор устройств»): ключи плиток
// непрозрачны (cpu-0/cpu-1, vrm-0/vrm-1, vrm-board), плитка «Плата» с peripheralC.
// Никаких приведений as: то, что фикстура компилируется как HardwareReport, и есть
// проверка типа (её держит tsc -b).
function multiSocketReport(): HardwareReport {
  return {
    at: 1700000000000,
    status: 'ok',
    level: 'warn',
    gpus: [],
    nodes: [
      { key: 'cpu-0', kind: 'cpu', name: 'Процессор 1', tempC: 74, sensor: 'ok', level: 'ok', warnC: 85, dangerC: 95, detail: { ccdC: [70] }, spark15m: [] },
      { key: 'cpu-1', kind: 'cpu', name: 'Процессор 2', tempC: 88, sensor: 'ok', level: 'warn', warnC: 85, dangerC: 95, detail: { ccdC: [81] }, spark15m: [] },
      { key: 'vrm-0', kind: 'vrm', name: 'Питание процессора 1', tempC: 55, sensor: 'ok', level: 'ok', warnC: 90, dangerC: 105, detail: {}, spark15m: [] },
      { key: 'vrm-1', kind: 'vrm', name: 'Питание процессора 2', tempC: 61, sensor: 'ok', level: 'ok', warnC: 90, dangerC: 105, detail: {}, spark15m: [] },
      { key: 'vrm-board', kind: 'vrm', name: 'Питание (плата)', tempC: 66, sensor: 'ok', level: 'ok', warnC: 90, dangerC: 105, detail: {}, spark15m: [] },
      { key: 'board', kind: 'board', name: 'Плата', tempC: 41, sensor: 'ok', level: 'ok', warnC: 80, dangerC: 85, detail: { peripheralC: 37 }, spark15m: [] },
    ],
    caseFans: [],
    series: [],
    stepMs: 10000,
    node: { key: 'worker', title: 'Worker' },
    uptimeS: 987654,
    cpuLoadPerc: 12,
  };
}

// Сводка фермы: поле farmNodes (не nodes); нештатный узел (down) не красится —
// level 'ok', о нештатности говорят status и since.
function farmFixture(): HardwareFarm {
  return {
    at: 1700000000000,
    level: 'warn',
    farmNodes: [
      {
        key: 'worker', title: 'Worker', roles: ['text', 'media'], engines: ['main'], media: true,
        status: 'ok', since: null, level: 'warn',
        hottest: { label: 'Процессор 2', tempC: 88, level: 'warn' },
        gpus: [{ uuid: 'gpu-a', memTempC: 81, level: 'ok', powerCap: true, thermal: false }],
        uptimeS: 987654, cpuLoadPerc: 12, ramUsedPerc: 44,
      },
      {
        key: 'asus', title: 'ASUS', roles: ['text'], engines: ['gemma'], media: false,
        status: 'down', since: 1699999000000, level: 'ok',
        hottest: null, gpus: [],
        uptimeS: null, cpuLoadPerc: null, ramUsedPerc: null,
      },
    ],
  };
}

// === 1. Гонка выбора узла: ответ принимается только последнего поколения ===

describe('createHardwarePoller — гонка смены узла (поколение запросов)', () => {
  it('узел A → B: ответ A, разрешившийся ПОСЛЕ ответа B, в снимок не попадает', async () => {
    let resolveA!: (r: HardwareReport) => void;
    const pendingA = new Promise<HardwareReport>(r => { resolveA = r; });
    const fetchFn = vi.fn()
      .mockImplementationOnce(() => pendingA) // первый снимок — узел A, в полёте
      .mockImplementationOnce(() => Promise.resolve(nodeReport('b', 'Второй'))); // после restart — узел B

    const p = createHardwarePoller<HardwareReport>(fetchFn, 2000, () => 1, vi.fn());
    p.setActive(true, true); // запрос A (поколение 1) начат
    p.restart(); // выбор узла B: поколение поднято, немедленный снимок
    await flush(); // ответ B разрешён — он в снимке

    resolveA(nodeReport('a', 'Первый')); // старый ответ приходит позже нового
    await flush();

    // Мутация «убрать проверку поколения» (if (myGen !== gen) в .then) краснеет здесь:
    // без неё A перезаписал бы B.
    expect(p.getSnapshot().data?.node?.key).toBe('b');
    p.dispose();
  });

  it('смена узла: data сбрасывается в null, летящий ответ старого узла не возвращается', async () => {
    const fetchFn = vi.fn().mockImplementation(() => new Promise<HardwareReport>(() => {})); // никогда не разрешается
    const p = createHardwarePoller<HardwareReport>(fetchFn, 2000, () => 1, vi.fn());
    p.setActive(true, true); // запрос старого узла в полёте
    p.restart(); // смена узла: снимок сброшен, новый запрос в полёте
    expect(p.getSnapshot().data).toBeNull();
    expect(p.getSnapshot().loading).toBe(true);
    p.dispose();
  });

  it('снимок по таймеру и refetch параллельно: ответ более раннего запроса не перетирает более поздний', async () => {
    let resolveOld!: (r: HardwareReport) => void;
    const pendingOld = new Promise<HardwareReport>(r => { resolveOld = r; });
    const fetchFn = vi.fn()
      .mockResolvedValueOnce(nodeReport('init', 'Init'))
      .mockImplementationOnce(() => pendingOld) // refetch (смена окна) — запрос в полёте
      .mockResolvedValueOnce(nodeReport('tick', 'Tick')); // следующий тик — более новый запрос

    let tick!: () => void;
    const p = createHardwarePoller<HardwareReport>(fetchFn, 2000, (fn) => { tick = fn; return 1; }, vi.fn());
    p.setActive(true, true);
    await flush(); // init в снимке
    p.refetch(); // старый запрос в полёте
    tick(); // тик ставит более новый
    await flush(); // tick разрешён
    resolveOld(nodeReport('old', 'Old'));
    await flush();
    expect(p.getSnapshot().data?.node?.key).toBe('tick');
    p.dispose();
  });
});

// === 2. Отказ опроса: прежние данные остаются, исключение наружу не уходит ===

describe('createHardwarePoller — отказ fetch', () => {
  it('отклонённый fetch: loading=false, прежние данные остаются', async () => {
    const fetchFn = vi.fn()
      .mockResolvedValueOnce(nodeReport('a', 'A'))
      .mockRejectedValueOnce(new Error('нет связи'));
    const p = createHardwarePoller<HardwareReport>(fetchFn, 2000, () => 1, vi.fn());
    p.setActive(true, true);
    await flush();
    expect(p.getSnapshot().data?.node?.key).toBe('a');
    expect(p.getSnapshot().loading).toBe(false);

    p.refetch();
    expect(p.getSnapshot().loading).toBe(true); // запрос в полёте
    await flush(); // он отклонён — наружу ничего не вышло, тест не упал

    expect(p.getSnapshot().loading).toBe(false);
    expect(p.getSnapshot().data?.node?.key).toBe('a');
    p.dispose();
  });
});

// === 3. Новый контракт: ключи плиток и поля проходят через типы без приведений ===

describe('новый контракт (Ш3a/Ш3b): фикстуры через типы без as', () => {
  it('два сокета: cpu-0/cpu-1, vrm-board и board с peripheralC — поля типа', () => {
    const r = multiSocketReport();
    expect(r.nodes.map(n => n.key)).toEqual(['cpu-0', 'cpu-1', 'vrm-0', 'vrm-1', 'vrm-board', 'board']);
    const board = r.nodes.find(n => n.kind === 'board')!;
    expect(board.kind).toBe('board');
    expect(board.detail.peripheralC).toBe(37);
    // Ветвление по kind, а не по ключу: две плитки одного kind — норма.
    expect(r.nodes.filter(n => n.kind === 'cpu').map(n => n.key)).toEqual(['cpu-0', 'cpu-1']);
  });

  it('отчёт с плиткой «Плата» доходит до снимка через поллер', async () => {
    const p = createHardwarePoller<HardwareReport>(async () => multiSocketReport(), 2000, () => 1, vi.fn());
    p.setActive(true, true);
    await flush();
    expect(p.getSnapshot().data?.nodes.some(n => n.kind === 'board' && n.detail.peripheralC === 37)).toBe(true);
    p.dispose();
  });

  it('сводка фермы: список — поле farmNodes; нештатный узел не красится', () => {
    const farm = farmFixture();
    expect(farm.farmNodes.map(n => n.key)).toEqual(['worker', 'asus']);
    const down = farm.farmNodes.find(n => n.key === 'asus')!;
    expect(down.status).toBe('down');
    // Нештатность — не тревога: уровень 'ok', о ней говорят status и since.
    expect(down.level).toBe('ok');
    expect(down.since).toBe(1699999000000);
    expect(down.hottest).toBeNull();
  });

  it('сводка фермы доходит до снимка тем же поллером', async () => {
    const p = createHardwarePoller<HardwareFarm>(async () => farmFixture(), 2000, () => 1, vi.fn());
    p.setActive(true, true);
    await flush();
    expect(p.getSnapshot().data?.farmNodes[0].title).toBe('Worker');
    p.dispose();
  });
});

// === 4. Точные URL ручек (паттерн localModelApi.test.ts) ===

const { idbGet, idbSet } = vi.hoisted(() => ({
  idbGet: vi.fn(),
  idbSet: vi.fn(),
}));
vi.mock('../../lib/idb', () => ({ idbGet, idbSet }));

function storageStub() {
  const m = new Map<string, string>([['cc_token', 'tok']]);
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, v); },
    removeItem: (k: string) => { m.delete(k); },
  };
}

function jsonResponse(data: unknown, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: `HTTP ${status}`,
    json: async () => data,
    text: async () => JSON.stringify(data),
  };
}

describe('api.localModel.hardware / hardwareFarm — точные URL', () => {
  let fetchMock: ReturnType<typeof vi.fn>;
  let api: typeof import('../../lib/api').api;

  beforeEach(async () => {
    // Состояние offline.ts (флаг _online) — модульное, поэтому свежий импорт на каждый тест
    vi.resetModules();
    idbGet.mockReset();
    idbSet.mockReset();
    idbGet.mockResolvedValue(undefined);
    idbSet.mockResolvedValue('key');
    fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    vi.stubGlobal('navigator', { onLine: true });
    vi.stubGlobal('window', { addEventListener: vi.fn(), dispatchEvent: () => true });
    vi.stubGlobal('localStorage', storageStub());
    vi.stubGlobal('sessionStorage', storageStub());
    api = (await import('../../lib/api')).api;
  });

  afterEach(() => { vi.unstubAllGlobals(); });

  it('без node URL прежний: ровно /local-model/hardware?window=… (регрессия вкладки «Железо»)', async () => {
    const dto = nodeReport('worker', 'Worker');
    fetchMock.mockResolvedValue(jsonResponse(dto));

    const data = await api.localModel.hardware('15m');

    // request() сам добавляет префикс /api — двойного не должно быть
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0][0]).toBe('/api/local-model/hardware?window=15m');
    expect(data).toEqual(dto);
  });

  it('node идёт в query после window', async () => {
    fetchMock.mockResolvedValue(jsonResponse(nodeReport('ai', 'Стенд AI')));

    await api.localModel.hardware('15m', 'ai');
    expect(fetchMock.mock.calls[0][0]).toBe('/api/local-model/hardware?window=15m&node=ai');

    fetchMock.mockClear();
    await api.localModel.hardware('1h', 'ai');
    expect(fetchMock.mock.calls[0][0]).toBe('/api/local-model/hardware?window=1h&node=ai');
  });

  it('node кодируется: ключ с пробелом и слэшем не рвёт query', async () => {
    fetchMock.mockResolvedValue(jsonResponse(nodeReport('a b/c', 'x')));

    await api.localModel.hardware('15m', 'a b/c');
    expect(fetchMock.mock.calls[0][0]).toBe('/api/local-model/hardware?window=15m&node=a%20b%2Fc');
  });

  it('hardwareFarm — ровно /local-model/hardware/farm, сводка возвращается как есть', async () => {
    const farm = farmFixture();
    fetchMock.mockResolvedValue(jsonResponse(farm));

    const data = await api.localModel.hardwareFarm();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0][0]).toBe('/api/local-model/hardware/farm');
    expect(data).toEqual(farm);
  });
});
