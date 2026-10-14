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
// - «since не передавать» — query потеряет параметр хвоста.

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

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
