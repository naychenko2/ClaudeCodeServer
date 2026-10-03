import { beforeEach, describe, expect, it } from 'vitest';
import { objectKey, rememberAction, resetActionMemory } from './actionMemory';
import { selectRowAction } from './rowExec';
import type { ChatContextPrimary, ContextAction, ContextKindApi } from './types';

const primary = (by: 'human' | 'agent'): ChatContextPrimary => ({
  id: 'p1', kind: 'image', ref: { threadId: 't' }, by, addedAt: '', label: 'hero.png', version: 'v2', thumb: null, missing: false, role: null,
});
const run = (id: string): ContextAction => ({ id, kind: 'run', label: id, hint: id, op: id });
const model = { rows: [], value: 'auto', onChange: () => {} };
const api: ContextKindApi = {
  kinds: ['image'], icon: () => null, preview: () => null,
  actions: () => [run('edit'), run('removeBg')],
  executors: () => model,
};
const ctx = { projectId: 'p', sessionId: 's1', isMobile: false };

beforeEach(() => resetActionMemory());

describe('«Чем» в строке контекста (Р2)', () => {
  it('объект человека: выбрано первое действие — исполнители есть', () => {
    const sel = selectRowAction(api, ctx, primary('human'), []);
    expect(sel.action?.id).toBe('edit');
    expect(sel.executors).toBe(model);
  });

  it('«Чат» — «Чем» нет: объект агента', () => {
    const sel = selectRowAction(api, ctx, primary('agent'), []);
    expect(sel.action).toBeNull();
    expect(sel.executors).toBeNull();
  });

  it('«Чат» — «Чем» нет: ручной выбор «Чата» у объекта человека держится', () => {
    rememberAction('s1', objectKey(primary('human')), null);
    const sel = selectRowAction(api, ctx, primary('human'), []);
    expect(sel.action).toBeNull();
    expect(sel.executors).toBeNull();
  });

  it('вид без реестра (вертикаль выключена): действий и «Чем» нет', () => {
    const sel = selectRowAction(null, ctx, primary('human'), []);
    expect(sel).toEqual({ action: null, executors: null });
  });
});
