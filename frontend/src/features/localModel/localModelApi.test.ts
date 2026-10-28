// Контракт-тест адаптера «Локальные модели» (lib/api.ts → бэкенд). Точные URL и
// состав тела сверяются с record'ами бэкенда: LocalModelStatsController
// (GET /api/local-model/stats) и SessionLocalModelController
// (GET /api/sessions/{id}/local-model). Ответ возвращается как есть: время —
// unix-мс, kv — доли 0..1, phase — строка, чужие чаты анонимны
// (own=false, без sessionId/projectId).
//
// Мутации, которые обязаны покрасить:
// - «двойной префикс /api/api/…» (request() сам добавляет /api) — URL пойдёт не туда;
// - «считать 404 успехом» (lastTurn) — 404 (чужая/неизвестная сессия) пройдёт
//   как результат, а не как ошибка;
// - «since не передавать» — query потеряет параметр хвоста;
// - «engine не передавать» (stats) — стенд Bonsai опрашивал бы основной;
// - «engine в URL всегда» (stats) — основной стенд потерял бы обратную совместимость.

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { BONSAI_UNSUPPORTED, mockBonsaiStats } from './localModel.fixtures';

// IndexedDB-обёртку offline.ts мокаем целиком (паттерн lib/__tests__/offline.test.ts):
// idbSet возвращает Promise — иначе .catch() на undefined падает
const { idbGet, idbSet } = vi.hoisted(() => ({
  idbGet: vi.fn(),
  idbSet: vi.fn(),
}));
vi.mock('../../lib/idb', () => ({ idbGet, idbSet }));

// Простая замена Web Storage (токен для Authorization)
function storageStub() {
  const m = new Map<string, string>([['cc_token', 'tok']]);
  return {
    getItem: (k: string) => m.get(k) ?? null,
    setItem: (k: string, v: string) => { m.set(k, v); },
    removeItem: (k: string) => { m.delete(k); },
  };
}

// Ответ fetch с телом-JSON
function jsonResponse(data: unknown, status = 200) {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: `HTTP ${status}`,
    json: async () => data,
    text: async () => JSON.stringify(data),
  };
}

let fetchMock: ReturnType<typeof vi.fn>;
let api: typeof import('../../lib/api').api;

