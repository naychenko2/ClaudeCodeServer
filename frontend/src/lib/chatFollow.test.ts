import { describe, expect, it, vi } from 'vitest';
import { followChat, onChatFollow } from './chatFollow';

describe('chatFollow', () => {
  it('будит только ленту своего чата', () => {
    const a = vi.fn();
    const b = vi.fn();
    const offA = onChatFollow('s1', a);
    const offB = onChatFollow('s2', b);
    followChat('s1');
    expect(a).toHaveBeenCalledTimes(1);
    expect(b).not.toHaveBeenCalled();
    offA();
    offB();
  });

  it('после отписки лента сигнал не получает', () => {
    const a = vi.fn();
    const off = onChatFollow('s1', a);
    off();
    followChat('s1');
    expect(a).not.toHaveBeenCalled();
  });
});
