// Личные пороги «кэш скоро остынет» (фича «Индикатор prompt cache и перенос в новый
// чат», шаг F-3 и доработка F-3r). Две части: стор cacheWarnPrefs (засев из /me
// внешним, сохранение, сброс, 400) и применение порога к индикатору — RingPillBadge
// рендерится статически, как в RingPillBadge.test.ts; без личного порога работают
// дефолты (5 мин и 1 мин). Диалог подписан на стор: поздний засев обновляет поля,
// пока пользователь не правил их руками; числа разбираются строго («5abc» — ошибка).
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { act, createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { afterAll, beforeAll, describe, expect, it, vi } from 'vitest';
// Диалог — Modal на портале: серверный рендер порталы не поддерживает,
// поэтому он монтируется фейк-DOM-клиентом (паттерн TaskCreatedView.test.ts)
import { installFakeDom, mount, reactProps, textNodes, uninstallFakeDom } from './fakeDom';

// offline.request подменяется до импорта стора: прогона сети в тестах нет
let mockRequest: (url: string, opts?: { method?: string; body?: string }) => Promise<unknown> = () => Promise.resolve({});
vi.mock('../../../lib/offline', () => ({
  __esModule: true,
  request: (url: string, opts?: { method?: string; body?: string }) => mockRequest(url, opts),
}));

import {
  getCacheWarnThresholds, resetCacheWarnOnLogout, saveCacheWarnThresholds,
  setCacheWarnThresholdsFromServer,
} from '../../../lib/cacheWarnPrefs';
import { CacheWarnDialog } from '../CacheWarnDialog';
import { RingPillBadge, type CostStats, type FalCostStats, type ProviderBalance } from '../ChatHeaderBar';
import type { GlifGenStats } from '../glifStats';
import type { ClaudeBilling, ChatItem } from '../../../types';
import type { ContextEstimate } from '../../../lib/context';
import type { RateWindow } from '../../../lib/rateLimit';
import type { PromptCacheWire } from '../../../lib/promptCache';

const cost: CostStats = { cost: 0, input: 0, output: 0, cacheRead: 0, cacheCreate: 0, turns: 0, results: 0 };
const falCost: FalCostStats = { total: 0, count: 0, byModel: new Map() };
const glifCost: GlifGenStats = { count: 0, credits: 0, hasCredits: false, byType: new Map() };
const estimate: ContextEstimate = { window: 200000, pct: 42, fresh: false, level: 'normal' };
const balance: ProviderBalance | null = null;
const billing: ClaudeBilling = 'subscription';
const windows: RateWindow[] = [];

const MIN = 60_000;
const cacheItem = (over: Partial<PromptCacheWire> = {}): ChatItem => ({
  kind: 'result', subtype: 'success', durationMs: 1000, numTurns: 1,
  promptCache: { readTokens: 184000, writtenTokens: 1000, anchorAtMs: Date.now(), ttl: '1h', missReason: 'none', ...over },
} as ChatItem);

function renderPill(items: ChatItem[]): string {
  return renderToStaticMarkup(createElement(RingPillBadge, {
    estimate, isWaiting: false, isCompacting: false, canCompact: false, onCompact: () => {},
    online: true, assistantName: 'Тест',
    isCliProvider: false, providerName: 'Claude', cost, falCost, glifCost, balance, billing, windows,
    items,
  }));
}

// Видимый текст минут/«остыл» у циферблата: появляется только в состояниях
// «остывает» и «остыл» — то есть ровно когда личный порог применён
const visibleLeftText = (html: string): string | null => />(\d+ мин|остыл)</.exec(html)?.[1] ?? null;

describe('cacheWarnPrefs: чтение /me, сохранение, сброс', () => {
  it('значения из /me применяются; null — «не настроено»', () => {
    setCacheWarnThresholdsFromServer({ warn1hMin: 10, warn5mMin: 2 });
    expect(getCacheWarnThresholds()).toEqual({ warn1hMin: 10, warn5mMin: 2 });
    setCacheWarnThresholdsFromServer(null);
    expect(getCacheWarnThresholds()).toBeNull();
  });

  it('сохранение: PUT с телом {warn1hMin, warn5mMin}, состояние — из ответа', async () => {
    const calls: { url: string; body: string }[] = [];
    mockRequest = (url, opts) => {
      calls.push({ url, body: opts?.body ?? '' });
      return Promise.resolve({ cacheWarnThresholds: { warn1hMin: 10, warn5mMin: 2 } });
    };
    await saveCacheWarnThresholds({ warn1hMin: 10, warn5mMin: 2 });
    expect(calls[0].url).toBe('/auth/cache-thresholds');
    expect(JSON.parse(calls[0].body)).toEqual({ warn1hMin: 10, warn5mMin: 2 });
    expect(getCacheWarnThresholds()).toEqual({ warn1hMin: 10, warn5mMin: 2 });
  });

  it('сброс: пустое тело {}, состояние — null', async () => {
    const calls: { url: string; body: string }[] = [];
    mockRequest = (url, opts) => {
      calls.push({ url, body: opts?.body ?? '' });
      return Promise.resolve({ cacheWarnThresholds: null });
    };
    setCacheWarnThresholdsFromServer({ warn1hMin: 10, warn5mMin: 2 });
    await saveCacheWarnThresholds(null);
    expect(calls[0].body).toBe('{}');
    expect(getCacheWarnThresholds()).toBeNull();
  });

  it('400 бэка: ошибка с текстом наружу, состояние не меняется', async () => {
    mockRequest = () => Promise.reject(
      Object.assign(new Error('Порог часового кэша — 1–59 мин, 5-минутного — 1–4 мин'), { status: 400 }),
    );
    setCacheWarnThresholdsFromServer({ warn1hMin: 10, warn5mMin: 2 });
    const err = await saveCacheWarnThresholds({ warn1hMin: 60, warn5mMin: 2 }).catch(e => e as Error & { status?: number });
    expect(err.status).toBe(400);
    expect(err.message).toContain('1–59');
    expect(getCacheWarnThresholds()).toEqual({ warn1hMin: 10, warn5mMin: 2 });
  });

  it('смена владельца: повторный засев с null — пороги прежнего не применяются (M1)', () => {
    setCacheWarnThresholdsFromServer({ warn1hMin: 10, warn5mMin: 2 }); // первый владелец
    setCacheWarnThresholdsFromServer(null); // /me второго пользователя — не настроено
    expect(getCacheWarnThresholds()).toBeNull();
  });

});

describe('Применение личного порога к индикатору (F-3)', () => {
  it('часовой кэш, личный порог 15 мин: остаток 10 мин — «остывает», минуты на циферблате', () => {
    setCacheWarnThresholdsFromServer({ warn1hMin: 15, warn5mMin: 2 });
    const html = renderPill([cacheItem({ anchorAtMs: Date.now() - 50 * MIN })]);
    expect(visibleLeftText(html)).toBe('10 мин');
    expect(html).toContain('cc-cache-breathe');
  });

  it('без личного порога тот же остаток 10 мин — тёплый: минут нет', () => {
    setCacheWarnThresholdsFromServer(null);
    const html = renderPill([cacheItem({ anchorAtMs: Date.now() - 50 * MIN })]);
    expect(visibleLeftText(html)).toBeNull();
    expect(html).not.toContain('cc-cache-breathe');
  });

  it('5-минутный кэш, порог 4 мин: остаток 3 мин — «остывает»; дефолт порога — тёплый', () => {
    setCacheWarnThresholdsFromServer({ warn1hMin: 5, warn5mMin: 4 });
    expect(visibleLeftText(renderPill([cacheItem({ ttl: '5m', anchorAtMs: Date.now() - 2 * MIN })]))).toBe('3 мин');
    setCacheWarnThresholdsFromServer(null);
    expect(visibleLeftText(renderPill([cacheItem({ ttl: '5m', anchorAtMs: Date.now() - 2 * MIN })]))).toBeNull();
  });
});

describe('CacheWarnDialog: поля и дефолты', () => {
  beforeAll(() => installFakeDom());
  afterAll(() => uninstallFakeDom());
  // Диалог — портал в document.body: поля ищем обходом body, не контейнером mount
  const openDialog = () => mount(createElement(CacheWarnDialog, { onClose: () => {} }));
  const body = () => (globalThis as { document: { body: unknown } }).document.body as never;
  const walk = (x: { nodeType: number; nodeName: string; childNodes?: unknown[] }, f: (n: never) => void) => {
    if (x.nodeType === 1) f(x as never);
    for (const c of x.childNodes ?? []) walk(c as never, f);
  };
  const inputValues = (): string[] => {
    const out: string[] = [];
    walk(body(), (x) => {
      if ((x as { nodeName: string }).nodeName === 'INPUT') out.push(String(reactProps(x)?.value ?? ''));
    });
    return out;
  };
  const buttonByLabel = (label: string): never => {
    let hit: never | null = null;
    walk(body(), (x) => {
      if (!hit && (x as { nodeName: string }).nodeName === 'BUTTON' && textNodes(x).join('').includes(label)) hit = x;
    });
    return hit!;
  };
  const typeInto = (v: string) => typeIntoIdx(0, v);
  const typeIntoIdx = (i: number, v: string) => {
    const inputs: never[] = [];
    walk(body(), (x) => { if ((x as { nodeName: string }).nodeName === 'INPUT') inputs.push(x as never); });
    const props = reactProps(inputs[i]);
    (props!.onChange as (e: { target: { value: string } }) => void)({ target: { value: v } });
  };
  const visibleText = (): string => textNodes(body()).join(' ');

  it('не настроено — поля с дефолтами индикатора (5 мин и 1 мин)', () => {
    setCacheWarnThresholdsFromServer(null);
    const { root } = openDialog();
    expect(inputValues()).toEqual(['5', '1']);
    root.unmount();
  });

  it('настроено — поля с личными значениями', () => {
    setCacheWarnThresholdsFromServer({ warn1hMin: 10, warn5mMin: 2 });
    const { root } = openDialog();
    expect(inputValues()).toEqual(['10', '2']);
    root.unmount();
  });

  it('заголовок и кнопка сброса на месте', () => {
    setCacheWarnThresholdsFromServer(null);
    const { root } = openDialog();
    const text = visibleText();
    expect(text).toContain('Когда предупреждать об остывании кэша');
    expect(text).toContain('Сбросить к дефолту');
    root.unmount();
  });

  it('стор сам не подгружает /me: без внешнего посева — дефолты (M1)', async () => {
    setCacheWarnThresholdsFromServer(null);
    // Старый ensureCacheWarnLoaded сходил бы на /me сам и применил эти значения;
    // теперь засев только снаружи (App.tsx), внутреннего запроса у стора нет.
    mockRequest = () => Promise.resolve({ cacheWarnThresholds: { warn1hMin: 10, warn5mMin: 2 } });
    const { root } = openDialog();
    await act(async () => {});
    expect(inputValues()).toEqual(['5', '1']);
    root.unmount();
  });

  it('поздний засев стора — диалог показывает реальные значения до первого сохранения (M2)', () => {
    setCacheWarnThresholdsFromServer(null);
    const { root } = openDialog();
    expect(inputValues()).toEqual(['5', '1']);
    act(() => setCacheWarnThresholdsFromServer({ warn1hMin: 7, warn5mMin: 3 }));
    expect(inputValues()).toEqual(['7', '3']);
    root.unmount();
  });

  it('флаг «правил сам» пофайловый: правка часового не затирается, 5-минутный пересевается (F-3r2)', () => {
    setCacheWarnThresholdsFromServer(null);
    const { root } = openDialog();
    act(() => typeInto('12')); // правил только часовое поле
    act(() => setCacheWarnThresholdsFromServer({ warn1hMin: 7, warn5mMin: 3 }));
    // правленное поле сохранено, незатронутое — поздний засев применил
    expect(inputValues()).toEqual(['12', '3']);
    root.unmount();
  });

  it('и наоборот: правка 5-минутного не блокирует поздний засев часового (F-3r2)', () => {
    setCacheWarnThresholdsFromServer(null);
    const { root } = openDialog();
    act(() => typeIntoIdx(1, '2')); // правил только 5-минутное поле
    act(() => setCacheWarnThresholdsFromServer({ warn1hMin: 7, warn5mMin: 3 }));
    expect(inputValues()).toEqual(['7', '2']);
    root.unmount();
  });

  it('«5abc» не проходит локальную проверку: текст ошибки, PUT нет (T1)', async () => {
    setCacheWarnThresholdsFromServer(null);
    const calls: string[] = [];
    mockRequest = (url) => { calls.push(url); return Promise.resolve({}); };
    const { root } = openDialog();
    act(() => typeInto('5abc'));
    await act(async () => { (reactProps(buttonByLabel('Сохранить'))!.onClick as () => void)(); });
    expect(visibleText()).toContain('Введите целые числа в минутах');
    expect(calls.length).toBe(0);
    root.unmount();
  });

  it('400 бэка: в диалоге виден текст ошибки ввода, а не общий сбой (T2)', async () => {
    setCacheWarnThresholdsFromServer(null);
    mockRequest = () => Promise.reject(
      Object.assign(new Error('Порог часового кэша — 1–59 мин, 5-минутного — 1–4 мин'), { status: 400 }),
    );
    const { root } = openDialog();
    await act(async () => { (reactProps(buttonByLabel('Сохранить'))!.onClick as () => void)(); });
    expect(visibleText()).toContain('1–59');
    expect(visibleText()).not.toContain('Не удалось сохранить');
    root.unmount();
  });

  it('общий сбой (не-400): показывается фолбэк, а не e.message (F-3r2)', async () => {
    setCacheWarnThresholdsFromServer(null);
    mockRequest = () => Promise.reject(new Error('connection refused'));
    const { root } = openDialog();
    await act(async () => { (reactProps(buttonByLabel('Сохранить'))!.onClick as () => void)(); });
    expect(visibleText()).toContain('Не удалось сохранить');
    expect(visibleText()).not.toContain('connection refused');
    root.unmount();
  });

  it('logout: сброс стора — до нового /me работают дефолты (F-3r2, F-4r2)', () => {
    setCacheWarnThresholdsFromServer({ warn1hMin: 10, warn5mMin: 2 }); // прежний владелец
    // logout и cc-unauthorized в App.tsx зовут ровно это до ответа следующего /me
    resetCacheWarnOnLogout();
    const { root } = openDialog();
    expect(inputValues()).toEqual(['5', '1']);
    root.unmount();
  });

  it('App.tsx вызывает resetCacheWarnOnLogout на logout и cc-unauthorized (F-4r2)', () => {
    // Сам сброс проверяется тестом выше; здесь — wiring: удаление любого из двух
    // вызовов из App.tsx (мутация) краснит этот тест.
    const src = readFileSync(fileURLToPath(new URL('../../../App.tsx', import.meta.url)), 'utf8');
    const calls = src.match(/resetCacheWarnOnLogout\(\)/g);
    expect(calls?.length, 'вызовы сброса в App.tsx: cc-unauthorized и logout').toBe(2);
  });
});
