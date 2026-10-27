import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mockBonsaiStats, mockLocalModelStats } from './localModel.fixtures';
import {
  configuredFetch,
  createStatsPoller,
  getLocalModelConfigured,
  isLocalModelPanelVisible,
  makeStatsFetch,
  setLocalModelConfigured,
  subscribeLocalModelConfigured,
} from './useLocalModelStats';

// Блокёр: окно из UI доходит до fetch. makeStatsFetch читает windowRef.current на
// каждый вызов; мутация «захватить окно при создании poller» краснеет.
const { fetchLocalModelStats, fetchLocalModelMedia } = vi.hoisted(() => ({
  fetchLocalModelStats: vi.fn(),
  fetchLocalModelMedia: vi.fn(),
}));
vi.mock('./api', () => ({ fetchLocalModelStats, fetchLocalModelMedia }));

describe('isLocalModelPanelVisible: видимость панели', () => {
  // Админ: флаг + configured (как раньше)
  it('admin: флаг + configured — показывается', () => {
    expect(isLocalModelPanelVisible({ flag: true, isAdmin: true, configured: true })).toBe(true);
  });
  it('admin: без флага — не показывается', () => {
    expect(isLocalModelPanelVisible({ flag: false, isAdmin: true, configured: true })).toBe(false);
  });
  it('admin: configured: false — нет', () => {
    expect(isLocalModelPanelVisible({ flag: true, isAdmin: true, configured: false })).toBe(false);
  });
  it('admin: configured: null — сервер ещё не ответил, нет', () => {
    expect(isLocalModelPanelVisible({ flag: true, isAdmin: true, configured: null })).toBe(false);
  });
  // Не-админ: только configured (флаг local-model-stats его не гейтит)
  it('не admin: configured: true — показывается, флаг не важен', () => {
    expect(isLocalModelPanelVisible({ flag: true, isAdmin: false, configured: true })).toBe(true);
    expect(isLocalModelPanelVisible({ flag: false, isAdmin: false, configured: true })).toBe(true);
  });
  it('не admin: configured: false — нет', () => {
    expect(isLocalModelPanelVisible({ flag: true, isAdmin: false, configured: false })).toBe(false);
  });
  it('не admin: configured: null — нет', () => {
    expect(isLocalModelPanelVisible({ flag: true, isAdmin: false, configured: null })).toBe(false);
  });
});

describe('configuredFetch: не-админ (/stats/media) /stats не трогает', () => {
  it('media → fetchLocalModelMedia; /stats не вызван', async () => {
    vi.mocked(fetchLocalModelStats).mockReset();
    vi.mocked(fetchLocalModelMedia).mockReset();
    vi.mocked(fetchLocalModelMedia).mockResolvedValue({ configured: true });
    await configuredFetch('media')();
    expect(vi.mocked(fetchLocalModelMedia)).toHaveBeenCalledTimes(1);
    expect(vi.mocked(fetchLocalModelStats)).not.toHaveBeenCalled();
  });
  it('stats → fetchLocalModelStats; /stats/media не вызван', async () => {
    vi.mocked(fetchLocalModelStats).mockReset();
    vi.mocked(fetchLocalModelMedia).mockReset();
    vi.mocked(fetchLocalModelStats).mockResolvedValue({ configured: true });
    await configuredFetch('stats')();
    expect(vi.mocked(fetchLocalModelStats)).toHaveBeenCalledTimes(1);
    expect(vi.mocked(fetchLocalModelMedia)).not.toHaveBeenCalled();
  });
});

