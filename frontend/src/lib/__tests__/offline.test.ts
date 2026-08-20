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

// Ответ /api/health — NoContent, как реальный HealthController
function healthResponse(status = 204) {
  return { ok: status >= 200 && status < 300, status, statusText: `HTTP ${status}`, json: async () => ({}), text: async () => '' };
}

let offline: typeof import('../offline');
let fetchMock: ReturnType<typeof vi.fn>;
let dispatched: string[];

beforeEach(async () => {
  // Состояние offline.ts (флаг _online) — модульное, поэтому свежий импорт на каждый тест
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

  it('GET при сетевой ошибке отдаёт данные из кэша и уходит в offline', async () => {
    fetchMock.mockRejectedValue(new TypeError('failed to fetch'));
    idbGet.mockResolvedValue({ data: { cached: true }, savedAt: 1 });

    const data = await offline.request('/projects');

    expect(data).toEqual({ cached: true });
    expect(idbGet).toHaveBeenCalledWith('/projects');
    // Сеть молчит и на запросе, и на подтверждающем пинге → офлайн
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
    offline.setOnline(false);

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

describe('request: свой таймаут', () => {
  // fetch, который висит до отмены и реджектит по abort — как настоящий
  function hangUntilAbort() {
    return (_url: string, init: RequestInit) => new Promise<never>((_resolve, reject) => {
      init.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
    });
  }

  it('явный timeoutMs → RequestTimeoutError, связь не трогается', async () => {
    fetchMock.mockImplementation(hangUntilAbort());

    const promise = offline.request('/sessions/s1/prompt/p1/analyze', {
      method: 'POST', body: '{}', timeoutMs: 180_000,
    });
    // Ожидание вешаем ДО прокрутки таймеров: иначе реджект остаётся без обработчика
    const assertion = expect(promise).rejects.toThrowError(offline.RequestTimeoutError);
    await vi.advanceTimersByTimeAsync(180_000);
    await assertion;

    // Долгий ход модели — не улика против связи: приложение остаётся онлайн
    expect(offline.isOnline()).toBe(true);
  });

  it('дефолтный таймаут → прежнее поведение: OfflineError и уход в офлайн', async () => {
    fetchMock.mockImplementation(hangUntilAbort());

    const promise = offline.request('/projects', { method: 'POST', body: '{}' });
    const assertion = expect(promise).rejects.toThrowError(offline.OfflineError);
    await vi.advanceTimersByTimeAsync(30_000);
    // Подтверждающий пинг тоже висит до своего abort'а (PING_TIMEOUT_MS=3с) — сервер молчит
    await vi.advanceTimersByTimeAsync(3_000);
    await assertion;

    // 30-сек дефолтный таймаут без явного timeoutMs — зависший запрос это пропажа связи
    expect(offline.isOnline()).toBe(false);
  });
});

// --- Подтверждающий пинг перед уходом в офлайн ---
// Раньше ЛЮБОЙ единичный провал fetch единолично ставил офлайн, а зонд через
// секунды возвращал онлайн — на планшете это давало мигание индикатора при
// разморозке вкладки (браузер реджектит все in-flight запросы разом).
describe('request: подтверждение офлайна пингом', () => {
  // /api/health отвечает, всё остальное — сетевая ошибка
  function serverAliveButRequestFails() {
    fetchMock.mockImplementation((url: string) =>
      url === '/api/health'
        ? Promise.resolve(healthResponse(204))
        : Promise.reject(new TypeError('failed to fetch')));
  }

  it('одиночный провал fetch при живом сервере НЕ уводит в офлайн', async () => {
    serverAliveButRequestFails();
    idbGet.mockResolvedValue({ data: { cached: true }, savedAt: 1 });

    const data = await offline.request('/projects');

    // Кэш всё равно выручает (ответа-то не было), но состояние связи не мигает
    expect(data).toEqual({ cached: true });
    expect(fetchMock).toHaveBeenCalledWith('/api/health', expect.objectContaining({ method: 'GET' }));
    expect(offline.isOnline()).toBe(true);
  });

  it('мутация при живом сервере: OfflineError отдаётся, но офлайн не включается', async () => {
    serverAliveButRequestFails();

    await expect(offline.request('/projects', { method: 'POST', body: '{}' }))
      .rejects.toThrowError(offline.OfflineError);
    expect(offline.isOnline()).toBe(true);
  });

  it('пачка параллельных провалов даёт ОДИН пинг (общий полёт)', async () => {
    let pings = 0;
    fetchMock.mockImplementation((url: string) => {
      if (url === '/api/health') { pings++; return Promise.resolve(healthResponse(204)); }
      return Promise.reject(new TypeError('failed to fetch'));
    });
    idbGet.mockResolvedValue({ data: {}, savedAt: 1 });

    await Promise.all([offline.request('/a'), offline.request('/b'), offline.request('/c')]);

    expect(pings).toBe(1);
    expect(offline.isOnline()).toBe(true);
  });

  it('молчит и пинг — уходим в офлайн (прежнее поведение при реальном разрыве)', async () => {
    fetchMock.mockRejectedValue(new TypeError('failed to fetch'));
    idbGet.mockResolvedValue(undefined);

    await expect(offline.request('/projects')).rejects.toThrowError(offline.OfflineError);
    expect(offline.isOnline()).toBe(false);
  });

  it('502 от прокси на пинге = бэкенд мёртв → офлайн', async () => {
    fetchMock.mockImplementation((url: string) =>
      url === '/api/health'
        ? Promise.resolve(healthResponse(502))
        : Promise.reject(new TypeError('failed to fetch')));
    idbGet.mockResolvedValue(undefined);

    await expect(offline.request('/projects')).rejects.toThrowError(offline.OfflineError);
    expect(offline.isOnline()).toBe(false);
  });

  it('в офлайне провалившийся GET пинг не шлёт (подтверждать нечего)', async () => {
    offline.setOnline(false);
    fetchMock.mockRejectedValue(new TypeError('failed to fetch'));
    idbGet.mockResolvedValue({ data: { cached: true }, savedAt: 1 });

    await offline.request('/projects');

    // Ровно один fetch — сам запрос; /api/health не дёргался
    expect(fetchMock).toHaveBeenCalledTimes(1);
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

describe('subscribeOnline', () => {
  it('подписчики уведомляются о смене состояния, отписка работает', () => {
    const fn = vi.fn();
    const unsub = offline.subscribeOnline(fn);

    offline.setOnline(false);
    expect(fn).toHaveBeenCalledTimes(1);

    offline.setOnline(false); // без смены значения — не уведомляем
    expect(fn).toHaveBeenCalledTimes(1);

    unsub();
    offline.setOnline(true);
    expect(fn).toHaveBeenCalledTimes(1);
  });
});

describe('setOnline: идемпотентность', () => {
  it('повторный вызов с тем же значением не дёргает подписчиков', () => {
    const fn = vi.fn();
    offline.subscribeOnline(fn);

    expect(offline.isOnline()).toBe(true);
    offline.setOnline(true);
    expect(fn).not.toHaveBeenCalled();

    offline.setOnline(false);
    expect(fn).toHaveBeenCalledTimes(1);
    offline.setOnline(false);
    expect(fn).toHaveBeenCalledTimes(1);
  });
});

describe('setConnectionState: совместимость с signalr.ts', () => {
  // signalr.ts зовёт setConnectionState('online' | 'offline') — проверяем, что
  // старый контракт жив и подписчики получают нотификации
  it('setConnectionState("online") из offline поднимает флаг', () => {
    offline.setConnectionState('offline');
    expect(offline.isOnline()).toBe(false);

    const fn = vi.fn();
    offline.subscribeOnline(fn);
    offline.setConnectionState('online');
    expect(offline.isOnline()).toBe(true);
    expect(fn).toHaveBeenCalledTimes(1);
  });
});

describe('becameVisibleRecently: тихое окно после возврата вкладки', () => {
  // Достаём visibilitychange-хендлер, зарегистрированный initConnectivity в застабленном
  // document, — тесты гоняют реальный обработчик, а не переизобретают его логику
  function visibilityHandler(doc: { addEventListener: ReturnType<typeof vi.fn> }): () => void {
    const call = doc.addEventListener.mock.calls.find(([ev]) => ev === 'visibilitychange');
    expect(call).toBeDefined();
    return call![1] as () => void;
  }

  it('вкладка не уходила в фон — окно не активно, тост не глушится', () => {
    vi.stubGlobal('document', { visibilityState: 'visible', addEventListener: vi.fn() });
    offline.initConnectivity();
    expect(offline.becameVisibleRecently()).toBe(false);
  });

  it('возврат вкладки открывает окно; спустя 5с оно закрывается', () => {
    const doc = { visibilityState: 'hidden', addEventListener: vi.fn() };
    vi.stubGlobal('document', doc);
    offline.initConnectivity();
    const handler = visibilityHandler(doc);

    doc.visibilityState = 'visible';
    handler();
    expect(offline.becameVisibleRecently()).toBe(true);

    // Восстановление позже тихого окна (пользователь наблюдал офлайн) — тост уместен
    vi.advanceTimersByTime(5_100);
    expect(offline.becameVisibleRecently()).toBe(false);
  });

  it('уход вкладки в фон отметку не ставит', () => {
    const doc = { visibilityState: 'hidden', addEventListener: vi.fn() };
    vi.stubGlobal('document', doc);
    offline.initConnectivity();
    const handler = visibilityHandler(doc);

    handler(); // visibilitychange при hidden — уход в фон
    expect(offline.becameVisibleRecently()).toBe(false);
  });
});

describe('initConnectivity: ОС-события без пинга', () => {
  function handlerFor(event: string): () => void {
    const calls = (window.addEventListener as ReturnType<typeof vi.fn>).mock.calls;
    const call = calls.find(([ev]) => ev === event);
    expect(call).toBeDefined();
    return call![1] as () => void;
  }

  it('window "online" — setOnline(true) сразу, без fetch', () => {
    offline.setOnline(false);
    expect(offline.isOnline()).toBe(false);

    offline.initConnectivity();
    handlerFor('online')();

    expect(offline.isOnline()).toBe(true);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('window "offline" — setOnline(false) сразу', () => {
    expect(offline.isOnline()).toBe(true);

    offline.initConnectivity();
    handlerFor('offline')();

    expect(offline.isOnline()).toBe(false);
  });
});

// --- Односторонний зонд возврата (offline → online) ---
// На этой логике держится вся живучесть выхода из офлайна: эвристика вырезана,
// а единственный автоматический путь подъёма флага — это ОС-событие online,
// SignalR onreconnected и успешный ответ сервера в request(). Когда ни одно
// из них не сработало (хаб не стартован, экран логина, авиарежим включился
// без window-события) — поднимает флаг зонд.
describe('зонд возврата', () => {
  it('из офлайна успешный GET через request() поднимает флаг', async () => {
    offline.setOnline(false);
    expect(offline.isOnline()).toBe(false);

    fetchMock.mockResolvedValue(jsonResponse({ items: [] }));
    await offline.request('/projects');

    expect(offline.isOnline()).toBe(true);
  });

  it('зонд в офлайне: ответ /api/health поднимает флаг и гасит таймер', async () => {
    offline.setOnline(false);
    fetchMock.mockResolvedValue(healthResponse(204));

    offline.initConnectivity();
    // Один тик (PROBE_INTERVAL_MS) — fetch должен вызваться на /api/health
    await vi.advanceTimersByTimeAsync(4_000);

    expect(fetchMock).toHaveBeenCalledWith('/api/health', expect.objectContaining({
      method: 'GET', cache: 'no-store',
    }));
    expect(offline.isOnline()).toBe(true);

    // Поднялись — таймер больше не тикает
    fetchMock.mockClear();
    await vi.advanceTimersByTimeAsync(20_000);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('зонд в офлайне: провал зонда НЕ меняет состояние, таймер продолжает тикать', async () => {
    offline.setOnline(false);
    fetchMock.mockRejectedValue(new TypeError('failed to fetch'));

    offline.initConnectivity();

    // Несколько тиков подряд — каждый проваливается, но флаг остаётся offline
    await vi.advanceTimersByTimeAsync(4_000);
    expect(offline.isOnline()).toBe(false);
    expect(fetchMock).toHaveBeenCalledTimes(1);

    await vi.advanceTimersByTimeAsync(4_000);
    expect(offline.isOnline()).toBe(false);
    expect(fetchMock).toHaveBeenCalledTimes(2);

    // Теперь ответил — флаг поднялся
    fetchMock.mockResolvedValue(healthResponse(204));
    await vi.advanceTimersByTimeAsync(4_000);
    expect(offline.isOnline()).toBe(true);
  });

  it('зонд в офлайне: 502/503/504 = недостижим, флаг НЕ поднимается', async () => {
    offline.setOnline(false);
    fetchMock.mockResolvedValue(healthResponse(503));

    offline.initConnectivity();
    await vi.advanceTimersByTimeAsync(4_000);

    expect(fetchMock).toHaveBeenCalled();
    expect(offline.isOnline()).toBe(false);
  });

  it('зонд в онлайне не запускается вообще (инвариант односторонности)', async () => {
    expect(offline.isOnline()).toBe(true);
    offline.initConnectivity();

    // Сколько угодно времени — fetch не должен зваться
    await vi.advanceTimersByTimeAsync(60_000);
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('провал зонда не может увести из online в offline (нет мигания)', async () => {
    // На старте мы онлайн — зонд не должен тикать
    expect(offline.isOnline()).toBe(true);
    offline.initConnectivity();

    fetchMock.mockRejectedValue(new TypeError('network down'));
    await vi.advanceTimersByTimeAsync(20_000);

    // Зонд не дёргался — флаг остался online
    expect(fetchMock).not.toHaveBeenCalled();
    expect(offline.isOnline()).toBe(true);
  });

  it('при visibilityState === "hidden" тик пропускается', async () => {
    const doc = { visibilityState: 'hidden', addEventListener: vi.fn() };
    vi.stubGlobal('document', doc);

    offline.setOnline(false);
    fetchMock.mockResolvedValue(healthResponse(204));
    offline.initConnectivity();

    await vi.advanceTimersByTimeAsync(20_000);
    expect(fetchMock).not.toHaveBeenCalled();

    // Вернулись на вкладку — сразу тикаем (форсированный тик)
    doc.visibilityState = 'visible';
    const visibilityHandler = doc.addEventListener.mock.calls.find(([ev]) => ev === 'visibilitychange')![1] as () => void;
    visibilityHandler();
    await vi.advanceTimersByTimeAsync(0);

    expect(fetchMock).toHaveBeenCalled();
  });

  it('идемпотентность setOnline(false): повторный вход в offline не плодит таймеры', async () => {
    offline.setOnline(false);
    offline.setOnline(false);
    offline.setOnline(false);

    fetchMock.mockResolvedValue(healthResponse(204));
    await vi.advanceTimersByTimeAsync(4_000);

    // Один пинг — не три
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('fireProbeNow троттлится: 5 visibilitychange за <2с → один пинг, не пять', async () => {
    // На мобиле visibilitychange → visible летит пачками (клавиатура, шторка,
    // уведомления). Без троттла fetch к мёртвому серверу реджектит мгновенно
    // и пять раз зовёт /api/health. Троттл MIN_FORCED_PROBE_INTERVAL_MS=2с.
    // Провал зонда (TypeError) — состояние не меняется, иначе после первого
    // успеха флаг стал бы online и следующий fireProbeNow тихо вернул бы из
    // _online-проверки (минуя троттл — маскируя баг).
    // advanceTimersByTimeAsync — обязателен: fireProbeNow зовёт runProbeTick через
    // `void`, и обычный advanceTimersByTime не промывает микрозадачу, на которой
    // finally() сбрасывает _probeInFlight. Без async-прогонки _probeInFlight
    // остаётся не-null и блокирует второй тик.
    const doc = { visibilityState: 'hidden', addEventListener: vi.fn() };
    vi.stubGlobal('document', doc);

    offline.setOnline(false);
    fetchMock.mockRejectedValue(new TypeError('failed to fetch'));
    offline.initConnectivity();
    const handler = doc.addEventListener.mock.calls.find(([ev]) => ev === 'visibilitychange')![1] as () => void;

    doc.visibilityState = 'visible';
    handler();
    handler();
    handler();
    handler();
    handler();
    await vi.advanceTimersByTimeAsync(0);

    expect(fetchMock).toHaveBeenCalledTimes(1);

    // Спустя >2с следующий возврат вкладки — снова пинг. Ровно 2с ещё блокируется
    // (условие `< 2_000`, не `<=`), берём с запасом.
    await vi.advanceTimersByTimeAsync(2_500);
    handler();
    await vi.advanceTimersByTimeAsync(0);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
