// Чистый контроллер «История · 7 дней» (createHistoryController) — тест без React/DOM.
// Паттерн — как useLocalModelMedia.test.ts: управляемый fetchPage (deferred),
// микрозадачи прогоняем через tick().
//
// Мутации, которые обязаны покрасить:
// - «устаревший ответ перезаписывает свежие» — seq-счётчик;
// - «тихая перезагрузка показывает скелетон» — setFilters/refresh не трогают loading;
// - «loadMore без курсора ходит за страницей» — подгрузка дублирует;
// - «live-маркер не перечитывает при совпадении» — лишний запрос;
// - «dispose не гасит последующие ответы» — устаревший ответ всё равно применяется.

import { describe, it, expect, vi } from 'vitest';
import { createHistoryController } from './useMediaHistory';
import type { MediaHistoryPage, MediaHistoryQuery, MediaHistoryRecord } from './api';

const tick = () => new Promise<void>(r => setTimeout(r, 0));

function rec(id: string): MediaHistoryRecord {
  return { id, kind: 'image', op: 'generate_image', model: 'M', prompt: null, params: null, owner: 'u', status: 'completed', outcome: 'completed', error: null, runSeconds: 10, enqueuedAt: null, startedAt: null, finishedAt: null, source: null };
}

function page(ids: string[], nextCursor: string | null): MediaHistoryPage {
  return { records: ids.map(rec), nextCursor, counters: { kinds: {}, outcomes: {} } };
}

function make() {
  const calls: MediaHistoryQuery[] = [];
  const pendings: Array<{ resolve: (p: MediaHistoryPage) => void; reject: (e: Error) => void }> = [];
  const fetchPage = vi.fn((q: MediaHistoryQuery) => {
    calls.push(q);
    return new Promise<MediaHistoryPage>((resolve, reject) => {
      pendings.push({ resolve, reject });
    });
  });
  const c = createHistoryController(fetchPage);
  const last = () => pendings[pendings.length - 1];
  return {
    c, calls, fetchPage, pendings,
    resolveLast: (p: MediaHistoryPage) => { if (last()) last().resolve(p); },
    rejectLast: (e: Error) => { if (last()) last().reject(e); },
    resolveAt: (i: number, p: MediaHistoryPage) => { if (pendings[i]) pendings[i].resolve(p); },
  };
}

describe('createHistoryController: первичная загрузка', () => {
  it('activate → скелетон, затем данные и курсор', async () => {
    const { c, resolveLast } = make();
    c.activate();
    expect(c.getSnapshot().loading).toBe(true);
    resolveLast(page(['a', 'b'], 'c1'));
    await tick();
    const s = c.getSnapshot();
    expect(s.loading).toBe(false);
    expect(s.records.map(r => r.id)).toEqual(['a', 'b']);
    expect(s.cursor).toBe('c1');
    expect(s.error).toBeNull();
  });

  it('сбой первичной загрузки → error «list»', async () => {
    const { c, rejectLast } = make();
    c.activate();
    rejectLast(new Error('net'));
    await tick();
    expect(c.getSnapshot().error).toBe('list');
    expect(c.getSnapshot().loading).toBe(false);
  });
});

describe('createHistoryController: «Показать ещё»', () => {
  it('loadMore дописывает следующую страницу, курсор обновляется', async () => {
    const { c, resolveLast } = make();
    c.activate();
    resolveLast(page(['a', 'b'], 'c1'));
    await tick();
    c.loadMore();
    expect(c.getSnapshot().loadingMore).toBe(true);
    resolveLast(page(['c'], null));
    await tick();
    const s = c.getSnapshot();
    expect(s.records.map(r => r.id)).toEqual(['a', 'b', 'c']);
    expect(s.cursor).toBeNull();
    expect(s.loadingMore).toBe(false);
  });

  it('loadMore без курсора — новый запрос не идёт', async () => {
    const { c, resolveLast, calls } = make();
    c.activate();
    resolveLast(page(['a'], null));
    await tick();
    const before = calls.length;
    c.loadMore();
    await tick();
    expect(calls.length).toBe(before);
    expect(c.getSnapshot().loadingMore).toBe(false);
  });

  it('сбой подгрузки → error «more», список не тронут', async () => {
    const { c, resolveLast, rejectLast } = make();
    c.activate();
    resolveLast(page(['a', 'b'], 'c1'));
    await tick();
    c.loadMore();
    rejectLast(new Error('net'));
    await tick();
    expect(c.getSnapshot().error).toBe('more');
    expect(c.getSnapshot().loadingMore).toBe(false);
    expect(c.getSnapshot().records.map(r => r.id)).toEqual(['a', 'b']);
  });
});

