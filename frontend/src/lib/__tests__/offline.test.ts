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

  it('GET при сетевой ошибке отдаёт данные из кэша; первая ошибка → degraded, вторая → offline', async () => {
    fetchMock.mockRejectedValue(new TypeError('failed to fetch'));
    idbGet.mockResolvedValue({ data: { cached: true }, savedAt: 1 });

    const data = await offline.request('/projects');

    expect(data).toEqual({ cached: true });
    expect(idbGet).toHaveBeenCalledWith('/projects');
    // Уступчивый переход: первая сетевая ошибка — degraded (ещё не offline)
    expect(offline.getConnectionState()).toBe('degraded');
    expect(offline.isOnline()).toBe(true);

    // Повторная ошибка из degraded — уже offline
    await offline.request('/projects');
    expect(offline.getConnectionState()).toBe('offline');
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
    // Первая ошибка — degraded; вторая — offline
    expect(offline.getConnectionState()).toBe('degraded');
    await expect(offline.request('/projects', { method: 'PUT', body: '{}' }))
      .rejects.toThrowError(offline.OfflineError);
    expect(offline.getConnectionState()).toBe('offline');
  });
});

describe('request: свой таймаут', () => {
  // fetch, который висит до отмены и реджектит по abort — как настоящий
  function hangUntilAbort() {
    return (_url: string, init: RequestInit) => new Promise<never>((_resolve, reject) => {
      init.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
    });
  }

  it('явный timeoutMs → RequestTimeoutError, связь не деградирует', async () => {
    fetchMock.mockImplementation(hangUntilAbort());

    const promise = offline.request('/sessions/s1/prompt/p1/analyze', {
      method: 'POST', body: '{}', timeoutMs: 180_000,
    });
    // Ожидание вешаем ДО прокрутки таймеров: иначе реджект остаётся без обработчика
    const assertion = expect(promise).rejects.toThrowError(offline.RequestTimeoutError);
    await vi.advanceTimersByTimeAsync(180_000);
    await assertion;

    // Долгий ход модели — не улика против связи: приложение остаётся онлайн
    expect(offline.getConnectionState()).toBe('online');
  });

  it('дефолтный таймаут → прежнее поведение: OfflineError и уход в офлайн', async () => {
    fetchMock.mockImplementation(hangUntilAbort());

    const promise = offline.request('/projects', { method: 'POST', body: '{}' });
    const assertion = expect(promise).rejects.toThrowError(offline.OfflineError);
    await vi.advanceTimersByTimeAsync(30_000);
    await assertion;

    // За 30 с успевает и ранний degraded-таймер (2 с), и промахи health-пинга —
    // зависший запрос без явного лимита это и правда пропажа связи
    expect(offline.getConnectionState()).toBe('offline');
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
  it('в офлайне раз в 4с пингует /health и при ответе возвращает онлайн', async () => {
    offline.notifyOffline();
    expect(offline.isOnline()).toBe(false);

    fetchMock.mockResolvedValue(jsonResponse({}, 401)); // даже 401 = сеть жива
    await vi.advanceTimersByTimeAsync(4000);

    expect(fetchMock).toHaveBeenCalledWith('/api/health', expect.objectContaining({ method: 'GET' }));
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

describe('heartbeat: детекция пропажи сервера в онлайне', () => {
  // initConnectivity запускает цикл монитора; вкладка видима
  function bootOnline() {
    vi.stubGlobal('document', { visibilityState: 'visible', addEventListener: vi.fn() });
    offline.initConnectivity();
  }

  it('серия провалов суммарно ≥OFFLINE_DWELL_MS (8с) приводит к offline', async () => {
    // Вызываем runMonitorTick напрямую, управляем результатом pingServer через spy.
    // Это позволяет тестировать логику длительности без привязки к сетке таймеров.
    // mockReturnValueOnce(Promise.resolve(false)) — резолвится сразу при вызове,
    // не нужно advanceTimers чтобы сбросить _pingInFlight между тиками.
    const pingServerSpy = vi.spyOn(offline, 'pingServer');

    // Провал №1 → degraded
    pingServerSpy.mockReturnValueOnce(Promise.resolve(false));
    await offline.runMonitorTick();
    expect(offline.getConnectionState()).toBe('degraded');
    expect(offline._firstFailureAt).not.toBeNull();

    // 4с спустя (суммарно 4с < 8с) — ещё degraded
    await vi.advanceTimersByTimeAsync(4_000);
    pingServerSpy.mockReturnValueOnce(Promise.resolve(false));
    await offline.runMonitorTick();
    expect(offline.getConnectionState()).toBe('degraded');

    // Ещё 5с спустя (суммарно 9с ≥ 8с) — уходим в offline
    await vi.advanceTimersByTimeAsync(5_000);
    pingServerSpy.mockReturnValueOnce(Promise.resolve(false));
    await offline.runMonitorTick();
    expect(offline.getConnectionState()).toBe('offline');

    pingServerSpy.mockRestore();
  });

  it('успешный пинг из degraded возвращает online и обнуляет серию промахов', async () => {
    // Мокаем fetch напрямую: pingServer ходит в /api/health через fetch.
    // Меняем мок между вызовами runMonitorTick.
    let pingResult = false;
    fetchMock.mockImplementation(async () => {
      if (pingResult) return jsonResponse({}, 204);
      throw new TypeError('failed to fetch');
    });

    // Провал → degraded
    pingResult = false;
    await offline.runMonitorTick();
    expect(offline.getConnectionState()).toBe('degraded');
    expect(offline._firstFailureAt).not.toBeNull();

    // Успешный пинг → сразу online, _firstFailureAt сброшен
    pingResult = true;
    await offline.runMonitorTick();
    expect(offline.getConnectionState()).toBe('online');
    expect(offline._firstFailureAt).toBeNull();

    // Серия обнулена: следующий промах — снова degraded (с нуля)
    pingResult = false;
    await offline.runMonitorTick();
    expect(offline.getConnectionState()).toBe('degraded');
  });

  it('успешный heartbeat держит онлайн и сбрасывает серию промахов', async () => {
    bootOnline();
    fetchMock.mockResolvedValue(jsonResponse({}, 204));

    await vi.advanceTimersByTimeAsync(15_000);
    expect(offline.isOnline()).toBe(true);
    expect(fetchMock).toHaveBeenCalledWith('/api/health', expect.objectContaining({ method: 'GET' }));
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

describe('тройное состояние связи', () => {
  it('переходы online → degraded → offline → online', () => {
    expect(offline.getConnectionState()).toBe('online');

    offline.setDegraded('тест');
    expect(offline.getConnectionState()).toBe('degraded');
    // degraded — «ещё онлайн»: запросы и мутации не блокируются
    expect(offline.isOnline()).toBe(true);

    offline.setConnectionState('offline');
    expect(offline.getConnectionState()).toBe('offline');
    expect(offline.isOnline()).toBe(false);

    offline.setConnectionState('online');
    expect(offline.getConnectionState()).toBe('online');
    expect(offline.isOnline()).toBe(true);
  });

  it('setConnectionState идемпотентен — подписчики не дёргаются без смены значения', () => {
    const fn = vi.fn();
    offline.subscribeConnectionState(fn);

    offline.setConnectionState('online'); // уже online
    expect(fn).not.toHaveBeenCalled();

    offline.setConnectionState('degraded');
    expect(fn).toHaveBeenCalledTimes(1);
    offline.setConnectionState('degraded');
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it('setDegraded из offline не выходит — возврат только по явному успеху', () => {
    offline.setConnectionState('offline');
    offline.setDegraded('тест');
    expect(offline.getConnectionState()).toBe('offline');
  });

  it('setDegraded из degraded — без изменений (не понижает и не дёргает)', () => {
    const fn = vi.fn();
    offline.setDegraded('первый');
    offline.subscribeConnectionState(fn);

    offline.setDegraded('второй');
    expect(offline.getConnectionState()).toBe('degraded');
    expect(fn).not.toHaveBeenCalled();
  });

  it('notifyOnline/notifyOffline маппятся на крайние состояния', () => {
    offline.notifyOffline();
    expect(offline.getConnectionState()).toBe('offline');
    offline.notifyOnline();
    expect(offline.getConnectionState()).toBe('online');
  });

  it('мутации в degraded не блокируются (запрос уходит в сеть)', async () => {
    offline.setDegraded('тест');
    fetchMock.mockResolvedValue(jsonResponse({ ok: 1 }));

    await expect(offline.request('/projects', { method: 'POST', body: '{}' })).resolves.toEqual({ ok: 1 });
    expect(fetchMock).toHaveBeenCalled();
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

describe('ранний degraded-таймер в request()', () => {
  // «Призрачная сеть»: fetch отвечает, но медленно
  function delayed(ms: number, resp: unknown) {
    return new Promise(resolve => setTimeout(() => resolve(resp), ms));
  }

  it('зависший запрос (>5с) поднимает degraded; forceConnectivityCheck из degraded-таймера убран', async () => {
    // Fetch медленнее DEGRADED_THRESHOLD_MS=5с — degraded таймер сработает ДО ответа.
    // Используем реальную задержку (setTimeout), т.к. vi.useFakeTimers не замедляет
    // Promise.prototype.then.
    function slowFetch() {
      return new Promise(resolve => setTimeout(() => resolve(jsonResponse({ ok: 1 })), 6_000));
    }
    fetchMock.mockImplementation(slowFetch);

    const p = offline.request('/projects');
    // 5с — порог degraded, ещё до ответа fetch (6с)
    await vi.advanceTimersByTimeAsync(5_100);

    expect(offline.getConnectionState()).toBe('degraded');
    expect(offline.isOnline()).toBe(true);
    // forceConnectivityCheck больше НЕ вызывается из degraded-таймера request()
    expect(fetchMock).toHaveBeenCalledTimes(1); // только основной запрос

    // Ответ пришёл
    await vi.advanceTimersByTimeAsync(2_000);
    await expect(p).resolves.toEqual({ ok: 1 });
  });

  it('запрос с явным timeoutMs (заведомо долгий) ранним таймером не гейтится', async () => {
    fetchMock.mockImplementation(() => delayed(5_000, jsonResponse({ ok: 1 })));

    const p = offline.request('/ai/generate', { timeoutMs: 10_000 });
    await vi.advanceTimersByTimeAsync(5_100);

    // 5с прошло, а degraded нет — запрос заведомо долгий (явный timeoutMs)
    expect(offline.getConnectionState()).toBe('online');

    await vi.advanceTimersByTimeAsync(3_000);
    await expect(p).resolves.toEqual({ ok: 1 });
    expect(offline.getConnectionState()).toBe('online');
  });

  it('быстрый ответ (<2с) не поднимает degraded', async () => {
    fetchMock.mockImplementation(() => delayed(500, jsonResponse({ ok: 1 })));

    const p = offline.request('/projects');
    await vi.advanceTimersByTimeAsync(1_000);
    await expect(p).resolves.toEqual({ ok: 1 });
    expect(offline.getConnectionState()).toBe('online');
  });
});

describe('Мьютекс пинга: гонка forceConnectivityCheck', () => {
  function bootOnline() {
    vi.stubGlobal('document', { visibilityState: 'visible', addEventListener: vi.fn() });
    offline.initConnectivity();
  }

  it('три вызова forceConnectivityCheck подряд до резолва пининга — fetch вызывается один раз', async () => {
    bootOnline();
    // Пинг «зависает» — резолвится только после того как тест продвинет таймеры
    let resolvePing: (v: boolean) => void;
    fetchMock.mockImplementation(() => new Promise<boolean>(r => { resolvePing = r; }));

    offline.forceConnectivityCheck();
    offline.forceConnectivityCheck();
    offline.forceConnectivityCheck();

    // Таймеры не трогаем — пинг ещё в полёте
    expect(fetchMock).toHaveBeenCalledTimes(1);

    // Допускаем резолв — мьютекс снимается
    resolvePing!(true);
    await vi.advanceTimersByTimeAsync(0);
  });

  it('второй вызов после завершения первого пина — запускает новый пинг', async () => {
    bootOnline();
    let resolvePing: (v: boolean) => void;
    fetchMock.mockImplementation(() => new Promise<boolean>(r => { resolvePing = r; }));

    offline.forceConnectivityCheck();
    resolvePing!(true);
    // Сбрасываем _pingInFlight и ждём следующий тик (scheduled с delay=FAST_RETRY_MS=5s)
    await vi.advanceTimersByTimeAsync(5_001);
    // Теперь тик исполнился, _pingInFlight = null, пинг завершён
    offline.forceConnectivityCheck();
    expect(fetchMock).toHaveBeenCalledTimes(2);
    resolvePing!(true);
    await vi.advanceTimersByTimeAsync(5_001);
  });
});

describe('Длительность серии: offline по времени, не по счётчику', () => {
  it('первый промах → degraded, устойчивый провал ≥8с → offline', async () => {
    // Мокаем fetch: каждый вызов pingServer делает один fetch.
    let failCount = 0;
    fetchMock.mockImplementation(async () => {
      if (failCount < 10) { failCount++; throw new TypeError('failed to fetch'); }
      return jsonResponse({}, 204);
    });

    // Провал №1 → degraded, _firstFailureAt записан
    await offline.runMonitorTick();
    expect(offline.getConnectionState()).toBe('degraded');
    const t0 = offline._firstFailureAt;
    expect(t0).not.toBeNull();

    // 4с спустя — суммарно 4с < 8с, всё ещё degraded
    await vi.advanceTimersByTimeAsync(4_000);
    await offline.runMonitorTick();
    expect(offline.getConnectionState()).toBe('degraded');
    // _firstFailureAt не изменился
    expect(offline._firstFailureAt).toBe(t0);

    // Ещё 5с → суммарно 9с ≥ 8с → offline
    await vi.advanceTimersByTimeAsync(5_000);
    await offline.runMonitorTick();
    expect(offline.getConnectionState()).toBe('offline');
  });

  it('успешный пинг сбрасывает _firstFailureAt — новая серия считается с нуля', async () => {
    let pingResult = false;
    fetchMock.mockImplementation(async () => {
      if (!pingResult) throw new TypeError('failed to fetch');
      return jsonResponse({}, 204);
    });

    // Провал №1 → degraded
    pingResult = false;
    await offline.runMonitorTick();
    expect(offline.getConnectionState()).toBe('degraded');
    const t0 = offline._firstFailureAt;

    // Успех → online, _firstFailureAt сброшен
    pingResult = true;
    await offline.runMonitorTick();
    expect(offline.getConnectionState()).toBe('online');
    expect(offline._firstFailureAt).toBeNull();

    // Новая серия: промах с нуля
    pingResult = false;
    await vi.advanceTimersByTimeAsync(1); // сдвигаем fake time, чтобы t1 отличался от t0
    await offline.runMonitorTick();
    expect(offline.getConnectionState()).toBe('degraded');
    expect(offline._firstFailureAt).not.toBeNull();
    // t0 от первой серии, новый t1
    expect(offline._firstFailureAt).not.toBe(t0);
  });
});

describe('Дрожащая сеть: короткие провалы не уводят в offline', () => {
  it('успех внутри OFFLINE_DWELL_MS сбрасывает серию — флаппинг не роняет связь', async () => {
    let pingResult = false;
    fetchMock.mockImplementation(async () => {
      if (!pingResult) throw new TypeError('failed to fetch');
      return jsonResponse({}, 204);
    });

    // Провал → degraded, _firstFailureAt = T0
    pingResult = false;
    await offline.runMonitorTick();
    expect(offline.getConnectionState()).toBe('degraded');
    const t0 = offline._firstFailureAt;

    // 3с спустя (суммарно 3с < 8с) — ещё degraded
    await vi.advanceTimersByTimeAsync(3_000);
    await offline.runMonitorTick();
    expect(offline.getConnectionState()).toBe('degraded');
    expect(offline._firstFailureAt).toBe(t0); // не изменился

    // Успех → online, _firstFailureAt сброшен
    pingResult = true;
    await offline.runMonitorTick();
    expect(offline.getConnectionState()).toBe('online');
    expect(offline._firstFailureAt).toBeNull();

    // Новый провал — с нуля
    pingResult = false;
    await offline.runMonitorTick();
    expect(offline.getConnectionState()).toBe('degraded');
    expect(offline._firstFailureAt).not.toBe(t0); // новый момент
  });
});

describe('Троттлинг forceConnectivityCheck', () => {
  function bootOnline() {
    vi.stubGlobal('document', { visibilityState: 'visible', addEventListener: vi.fn() });
    offline.initConnectivity();
  }

  it('два вызова forceConnectivityCheck в течение 1с — второй не запускает пинг', async () => {
    bootOnline();
    let resolvePing: (v: boolean) => void;
    fetchMock.mockImplementation(() => new Promise<boolean>(r => { resolvePing = r; }));

    offline.forceConnectivityCheck();
    // Второй вызов через 500мс — под порогом MIN_FORCED_CHECK_INTERVAL_MS
    await vi.advanceTimersByTimeAsync(500);
    offline.forceConnectivityCheck();
    // Должен быть только один пинг
    expect(fetchMock).toHaveBeenCalledTimes(1);

    resolvePing!(true);
    await vi.advanceTimersByTimeAsync(0);
  });

  it('вызов после 2с достаточнрго интервала — новый пинг уходит', async () => {
    bootOnline();
    let resolvePing: (v: boolean) => void;
    fetchMock.mockImplementation(() => new Promise<boolean>(r => { resolvePing = r; }));

    offline.forceConnectivityCheck();
    resolvePing!(true);
    await vi.advanceTimersByTimeAsync(0);

    // Ждём достаточно для троттла
    await vi.advanceTimersByTimeAsync(2_000);
    offline.forceConnectivityCheck();

    expect(fetchMock).toHaveBeenCalledTimes(2);
    resolvePing!(true);
    await vi.advanceTimersByTimeAsync(0);
  });
});
