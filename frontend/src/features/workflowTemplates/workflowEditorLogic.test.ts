import { describe, expect, it, vi } from 'vitest';
import {
  CONFLICT_MESSAGE, blankTemplate, classifySaveError, forkToProject, listOrNull, parseList,
  saveGuarded, templateSnapshot,
} from './workflowEditorLogic';
import { STAGE_ROLES } from './workflowTemplateModel';
import type { WorkflowTemplate } from './types';

const tpl = (over: Partial<WorkflowTemplate> = {}): WorkflowTemplate => ({
  id: 'builtin-full-cycle', name: 'Полный цикл', version: 3, builtInKey: 'full-cycle',
  stages: [{ role: 'step', presence: 'on', prompt: 'x' }], ...over,
});

describe('parseList / listOrNull', () => {
  it('режет по запятой, убирает пустые и повторы', () => {
    expect(parseList(' tasks, web ,, tasks')).toEqual(['tasks', 'web']);
  });
  it('пустой список — null (без сужения)', () => {
    expect(listOrNull([])).toBeNull();
    expect(listOrNull(['a'])).toEqual(['a']);
  });
});

describe('blankTemplate / forkToProject', () => {
  it('новый шаблон — все роли маршрута, version 0, id пустой', () => {
    const t = blankTemplate();
    expect(t.version).toBe(0);
    expect(t.id).toBe('');
    expect(t.stages.map(s => s.role)).toEqual([...STAGE_ROLES]);
  });
  it('копия в проект: basedOn на версию оригинала, version 0, не встроенный, этапы не общие', () => {
    const g = tpl();
    const p = forkToProject(g);
    expect(p.basedOn).toEqual({ templateId: 'builtin-full-cycle', version: 3 });
    expect(p.version).toBe(0);
    expect(p.id).toBe('');
    expect(p.builtInKey).toBeNull();
    p.stages[0].prompt = 'изменено';
    expect(g.stages[0].prompt).toBe('x');
  });
});

describe('templateSnapshot', () => {
  it('одинаков у равных шаблонов и меняется от правки этапа', () => {
    expect(templateSnapshot(tpl())).toBe(templateSnapshot(tpl()));
    const edited = tpl({ stages: [{ role: 'step', presence: 'on', prompt: 'y' }] });
    expect(templateSnapshot(edited)).not.toBe(templateSnapshot(tpl()));
  });
});

describe('classifySaveError', () => {
  it('409 — «перечитать»', () => {
    const r = classifySaveError(Object.assign(new Error('x'), { status: 409 }));
    expect(r.kind).toBe('conflict');
    expect(r.message).toBe(CONFLICT_MESSAGE);
  });
  it('400 — ошибки валидатора раскладываются по ролям этапов, прочее — общие', () => {
    const r = classifySaveError(Object.assign(new Error('bad'), {
      status: 400,
      body: { error: 'bad', errors: ['этап «final»: роль несъёмная, Presence «off» запрещён.', 'шаблон: список этапов не задан.'] },
    }));
    expect(r.kind).toBe('invalid');
    expect(r.stageErrors.final).toEqual(['роль несъёмная, Presence «off» запрещён.']);
    expect(r.generalErrors).toEqual(['шаблон: список этапов не задан.']);
  });
  it('400 без списка — текст ошибки общим', () => {
    const r = classifySaveError(Object.assign(new Error('Шаблон не задан.'), { status: 400, body: { error: 'Шаблон не задан.' } }));
    expect(r.generalErrors).toEqual(['Шаблон не задан.']);
  });
  it('прочее (в том числе не Error) — failed, без исключения', () => {
    expect(classifySaveError(new Error('сеть')).kind).toBe('failed');
    expect(classifySaveError('строка').message).toBe('Не удалось сохранить шаблон');
  });
});

describe('saveGuarded', () => {
  it('форма та же — applied', async () => {
    const saved = tpl({ version: 4 });
    const r = await saveGuarded({ tpl: tpl(), save: async () => saved, activeKey: () => 'a' });
    expect(r).toEqual({ kind: 'applied', saved });
  });

  it('форму переключили во время сохранения — stale: ответ старого не применяется к форме нового', async () => {
    let key = 'a';
    const saved = tpl({ version: 4 });
    const r = await saveGuarded({
      tpl: tpl(),
      save: async () => { key = 'b'; return saved; },
      activeKey: () => key,
    });
    expect(r.kind).toBe('stale');
  });

  it('отказ превращается в значение, а не в rejection', async () => {
    const save = vi.fn().mockRejectedValue(Object.assign(new Error('c'), { status: 409 }));
    const r = await saveGuarded({ tpl: tpl(), save, activeKey: () => 'a' });
    expect(r.kind).toBe('conflict');
  });
});