describe('createStatsPoller: 2-с опрос', () => {
  const mock = mockLocalModelStats();

  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  const make = () => {
    const fetch = vi.fn(async () => mock);
    const p = createStatsPoller(fetch, 2000, (fn, ms) => setInterval(fn, ms), (id) => clearInterval(id));
    return { fetch, p };
  };

  it('закрытая панель — таймера нет, опроса нет', async () => {
    const { fetch, p } = make();
    p.setActive(false, true);
    await vi.advanceTimersByTimeAsync(10_000);
    expect(fetch).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('открытая панель и видимая вкладка — опрос сразу и каждые 2 с', async () => {
    const { fetch, p } = make();
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    expect(fetch).toHaveBeenCalledTimes(1); // первый ответ сразу
    await vi.advanceTimersByTimeAsync(2000);
    expect(fetch).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(4000);
    expect(fetch).toHaveBeenCalledTimes(4);
    p.dispose();
    expect(vi.getTimerCount()).toBe(0); // dispose — без утечки таймера
  });

  it('вкладка не видима — опрос стоит; снова видима — продолжается', async () => {
    const { fetch, p } = make();
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    p.setActive(true, false); // скрыли вкладку
    await vi.advanceTimersByTimeAsync(10_000);
    expect(fetch).toHaveBeenCalledTimes(1);
    p.setActive(true, true); // вернули: немедленный fetch (данные могли устать) + таймер
    await vi.advanceTimersByTimeAsync(0);
    expect(fetch).toHaveBeenCalledTimes(2);
    await vi.advanceTimersByTimeAsync(2000);
    expect(fetch).toHaveBeenCalledTimes(3);
  });

  it('снапшот: данные после первого ответа, loading гаснет', async () => {
    const { p } = make();
    p.setActive(true, true);
    expect(p.getSnapshot().data).toBeNull();
    await vi.advanceTimersByTimeAsync(0);
    const snap = p.getSnapshot();
    expect(snap.data).toEqual(mock);
    expect(snap.loading).toBe(false);
  });

  it('сбой опроса не роняет: прежние данные, loading гаснет', async () => {
    const fetch = vi.fn(async () => mock);
    const p = createStatsPoller(fetch, 2000, (fn, ms) => setInterval(fn, ms), (id) => clearInterval(id));
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    expect(p.getSnapshot().data).toEqual(mock);
    fetch.mockImplementationOnce(async () => { throw new Error('offline'); });
    await vi.advanceTimersByTimeAsync(2000);
    expect(p.getSnapshot().data).toEqual(mock); // прежние данные
    expect(p.getSnapshot().loading).toBe(false);
  });

  it('getSnapshot: два подряд вызова без событий — один объект; после fetch — новый', async () => {
    const { p } = make();
    const s1 = p.getSnapshot();
    const s2 = p.getSnapshot();
    expect(s1).toBe(s2); // === : кэш, а не новый объект на каждый вызов
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    const s3 = p.getSnapshot();
    expect(s3).not.toBe(s1); // после завершения fetch — пересобранный снимок
  });
});

// Ревью Ф1 (MAJOR 1-2): поллер знает движок каждого запроса — поздний ответ
// старого движка не затирает данные нового; смена движка сбрасывает чужой снимок.
// Мутация «убрать проверку движка в .then» краснеет в первом тесте, «убрать
// сброс при несовпадении движков» — во втором.
describe('createStatsPoller: движок снимка (MAJOR 1-2)', () => {
  const mainData = mockLocalModelStats(); // данные основной (vLLM)
  const bonsaiData = mockBonsaiStats('ready'); // данные стенда Bonsai

  const timers = (fn: () => void, ms: number) => setInterval(fn, ms);
  const untimer = (id: unknown) => clearInterval(id as ReturnType<typeof setInterval>);

  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  it('поздний ответ старого движка не затирает данные нового', async () => {
    const engine = { current: 'main' as 'main' | 'bonsai' };
    let resolveMain: (v: typeof mainData) => void = () => {};
    const mainPromise = new Promise<typeof mainData>((r) => { resolveMain = r; });
    const fetch = vi.fn<() => Promise<typeof mainData>>();
    fetch.mockImplementationOnce(() => mainPromise); // запрос main отложен
    fetch.mockImplementationOnce(async () => bonsaiData); // bonsai приходит первым
    const p = createStatsPoller(fetch, 2000, timers, untimer, engine);
    p.setActive(true, true); // первый тик — запрос main
    engine.current = 'bonsai'; // пользователь переключился, main всё ещё в пути
    p.refetch(); // запрос bonsai (как эффект смены движка в хуке)
    await vi.advanceTimersByTimeAsync(0);
    expect(p.getSnapshot().data).toEqual(bonsaiData);
    resolveMain(mainData); // поздний ответ main
    await vi.advanceTimersByTimeAsync(0);
    expect(p.getSnapshot().data).toEqual(bonsaiData); // снимок остаётся bonsai
    expect(p.getSnapshot().loading).toBe(false);
    p.dispose();
  });

  it('смена движка сбрасывает снимок; отказ нового движка не оставляет чужих данных', async () => {
    const engine = { current: 'main' as 'main' | 'bonsai' };
    const fetch = vi.fn<() => Promise<typeof mainData>>().mockResolvedValue(mainData);
    const p = createStatsPoller(fetch, 2000, timers, untimer, engine);
    p.setActive(true, true);
    await vi.advanceTimersByTimeAsync(0);
    expect(p.getSnapshot().data).toEqual(mainData); // данные main показаны
    engine.current = 'bonsai';
    fetch.mockImplementationOnce(async () => { throw new Error('bonsai offline'); });
    p.refetch(); // смена движка → немедленный запрос нового
    expect(p.getSnapshot().data).toBeNull(); // чужой снимок сброшен сразу
    expect(p.getSnapshot().loading).toBe(true);
    await vi.advanceTimersByTimeAsync(0); // отказ нового движка
    expect(p.getSnapshot().data).toBeNull(); // не данных предыдущего стенда
    expect(p.getSnapshot().loading).toBe(false);
    p.dispose();
  });
});

describe('стор configured: лёгкий опрос для значка', () => {
  it('null до первого ответа, значение обновляется и подписчики видят смену', () => {
    expect(getLocalModelConfigured()).toBeNull();
    const seen: (boolean | null)[] = [];
    const unsub = subscribeLocalModelConfigured(() => { seen.push(getLocalModelConfigured()); });
    setLocalModelConfigured(true);
    setLocalModelConfigured(true); // повторное значение — без дубля
    setLocalModelConfigured(false);
    unsub();
    setLocalModelConfigured(null);
    expect(seen).toEqual([true, false]);
    expect(getLocalModelConfigured()).toBeNull();
  });
});

describe('makeStatsFetch: окно доходит до запроса (блокер)', () => {
  it('fetch читает ТЕКУЩЕЕ окно: 15m → 1h, смена ref меняет следующий запрос', async () => {
    vi.mocked(fetchLocalModelStats).mockReset();
    const ref = { current: '15m' as const };
    const fetch = makeStatsFetch(ref);

    await fetch();
    expect(vi.mocked(fetchLocalModelStats)).toHaveBeenLastCalledWith('15m');

    ref.current = '1h'; // переключатель на «1 ч»
    await fetch();
    expect(vi.mocked(fetchLocalModelStats)).toHaveBeenLastCalledWith('1h');
  });

  it('движок: без engineRef и с main — прежний вызов (без параметра engine)', async () => {
    vi.mocked(fetchLocalModelStats).mockReset();
    const win = { current: '5m' as const };
    await makeStatsFetch(win)();
    expect(vi.mocked(fetchLocalModelStats)).toHaveBeenLastCalledWith('5m');
    await makeStatsFetch(win, { current: 'main' })();
    expect(vi.mocked(fetchLocalModelStats)).toHaveBeenLastCalledWith('5m');
  });

  it('смена движка в ref меняет запрос со следующего тика поллера', async () => {
    vi.useFakeTimers();
    try {
      vi.mocked(fetchLocalModelStats).mockReset();
      vi.mocked(fetchLocalModelStats).mockResolvedValue(mockLocalModelStats());
      const engine = { current: 'main' as 'main' | 'bonsai' };
      const p = createStatsPoller(
        makeStatsFetch({ current: '5m' }, engine), 2000,
        (fn, ms) => setInterval(fn, ms), (id) => clearInterval(id as ReturnType<typeof setInterval>),
      );
      p.setActive(true, true); // первый тик — main
      expect(vi.mocked(fetchLocalModelStats)).toHaveBeenLastCalledWith('5m');
      engine.current = 'bonsai'; // выбор переключили между тиками
      await vi.advanceTimersByTimeAsync(2000); // следующий тик
      expect(vi.mocked(fetchLocalModelStats)).toHaveBeenLastCalledWith('5m', undefined, 'bonsai');
      p.dispose();
    } finally { vi.useRealTimers(); }
  });

  it('5m → 5m (не 15m по умолчанию): мутация «окно не пробрасывается» краснеет', async () => {
    vi.mocked(fetchLocalModelStats).mockReset();
    const ref = { current: '5m' as const };
    await makeStatsFetch(ref)();
    expect(vi.mocked(fetchLocalModelStats)).toHaveBeenLastCalledWith('5m');
  });
});
