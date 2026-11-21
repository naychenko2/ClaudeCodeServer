// Тесты TTS-диагностики режима разговора: ребёнок не шлёт POST /api/tts/diag —
// это взрослая отладочная ручка без [ChildAllowed] (дефект cc43582b: ребёнку на
// ленте урока был 403). Проверяется гейт в единственной точке запроса (talkDiagUpload)
// и то, что локальный дамп (localStorage 'talkDiagLast') пишется как прежде.
import { describe, it, expect, vi, beforeEach } from 'vitest';

vi.mock('./offline', () => ({
  request: vi.fn(),
}));

vi.mock('./defaultPersona', () => ({
  getMeSnapshot: vi.fn(),
}));

import { request } from './offline';
import { getMeSnapshot } from './defaultPersona';
import { talkDiagSave, talkDiagUpload } from './talkDiag';

// Хранилище в памяти вместо localStorage (окружение тестов — node)
const store = new Map<string, string>();
const memStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => { store.set(k, v); },
};
(globalThis as { localStorage?: unknown }).localStorage = memStorage;

function asMe(isChild: boolean) {
  vi.mocked(getMeSnapshot).mockReturnValue({ isChild } as never);
}

beforeEach(() => {
  store.clear();
  vi.clearAllMocks();
});

describe('talkDiag: детский путь без запроса на сервер', () => {
  it('ребёнок: talkDiagSave → запрос /tts/diag 0 вызовов, localStorage talkDiagLast записан', () => {
    asMe(true);
    talkDiagSave();
    expect(request).not.toHaveBeenCalled();
    const dump = memStorage.getItem('talkDiagLast');
    expect(dump).toContain('=== talk diag ===');
  });

  it('ребёнок: и прямой talkDiagUpload не спрашивает и резолвится false', async () => {
    asMe(true);
    await expect(talkDiagUpload()).resolves.toBe(false);
    expect(request).not.toHaveBeenCalled();
  });
});

describe('talkDiag: взрослый путь как прежде', () => {
  it('взрослый: talkDiagUpload → один запрос /tts/diag и true', async () => {
    asMe(false);
    vi.mocked(request).mockResolvedValue(undefined as never);
    await expect(talkDiagUpload()).resolves.toBe(true);
    expect(request).toHaveBeenCalledTimes(1);
    expect(request).toHaveBeenCalledWith('/tts/diag', expect.objectContaining({ method: 'POST' }));
  });

  it('взрослый, request отклонён → talkDiagUpload резолвится false, без необработанного исключения', async () => {
    asMe(false);
    vi.mocked(request).mockRejectedValue(new Error('связи нет'));
    await expect(talkDiagUpload()).resolves.toBe(false);
  });
});
