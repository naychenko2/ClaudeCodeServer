import { describe, it, expect, vi, beforeEach } from 'vitest';

// api.ts берёт request/readStoredToken из offline.ts — мокаем, чтобы проверить,
// с каким timeoutMs «continue» ходит в сеть, не поднимая fetch и не трогая IndexedDB.
// Суть дефекта: без явного timeoutMs запрос рвался дефолтным 30 с (offline.ts) и
// выдавался за «офлайн», хотя бэкенд ждёт ход записки до 15 мин (ChatContinueService.cs).
const { request, readStoredToken } = vi.hoisted(() => ({
  request: vi.fn(),
  readStoredToken: vi.fn(() => 'tok'),
}));
vi.mock('../offline', () => ({ request, readStoredToken }));

import { api } from '../api';

describe('api.chats.continue — таймаут хода записки', () => {
  beforeEach(() => {
    request.mockReset();
    request.mockResolvedValue({ newSessionId: 'n1', handoffNoteId: null });
  });

  it('зовёт request с timeoutMs >= 15 мин (серверный лимит HandoffTurnTimeout)', async () => {
    await api.chats.continue('s1', 'дальше');

    expect(request).toHaveBeenCalledTimes(1);
    const [url, options] = request.mock.calls[0] as [
      string,
      { method?: string; body?: string; timeoutMs?: number },
    ];
    expect(url).toBe('/chats/s1/continue');
    expect(options.method).toBe('POST');
    // 15 мин = серверный HandoffTurnTimeout; фронт берёт чуть больше (16 мин),
    // чтобы обрыв по таймауту был RequestTimeoutError, а не OfflineError
    expect(options.timeoutMs).toBeGreaterThanOrEqual(15 * 60 * 1000);
  });

  it('note уходит в теле, без note — пустое тело', async () => {
    await api.chats.continue('s1', 'дальше');
    expect(JSON.parse((request.mock.calls[0] as [string, { body?: string }])[1].body!)).toEqual({ note: 'дальше' });

    request.mockClear();
    await api.chats.continue('s1');
    expect(JSON.parse((request.mock.calls[0] as [string, { body?: string }])[1].body!)).toEqual({});
  });
});
