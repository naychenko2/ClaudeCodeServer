// W12 · Счётчики рельсы: inbox — пакеты со всех разделов, health — находки последнего отчёта
// выбранного раздела; ключа нет — health=null без запроса; отказ (409, сеть, отчёт «## Отказ») —
// null без исключения и без console.error; пустой отчёт — честный ноль. Перечитка: монтирование,
// смена ключа, refreshWikiRailCounts(), visibilitychange → visible. Порядок ответов — поколение
// на поток: гонка a→b (ответ a последним) не пишет число a; два refresh подряд — итог по последнему
// ответу. Размонтирование: поздний ответ не зовёт setState (модульный стор: компонент не держит
// состояния — тест проверяет отсутствие побочных эффектов).
//
// Мокан только транспорт (offline.request) и signalr — тот же приём, что в WikiInbox.test;
// транспорт stора (listWikiPackages/getWikiHealth) настоящий.
//
// Мутации этого прогона (вывод — в отчёте задачи):
//  (1) снять проверку поколения в loadHealth («снять поколение») → «гонка: ответ a…» краснеет;
//  (2) не ставить null при отказе (убрать .catch-ветку в loadInbox) → «отказ — числа нет» краснеет;
//  (3) считать находки и при «## Отказ» (value = findings.length без проверки failure) →
//      «отказ прогона в отчёте» краснеет;
//  (4) не перечитывать по refreshWikiRailCounts() (пустое тело) → «refresh перечитывает» краснеет.

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { mount, installFakeDom, uninstallFakeDom } from '../../../../components/chat/__tests__/fakeDom';
import { useWikiRailCounts, refreshWikiRailCounts } from '../useWikiRailCounts';

vi.mock('../../../../lib/signalr', async (orig) => ({
  ...await orig<typeof import('../../../../lib/signalr')>(),
  onMessage: vi.fn(() => () => {}),
  onReconnected: vi.fn(() => () => {}),
  joinUser: vi.fn(() => Promise.resolve()),
}));
vi.mock('../../../../lib/offline', async (orig) => ({
  ...await orig<typeof import('../../../../lib/offline')>(),
  request: vi.fn(),
  readStoredUserId: vi.fn(() => 'u1'),
}));

import { request } from '../../../../lib/offline';

type MockFn = ReturnType<typeof vi.fn>;
const req = request as unknown as MockFn;

const SEC_A = 'projects/p1';
const SEC_B = 'projects/beta';

// _health.md с n находками — форма бэкового RenderHealth (тот же образец, что HEALTH в WikiHealth.test)
const healthDoc = (n: number, section = 'projects/p1'): string => {
  const rows = Array.from({ length: n }, (_, i) => `- сирота · страница \`${section}/Т${i}.md\` · на страницу никто не ссылается`);
  return [`# Здоровье раздела «${section}»`, '', `## Находки (${n})`, ...rows, ''].join('\n');
};

const pkgList = (n: number) => ({
  packages: Array.from({ length: n }, (_, i) => ({ id: `pk${i}` })),
  invalidCount: 0,
});

type Counts = ReturnType<typeof useWikiRailCounts>;
const roots: Root[] = [];

function mountHook(key: string | null): { api: { current: Counts }; root: Root; rerender: (k: string | null) => void } {
  installFakeDom();
  const api = { current: null as unknown as Counts };
  const Harness = ({ k }: { k: string | null }) => { api.current = useWikiRailCounts(k); return null; };
  const { root } = mount(createElement(Harness, { k: key }));
  roots.push(root);
  return { api, root, rerender: k => act(() => { root.render(createElement(Harness, { k })); }) };
}

const listCalls = () => req.mock.calls.filter(([u]) => String(u).startsWith('/wiki/packages'));
const healthCalls = () => req.mock.calls.filter(([u]) => String(u).startsWith('/wiki/health/'));

async function settle(times = 10) {
  for (let i = 0; i < times; i++) await act(async () => { await Promise.resolve(); });
}

let errSpy: MockFn;
beforeEach(() => {
  req.mockReset();
  req.mockImplementation(() => Promise.resolve(null));
  errSpy = vi.spyOn(console, 'error').mockImplementation(() => {}) as unknown as MockFn;
});
afterEach(() => {
  for (const r of roots) act(() => r.unmount());
  roots.length = 0;
  errSpy.mockRestore();
  uninstallFakeDom();
});

