import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

// Мокаем IndexedDB-обёртку целиком: offline.ts работает с ней только через idbGet/idbSet
const { idbGet, idbSet } = vi.hoisted(() => ({
  idbGet: vi.fn(),
  idbSet: vi.fn(),
}));
vi.mock('../idb', () => ({ idbGet, idbSet }));

// Простая замена Web Storage
function storageStub(init: Record<string, string> = {}) {
  const m = new Map(Object.entries(init));
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

let offline: typeof import('../offline');
let fetchMock: ReturnType<typeof vi.fn>;
let dispatched: string[];

beforeEach(async () => {
  // Состояние offline.ts (флаг _online, зонд) — модульное, поэтому свежий импорт на каждый тест
  vi.resetModules();
  vi.useFakeTimers();
  idbGet.mockReset();
  idbSet.mockReset();
  idbSet.mockResolvedValue('key');

  fetchMock = vi.fn();
  dispatched = [];
  vi.stubGlobal('fetch', fetchMock);
  vi.stubGlobal('navigator', { onLine: true });
  vi.stubGlobal('window', {
    addEventListener: vi.fn(),
    dispatchEvent: (e: Event) => { dispatched.push(e.type); return true; },
  });
  vi.stubGlobal('localStorage', storageStub({ cc_token: 'tok' }));
  vi.stubGlobal('sessionStorage', storageStub());

  offline = await import('../offline');
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('request: network-first', () => {
  it('успешный GET возвращает данные и кладёт их в IDB-кэш', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ items: [1, 2] }));

    const data = await offline.request<{ items: number[] }>('/projects');

    expect(data).toEqual({ items: [1, 2] });
    expect(fetchMock).toHaveBeenCalledWith('/api/projects', expect.objectContaining({
      headers: expect.objectContaining({ Authorization: 'Bearer tok' }),
    }));
    expect(idbSet).toHaveBeenCalledWith('/projects', expect.objectContaining({ data: { items: [1, 2] } }));
    expect(offline.isOnline()).toBe(true);
  });

  it('GET при сетевой ошибке отдаёт данные из кэша и переводит в офлайн', async () => {
    fetchMock.mockRejectedValue(new TypeError('failed to fetch'));
    idbGet.mockResolvedValue({ data: { cached: true }, savedAt: 1 });

    const data = await offline.request('/projects');

    expect(data).toEqual({ cached: true });
    expect(idbGet).toHaveBeenCalledWith('/projects');
    expect(offline.isOnline()).toBe(false);
  });

  it('GET при сетевой ошибке без кэша → OfflineError', async () => {
    fetchMock.mockRejectedValue(new TypeError('failed to fetch'));
    idbGet.mockResolvedValue(undefined);

    await expect(offline.request('/projects')).rejects.toThrowError(offline.OfflineError);
    await expect(offline.request('/projects')).rejects.toThrow('Нет сохранённых данных');
  });

  it('204 → undefined, пустое тело не парсится как JSON', async () => {
    fetchMock.mockResolvedValue({ ok: true, status: 204, statusText: 'No Content', json: async () => ({}), text: async () => '' });
    await expect(offline.request('/tasks/1', { method: 'DELETE' })).resolves.toBeUndefined();
  });
});

describe('request: мутации офлайн', () => {
  it('мутация в офлайне отклоняется OfflineError без похода в сеть', async () => {
    offline.notifyOffline();

    await expect(offline.request('/projects', { method: 'POST', body: '{}' }))
      .rejects.toThrowError(offline.OfflineError);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('мутация при сетевой ошибке → OfflineError, IDB-fallback не используется', async () => {
    fetchMock.mockRejectedValue(new TypeError('failed to fetch'));

    await expect(offline.request('/projects', { method: 'PUT', body: '{}' }))
      .rejects.toThrowError(offline.OfflineError);
    expect(idbGet).not.toHaveBeenCalled();
    expect(offline.isOnline()).toBe(false);
  });
});

describe('request: HTTP-ошибки', () => {
  it('401 с токеном → событие cc-unauthorized (логаут) и Error с текстом сервера', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ error: 'ключ отозван' }, 401));

    await expect(offline.request('/projects')).rejects.toThrow('ключ отозван');
    expect(dispatched).toContain('cc-unauthorized');
    // Сервер ответил → мы онлайн, это не сетевой сбой
    expect(offline.isOnline()).toBe(true);
  });

  it('401 без токена (экран логина) — событие логаута не шлётся', async () => {
    vi.stubGlobal('localStorage', storageStub());
    fetchMock.mockResolvedValue(jsonResponse({ error: 'нет ключа' }, 401));

    await expect(offline.request('/auth/me')).rejects.toThrow('нет ключа');
    expect(dispatched).not.toContain('cc-unauthorized');
  });

  it('500 пробрасывается как обычная ошибка, офлайн-режим не включается', async () => {
    fetchMock.mockResolvedValue(jsonResponse({ error: 'внутренняя ошибка' }, 500));

    await expect(offline.request('/projects')).rejects.toThrow('внутренняя ошибка');
    expect(offline.isOnline()).toBe(true);
    expect(idbGet).not.toHaveBeenCalled();
  });
});

describe('зонд восстановления связи', () => {
  it('в офлайне раз в 4с шлёт HEAD и при ответе возвращает онлайн', async () => {
    offline.notifyOffline();
    expect(offline.isOnline()).toBe(false);

    fetchMock.mockResolvedValue(jsonResponse({}, 401)); // даже 401 = сеть жива
    await vi.advanceTimersByTimeAsync(4000);

    expect(fetchMock).toHaveBeenCalledWith('/api/projects', expect.objectContaining({ method: 'HEAD' }));
    expect(offline.isOnline()).toBe(true);
  });

  it('пока сети нет — остаётся в офлайне и продолжает зондировать', async () => {
    offline.notifyOffline();
    fetchMock.mockRejectedValue(new TypeError('failed to fetch'));

    await vi.advanceTimersByTimeAsync(8000);

    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(offline.isOnline()).toBe(false);
  });
});

describe('subscribeOnline', () => {
  it('подписчики уведомляются о смене состояния, отписка работает', () => {
    const fn = vi.fn();
    const unsub = offline.subscribeOnline(fn);

    offline.notifyOffline();
    expect(fn).toHaveBeenCalledTimes(1);

    offline.notifyOffline(); // без смены значения — не уведомляем
    expect(fn).toHaveBeenCalledTimes(1);

    unsub();
    offline.notifyOnline();
    expect(fn).toHaveBeenCalledTimes(1);
  });
});
