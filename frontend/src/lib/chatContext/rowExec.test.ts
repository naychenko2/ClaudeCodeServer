import { beforeEach, describe, expect, it } from 'vitest';
import { objectKey, rememberAction, resetActionMemory } from './actionMemory';
import { setRunParam } from './actionRun';
import { execMenuTitle, selectRowAction } from './rowExec';
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

describe('заголовок меню «Чем»', () => {
  it('называет выбранное действие', () => {
    expect(execMenuTitle(run('stems'))).toBe('Чем выполнить «stems»');
    expect(execMenuTitle(null)).toBe('Чем выполнить');
  });
});

describe('«Чем» и ответ вопроса действия', () => {
  const stems: ContextAction = {
    id: 'stems', kind: 'run', label: 'Стемы', hint: 'Стемы', op: 'separate',
    question: { param: 'stemSet', title: 'Набор', options: [{ value: 'vocals', label: 'Вокал + минус' }, { value: '4', label: '4' }] },
  };
  const seen: (string | null | undefined)[] = [];
  const withQuestion: ContextKindApi = {
    kinds: ['audio'], icon: () => null, preview: () => null, actions: () => [stems],
    executors: (_c, _id, answer) => { seen.push(answer); return model; },
  };
  const audio = { ...primary('human'), kind: 'audio' };

  it('вид получает первый вариант вопроса, пока человек не выбрал', () => {
    seen.length = 0;
    selectRowAction(withQuestion, ctx, audio, []);
    expect(seen.at(-1)).toBe('vocals');
  });

  it('и выбранный человеком ответ той же области, что у кнопки и панели', () => {
    seen.length = 0;
    setRunParam('s1', `${objectKey(audio)}:stems`, 'stemSet', '4');
    selectRowAction(withQuestion, ctx, audio, []);
    expect(seen.at(-1)).toBe('4');
  });

  it('у действия без вопроса ответа нет', () => {
    seen.length = 0;
    selectRowAction({ ...withQuestion, actions: () => [run('denoise')] }, ctx, audio, []);
    expect(seen.at(-1)).toBeNull();
  });
});
