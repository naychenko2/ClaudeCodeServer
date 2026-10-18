import { describe, it, expect } from 'vitest';
import type { ChatItem } from '../../types';
import { contextHandoffOffered, contextHandoffWarm, takeContextDangerToast } from '../contextHandoff';

const MIN = 60_000;
const T0 = 1_700_000_000_000;

function memStorage() {
  const m = new Map<string, string>();
  return { getItem: (k: string) => m.get(k) ?? null, setItem: (k: string, v: string) => { m.set(k, v); } };
}

describe('contextHandoffOffered', () => {
  it('кнопка только при warn и danger', () => {
    expect(contextHandoffOffered('normal')).toBe(false);
    expect(contextHandoffOffered('warn')).toBe(true);
    expect(contextHandoffOffered('danger')).toBe(true);
  });
});

describe('contextHandoffWarm', () => {
  const feed: ChatItem[] = [{
    kind: 'result', subtype: 'success', durationMs: 1, numTurns: 1, contextTokens: 120_000,
    promptCache: { ttl: '1h', anchorAtMs: T0, prefixTokens: 118_000 },
  }];

  it('тёплый кэш — тёплый путь, остывший — холодный', () => {
    expect(contextHandoffWarm(feed, T0 + 10 * MIN)).toBe(true);
    expect(contextHandoffWarm(feed, T0 + 130 * MIN)).toBe(false);
  });

  it('нет ленты или статус unknown (сторонний провайдер) — холодный', () => {
    expect(contextHandoffWarm(undefined, T0)).toBe(false);
    const unknown: ChatItem[] = [{ kind: 'result', subtype: 'success', durationMs: 1, numTurns: 1, contextTokens: 5_000 }];
    expect(contextHandoffWarm(unknown, T0)).toBe(false);
  });
});

describe('takeContextDangerToast', () => {
  it('один тост на чат: повтор после сжатия и нового роста молчит', () => {
    const s = memStorage();
    expect(takeContextDangerToast('chat-1', 'danger', false, false, s)).toBe(true);
    expect(takeContextDangerToast('chat-1', 'danger', false, false, s)).toBe(false);
    // сжали — уровень упал, потом снова вырос
    expect(takeContextDangerToast('chat-1', 'normal', false, false, s)).toBe(false);
    expect(takeContextDangerToast('chat-1', 'danger', false, false, s)).toBe(false);
    expect(takeContextDangerToast('chat-2', 'danger', false, false, s)).toBe(true);
  });

  it('во время хода и сжатия молчит и показ не тратит', () => {
    const s = memStorage();
    expect(takeContextDangerToast('chat-1', 'danger', true, false, s)).toBe(false);
    expect(takeContextDangerToast('chat-1', 'danger', false, true, s)).toBe(false);
    expect(takeContextDangerToast('chat-1', 'danger', false, false, s)).toBe(true);
  });

  it('normal и warn — без тоста', () => {
    const s = memStorage();
    expect(takeContextDangerToast('chat-1', 'normal', false, false, s)).toBe(false);
    expect(takeContextDangerToast('chat-1', 'warn', false, false, s)).toBe(false);
    expect(takeContextDangerToast('chat-1', 'danger', false, false, s)).toBe(true);
  });

  it('без чата или без хранилища — без тоста', () => {
    expect(takeContextDangerToast(undefined, 'danger', false, false, memStorage())).toBe(false);
    expect(takeContextDangerToast('chat-1', 'danger', false, false, null)).toBe(false);
  });
});