describe('useWikiRailCounts (W12)', () => {
  it('числа: 3 пакета → inbox=3; отчёт с 2 находками → health=2', async () => {
    req.mockImplementation((url: string) => {
      const u = String(url);
      if (u.startsWith('/wiki/packages')) return Promise.resolve(pkgList(3));
      if (u.startsWith('/wiki/health/')) return Promise.resolve({ section: SEC_A, content: healthDoc(2) });
      return Promise.resolve(null);
    });
    const { api } = mountHook(SEC_A);
    await settle();
    expect(api.current.inbox).toBe(3);
    expect(api.current.health).toBe(2);
  });

  it('ключа раздела нет — health=null без запроса; inbox считается', async () => {
    req.mockImplementation((url: string) => {
      const u = String(url);
      if (u.startsWith('/wiki/packages')) return Promise.resolve(pkgList(3));
      if (u.startsWith('/wiki/health/')) return Promise.resolve({ section: SEC_A, content: healthDoc(2) });
      return Promise.resolve(null);
    });
    const { api } = mountHook(null);
    await settle();
    expect(api.current.inbox).toBe(3);
    expect(api.current.health).toBe(null);
    expect(healthCalls()).toHaveLength(0);
  });

  it('отказ (409 «вики выключена» и сеть) — числа нет, без исключения и console.error', async () => {
    req.mockImplementation((url: string) => {
      const u = String(url);
      if (u.startsWith('/wiki/packages')) return Promise.reject(new Error('offline'));
      if (u.startsWith('/wiki/health/')) return Promise.reject({ status: 409, body: { code: 'wiki_llm_wiki_off', message: 'off' } });
      return Promise.resolve(null);
    });
    const { api } = mountHook(SEC_A);
    await settle();
    expect(api.current.inbox).toBe(null);
    expect(api.current.health).toBe(null);
    expect(errSpy).not.toHaveBeenCalled();
  });

  it('отказ прогона в отчёте («## Отказ») — числа нет', async () => {
    req.mockImplementation((url: string) => {
      const u = String(url);
      if (u.startsWith('/wiki/packages')) return Promise.resolve(pkgList(0));
      if (u.startsWith('/wiki/health/')) {
        return Promise.resolve({ section: SEC_A, content: ['# Здоровье раздела «projects/p1»', '', '## Отказ', '- чтение раздела не завершилось: timeout'].join('\n') });
      }
      return Promise.resolve(null);
    });
    const { api } = mountHook(SEC_A);
    await settle();
    expect(api.current.health).toBe(null);
  });

  it('пустой отчёт — честный ноль, не отказ', async () => {
    req.mockImplementation((url: string) => {
      const u = String(url);
      if (u.startsWith('/wiki/packages')) return Promise.resolve(pkgList(0));
      if (u.startsWith('/wiki/health/')) return Promise.resolve({ section: SEC_A, content: healthDoc(0) });
      return Promise.resolve(null);
    });
    const { api } = mountHook(SEC_A);
    await settle();
    expect(api.current.health).toBe(0);
  });

  it('refreshWikiRailCounts() перечитывает числа по свежим данным', async () => {
    req.mockImplementation((url: string) => {
      const u = String(url);
      if (u.startsWith('/wiki/packages')) return Promise.resolve(pkgList(3));
      if (u.startsWith('/wiki/health/')) return Promise.resolve({ section: SEC_A, content: healthDoc(2) });
      return Promise.resolve(null);
    });
    const { api } = mountHook(SEC_A);
    await settle();
    expect(api.current.inbox).toBe(3);
    expect(api.current.health).toBe(2);
    const before = listCalls().length;
    req.mockImplementation((url: string) => {
      const u = String(url);
      if (u.startsWith('/wiki/packages')) return Promise.resolve(pkgList(4));
      if (u.startsWith('/wiki/health/')) return Promise.resolve({ section: SEC_A, content: healthDoc(1) });
      return Promise.resolve(null);
    });
    await act(async () => { refreshWikiRailCounts(); });
    await settle();
    expect(api.current.inbox).toBe(4);
    expect(api.current.health).toBe(1);
    expect(listCalls().length).toBeGreaterThan(before);
  });

  it('visibilitychange → visible: счётчики перечитываются; hidden — нет', async () => {
    // Перечитка живого экрана, побывавшего в фоне (useWikiRailCounts.ts:86-98): за время в фоне
    // счёт мог устареть (ночной прогон, решение в другой вкладке). Слушатель ставится на первой
    // подписке (subscribe:102) — мутация «снять attachVisibility()» здесь краснеет: слушателя нет.
    req.mockImplementation((url: string) => {
      const u = String(url);
      if (u.startsWith('/wiki/packages')) return Promise.resolve(pkgList(3));
      if (u.startsWith('/wiki/health/')) return Promise.resolve({ section: SEC_A, content: healthDoc(2) });
      return Promise.resolve(null);
    });
    const { api } = mountHook(SEC_A);
    await settle();
    expect(api.current.inbox).toBe(3);
    expect(api.current.health).toBe(2);

    // Слушатель висит на document фейк-DOM (образец: tabOwnerGuard.test.ts:100-106, приём
    // WikiPageView.test.ts:613); в этом файле иных visibilitychange-подписок нет — initConnectivity
    // не зовётся, signalr замокан
    const doc = (globalThis as unknown as { document: { visibilityState?: string; listeners: Record<string, Set<() => void>> } }).document;
    const handlers = [...(doc.listeners['visibilitychange'] ?? [])];
    expect(handlers).toHaveLength(1);
    const onVisibility = handlers[0];

    const beforeList = listCalls().length;
    const beforeHealth = healthCalls().length;
    req.mockImplementation((url: string) => {
      const u = String(url);
      if (u.startsWith('/wiki/packages')) return Promise.resolve(pkgList(4));
      if (u.startsWith('/wiki/health/')) return Promise.resolve({ section: SEC_A, content: healthDoc(1) });
      return Promise.resolve(null);
    });

    // Ушёл в фон: guard по visibilityState в обработчике — ни запроса, ни новых чисел
    doc.visibilityState = 'hidden';
    await act(async () => { onVisibility(); });
    await settle();
    expect(listCalls().length).toBe(beforeList);
    expect(healthCalls().length).toBe(beforeHealth);
    expect(api.current.inbox).toBe(3);

    // Вернулся на вкладку: оба числа перечитаны по свежим данным
    doc.visibilityState = 'visible';
    await act(async () => { onVisibility(); });
    await settle();
    expect(listCalls().length).toBe(beforeList + 1);
    expect(healthCalls().length).toBe(beforeHealth + 1);
    expect(api.current.inbox).toBe(4);
    expect(api.current.health).toBe(1);
  });

  it('гонка: ключ a → b, запоздалый ответ a не пишет своё число (поколение на поток)', async () => {
    let resolveA: (v: unknown) => void = () => {};
    req.mockImplementation((url: string) => {
      const u = String(url);
      if (u.startsWith('/wiki/packages')) return Promise.resolve(pkgList(3));
      if (u.startsWith('/wiki/health/')) {
        if (u.includes(SEC_A)) return new Promise(res => { resolveA = res; });
        return Promise.resolve({ section: SEC_B, content: healthDoc(5, 'projects/beta') });
      }
      return Promise.resolve(null);
    });
    const { api, rerender } = mountHook(SEC_A);
    await settle(); // ответ a висит
    rerender(SEC_B);
    await settle(); // ответ b — 5 находок
    expect(api.current.health).toBe(5);
    await act(async () => { resolveA({ section: SEC_A, content: healthDoc(2) }); });
    await settle();
    // число a (2) поверх b не легло
    expect(api.current.health).toBe(5);
  });

  it('два refresh подряд: итог по последнему ответу, без двойного счёта', async () => {
    req.mockImplementation((url: string) => {
      const u = String(url);
      if (u.startsWith('/wiki/packages')) return Promise.resolve(pkgList(3));
      if (u.startsWith('/wiki/health/')) return Promise.resolve({ section: SEC_A, content: healthDoc(2) });
      return Promise.resolve(null);
    });
    const { api } = mountHook(SEC_A);
    await settle();
    let resolveL: (v: unknown) => void = () => {};
    let resolveH: (v: unknown) => void = () => {};
    let n = 0;
    req.mockImplementation((url: string) => {
      const u = String(url);
      if (u.startsWith('/wiki/packages')) {
        n++;
        if (n === 1) return new Promise(res => { resolveL = res; });
        return Promise.resolve(pkgList(4));
      }
      if (u.startsWith('/wiki/health/')) {
        if (n === 1) return new Promise(res => { resolveH = res; });
        return Promise.resolve({ section: SEC_A, content: healthDoc(1) });
      }
      return Promise.resolve(null);
    });
    await act(async () => { refreshWikiRailCounts(); refreshWikiRailCounts(); });
    await settle(); // второй ответ быстрее первого
    expect(api.current.inbox).toBe(4);
    expect(api.current.health).toBe(1);
    await act(async () => { resolveL(pkgList(9)); resolveH({ section: SEC_A, content: healthDoc(9) }); });
    await settle();
    // запоздалый первый ответ отброшен: ни 9, ни двойного счёта
    expect(api.current.inbox).toBe(4);
    expect(api.current.health).toBe(1);
  });

  it('размонтирование: поздний ответ не зовёт setState, без исключения и console.error', async () => {
    let resolveH: (v: unknown) => void = () => {};
    req.mockImplementation((url: string) => {
      const u = String(url);
      if (u.startsWith('/wiki/packages')) return Promise.resolve(pkgList(3));
      if (u.startsWith('/wiki/health/')) return new Promise(res => { resolveH = res; });
      return Promise.resolve(null);
    });
    const { api, root } = mountHook(SEC_A);
    await settle();
    expect(api.current.inbox).toBe(3);
    roots.splice(roots.indexOf(root), 1);
    act(() => { root.unmount(); });
    const callsBefore = req.mock.calls.length;
    await act(async () => { resolveH({ section: SEC_A, content: healthDoc(2) }); });
    await settle();
    // поздний ответ не породил ни рендера подписчика (его нет), ни нового запроса, ни ошибки
    expect(req.mock.calls.length).toBe(callsBefore);
    expect(errSpy).not.toHaveBeenCalled();
  });
});
