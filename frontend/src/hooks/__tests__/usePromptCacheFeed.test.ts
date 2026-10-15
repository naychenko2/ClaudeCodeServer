import { describe, it, expect } from 'vitest';
import type { ChatItem } from '../../types';
import { nextPromptCacheFeed } from '../usePromptCache';

const user = (text: string): ChatItem => ({ kind: 'user', text } as ChatItem);
const text = (t: string): ChatItem => ({ kind: 'text', text: t } as ChatItem);
const result = (): ChatItem => ({ kind: 'result', subtype: 'success', durationMs: 1, numTurns: 1 });

describe('nextPromptCacheFeed', () => {
  it('стрим-дельты при том же result держат прежнюю ленту', () => {
    const r = result();
    const before = [user('a'), r, user('b')];
    const feed = nextPromptCacheFeed(null, 'S', before);
    const delta = [...before, text('час')];
    const next = nextPromptCacheFeed(feed, 'S', delta);
    expect(next).toBe(feed);
    expect(next.items).toBe(before);
  });

  it('новый result хода сменяет ленту', () => {
    const feed = nextPromptCacheFeed(null, 'S', [user('a'), result()]);
    const after = [...feed.items, user('b'), result()];
    expect(nextPromptCacheFeed(feed, 'S', after).items).toBe(after);
  });

  it('переход из чата A без хода в чат B посреди хода показывает ленту B', () => {
    const a = [user('a'), result()];
    const feedA = nextPromptCacheFeed(null, 'A', a);
    const b = [user('b'), result(), user('b2'), text('стрим')];
    expect(nextPromptCacheFeed(feedA, 'B', b).items).toBe(b);
  });

  it('чат, открытый посреди хода: подгрузка истории сменяет пустую ленту', () => {
    const empty = nextPromptCacheFeed(null, 'S', []);
    const loaded = [user('a'), result(), user('b'), text('стрим')];
    expect(nextPromptCacheFeed(empty, 'S', loaded).items).toBe(loaded);
  });
});
