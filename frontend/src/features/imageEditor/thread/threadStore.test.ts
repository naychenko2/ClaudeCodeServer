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
import { __resetComposerStrips, getActiveStrip, selectStrip } from '../../../lib/composerStrips';
import { __applyThreads, __resetThreadStore, enterChat, ensureThreads, IMAGES_STRIP } from './threadStore';
import { threadsApi, type ImageThreadsState } from './threadsApi';

// Полоса над композером от стора нитей (решение 3 ADR-019): фокус нити чата → «Картинки»,
// иначе запомненная полоса чата, иначе Git
const AVAIL = ['git', IMAGES_STRIP];
const withFocus = (revision = 1): ImageThreadsState => ({ focus: 't1', revision, threads: [] });
const noFocus = (revision = 1): ImageThreadsState => ({ focus: null, revision, threads: [] });

beforeEach(() => {
  localStorage.clear();
  __resetThreadStore();
  __resetComposerStrips();
  vi.restoreAllMocks();
});

describe('стор нитей и полоса над композером', () => {
  it('загрузка чата с серверным фокусом открывает «Картинки» (как после перезагрузки)', async () => {
    vi.spyOn(threadsApi, 'get').mockResolvedValue(withFocus());
    await ensureThreads('p1', 'qa');
    expect(getActiveStrip('qa', AVAIL)).toBe(IMAGES_STRIP);
  });

  it('запрос полосы адресуется чату нити, а не открытому последним', () => {
    __applyThreads('qa', 'p1', noFocus());
    __applyThreads('git-chat', 'p1', noFocus());
    // Фокус в «QA gen» приходит событием, пока открыт другой чат
    __applyThreads('qa', 'p1', withFocus(2));
    expect(getActiveStrip('qa', AVAIL)).toBe(IMAGES_STRIP);
    expect(getActiveStrip('git-chat', AVAIL)).toBe('git');
  });

  it('ручной уход на Git держится до выхода из чата: при входе фокус снова даёт «Картинки»', () => {
    __applyThreads('qa', 'p1', withFocus());
    selectStrip('qa', 'git');
    expect(getActiveStrip('qa', AVAIL)).toBe('git');
    enterChat('git-chat');
    enterChat('qa');
    expect(getActiveStrip('qa', AVAIL)).toBe(IMAGES_STRIP);
  });

  it('вход в чат без фокуса — запомненная полоса, без неё Git', () => {
    __applyThreads('a', 'p1', noFocus());
    __applyThreads('b', 'p1', noFocus());
    selectStrip('a', IMAGES_STRIP);
    enterChat('a');
    enterChat('b');
    expect(getActiveStrip('a', AVAIL)).toBe(IMAGES_STRIP);
    expect(getActiveStrip('b', AVAIL)).toBe('git');
  });
});
