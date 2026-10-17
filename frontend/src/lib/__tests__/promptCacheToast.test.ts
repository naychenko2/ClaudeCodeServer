import { describe, it, expect } from 'vitest';
import type { ChatItem, PromptCacheInfo } from '../../types';
import { promptCacheState } from '../promptCache';
import { takeExpiringCacheToast } from '../promptCacheToast';

const MIN = 60_000;
const T0 = 1_700_000_000_000;

const result = (promptCache?: PromptCacheInfo | null, contextTokens?: number): ChatItem =>
  ({ kind: 'result', subtype: 'success', durationMs: 100, numTurns: 1, contextTokens, promptCache });
const pc = (p: Partial<PromptCacheInfo>): PromptCacheInfo => ({ prefixTokens: 120_000, ...p });

function memStorage() {
  const m = new Map<string, string>();
  return { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => { m.set(k, v); } };
}

describe('takeExpiringCacheToast', () => {
  const items = [result(pc({ ttl: '1h', anchorAtMs: T0 }), 120_000)];
  const expiring = promptCacheState(items, T0 + 56 * MIN);

  it('один тост на якорь: повторные тики и вторая шапка молчат', () => {
    const s = memStorage();
    expect(expiring.status).toBe('expiring');
    expect(takeExpiringCacheToast('chat-1', expiring, false, s)).toBe(true);
    expect(takeExpiringCacheToast('chat-1', promptCacheState(items, T0 + 57 * MIN), false, s)).toBe(false);
    expect(takeExpiringCacheToast('chat-1', expiring, false, s)).toBe(false);
  });

  it('новый якорь — новый тост; другой чат — свой учёт', () => {
    const s = memStorage();
    takeExpiringCacheToast('chat-1', expiring, false, s);
    const next = [...items, result(pc({ ttl: '1h', anchorAtMs: T0 + 30 * MIN }), 121_000)];
    expect(takeExpiringCacheToast('chat-1', promptCacheState(next, T0 + 86 * MIN), false, s)).toBe(true);
    expect(takeExpiringCacheToast('chat-2', expiring, false, s)).toBe(true);
  });

  it('компакт-ход якорь не двигает — второго тоста нет', () => {
    const s = memStorage();
    takeExpiringCacheToast('chat-1', expiring, false, s);
    const compacted = [...items, { kind: 'compact_boundary', trigger: 'manual' } as ChatItem, result(undefined, undefined)];
    const after = promptCacheState(compacted, T0 + 57 * MIN);
    expect(after.anchorAtMs).toBe(T0);
    expect(takeExpiringCacheToast('chat-1', after, false, s)).toBe(false);
  });

  it('во время хода молчит и не тратит якорь', () => {
    const s = memStorage();
    expect(takeExpiringCacheToast('chat-1', expiring, true, s)).toBe(false);
    expect(takeExpiringCacheToast('chat-1', expiring, false, s)).toBe(true);
  });

  it('тёплый, остывший и неизвестный кэш — без тоста', () => {
    const s = memStorage();
    expect(takeExpiringCacheToast('chat-1', promptCacheState(items, T0 + 10 * MIN), false, s)).toBe(false);
    expect(takeExpiringCacheToast('chat-1', promptCacheState(items, T0 + 61 * MIN), false, s)).toBe(false);
    expect(takeExpiringCacheToast('chat-1', promptCacheState([result(undefined, 5_000)], T0), false, s)).toBe(false);
  });

  it('без чата или без хранилища — без тоста', () => {
    expect(takeExpiringCacheToast(undefined, expiring, false, memStorage())).toBe(false);
    expect(takeExpiringCacheToast('chat-1', expiring, false, null)).toBe(false);
  });
});
