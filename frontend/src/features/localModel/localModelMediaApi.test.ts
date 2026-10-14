// Контракт-тест адаптера вкладки «Медиа» (lib/api.ts → бэкенд). Точный URL и
// состав тела сверяются с record'ом бэкенда: LocalModelStatsController
// (GET /api/local-model/stats/media). Ответ возвращается как есть: время — unix-мс,
// chужие прогоны анонимны (own=false, без sessionId/projectId), gpus может быть null,
// loadedModels — необязательное (null/отсутствует — узел не сообщает).
//
// Мутации, которые обязаны покрасить:
// - «двойной префикс /api/api/…» (request() сам добавляет /api) — URL пойдёт не туда;
// - «since не передавать» — query потеряет параметр хвоста;
// - «считать 404/403 успехом» — ошибка пройдёт как результат, а не reject;
// - «loadedModels не принимать» — поле с именами моделей узла потеряется.

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// IndexedDB-обёртку offline.ts мокаем целиком (паттерн localModelApi.test.ts)
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

let fetchMock: ReturnType<typeof vi.fn>;
let api: typeof import('../../lib/api').api;

beforeEach(async () => {
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

// Снимок «Медиа», как его отдаёт LocalModelStatsController (кабельный camelCase)
function mediaDto(): Record<string, unknown> {
  return {
    configured: true,
    comfy: { status: 'working', downSince: null, version: '0.4' },
    slots: { used: 1, limit: 4 },
    gpus: [{ uuid: 'g0', index: 0, name: 'RTX 3090', roles: ['media'], utilPerc: 97, memUsedMb: 22464, memTotalMb: 24576, tempC: 74, powerW: 331, powerLimitW: 350, mediaMemMb: 22464 }],
    loadedModels: ['MiniMax H3'],
    running: [
      { kind: 'video', op: 'Видео 5 с · MiniMax H3', heavy: false, own: true, sessionId: 's1', projectId: 'p1', title: null, foreign: null, steps: { step: 12, total: 25, stage: null }, percent: 48, percentExact: true, etaSeconds: 120, remainingSeconds: 120, startedAt: 1700000000000 },
    ],
    queue: [
      { position: 1, kind: 'image', op: 'Картинка · Qwen-Image 2.1', heavy: false, own: false, sessionId: null, projectId: null, title: 'Чат другого пользователя', foreign: null, etaSeconds: null, startInSeconds: null },
    ],
    lastHour: [
      { kind: 'video', op: 'Видео 5 с · MiniMax H3', own: true, outcome: 'completed', runSeconds: 340, etaSeconds: 330, finishedAt: 1700000000000 },
    ],
    unsupported: [],
  };
}

describe('api.localModel.media — контракт GET /api/local-model/stats/media', () => {
  it('без since — только путь; снимок возвращается как есть (unix-мс, анонимные, gpus)', async () => {
    const dto = mediaDto();
    fetchMock.mockResolvedValue(jsonResponse(dto));

    const data = await api.localModel.media();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    // request() сам добавляет префикс /api — двойного не должно быть
    expect(fetchMock.mock.calls[0][0]).toBe('/api/local-model/stats/media');
    expect(data).toEqual(dto);
  });

  it('since передаётся unix-мс в query', async () => {
    fetchMock.mockResolvedValue(jsonResponse(mediaDto()));

    await api.localModel.media(1700000000000);
    expect(fetchMock.mock.calls[0][0]).toBe('/api/local-model/stats/media?since=1700000000000');
  });

  it('configured:false (подсистема images выключена) — 200, не 500', async () => {
    const dto = { configured: false, comfy: { status: 'disabled', downSince: null, version: null }, slots: { used: 0, limit: 4 }, gpus: null, running: [], queue: [], lastHour: [], unsupported: [] };
    fetchMock.mockResolvedValue(jsonResponse(dto));

    const data = await api.localModel.media();

    expect(data.configured).toBe(false);
    expect(data.comfy.status).toBe('disabled');
  });

  it('loadedModels: массив принимается; null и отсутствие поля — не ломают ответ', async () => {
    // Узел сообщает имена
    let dto = mediaDto();
    fetchMock.mockResolvedValue(jsonResponse(dto));
    let data = await api.localModel.media();
    expect(data.loadedModels).toEqual(['MiniMax H3']);

    // Узел не сообщает: null
    dto = { ...mediaDto(), loadedModels: null };
    fetchMock.mockResolvedValue(jsonResponse(dto));
    data = await api.localModel.media();
    expect(data.loadedModels).toBeNull();

    // Узел не сообщает: поля нет вовсе (старый узел)
    dto = mediaDto();
    delete (dto as Record<string, unknown>).loadedModels;
    fetchMock.mockResolvedValue(jsonResponse(dto));
    data = await api.localModel.media();
    expect(data.loadedModels).toBeUndefined();
  });

  it('404 (неизвестный маршрут) — ошибка со статусом, не результат', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ error: 'Не найдено' }, 404));
    await expect(api.localModel.media()).rejects.toMatchObject({ status: 404 });
  });

  it('403 (не админ) — ошибка со статусом', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ error: 'Нет прав' }, 403));
    await expect(api.localModel.media()).rejects.toMatchObject({ status: 403 });
  });

  it('сетевой сбой — reject, не падение', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ error: 'offline' }, 0));
    await expect(api.localModel.media()).rejects.toBeDefined();
  });
});
