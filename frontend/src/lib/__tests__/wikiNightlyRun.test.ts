// Тесты стора «Разобрать сейчас» (WN-6a): ручной запуск ночного прохода и опрос до его конца.
// Свойства: два вызова, пока первый в полёте, — ровно один POST; опрос — раз в 5 с, пока GET
// отвечает nightlyRunning=true, последний ответ (свежий lastNight) — финальный снимок, дальше
// ни одного запроса; опрос — один на стор; смена владельца во время опроса его останавливает,
// ответ прежнего владельца в снимок не пишется. Отказы по коду и сетевой сбой — результат, а не
// исключение, и состояние не залипает «идёт». Новые поля настроек читаются как есть: старый
// бэк без них — false / undefined, а не ошибка; PUT состояния не несёт — оно сохраняется.
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';

vi.mock('../signalr', () => ({
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
  joinUser: vi.fn(() => Promise.resolve()),
}));

vi.mock('../offline', () => ({
  request: vi.fn(),
  readStoredUserId: vi.fn(() => 'u1'),
}));

import { readStoredUserId, request } from '../offline';
import {
  __test,
  getWikiSettingsSnapshot,
  loadWikiSettings,
  putWikiNightly,
  runWikiNightlyNow,
} from '../wiki';

type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;
const userId = vi.mocked(readStoredUserId);

const RUN_URL = '/wiki/settings/nightly/run';
const SETTINGS_URL = '/wiki/settings';

// Отложенный ответ: тест сам решает, когда резолвится запрос (пробы перекрывающихся запросов)
function deferred<T = unknown>(): { promise: Promise<T>; resolve: (v: T) => void } {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>(r => { resolve = r; });
  return { promise, resolve };
}

// Настройки как их отдаёт GET /api/wiki/settings (WN-5: с полем nightlyRunning)
const SETTINGS = {
  autoApplyEnabled: false,
  classes: [],
  nightly: {
    enabled: true, windowStartHour: 3, windowEndHour: 6,
    maxPackagesPerNight: 20, maxPackagesPerSection: 6, maxChatsPerSection: 10,
    inputTokenBudget: 700000,
  },
};
const RUNNING = (v: boolean, lastNight?: unknown) => ({
  ...SETTINGS, nightlyRunning: v, ...(lastNight !== undefined ? { lastNight } : {}),
});
const LAST_NIGHT = {
  date: '2026-11-05', selected: 4, analyzed: 3, applied: 2, toInbox: 1,
  estTokens: 180000, outcome: 'completed', rejected: 2, manual: true,
};

// Очередь ответов на ключ «МЕТод URL» (как в образце wiki.test.ts): каждый следующий запрос
// берёт следующий элемент; неоговорённый запрос или пустая очередь — падение теста, не повтор.
function serveSeq(routes: Record<string, unknown[]>): void {
  req.mockImplementation(async (url: string, init?: RequestInit) => {
    const key = `${((init?.method ?? 'GET').toUpperCase())} ${url}`;
    const q = routes[key];
    if (!q?.length) throw new Error(`неожиданный запрос: ${key}`);
    const value = q.shift()!;
    if (value instanceof Error) throw value;
    return value;
  });
}

const httpError = (status: number, body?: unknown): Error =>
  Object.assign(new Error(`HTTP ${status}`), { status, body });

const countOf = (url: string): number => req.mock.calls.filter((c: unknown[]) => c[0] === url).length;

// Дождаться завершения цепочки запроса, не двигая время
async function settle(): Promise<void> {
  await vi.advanceTimersByTimeAsync(0);
}

beforeEach(() => {
  vi.useFakeTimers();
  __test.reset();
  req.mockReset();
  userId.mockReturnValue('u1');
});

afterEach(() => {
  // опрос — цепочка таймеров стора: тест не оставляет её следующему
  __test.reset();
  vi.useRealTimers();
});