beforeEach(async () => {
  // Состояние offline.ts (флаг _online) — модульное, поэтому свежий импорт на каждый тест
  vi.resetModules();
  idbGet.mockReset();
  idbSet.mockReset();
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

// Снимок панели, как его отдаёт LocalModelStatsController (кабельный camelCase)
function statsDto(): Record<string, unknown> {
  return {
    configured: true,
    engine: 'vllm',
    model: 'qwen3-32b',
    status: 'working',
    downSince: null,
    running: 2,
    waiting: 0,
    othersCount: 1,
    speed: { genPerStream: 38, prefillPerStream: 1240 },
    // доли 0..1, не проценты
    kv: { usagePerc: 0.61, ramTierPerc: 0.39, prefixHitPerc: 0.87, ramTierHitPerc: 0.52, preemptions15m: 0 },
    gpus: [{ index: 0, name: 'RTX 3090', utilPerc: 71, memUsedMb: 21934, memTotalMb: 24576, tempC: 68, powerW: 176, powerLimitW: 220 }],
    // t — unix-мс
    history: [{ t: 1700000000000, genThroughput: 38, waiting: 0, running: 2 }],
    // свой чат — с id/проектом; чужой — анонимный (sessionId/projectId — null)
    chats: [
      { own: true, sessionId: 's1', projectId: 'p1', title: 'Рефакторинг сторожа', phase: 'generating', genTps: 38 },
      { own: false, sessionId: null, projectId: null, title: 'Чат другого пользователя', phase: 'reading', genTps: null },
    ],
    unsupported: [],
  };
}

describe('api.localModel.stats — контракт GET /api/local-model/stats', () => {
  it('шлёт window в query и возвращает снимок как есть (unix-мс, доли, phase, анонимные чаты)', async () => {
    const dto = statsDto();
    fetchMock.mockResolvedValue(jsonResponse(dto));

    const data = await api.localModel.stats('15m');

    expect(fetchMock).toHaveBeenCalledTimes(1);
    // request() сам добавляет префикс /api — двойного не должно быть
    expect(fetchMock.mock.calls[0][0]).toBe('/api/local-model/stats?window=15m');
    expect(data).toEqual(dto);
  });

  it('since передаётся unix-мс в query; без since — только window', async () => {
    fetchMock.mockResolvedValue(jsonResponse(statsDto()));

    await api.localModel.stats('1h', 1700000000000);
    expect(fetchMock.mock.calls[0][0]).toBe('/api/local-model/stats?window=1h&since=1700000000000');

    fetchMock.mockClear();
    await api.localModel.stats('1h');
    expect(fetchMock.mock.calls[0][0]).toBe('/api/local-model/stats?window=1h');
  });

  it('configured:false (подсистема Llm выключена) — 200, не 500: снимок приходит, панель скроется по нему', async () => {
    const dto = { configured: false, status: 'notConnected', speed: {}, kv: {}, gpus: null, history: [], chats: [], unsupported: [] };
    fetchMock.mockResolvedValue(jsonResponse(dto));

    const data = await api.localModel.stats('15m');

    expect(data.configured).toBe(false);
    expect(data.status).toBe('notConnected');
  });
});

describe('api.localModel.lastTurn — контракт GET /api/sessions/{id}/local-model', () => {
  it('итог хода возвращается как есть; null — ходов не было', async () => {
    const turn = { genTps: 30, prefillTps: null, ttftMs: 800, queueMsApprox: 4000, cacheHitPerc: 0.87, outTokens: 500 };
    fetchMock.mockResolvedValue(jsonResponse({ lastTurn: turn }));

    const res = await api.localModel.lastTurn('sess-1');

    expect(fetchMock.mock.calls[0][0]).toBe('/api/sessions/sess-1/local-model');
    expect(res.lastTurn).toEqual(turn);

    fetchMock.mockClear();
    fetchMock.mockResolvedValueOnce(jsonResponse({ lastTurn: null }));
    expect((await api.localModel.lastTurn('sess-1')).lastTurn).toBeNull();
  });

  it('404 (чужая/неизвестная сессия) — ошибка со статусом, не результат: мутация «считать 404 успехом» краснеет', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ error: 'Не найдено' }, 404));

    const p = api.localModel.lastTurn('sess-1');
    await expect(p).rejects.toMatchObject({ status: 404 });
  });
});

// Стенд второй модели (разрез v2, §5): список движков для переключателя и опрос
// конкретного стенда. Ключ движка — только ключ словаря: из него не строится URL.
describe('api.localModel.engines — контракт GET /api/local-model/engines', () => {
  it('шлёт без query и возвращает список как есть (порядок и configured — за бэкендом)', async () => {
    const list = [
      { name: 'main', title: 'Основная', configured: true },
      { name: 'bonsai', title: 'Bonsai', configured: true },
    ];
    fetchMock.mockResolvedValue(jsonResponse(list));

    const { fetchLocalModelEngines } = await import('./api');
    const res = await fetchLocalModelEngines();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0][0]).toBe('/api/local-model/engines');
    expect(res).toEqual(list);
  });
});

describe('api.localModel.stats?engine= — опрос стенда второй модели', () => {
  it('без engine URL прежний: /local-model/stats?window=… (регрессия основного стенда)', async () => {
    fetchMock.mockResolvedValue(jsonResponse(statsDto()));

    const { fetchLocalModelStats } = await import('./api');
    await fetchLocalModelStats('15m');
    expect(fetchMock.mock.calls[0][0]).toBe('/api/local-model/stats?window=15m');

    fetchMock.mockClear();
    await fetchLocalModelStats('1h', 1700000000000);
    expect(fetchMock.mock.calls[0][0]).toBe('/api/local-model/stats?window=1h&since=1700000000000');
  });

  it('engine=bonsai идёт в query после window; с since — после него', async () => {
    fetchMock.mockResolvedValue(jsonResponse(mockBonsaiStats('yielded')));

    const { fetchLocalModelStats } = await import('./api');
    await fetchLocalModelStats('15m', undefined, 'bonsai');
    expect(fetchMock.mock.calls[0][0]).toBe('/api/local-model/stats?window=15m&engine=bonsai');

    fetchMock.mockClear();
    await fetchLocalModelStats('5m', 1700000000000, 'bonsai');
    expect(fetchMock.mock.calls[0][0]).toBe('/api/local-model/stats?window=5m&since=1700000000000&engine=bonsai');
  });

  it('ответ с duty проходит как есть: yielded без downSince, арбитр и вызовы мест без потерь', async () => {
    const dto = mockBonsaiStats('yielded');
    fetchMock.mockResolvedValue(jsonResponse(dto));

    const data = await api.localModel.stats('5m', undefined, 'bonsai');

    expect(data.status).toBe('yielded');
    // уступка — не авария: DownSince не ставится (разрез v2, §1)
    expect(data.downSince).toBeNull();
    expect(data.duty?.arbiter?.state).toBe('yielded');
    expect(data.duty?.arbiter?.reasons?.[0]).toEqual({ kind: 'comfy', raw: 'a1b2c3d4', jobKind: 'video', known: true });
    expect(data.duty?.callsNow?.[0].link).toBe('insurance');
    // unsupported — имена полей снимка: по ним фронт прячет блоки «Кэш»/«Вытеснения»/время агентов
    expect(data.unsupported).toEqual(BONSAI_UNSUPPORTED);
  });
});