describe('createHistoryController: фильтры', () => {
  it('setFilters → тихая перезагрузка: старый список держится, loading не горит', async () => {
    const { c, resolveLast, calls } = make();
    c.activate();
    resolveLast(page(['a', 'b'], 'c1'));
    await tick();
    c.setFilters({ kind: 'image' });
    const mid = c.getSnapshot();
    expect(mid.records.map(r => r.id)).toEqual(['a', 'b']); // старый список
    expect(mid.loading).toBe(false); // тихая — без скелетона
    resolveLast(page(['x'], null));
    await tick();
    expect(calls[1].kind).toBe('image');
    expect(calls[1].from).toBeTruthy(); // период → from
    expect(c.getSnapshot().records.map(r => r.id)).toEqual(['x']);
  });

  it('setFilters на те же значения → запрос не идёт', async () => {
    const { c, resolveLast, calls } = make();
    c.activate();
    resolveLast(page(['a'], 'c1'));
    await tick();
    const before = calls.length;
    c.setFilters({ kind: 'all' }); // дефолт
    await tick();
    expect(calls.length).toBe(before);
  });

  it('resetFilters → дефолт (kind all, period 7d) и тихая перезагрузка', async () => {
    const { c, resolveLast, calls } = make();
    c.activate();
    resolveLast(page(['a'], 'c1'));
    await tick();
    c.setFilters({ kind: 'video', period: '24h' });
    resolveLast(page(['b'], 'c2'));
    await tick();
    c.resetFilters();
    expect(c.getSnapshot().filters).toEqual({ kind: 'all', period: '7d', outcome: null, model: null, login: null });
    resolveLast(page(['c'], null));
    await tick();
    expect(calls[calls.length - 1].kind).toBeNull(); // all → null
  });
});

describe('createHistoryController: retry и live-маркер', () => {
  it('retry после ошибки списка → скелетон, затем данные', async () => {
    const { c, resolveLast, rejectLast } = make();
    c.activate();
    rejectLast(new Error('net'));
    await tick();
    expect(c.getSnapshot().error).toBe('list');
    c.retry();
    expect(c.getSnapshot().loading).toBe(true);
    resolveLast(page(['a'], null));
    await tick();
    expect(c.getSnapshot().error).toBeNull();
    expect(c.getSnapshot().records.map(r => r.id)).toEqual(['a']);
  });

  it('onLiveMarker: совпадение → без запроса; изменение → тихая перезагрузка', async () => {
    const { c, resolveLast, calls } = make();
    c.activate();
    resolveLast(page(['a'], 'c1'));
    await tick();
    const media = { lastHour: [{ finishedAt: 1, op: 'x' }], running: [], queue: [] };
    c.onLiveMarker(media); // первый маркер (≠ '') → тихая перезагрузка
    await tick();
    expect(calls.length).toBe(2);
    resolveLast(page(['a'], 'c1'));
    await tick();
    // тот же маркер → тишина
    c.onLiveMarker(media);
    await tick();
    expect(calls.length).toBe(2);
    // изменился → перезагрузка
    c.onLiveMarker({ lastHour: [{ finishedAt: 2, op: 'x' }], running: [], queue: [] });
    await tick();
    expect(calls.length).toBe(3);
  });

  it('onLiveMarker во время первичной загрузки → без лишнего запроса', async () => {
    const { c, resolveLast, calls } = make();
    c.activate();
    expect(c.getSnapshot().loading).toBe(true);
    c.onLiveMarker({ lastHour: [{ finishedAt: 1, op: 'x' }], running: [], queue: [] });
    await tick();
    expect(calls.length).toBe(1); // только первичный
    resolveLast(page(['a'], null));
    await tick();
  });
});

describe('createHistoryController: seq и dispose', () => {
  it('устаревший ответ (после смены фильтров) не перезаписывает свежие данные', async () => {
    const { c, resolveAt, calls } = make();
    c.activate(); // запрос #1 (my=1)
    c.setFilters({ kind: 'image' }); // запрос #2 (my=2)
    // сначала ответит ПЕРВИЧНЫЙ (устаревший): my=1 ≠ seq=2 → игнор
    resolveAt(0, page(['old'], 'c1'));
    await tick();
    expect(c.getSnapshot().records).toEqual([]);
    // теперь свежий: my=2 = seq=2 → применяется
    resolveAt(1, page(['new'], null));
    await tick();
    expect(c.getSnapshot().records.map(r => r.id)).toEqual(['new']);
    expect(calls.length).toBe(2);
  });

  it('dispose: последующий ответ не применяется', async () => {
    const { c, resolveLast } = make();
    c.activate();
    c.dispose();
    resolveLast(page(['a'], null));
    await tick();
    expect(c.getSnapshot().records).toEqual([]);
  });
});