describe('вики: ручной запуск ночного разбора и опрос (WN-6a)', () => {
  it('runWikiNightlyNow_ДваВызоваПокаПервыйВПолёте_РовноОдинPOST', async () => {
    const post = deferred();
    serveSeq({ [`POST ${RUN_URL}`]: [post.promise] });
    const p1 = runWikiNightlyNow();
    const p2 = runWikiNightlyNow(); // первый в сети: второй делит его результат, нового POST нет
    expect(countOf(RUN_URL)).toBe(1);
    post.resolve({ started: true });
    const [r1, r2] = await Promise.all([p1, p2]);
    expect(r1).toEqual({ ok: true });
    expect(r2).toEqual({ ok: true });
    expect(countOf(RUN_URL)).toBe(1);
    // следующий вызов после завершения — новый запрос (полёт снят); 409 «уже идёт» —
    // не отказ: тот же путь, что после 202, и второй цепочки опроса не заводит
    serveSeq({ [`POST ${RUN_URL}`]: [httpError(409, { code: 'wiki_nightly_running', message: 'разбор этой вики уже идёт' })] });
    const r3 = await runWikiNightlyNow();
    expect(r3).toEqual({ ok: true });
    expect(countOf(RUN_URL)).toBe(2);
    // опрос остался один (заведён первым запуском): за 30 с ровно один GET, не два
    serveSeq({ [`GET ${SETTINGS_URL}`]: [RUNNING(false)] });
    await vi.advanceTimersByTimeAsync(5000);
    expect(countOf(SETTINGS_URL)).toBe(1);
    await vi.advanceTimersByTimeAsync(30000);
    expect(countOf(SETTINGS_URL)).toBe(1);
  });

  it('runWikiNightlyNow_ОпросРазВ5СПокаИдёт_ПоследнийОтветФинальныйСнимок', async () => {
    serveSeq({
      [`POST ${RUN_URL}`]: [{ started: true }],
      [`GET ${SETTINGS_URL}`]: [RUNNING(true), RUNNING(true), RUNNING(false, LAST_NIGHT)],
    });
    const r = await runWikiNightlyNow();
    expect(r).toEqual({ ok: true });
    expect(countOf(SETTINGS_URL)).toBe(0); // до первого интервала — ни одного чтения
    await vi.advanceTimersByTimeAsync(5000);
    expect(countOf(SETTINGS_URL)).toBe(1);
    expect(getWikiSettingsSnapshot()!.nightlyRunning).toBe(true); // состояние прохода видно
    await vi.advanceTimersByTimeAsync(5000);
    expect(countOf(SETTINGS_URL)).toBe(2);
    await vi.advanceTimersByTimeAsync(5000);
    expect(countOf(SETTINGS_URL)).toBe(3);
    // третий ответ сказал «не идёт»: он и есть финальный снимок со свежим lastNight
    expect(getWikiSettingsSnapshot()!.nightlyRunning).toBe(false);
    expect(getWikiSettingsSnapshot()!.lastNight).toEqual(LAST_NIGHT);
    await vi.advanceTimersByTimeAsync(30000);
    expect(countOf(SETTINGS_URL)).toBe(3); // опрос встал: за 30 с ни одного нового GET
  });

  it('runWikiNightlyNow_СменаВладельцаВоВремяОпроса_ОтветПрежневоНеПишетсяНовыхЗапросовНет', async () => {
    const post = deferred();
    const get1 = deferred();
    serveSeq({
      [`GET ${SETTINGS_URL}`]: [RUNNING(false), get1.promise],
      [`POST ${RUN_URL}`]: [post.promise],
    });
    await loadWikiSettings(); // снимок есть до запуска
    post.resolve({ started: true });
    const r = await runWikiNightlyNow();
    expect(r).toEqual({ ok: true });
    expect(getWikiSettingsSnapshot()!.nightlyRunning).toBe(true); // оптимистичная запись u1
    const afterOptimistic = getWikiSettingsSnapshot();
    await vi.advanceTimersByTimeAsync(5000); // первый опрос начат (запрос u1, в сети)
    expect(countOf(SETTINGS_URL)).toBe(2);
    userId.mockReturnValue('u2'); // смена аккаунта посреди опроса
    get1.resolve(RUNNING(true, { ...LAST_NIGHT })); // ответ прежнего владельца приходит после смены
    await settle();
    // в снимок он не пишется (M2): состояние — то, что было на момент смены
    expect(getWikiSettingsSnapshot()).toBe(afterOptimistic);
    expect(getWikiSettingsSnapshot()!.lastNight).toBeNull();
    // опрос встал сменой: новых запросов нет
    await vi.advanceTimersByTimeAsync(30000);
    expect(countOf(SETTINGS_URL)).toBe(2);
  });

  it('runWikiNightlyNow_ОтказПоКоду_РезультатАНеИсключение_СостояниеНеЗалипает', async () => {
    serveSeq({ [`GET ${SETTINGS_URL}`]: [RUNNING(false)] });
    await loadWikiSettings();
    for (const [status, code] of [
      [409, 'wiki_nightly_disabled'],
      [409, 'wiki_llm_wiki_off'],
      [409, 'wiki_settings_invalid'],
    ] as Array<[number, string]>) {
      serveSeq({ [`POST ${RUN_URL}`]: [httpError(status, { code, message: 'отказ по контракту' })] });
      const r = await runWikiNightlyNow();
      if (r.ok) throw new Error('ожидался отказ по контракту, а запрос прошёл');
      expect(r).toEqual({ ok: false, code });
      // прохода нет: состояние не тронуто, опрос не заведён
      expect(getWikiSettingsSnapshot()!.nightlyRunning).toBe(false);
      await vi.advanceTimersByTimeAsync(30000);
      expect(countOf(SETTINGS_URL)).toBe(1); // только первая перечитка настроек
    }
  });

  it('runWikiNightlyNow_POSTСетевойСбой_РезультатБезКода_НеобработанногоRejectionНет', async () => {
    const seen: unknown[] = [];
    const onUnhandled = (e: unknown) => { seen.push(e); };
    process.on('unhandledRejection', onUnhandled);
    try {
      serveSeq({ [`GET ${SETTINGS_URL}`]: [RUNNING(false)], [`POST ${RUN_URL}`]: [new Error('сетевой сбой')] });
      await loadWikiSettings();
      const r = await runWikiNightlyNow();
      if (r.ok) throw new Error('ожидался отказ, а запрос прошёл');
      expect(r).toEqual({ ok: false, code: null }); // сетевой сбой — не отказ контракта: кода нет
      expect(getWikiSettingsSnapshot()!.nightlyRunning).toBe(false); // «идёт» не залипает
      await vi.advanceTimersByTimeAsync(10000);
      expect(countOf(SETTINGS_URL)).toBe(1); // опрос не заведён
      await settle();
      expect(seen).toEqual([]);
    } finally {
      process.off('unhandledRejection', onUnhandled);
    }
  });

  it('настройки: старый бэк без новых полей — false/undefined; с полями — читаются как есть', async () => {
    const OLD_LAST_NIGHT = {
      date: '2026-11-04', selected: 1, analyzed: 1, applied: 1, toInbox: 0,
      estTokens: 1000, outcome: 'completed',
    };
    serveSeq({ [`GET ${SETTINGS_URL}`]: [{ ...SETTINGS, lastNight: OLD_LAST_NIGHT }] });
    const snap = await loadWikiSettings();
    expect(snap.nightlyRunning).toBe(false); // поля нет — «не идёт», не ошибка
    expect(snap.lastNight!.rejected).toBeUndefined();
    expect(snap.lastNight!.manual).toBeUndefined();
    serveSeq({ [`GET ${SETTINGS_URL}`]: [RUNNING(true, LAST_NIGHT)] });
    const snap2 = await loadWikiSettings();
    expect(snap2.nightlyRunning).toBe(true);
    expect(snap2.lastNight).toEqual(LAST_NIGHT); // rejected/manual доходят до вызывающего
  });

  it('putWikiNightly_PUTСостоянияПрогонаНесёт_ОноСохраняетсяОтПоследнегоЧтения', async () => {
    serveSeq({ [`GET ${SETTINGS_URL}`]: [RUNNING(true)] });
    await loadWikiSettings();
    expect(getWikiSettingsSnapshot()!.nightlyRunning).toBe(true);
    // ответ PUT — настройки без поля («никакой записи настроек действие не делает», WN-5)
    serveSeq({ [`GET ${SETTINGS_URL}`]: [], [`POST ${RUN_URL}`]: [], 'PUT /wiki/settings': [SETTINGS] });
    const r = await putWikiNightly({ windowStartHour: 4 });
    expect(r.ok).toBe(true);
    expect(getWikiSettingsSnapshot()!.nightlyRunning).toBe(true); // не обнуляется PUT'ом
  });
});
