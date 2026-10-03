import { beforeEach, describe, expect, it } from 'vitest';
import {
  forgetActionMemory, objectKey, pickDefaultAction, presetAction, rememberAction, resetActionMemory, resolveAction,
} from './actionMemory';
import type { ContextAction } from './types';

const run = (id: string, extra: Partial<ContextAction> = {}): ContextAction =>
  ({ id, kind: 'run', label: id, hint: id, op: id, ...extra });
const editor: ContextAction = { id: 'editor', kind: 'editor', label: 'Редактор', hint: '', open: () => {} };

beforeEach(() => resetActionMemory());

describe('pickDefaultAction (Р1)', () => {
  it('объект человека: первое run без причины серости', () => {
    expect(pickDefaultAction([editor, run('edit'), run('removeBg')], 'human')).toBe('edit');
    expect(pickDefaultAction([run('repaint', { disabledReason: 'нужно выделение' }), run('stems')], 'human')).toBe('stems');
  });

  it('человек: все run серые или их нет — «Чат»', () => {
    expect(pickDefaultAction([run('repaint', { disabledReason: 'x' })], 'human')).toBeNull();
    expect(pickDefaultAction([editor], 'human')).toBeNull();
    expect(pickDefaultAction([], 'human')).toBeNull();
  });

  it('объект агента — всегда «Чат»', () => {
    expect(pickDefaultAction([run('edit'), run('removeBg')], 'agent')).toBeNull();
  });
});

describe('objectKey', () => {
  it('порядок ключей ref не важен, id элемента не входит', () => {
    const a = objectKey({ kind: 'image', ref: { threadId: 't', versionId: 'v' } });
    expect(objectKey({ kind: 'image', ref: { versionId: 'v', threadId: 't' } })).toBe(a);
    expect(objectKey({ kind: 'image', ref: { threadId: 't', versionId: 'v2' } })).not.toBe(a);
    expect(objectKey({ kind: 'audio', ref: { threadId: 't', versionId: 'v' } })).not.toBe(a);
  });
});

describe('память выбора действия', () => {
  const actions = [run('edit'), run('removeBg')];

  it('первый показ объекта человека берёт умолчание, объекта агента — «Чат»', () => {
    expect(resolveAction('s1', 'image:A', 'human', actions).actionId).toBe('edit');
    expect(resolveAction('s1', 'image:B', 'agent', actions).actionId).toBeNull();
  });

  it('выбор держится на том же объекте, у другого объекта своё умолчание', () => {
    resolveAction('s1', 'image:A', 'human', actions);
    rememberAction('s1', 'image:A', 'removeBg');
    expect(resolveAction('s1', 'image:A', 'human', actions).actionId).toBe('removeBg');
    expect(resolveAction('s1', 'image:C', 'human', actions).actionId).toBe('edit');
  });

  it('ручной «Чат» держится, пока ключ объекта тот же, умолчание его не откатывает', () => {
    resolveAction('s1', 'image:A', 'human', actions);
    rememberAction('s1', 'image:A', null);
    expect(resolveAction('s1', 'image:A', 'human', actions).actionId).toBeNull();
    expect(resolveAction('s1', 'image:A', 'human', actions).actionId).toBeNull();
  });

  it('выбранное действие пропало или посерело — «Чат»', () => {
    rememberAction('s1', 'image:A', 'removeBg');
    expect(resolveAction('s1', 'image:A', 'human', [run('edit')]).actionId).toBeNull();
    expect(resolveAction('s1', 'image:A', 'human', [run('edit'), run('removeBg', { disabledReason: 'x' })]).actionId).toBeNull();
  });

  it('память у чатов раздельная', () => {
    rememberAction('s1', 'image:A', 'removeBg');
    expect(resolveAction('s2', 'image:A', 'human', actions).actionId).toBe('edit');
  });
});

describe('предвыбор', () => {
  const actions = [run('edit'), run('draw')];

  it('сильнее умолчания, применяется один раз', () => {
    presetAction('s1', 'image:A', { actionId: 'draw', prefill: 'кот' });
    const first = resolveAction('s1', 'image:A', 'human', actions);
    expect(first).toMatchObject({ actionId: 'draw', preset: { prefill: 'кот' } });
    const again = resolveAction('s1', 'image:A', 'human', actions);
    expect(again.actionId).toBe('draw');
    expect(again.preset).toBeUndefined();
  });

  it('ждёт, пока основным станет объект с этим ключом', () => {
    presetAction('s1', 'image:A', { actionId: 'draw' });
    expect(resolveAction('s1', 'image:B', 'human', actions).actionId).toBe('edit');
    expect(resolveAction('s1', 'image:A', 'human', actions).actionId).toBe('draw');
  });

  it('предвыбор недоступного действия не применяется и не пропадает', () => {
    presetAction('s1', 'image:A', { actionId: 'draw' });
    expect(resolveAction('s1', 'image:A', 'human', [run('edit')]).actionId).toBe('edit');
  });
});

describe('чистка памяти', () => {
  it('удаление чата чистит только его, выход — всё', () => {
    for (const s of ['A', 'B']) rememberAction(s, 'image:X', 'edit');
    forgetActionMemory('A');
    expect(resolveAction('A', 'image:X', 'human', [run('draw')]).actionId).toBe('draw');
    expect(resolveAction('B', 'image:X', 'human', [run('draw'), run('edit')]).actionId).toBe('edit');
    resetActionMemory();
    expect(resolveAction('B', 'image:X', 'human', [run('draw'), run('edit')]).actionId).toBe('draw');
  });
});
