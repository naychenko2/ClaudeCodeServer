import { beforeEach, describe, expect, it } from 'vitest';
import { objectKey, resetActionMemory } from './actionMemory';
import { composerSurfaceFor } from './surface';
import type { ChatContextPrimary, ContextAction, ContextKindApi } from './types';

const primary = (by: 'human' | 'agent' = 'human'): ChatContextPrimary => ({
  id: 'p1', kind: 'image', ref: { threadId: 't' }, by, addedAt: '', label: 'hero.png', version: 'v2', thumb: null, missing: false, role: null,
});
const run = (id: string): ContextAction => ({ id, kind: 'run', label: id, hint: id, op: id });
const apiWith = (actions: readonly ContextAction[]): ContextKindApi =>
  ({ kinds: ['image'], icon: () => null, preview: () => null, actions: () => actions });
const ctx = { projectId: 'p', sessionId: 's1', isMobile: false };
const input = (over: Partial<Parameters<typeof composerSurfaceFor>[0]> = {}) =>
  ({ primary: primary(), refs: [], api: apiWith([run('edit'), run('removeBg')]), ctx, ...over });

beforeEach(() => resetActionMemory());

describe('мост поля ввода composerSurfaceFor', () => {
  it('вид с действиями — чипы; выбрано первое run-действие объекта человека', () => {
    const s = composerSurfaceFor(input());
    expect(s.surface).toBe('actions');
    if (s.surface === 'actions') {
      expect(s.action?.id).toBe('edit');
      expect(s.objectKey).toBe(objectKey(primary()));
    }
  });

  it('вид без действий — обычный «Чат»', () => {
    expect(composerSurfaceFor(input({ api: apiWith([]) })).surface).toBe('chat');
  });

  it('вид без вклада в слоте context-kind — обычный «Чат»', () => {
    expect(composerSurfaceFor(input({ api: null })).surface).toBe('chat');
  });

  it('без объекта строки действий нет', () => {
    expect(composerSurfaceFor(input({ primary: null })).surface).toBe('chat');
  });

  it('объект агента (By = Agent) встаёт на «Чат»', () => {
    const s = composerSurfaceFor(input({ primary: primary('agent') }));
    expect(s.surface).toBe('actions');
    if (s.surface === 'actions') expect(s.action).toBeNull();
  });
});
