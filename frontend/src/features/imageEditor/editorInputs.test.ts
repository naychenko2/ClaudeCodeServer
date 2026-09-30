import { describe, expect, it } from 'vitest';
import { AUTO_MODEL, jobForm, type ImageEditCatalog, type ImageEditModel, type ImageEditOp, type ImageEditProvider } from './api';
import {
  actionTitle, applyAgentReferences, currentSrc, EMPTY_HISTORY, goToStep, maxSamples, panelJobInput, pushStep, quickPlan,
  fallbackLabel, quickAvailability, quickOffered, samplesToJobInput, stepLabel, type History, type HistoryStep, type Sample,
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

  it('«Улучшить лица» — операция своей модели, заголовок шага', () => {
    expect(quickPlan('enhanceFaces', '1:1')).toMatchObject({ op: 'enhanceFaces', useMask: false, prompt: '' });
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

describe('образцы из состояния агента', () => {
  const make = (r: { path: string; role: Sample['role'] }) => project(`new:${r.path}`, r.path, r.role);

  it('агент убрал один из двух образцов проекта — остаётся нужный, образцы с компьютера целы', () => {
    const list = [
      project('p1', 'images/dusk.png', 'style'),
      upload('u1', 'лицо.png', 'character'),
      project('p2', 'brand/logo.png', 'object'),
    ];
    const next = applyAgentReferences(list, [{ path: 'brand/logo.png', role: 'object' }], make);
    expect(next.map(s => s.id)).toEqual(['u1', 'p2']);
    expect(next[0]).toBe(list[1]);
  });

  it('роль берётся у агента, новый образец дописывается в конец', () => {
    const list = [project('p1', 'images/dusk.png', 'style'), upload('u1', 'лицо.png', 'character')];
    const next = applyAgentReferences(list, [
      { path: 'images/dusk.png', role: 'character' }, { path: 'brand/logo.png', role: 'object' },
    ], make);
    expect(next.map(s => [s.id, s.role])).toEqual([['p1', 'character'], ['u1', 'character'], ['new:brand/logo.png', 'object']]);
  });

  it('агент убрал все образцы — остаются только с компьютера', () => {
    const list = [project('p1', 'images/dusk.png', 'style'), upload('u1', 'лицо.png', 'character')];
    expect(applyAgentReferences(list, [], make).map(s => s.id)).toEqual(['u1']);
  });
});

// Каталог как у живого сервера: у fal дорисовку умеет Bria Expand (один вариант), у локальных
// моделей её нет вовсе — Qwen-Image правит и рисует, FaceDetailer — только лица
const model = (id: string, ops: ImageEditOp[], maxCount = 4, price = 0.04, unit = 'usd', maxReferences = 3): ImageEditModel =>
  ({ id, label: id, caps: { ops, mask: 'none', maxReferences, maxCount, faceByReferences: false }, priceHint: { amount: price, unit, per: 'image' } });
const AUTO: ImageEditModel = { id: AUTO_MODEL, label: 'Авто' };
const FAL: ImageEditProvider = { key: 'fal', label: 'fal', priceUnit: 'usd', models: [
  AUTO, model('nano', ['edit', 'inpaint']), model('bria-expand', ['outpaint'], 1, 0.04, 'usd', 0), model('bria-rmbg', ['removeBackground'], 1, 0.018, 'usd', 0),
] };
const LOCAL: ImageEditProvider = { key: 'local', label: 'Локальные модели', priceUnit: 'free', models: [
  AUTO, model('qwen', ['generate', 'edit', 'inpaint'], 4, 0, 'free'), model('face', ['enhanceFaces'], 1, 0, 'free', 0),
] };
const catalog = (...providers: ImageEditProvider[]): ImageEditCatalog =>
  ({ default: { provider: providers[0].key, model: AUTO_MODEL }, providers, limits: { maxFileMb: 20, maxReferences: 6, maxCount: 4 }, reason: null });

describe('доступность быстрого действия по каталогу — до запуска', () => {
  it('«Локальные модели · Авто»: дорисовки нет — кнопка неактивна с причиной и «Взять fal»', () => {
    const r = quickAvailability('outpaint', catalog(LOCAL, FAL), 'local', AUTO_MODEL, 3);
    expect(r.route).toBeNull();
    expect(r.reason).toBe('Локальные модели не умеют дорисовку за края');
    // Разовый поставщик — модель, которая умеет, и вариантов в её пределах
    expect(r.fallback).toMatchObject({ provider: 'fal', model: 'bria-expand', count: 1, maxReferences: 0 });
    expect(fallbackLabel(r.fallback!)).toBe('Взять fal · ≈ $0.04');
  });

  it('у поставщика есть умеющая модель — берётся она, даже если в полосе выбрана другая', () => {
    const r = quickAvailability('outpaint', catalog(FAL, LOCAL), 'fal', 'nano', 4);
    expect(r).toMatchObject({ reason: '', fallback: null, route: { provider: 'fal', model: 'bria-expand', count: 1 } });
    // Выбранная модель умеет сама — остаётся она
    expect(quickAvailability('removeMarked', catalog(FAL), 'fal', 'nano', 2).route).toMatchObject({ model: 'nano', count: 2 });
  });

  it('то же правило для остальных действий: фон, лица, апскейл', () => {
    const c = catalog(LOCAL, FAL);
    expect(quickAvailability('removeBackground', c, 'local', 'qwen', 2)).toMatchObject({
      reason: 'Локальные модели не умеют убирать фон', fallback: { provider: 'fal', model: 'bria-rmbg' },
    });
    expect(quickAvailability('enhanceFaces', c, 'local', 'qwen', 3).route).toMatchObject({ provider: 'local', model: 'face', count: 1 });
    const faces = quickAvailability('enhanceFaces', c, 'fal', AUTO_MODEL, 3);
    expect(faces).toMatchObject({ reason: 'fal не умеет улучшать лица', fallback: { provider: 'local', model: 'face' } });
    expect(fallbackLabel(faces.fallback!)).toBe('Взять Локальные модели · бесплатно');
    // Апскейла нет ни у кого: причина есть, перехода нет, кнопки не показываем
    expect(quickAvailability('upscale', c, 'local', AUTO_MODEL, 1)).toMatchObject({ route: null, fallback: null });
    expect(quickOffered('upscale', c)).toBe(false);
    expect(quickOffered('outpaint', c)).toBe(true);
    expect(quickOffered('upscale', null)).toBe(true);
  });

  it('лежащий поставщик запасным не предлагается; поле не пришло — доступен', () => {
    const faces = (lp: ImageEditProvider) => quickAvailability('enhanceFaces', catalog(FAL, lp), 'fal', AUTO_MODEL, 1);
    expect(faces({ ...LOCAL, available: false })).toMatchObject({ reason: 'fal не умеет улучшать лица', fallback: null });
    expect(faces(LOCAL).fallback).toMatchObject({ provider: 'local', model: 'face' });
    expect(faces({ ...LOCAL, available: true }).fallback).toMatchObject({ provider: 'local', model: 'face' });
  });

  it('возможности модели неизвестны — «Авто», решает сервер', () => {
    const blind: ImageEditProvider = { ...LOCAL, models: [AUTO, { id: 'x', label: 'X' }] };
    expect(quickAvailability('outpaint', catalog(blind), 'local', AUTO_MODEL, 2).route).toMatchObject({ model: AUTO_MODEL, count: 2 });
  });
});
