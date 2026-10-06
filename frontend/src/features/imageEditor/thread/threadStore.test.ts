import { beforeEach, describe, expect, it, vi } from 'vitest';

// Окружение node — localStorage нет; мокаем минимальную реализацию на Map
const store = new Map<string, string>();
(globalThis as unknown as { localStorage: Storage }).localStorage = {
  getItem: (k: string) => store.get(k) ?? null,
  setItem: (k: string, v: string) => { store.set(k, v); },
  removeItem: (k: string) => { store.delete(k); },
  clear: () => store.clear(),
  key: () => null,
  length: 0,
} as Storage;
import {
  __applyThreads, __resetThreadStore, ensureThreads, getFocusedThread, getThreadsState, subscribeThreadStore,
} from './threadStore';
import { threadsApi, type ImageThread, type ImageThreadsState } from './threadsApi';

// Стор нитей чата: фокус с сервера, защита от устаревших событий, подписка вне React (мост вида контекста)
const t1 = { id: 't1' } as ImageThread;
const withFocus = (revision = 1): ImageThreadsState => ({ focus: 't1', revision, threads: [t1] });
const noFocus = (revision = 1): ImageThreadsState => ({ focus: null, revision, threads: [t1] });

beforeEach(() => {
  localStorage.clear();
  __resetThreadStore();
  vi.restoreAllMocks();
});

describe('стор нитей', () => {
  it('загрузка чата с серверным фокусом отдаёт сфокусированную нить', async () => {
    vi.spyOn(threadsApi, 'get').mockResolvedValue(withFocus());
    await ensureThreads('p1', 'qa');
    expect(getFocusedThread('qa')?.id).toBe('t1');
  });

  it('состояние адресуется чату нити, а не открытому последним', () => {
    __applyThreads('qa', 'p1', noFocus());
    __applyThreads('other', 'p1', noFocus());
    __applyThreads('qa', 'p1', withFocus(2));
    expect(getFocusedThread('qa')?.id).toBe('t1');
    expect(getFocusedThread('other')).toBeNull();
  });

  it('событие старше известного состояния пропускается', () => {
    __applyThreads('qa', 'p1', withFocus(5));
    __applyThreads('qa', 'p1', noFocus(3));
    expect(getThreadsState('qa').revision).toBe(5);
    expect(getFocusedThread('qa')?.id).toBe('t1');
  });

  it('подписка вне React зовётся при смене состояния', () => {
    vi.spyOn(threadsApi, 'subscribe').mockReturnValue(() => {});
    const fn = vi.fn();
    const off = subscribeThreadStore(fn);
    __applyThreads('qa', 'p1', withFocus());
    expect(fn).toHaveBeenCalled();
    off();
  });
});
