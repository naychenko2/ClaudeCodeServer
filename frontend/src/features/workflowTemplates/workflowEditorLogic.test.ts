import { describe, expect, it, vi } from 'vitest';
import {
  CONFLICT_MESSAGE, blankTemplate, classifySaveError, forkToProject, listOrNull, parseList,
  saveGuarded, templateSnapshot, normalizeForSave, presenceEditable, showWriteButton,
  canWriteProjectTemplates, splitEntries, newTemplateDraft, takeComposition, saveAsNew, discardChanges, canSave,
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
  // Дефект G7: копия обязана нести имена этапов — без них «Создать проектный» терял Title,
  // и в копии были бы две неотличимые «Проверки».
  it('копия в проект несёт имена этапов', () => {
    const g = tpl({ stages: [
      { role: 'qa', presence: 'optional', id: 'qa-plan', title: 'Проверка плана' },
      { role: 'qa', presence: 'optional', id: 'qa', title: 'Проверка' },
    ] });
    const p = forkToProject(g);
    expect(p.stages.map(s => s.title)).toEqual(['Проверка плана', 'Проверка']);
  });
});

describe('templateSnapshot', () => {
  it('одинаков у равных шаблонов и меняется от правки этапа', () => {
    expect(templateSnapshot(tpl())).toBe(templateSnapshot(tpl()));
    const edited = tpl({ stages: [{ role: 'step', presence: 'on', prompt: 'y' }] });
    expect(templateSnapshot(edited)).not.toBe(templateSnapshot(tpl()));
  });
  it('правка id, «после» и линии делает шаблон «грязным»; неявные значения — нет', () => {
    const two = (over: object) => tpl({ stages: [
      { role: 'plan', presence: 'on' },
      { role: 'step', presence: 'on', ...over },
    ] });
    const base = templateSnapshot(two({}));
    expect(templateSnapshot(two({ id: 'step', after: ['plan'] }))).toBe(base);
    expect(templateSnapshot(two({ id: 'step-b' }))).not.toBe(base);
    expect(templateSnapshot(two({ after: [] }))).not.toBe(base);
    expect(templateSnapshot(two({ lane: 'back' }))).not.toBe(base);
  });
  // Дефект G7: без имени в снимке правка одного Title не делала бы форму «грязной» —
  // её нельзя было бы сохранить.
  it('правка одного имени этапа делает шаблон «грязным»', () => {
    const two = (t: string | null) => tpl({ stages: [
      { role: 'qa', presence: 'optional', id: 'qa-plan', title: t },
      { role: 'qa', presence: 'optional', id: 'qa', title: 'Проверка' },
    ] });
    expect(templateSnapshot(two('Проверка плана'))).not.toBe(templateSnapshot(two(null)));
    expect(templateSnapshot(two('Проверка плана'))).not.toBe(templateSnapshot(two('Проверка плана2')));
    expect(templateSnapshot(two('Проверка плана'))).toBe(templateSnapshot(two('Проверка плана')));
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

describe('права и нормализация', () => {
  it('переключатель присутствия: у step/merge/final недоступен, у остальных — да', () => {
    for (const r of ['step', 'merge', 'final']) expect(presenceEditable(r)).toBe(false);
    for (const r of ['cut', 'design', 'plan', 'review', 'qa']) expect(presenceEditable(r)).toBe(true);
  });
  it('кнопка записи: не-владелец проекта её не видит', () => {
    expect(canWriteProjectTemplates('u1', 'u2')).toBe(false);
    expect(canWriteProjectTemplates('u1', undefined)).toBe(false);
    expect(canWriteProjectTemplates('u1', 'u1')).toBe(true);
    expect(showWriteButton(false, false)).toBe(false);
    expect(showWriteButton(true, true)).toBe(false);
    expect(showWriteButton(true, false)).toBe(true);
  });
  it('перед отправкой порог молчания не клампится: вне диапазона его не «чинят» молча', () => {
    const t = normalizeForSave(tpl({ stages: [
      { role: 'plan', presence: 'on', stallMinutes: 2 },
      { role: 'qa', presence: 'on' },
      { role: 'review', presence: 'on', stallMinutes: 999 },
    ] }));
    expect(t.stages.map(s => s.stallMinutes)).toEqual([2, null, 999]);
  });
  // Та же нормализация имени, что на сторе (WorkflowTemplateStore.Normalize: trim, «пустая → null»).
  it('перед отправкой имя этапа триммится; из одних пробелов — null', () => {
    const t = normalizeForSave(tpl({ stages: [
      { role: 'qa', presence: 'on', id: 'qa-plan', title: '  Проверка плана ' },
      { role: 'qa', presence: 'on', id: 'qa', title: '   ' },
      { role: 'final', presence: 'on' },
    ] }));
    expect(t.stages.map(s => s.title)).toEqual(['Проверка плана', null, null]);
  });
});

describe('splitEntries: форма ответа списка — обёртки с областью', () => {
  const entries = [
    { scope: 'global' as const, projectId: null, template: tpl({ id: 'g1', name: 'Глобальный' }) },
    { scope: 'project' as const, projectId: 'p1', template: tpl({ id: 'l1', name: 'Свой' }) },
  ];

  it('в проекте: глобальные отдельно, «Проектные» без глобальных', () => {
    const r = splitEntries(entries, true);
    expect(r.globals.map(t => t.id)).toEqual(['g1']);
    expect(r.locals.map(t => t.id)).toEqual(['l1']);
  });

  it('админская вкладка: раскрывает шаблоны, а не обёртки', () => {
    const r = splitEntries([entries[0]], false);
    expect(r.locals[0].name).toBe('Глобальный');
    expect(r.locals[0].stages).toBeDefined();
  });

  it('глобальный с тем же id, что у проектного, не прячет проектный', () => {
    const r = splitEntries([entries[0], { ...entries[1], template: tpl({ id: 'g1', name: 'Копия' }) }], true);
    expect(r.locals.map(t => t.name)).toEqual(['Копия']);
  });
});

describe('черновик: пустой, «Взять состав», копия, откат', () => {
  it('newTemplateDraft — без этапов, version 0', () => {
    const d = newTemplateDraft();
    expect(d.stages).toEqual([]);
    expect(d).toMatchObject({ id: '', version: 0 });
  });
  it('takeComposition копирует этапы пресета, имя черновика остаётся, пресет не трогается', () => {
    const preset = tpl({ stages: [{ role: 'cut', presence: 'on', after: [] }, { role: 'step', presence: 'on' }] });
    const d = takeComposition(preset, newTemplateDraft('Мой'));
    expect(d.name).toBe('Мой');
    expect(d.stages).toEqual(preset.stages);
    d.stages[0].presence = 'off';
    expect(preset.stages[0].presence).toBe('on');
  });
  it('saveAsNew — новый ключ, version 0, без встроенности и basedOn, этапы — копия', () => {
    const src = tpl({ basedOn: { templateId: 'g', version: 2 } });
    const c = saveAsNew(src);
    expect(c).toMatchObject({ id: '', version: 0, builtInKey: null, basedOn: null, name: 'Полный цикл (копия)' });
    expect(saveAsNew(src, '  Свой ').name).toBe('Свой');
    c.stages[0].prompt = 'y';
    expect(src.stages[0].prompt).toBe('x');
  });
  it('discardChanges возвращает копию сохранённого, правки черновика её не задевают', () => {
    const saved = tpl();
    const d = discardChanges(saved);
    expect(d).toEqual(saved);
    d.stages[0].prompt = 'changed';
    expect(saved.stages[0].prompt).toBe('x');
  });
});

describe('canSave', () => {
  const ok = tpl({ stages: [{ role: 'step', presence: 'on' }] });
  const fail = (kind: 'conflict' | 'invalid' | 'failed') =>
    ({ kind, message: '', stageErrors: {}, generalErrors: [] });
  it('чистый шаблон без отказа — можно', () => {
    expect(canSave(ok)).toBe(true);
    expect(canSave(ok, null)).toBe(true);
  });
  it('клиентская ошибка этапа (порог молчания вне диапазона) блокирует', () => {
    expect(canSave(tpl({ stages: [{ role: 'plan', presence: 'on', stallMinutes: 3 }] }))).toBe(false);
  });
  it('409 и 400 блокируют, сетевой отказ — нет', () => {
    expect(canSave(ok, fail('conflict'))).toBe(false);
    expect(canSave(ok, fail('invalid'))).toBe(false);
    expect(canSave(ok, fail('failed'))).toBe(true);
  });
});

describe('saveGuarded: конкурентность', () => {
  it('два одновременных save одного шаблона — одна отправка; после неё можно снова', async () => {
    let release: (t: WorkflowTemplate) => void = () => {};
    const save = vi.fn(() => new Promise<WorkflowTemplate>(r => { release = r; }));
    const a = saveGuarded({ tpl: tpl(), save, activeKey: () => 'a' });
    const b = saveGuarded({ tpl: tpl(), save, activeKey: () => 'a' });
    release(tpl({ version: 4 }));
    expect((await a).kind).toBe('applied');
    expect(await b).toEqual(await a);
    expect(save).toHaveBeenCalledTimes(1);
    const c = saveGuarded({ tpl: tpl(), save: async () => tpl({ version: 5 }), activeKey: () => 'a' });
    expect((await c).kind).toBe('applied');
  });
  it('разные шаблоны не блокируют друг друга', async () => {
    const save = vi.fn(async (t: WorkflowTemplate) => t);
    await Promise.all([
      saveGuarded({ tpl: tpl({ id: 'x' }), save, activeKey: () => 'a' }),
      saveGuarded({ tpl: tpl({ id: 'y' }), save, activeKey: () => 'a' }),
    ]);
    expect(save).toHaveBeenCalledTimes(2);
  });
  it('синхронный сбой save не выбрасывается наружу и снимает блокировку', async () => {
    const r = await saveGuarded({ tpl: tpl(), save: () => { throw new Error('boom'); }, activeKey: () => 'a' });
    expect(r.kind).toBe('failed');
    const r2 = await saveGuarded({ tpl: tpl(), save: async () => tpl(), activeKey: () => 'a' });
    expect(r2.kind).toBe('applied');
  });
});