describe('фикстуры состояний Bonsai (макет §4)', () => {
  it('каждое из пяти состояний арбитра есть: арбитр достижим и называет своё состояние', () => {
    for (const s of ['unknown', 'ready', 'yielded', 'starting', 'waiting-vram'] as const) {
      const m = mockBonsaiStats(s);
      expect(m.duty?.arbiter?.reachable).toBe(true);
      expect(m.duty?.arbiter?.state).toBe(s);
    }
  });

  it('уступка (yielded/starting/waiting-vram) — статус yielded без DownSince; ready — обычный статус; unknown — down (правило Б3b)', () => {
    for (const s of ['yielded', 'starting', 'waiting-vram'] as const) {
      const m = mockBonsaiStats(s);
      expect(m.status).toBe('yielded');
      expect(m.downSince).toBeNull();
      // выгруженная модель не ведёт чатов: «кто сейчас» — места из duty.callsNow
      expect(m.chats).toEqual([]);
      expect(m.duty!.callsNow!.length).toBeGreaterThan(0);
    }
    expect(mockBonsaiStats('ready').status).toBe('working');
    const u = mockBonsaiStats('unknown');
    expect(u.status).toBe('down');
    expect(u.downSince).toBeGreaterThan(0);
  });

  // engine — ТИП движка, а не имя стенда: бэкенд кладёт 'vllm' | 'llama-server' | 'ollama'
  // (backend/…/LocalStats/LocalModelStatsPoller.cs:11-12). Мутация «engine: 'vllm'»
  // в localModel.fixtures.ts краснеет на этом тесте.
  it('engine — тип движка, а не имя стенда: у Bonsai llama-server во всех состояниях', () => {
    for (const s of ['unknown', 'ready', 'yielded', 'starting', 'waiting-vram', 'no-arbiter'] as const) {
      expect(mockBonsaiStats(s).engine).toBe('llama-server');
    }
  });

  // link:null — «ждёт в очереди»: вызов уже в журнале, но ещё не вошёл ни в одно звено
  // (макет §4). Сузить тип в lib/api.ts обратно до LocalModelChainLink — краснеет tsc -b
  // (фикстура 'ready' с link:null не компилируется).
  it('callsNow[].link nullable: null — «ждёт в очереди», активные строки называют звено', () => {
    const calls = mockBonsaiStats('ready').duty!.callsNow!;
    const waiting = calls.find(c => c.link === null);
    expect(waiting).toBeDefined();
    expect(waiting!.place).toBeTruthy();
    // ждущая строка не одна: активные вызовы по-прежнему называют звено цепочки
    expect(calls.some(c => c.link === 'bonsai')).toBe(true);
  });

  it('arbiter:null (нет StateUrl) — «арбитр не подключён»: duty есть, статуса уступки нет', () => {
    const m = mockBonsaiStats('no-arbiter');
    expect(m.duty).not.toBeNull();
    expect(m.duty!.arbiter).toBeNull();
    // без арбитра выгруженная модель неотличима от аварии: 'down', как у основной
    expect(m.status).toBe('down');
    // счётчики мест и цепочка живут независимо от арбитра (EngineCallLedger)
    expect(m.duty!.answersLastHour?.bonsai).toBe(212);
    expect(m.duty!.chain).toEqual(['bonsai', 'insurance', 'claude']);
  });
});
