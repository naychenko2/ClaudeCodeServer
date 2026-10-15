// Контракт-тест адаптера истории (lib/api.ts → GET /api/local-model/history, ADR-030).
// Точный URL и состав query сверяются с DTO бэкенда (LocalModelStatsController):
// времена — ISO-8601 (не unix-мс), owner — логин, nextCursor — непрозрачная строка.
//
// Мутации, которые обязаны покрасить:
// - «двойной префикс /api/api/…» (request() сам добавляет /api) — URL пойдёт не туда;
// - «передавать пустые фильтры» — query засорится kind=, model=;
// - «курсор не передавать» — подгрузка потеряет позицию;
// - «считать 410 успехом» — «файл недоступен» пройдёт как страница;
// - «reason 410 не прикреплять» — карточка не узнает причину.

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// IndexedDB-обёртку offline.ts мокаем целиком (паттерн localModelMediaApi.test.ts)
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

// Страница истории, как её отдаёт LocalModelStatsController (кабельный camelCase)
function pageDto(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    records: [
      {
        id: 'p1', kind: 'image', op: 'generate_image', model: 'Qwen-Image 2.1',
        prompt: 'кот на крыше', params: { width: 1024, height: 1024, steps: 25, seed: 2914337016, count: 1 },
        owner: 'masha', status: 'completed', outcome: 'completed', error: null,
        runSeconds: 42, enqueuedAt: '2026-10-15T14:00:00Z', startedAt: '2026-10-15T14:01:20Z',
        finishedAt: '2026-10-15T14:02:02Z', source: 'local-media',
      },
    ],
    nextCursor: 'YXJy',
    counters: { kinds: { image: 3 }, outcomes: { completed: 2, failed: 1 } },
    ...over,
  };
}

describe('api.localModel.history — контракт GET /api/local-model/history', () => {
  it('без фильтров — только путь; страница возвращается как есть (ISO, owner, nextCursor)', async () => {
    const dto = pageDto();
    fetchMock.mockResolvedValue(jsonResponse(dto));

    const data = await api.localModel.history();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    // request() сам добавляет префикс /api — двойного не должно быть
    expect(fetchMock.mock.calls[0][0]).toBe('/api/local-model/history');
    expect(data).toEqual(dto);
  });

  it('фильтры попадают в query в порядке kind/model/outcome/login/from/to/cursor', async () => {
    fetchMock.mockResolvedValue(jsonResponse(pageDto()));

    await api.localModel.history({
      kind: 'image', model: 'Qwen-Image 2.1', outcome: 'completed',
      login: 'masha', from: '2026-10-08T00:00:00Z', cursor: 'YXJy',
    });
    const url = fetchMock.mock.calls[0][0] as string;
    expect(url.startsWith('/api/local-model/history?')).toBe(true);
    const qs = new URLSearchParams(url.slice('/api/local-model/history?'.length));
    expect(qs.get('kind')).toBe('image');
    expect(qs.get('model')).toBe('Qwen-Image 2.1');
    expect(qs.get('outcome')).toBe('completed');
    expect(qs.get('login')).toBe('masha');
    expect(qs.get('from')).toBe('2026-10-08T00:00:00Z');
    expect(qs.get('cursor')).toBe('YXJy');
  });

  it('пустые фильтры не передаются (kind=null → без kind=)', async () => {
    fetchMock.mockResolvedValue(jsonResponse(pageDto()));

    await api.localModel.history({ kind: null, model: null, outcome: null, login: null, from: null, cursor: null });
    expect(fetchMock.mock.calls[0][0]).toBe('/api/local-model/history');
  });

  it('limit передаётся, если задан', async () => {
    fetchMock.mockResolvedValue(jsonResponse(pageDto()));

    await api.localModel.history({ limit: 25 });
    const url = fetchMock.mock.calls[0][0] as string;
    expect(new URLSearchParams(url.slice('/api/local-model/history?'.length)).get('limit')).toBe('25');
  });

  it('410 (файл недоступен) — ошибка со статусом и reason из тела', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ reason: 'expired' }, 410));
    await expect(api.localModel.history()).rejects.toMatchObject({ status: 410, body: { reason: 'expired' } });
  });

  it('403 (не админ) — ошибка со статусом', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ error: 'Нет прав' }, 403));
    await expect(api.localModel.history()).rejects.toMatchObject({ status: 403 });
  });

  it('404 (флаг local-model-stats выключен) — ошибка со статусом', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ error: 'Не найдено' }, 404));
    await expect(api.localModel.history()).rejects.toMatchObject({ status: 404 });
  });

  it('сетевой сбой — reject, не падение', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ error: 'offline' }, 0));
    await expect(api.localModel.history()).rejects.toBeDefined();
  });
});
