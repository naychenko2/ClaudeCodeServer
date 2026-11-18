// Контракт-тест ручки вкладки «Встречи» (lib/api.ts → бэкенд). Путь сверяется с
// предложением v1 (docs/mockups/local-model-transcription-proposal.md, раздел
// «Черновик данных с бэкенда»): GET /api/local-model/transcription. Бэкенда ещё нет
// (Б1 не сдан) — запись держит фронт-контракт: путь и класс live (офлайн-кэш этому
// ответу вреден, см. комментарий к live в lib/offline.ts).
//
// Мутации, которые обязаны покрасить:
// - «двойной префикс /api/api/…» (request() сам добавляет /api) — URL пойдёт не туда;
// - «снять live: true» — ответ ляжет в IndexedDB (краснеет тест записи) и при обрыве
//   связи подставится из кэша (краснеет тест офлайна): вкладка показала бы стоящий
//   конвейер живым — ровно то вранье, ради которого live здесь и стоит.

import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// IndexedDB-обёртку мокаем целиком (паттерн lib/__tests__/offline.test.ts):
// idbSet возвращает Promise — иначе .catch() на undefined падает
const { idbGet, idbSet, idbDelete } = vi.hoisted(() => ({
  idbGet: vi.fn(),
  idbSet: vi.fn(),
  idbDelete: vi.fn(),
}));
vi.mock('../../lib/idb', () => ({ idbGet, idbSet, idbDelete }));

// Простая замена Web Storage (токен для Authorization; cc_user_id — владелец, без него
// пассивная запись не пишется вовсе и тест live был бы зелёным при любой мутации)
function storageStub() {
  const m = new Map<string, string>([['cc_token', 'tok'], ['cc_user_id', 'u1']]);
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
  // Состояние offline.ts (флаг _online) — модульное, поэтому свежий импорт на каждый тест
  vi.resetModules();
  idbGet.mockReset();
  idbSet.mockReset();
  idbSet.mockResolvedValue('key');
  idbDelete.mockReset();
  fetchMock = vi.fn();
  vi.stubGlobal('fetch', fetchMock);
  vi.stubGlobal('navigator', { onLine: true });
  vi.stubGlobal('window', { addEventListener: vi.fn(), dispatchEvent: () => true });
  vi.stubGlobal('localStorage', storageStub());
  vi.stubGlobal('sessionStorage', storageStub());
  api = (await import('../../lib/api')).api;
});

afterEach(() => { vi.unstubAllGlobals(); });

// Снимок конвейера по таблице раздела «Черновик данных с бэкенда» предложения v1:
// время — unix-мс, все блоки сразу (в тесте контроллера — тот же снимок).
function transcriptionDto(): Record<string, unknown> {
  return {
    phase: 'batch',
    updatedAt: 1_763_000_000_000,
    host: 'ai',
    pid: 4123,
    alive: true,
    outputHost: 'ai',
    outputDir: '/mnt/media/Transcripts',
    level: 'ok',
    file: '2026-11-18 планёрка.md',
    folder: 'Video/2026-11-18 планёрка',
    stage: 'asr',
    stageStartedAt: 1_762_999_960_000,
    durationMin: 4,
    batchIndex: 2,
    batchSize: 5,
    asrQueue: [{ file: 'next.mp4', folder: 'Video/next', since: 1_762_999_000_000 }],
    summaryQueue: 1,
    fails: [{ at: 1_762_900_000_000, folder: 'Video/x', file: 'x.mp4', minutes: 12, comment: 'ASR упала' }],
    recent: [{ at: 1_762_990_000_000, folder: 'Video/y', file: 'y.md', minutes: 31, speakers: 3, comment: '', mdPath: '/srv/meetings/y.md' }],
    dify: [{ folder: 'Video/y', local: 1, inDify: 1 }],
    checkedAt: 1_762_995_000_000,
    deps: [{ key: 'comfy', state: 'ok', checkedAt: 1_762_999_000_000 }],
  };
}

describe('api.localModel.transcription — контракт GET /api/local-model/transcription', () => {
  it('шлёт без query и возвращает снимок как есть (unix-мс, все блоки)', async () => {
    const dto = transcriptionDto();
    fetchMock.mockResolvedValue(jsonResponse(dto));

    const data = await api.localModel.transcription();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    // request() сам добавляет префикс /api — двойного не должно быть
    expect(fetchMock.mock.calls[0][0]).toBe('/api/local-model/transcription');
    expect(data).toEqual(dto);
  });

  it('live: true — ответ не сохраняется в IndexedDB (мутация «снять live» краснеет)', async () => {
    fetchMock.mockResolvedValue(jsonResponse(transcriptionDto()));

    await api.localModel.transcription();
    // Пассивная запись общего класса идёт в фоновой очереди — дождаться, иначе
    // «не было вызова» означала бы просто опережение ассертa (паттерн offline.test.ts)
    await (await import('../../lib/offline')).flushPassiveWrites();

    // live-ответ не кэшируется: подставленный из кэша прошлый снимок сказал бы
    // «конвейер жив» про конвейер, который уже встал.
    expect(idbSet).not.toHaveBeenCalled();
  });

  it('live: true — при обрыве связи ответ не достаётся из кэша: reject, не старый снимок', async () => {
    // Кэш мог бы ответить (запись есть) — live не даёт ему права: вызывающему нужен
    // факт обрыва, вкладка рисует пустое состояние, а не «конвейер жив» позавчерашней давности
    idbGet.mockResolvedValue({ data: transcriptionDto(), savedAt: 1_762_000_000_000 });
    fetchMock.mockRejectedValue(new TypeError('failed to fetch'));

    await expect(api.localModel.transcription()).rejects.toThrow();
    expect(idbGet).not.toHaveBeenCalled();
  });
});
