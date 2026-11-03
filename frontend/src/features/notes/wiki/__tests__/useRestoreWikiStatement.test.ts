// Хук «Вернуть мою» (F3b): один запрос на двойной клик, ответ после смены страницы
// отбрасывается по ключу «раздел + путь», 409 wiki_page_changed не перезаписывает, rejection
// наружу не выходит. Транспорт (offline.request) мокан, стор вики настоящий.
//
// Мутации этого прогона (вывод — в отчёте задачи):
//  (1) снять проверку ключа `idRef.current !== pageId` в хуке → «гонка: ответ после смены
//      страницы» и «гонка: отказ после смены страницы» краснеют;
//  (2) убрать замок inFlightRef → «двойной клик — один запрос» краснеет.

import { act, createElement } from 'react';
import type { Root } from 'react-dom/client';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { mount, installFakeDom, uninstallFakeDom } from '../../../../components/chat/__tests__/fakeDom';
import { useRestoreWikiStatement, type RestoreOutcome } from '../useRestoreWikiStatement';
import { __test as wikiStore, type WikiOverrideDto } from '../../../../lib/wiki';

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

const req = request as unknown as ReturnType<typeof vi.fn>;

const SEC = 'projects/p1';
const KEY_A = 'projects/p1/a';
const KEY_B = 'projects/p1/b';
const OV: WikiOverrideDto = {
  op: 'supersede', statement: 'Новая формулировка ИИ.', previous: 'Моя формулировка.',
  at: '2026-11-02T05:48:00Z', packageId: 'pk1',
};

type Api = ReturnType<typeof useRestoreWikiStatement>;
const roots: Root[] = [];

function mountHook(pageKey: string): { api: { current: Api }; rerender: (pageKey: string) => void } {
  installFakeDom();
  const api = { current: null as unknown as Api };
  const Harness = ({ pk }: { pk: string }) => { api.current = useRestoreWikiStatement(SEC, pk); return null; };
  const { root } = mount(createElement(Harness, { pk: pageKey }));
  roots.push(root);
  return { api, rerender: pk => act(() => { root.render(createElement(Harness, { pk })); }) };
}

const posts = () => req.mock.calls.filter(([u]) => String(u).startsWith('/wiki/pages/restore'));
const pageGets = () => req.mock.calls.filter(([u]) => String(u).startsWith('/wiki/page?'));

async function settle(times = 10) {
  for (let i = 0; i < times; i++) await act(async () => { await Promise.resolve(); });
}

beforeEach(() => {
  wikiStore.reset();
  req.mockReset();
  req.mockImplementation(() => Promise.resolve({ pageKey: KEY_A, content: '' }));
});

afterEach(() => {
  for (const r of roots) act(() => r.unmount());
  roots.length = 0;
  uninstallFakeDom();
});

