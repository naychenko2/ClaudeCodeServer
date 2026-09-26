import { describe, expect, it } from 'vitest';
import { jobForm } from './api';
import {
  actionTitle, currentSrc, EMPTY_HISTORY, goToStep, maxSamples, panelJobInput, pushStep, quickBlockReason, quickPlan,
  samplesToJobInput, stepLabel, type History, type HistoryStep, type Sample,
} from './editorInputs';

const upload = (id: string, name: string, role: Sample['role']): Sample =>
  ({ id, source: 'upload', name, role, file: new Blob([name], { type: 'image/png' }), url: `blob:${id}` });
const project = (id: string, path: string, role: Sample['role']): Sample =>
  ({ id, source: 'project', name: path.split('/').pop()!, role, path, url: `/f/${path}` });

describe('образцы во вход задачи', () => {
  const samples = [
    upload('u1', 'лицо.png', 'character'),
    project('p1', 'images/dusk.png', 'style'),
    upload('u2', 'торшер.jpg', 'object'),
    project('p2', 'brand/logo.png', 'character'),
  ];

  it('байты — в references, пути — в referencePaths, у каждого своя роль', () => {
    const { references, referencePaths } = samplesToJobInput(samples);
    expect(references.map(r => [r.name, r.role])).toEqual([['лицо.png', 'character'], ['торшер.jpg', 'object']]);
    expect(referencePaths).toEqual([{ path: 'images/dusk.png', role: 'style' }, { path: 'brand/logo.png', role: 'character' }]);
  });

  it('multipart запуска: роли — параллельные списки к файлам и путям, как в StartJobForm', async () => {
    const plan = quickPlan('outpaint', '9:16');
    const form = jobForm({ quoteId: 'q1', prompt: '', ...panelJobInput(samples, plan) });
    const files = form.getAll('references') as File[];
    expect(files.map(f => f.name)).toEqual(['лицо.png', 'торшер.jpg']);
    expect(await files[0].text()).toBe('лицо.png');
    expect(form.getAll('referenceRoles')).toEqual(['character', 'object']);
    expect(form.getAll('referencePaths')).toEqual(['images/dusk.png', 'brand/logo.png']);
    expect(form.getAll('referencePathRoles')).toEqual(['style', 'character']);
    expect(form.get('aspectRatio')).toBe('9:16');
  });

  it('без образцов и не «за края» — лишних полей в форме нет', () => {
    const form = jobForm({ quoteId: 'q1', prompt: 'кот', ...panelJobInput([], quickPlan('upscale', '1:1')) });
    for (const k of ['references', 'referenceRoles', 'referencePaths', 'referencePathRoles', 'aspectRatio']) expect(form.has(k)).toBe(false);
  });

  it('потолок образцов — меньший из лимита сервера и модели', () => {
    expect(maxSamples(6, null)).toBe(6);
    expect(maxSamples(6, 4)).toBe(4);
    expect(maxSamples(6, 10)).toBe(6);
  });
});

describe('быстрые действия', () => {
  it('операция из действия, маска — только у «Убрать отмеченное»', () => {
    expect(quickPlan('removeBackground', '1:1')).toMatchObject({ op: 'removeBackground', prompt: '', useMask: false });
    expect(quickPlan('upscale', '1:1')).toMatchObject({ op: 'upscale', useMask: false });
    expect(quickPlan('removeMarked', '1:1')).toMatchObject({ op: 'inpaint', useMask: true, removal: true });
    expect(quickPlan('outpaint', '16:9')).toMatchObject({ op: 'outpaint', aspectRatio: '16:9', useMask: false });
  });

  it('без картинки, без кисти и у модели без операции — недоступно с причиной', () => {
    expect(quickBlockReason('upscale', false, false, null)).toBe('Сначала загрузите картинку');
    expect(quickBlockReason('removeMarked', true, false, null)).toBe('Сначала отметьте кистью, что убрать');
    expect(quickBlockReason('removeMarked', true, true, null)).toBe('');
    expect(quickBlockReason('outpaint', true, false, ['edit', 'inpaint'])).not.toBe('');
    expect(quickBlockReason('outpaint', true, false, ['outpaint'])).toBe('');
  });

  it('«Улучшить лица» — только у поставщика, который умеет, и не зависит от выбранной модели', () => {
    expect(quickPlan('enhanceFaces', '1:1')).toMatchObject({ op: 'enhanceFaces', useMask: false, prompt: '' });
    expect(quickBlockReason('enhanceFaces', true, false, null, ['generate', 'edit', 'upscale']))
      .toBe('Есть только у «Локальных моделей» — выберите их в «Чем рисовать»');
    // Явно выбранная Qwen-Image лица не правит, но действие берёт свою модель
    expect(quickBlockReason('enhanceFaces', true, false, ['generate', 'edit'], ['generate', 'edit', 'enhanceFaces'])).toBe('');
    expect(quickBlockReason('enhanceFaces', false, false, null, ['enhanceFaces'])).toBe('Сначала загрузите картинку');
    // Чего не умеет поставщик целиком, то не лечится «Авто»
    expect(quickBlockReason('upscale', true, false, null, ['generate', 'edit', 'enhanceFaces']))
      .toBe('Этот поставщик так не умеет — возьмите другого в «Чем рисовать»');
    expect(actionTitle({ kind: 'enhanceFaces' })).toBe('Улучшены лица');
  });

  it('заголовок шага по действию', () => {
    expect(actionTitle({ kind: 'removeBackground' })).toBe('Убран фон');
    expect(actionTitle({ kind: 'outpaint', ratio: '16:9' })).toBe('Дорисовано до 16:9');
    expect(actionTitle({ kind: 'prompt', prompt: '' })).toBe('Правка');
    expect(actionTitle({ kind: 'prompt', prompt: 'x'.repeat(50) })).toBe(`${'x'.repeat(40)}…`);
  });
});

describe('история шагов', () => {
  const step = (id: string, original = false): HistoryStep => ({ id, original, title: id, src: `src:${id}` });
  const base: History = { steps: [step('o', true)], cur: 0 };

  it('шаг встаёт за текущим и становится текущим — холст показывает его', () => {
    const h = pushStep(pushStep(base, step('a')), step('b'));
    expect(h.cur).toBe(2);
    expect(currentSrc(h)).toBe('src:b');
    expect(currentSrc(goToStep(h, 0))).toBe('src:o');
  });

  it('после отката новая правка заменяет шаги после текущего', () => {
    const h = pushStep(goToStep(pushStep(pushStep(base, step('a')), step('b')), 1), step('c'));
    expect(h.steps.map(s => s.id)).toEqual(['o', 'a', 'c']);
    expect(h.cur).toBe(2);
  });

  it('переход за пределы ленты ничего не меняет', () => {
    expect(goToStep(base, 5)).toBe(base);
    expect(currentSrc(EMPTY_HISTORY)).toBeNull();
  });

  it('подписи: «Оригинал» и «Шаг N»; без оригинала — с единицы', () => {
    const h = pushStep(base, step('a'));
    expect([stepLabel(h, 0), stepLabel(h, 1)]).toEqual(['Оригинал', 'Шаг 1']);
    const scratch = pushStep(EMPTY_HISTORY, step('a'));
    expect(stepLabel(scratch, 0)).toBe('Шаг 1');
  });
});