describe('useRestoreWikiStatement', () => {
  it('отправляет пару overrides[] как current/restore с baseHash', async () => {
    req.mockImplementation((url: string) => (
      url.startsWith('/wiki/pages/restore') ? Promise.resolve({ status: 'applied', hash: 'h2', content: 'x' }) : Promise.resolve({ pageKey: KEY_A, content: '' })
    ));
    const { api } = mountHook(KEY_A);
    let out: RestoreOutcome | undefined;
    await act(async () => { out = await api.current.restore(OV, 'h1'); });
    expect(out).toBe('done');
    expect(posts()).toHaveLength(1);
    const [url, init] = posts()[0] as [string, { method: string; body: string }];
    expect(url).toBe('/wiki/pages/restore');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body)).toEqual({
      section: SEC, page: KEY_A, baseHash: 'h1', current: OV.statement, restore: OV.previous,
    });
    // страница перечитана после applied
    expect(pageGets()).toHaveLength(1);
  });

  it('без previous запрос не уходит', async () => {
    const { api } = mountHook(KEY_A);
    let out: RestoreOutcome | undefined;
    await act(async () => { out = await api.current.restore({ ...OV, previous: null }, 'h1'); });
    expect(out).toBe('nopair');
    expect(req).not.toHaveBeenCalled();
  });

  it('двойной клик — один запрос', async () => {
    let resolve!: (v: unknown) => void;
    req.mockImplementation((url: string) => (
      url.startsWith('/wiki/pages/restore') ? new Promise(r => { resolve = r; }) : Promise.resolve({ pageKey: KEY_A, content: '' })
    ));
    const { api } = mountHook(KEY_A);
    let second: RestoreOutcome | undefined;
    await act(async () => {
      void api.current.restore(OV, 'h1');
      second = await api.current.restore(OV, 'h1');
    });
    expect(second).toBe('busy');
    expect(posts()).toHaveLength(1);
    await act(async () => { resolve({ status: 'applied', hash: 'h2', content: '' }); });
    await settle();
    expect(posts()).toHaveLength(1);
  });

  it('409 wiki_page_changed: страница перечитана, повторного запроса нет, текст для Notice', async () => {
    req.mockImplementation((url: string) => (
      url.startsWith('/wiki/pages/restore')
        ? Promise.reject({ status: 409, body: { code: 'wiki_page_changed', message: 'изменилась', page: { pageKey: KEY_A, content: 'свежая', hash: 'h9' } } })
        : Promise.resolve({ pageKey: KEY_A, content: 'свежая' })
    ));
    const { api } = mountHook(KEY_A);
    let out: RestoreOutcome | undefined;
    await act(async () => { out = await api.current.restore(OV, 'h1'); });
    await settle();
    expect(out).toBe('conflict');
    expect(posts()).toHaveLength(1);
    expect(pageGets()).toHaveLength(1);
    expect(api.current.error).toContain('Страница изменилась');
    expect(api.current.busy).toBe(false);
  });

  it('сетевой сбой: rejection наружу не выходит, ошибка текстом', async () => {
    req.mockImplementation(() => Promise.reject(new Error('offline')));
    const { api } = mountHook(KEY_A);
    let out: RestoreOutcome | undefined;
    await act(async () => { out = await api.current.restore(OV, 'h1'); });
    expect(out).toBe('failed');
    expect(api.current.error).toBeTruthy();
  });

  it('гонка: ответ после смены страницы отбрасывается по ключу — ни перечитки, ни состояния', async () => {
    let resolve!: (v: unknown) => void;
    req.mockImplementation((url: string) => (
      url.startsWith('/wiki/pages/restore') ? new Promise(r => { resolve = r; }) : Promise.resolve({ pageKey: KEY_A, content: '' })
    ));
    const { api, rerender } = mountHook(KEY_A);
    let out: RestoreOutcome | undefined;
    let pending!: Promise<RestoreOutcome>;
    await act(async () => { pending = api.current.restore(OV, 'h1'); });
    rerender(KEY_B);
    await act(async () => { resolve({ status: 'applied', hash: 'h2', content: '' }); out = await pending; });
    await settle();
    expect(out).toBe('stale');
    // слот страницы B не перетёрт чужой перечиткой, состояние B чистое
    expect(pageGets()).toHaveLength(0);
    expect(api.current.busy).toBe(false);
    expect(api.current.error).toBeNull();
  });

  it('гонка: отказ после смены страницы тоже отбрасывается и не просачивается наружу', async () => {
    let reject!: (e: unknown) => void;
    req.mockImplementation((url: string) => (
      url.startsWith('/wiki/pages/restore') ? new Promise((_, rj) => { reject = rj; }) : Promise.resolve({ pageKey: KEY_A, content: '' })
    ));
    const { api, rerender } = mountHook(KEY_A);
    let out: RestoreOutcome | undefined;
    let pending!: Promise<RestoreOutcome>;
    await act(async () => { pending = api.current.restore(OV, 'h1'); });
    rerender(KEY_B);
    await act(async () => {
      reject({ status: 409, body: { code: 'wiki_page_changed', message: 'x' } });
      out = await pending;
    });
    await settle();
    expect(out).toBe('stale');
    expect(pageGets()).toHaveLength(0);
    expect(api.current.error).toBeNull();
  });
});
