import { describe, expect, it } from 'vitest';
import type { ImageEditCaps, ImageEditCatalog, ImageEditOp } from '../api';
import {
  AUTO_EXECUTOR, executorRows, executorSettings, executorSummary, executorValue, rowPrice, type ExecutorTask,
} from './executorRows';

const caps = (ops: ImageEditOp[], mask: ImageEditCaps['mask'] = 'none', maxReferences = 4): ImageEditCaps =>
  ({ ops, mask, maxReferences, maxCount: 4, faceByReferences: false });
const AUTO = { id: 'auto', label: 'Авто' };

// Облако объявлено раньше видеокарты: порядок групп список задаёт сам
const CATALOG: ImageEditCatalog = {
  default: { provider: 'local', model: 'auto' },
  providers: [
    { key: 'fal', label: 'fal', priceUnit: 'usd', models: [
      AUTO,
      { id: 'fal-ai/flux-pro/kontext', label: 'FLUX Kontext', caps: caps(['generate', 'edit']), priceHint: { amount: 0.04, unit: 'usd', per: 'image' } },
      { id: 'fal-ai/flux-pro/v1/fill', label: 'FLUX Fill', caps: caps(['inpaint', 'outpaint', 'edit'], 'native'), priceHint: { amount: 0.05, unit: 'usd', per: 'image' } },
    ] },
    { key: 'higgsfield', label: 'Higgsfield', priceUnit: 'credits', available: false, models: [
      { id: 'soul_2', label: 'Soul', caps: caps(['generate', 'edit']), priceHint: { amount: 2, unit: 'credits', per: 'image' } },
    ] },
    { key: 'local', label: 'Локальные модели', priceUnit: 'free', models: [
      AUTO,
      { id: 'qwen-image-2.1', label: 'Qwen-Image 2.1', caps: caps(['generate', 'edit', 'inpaint'], 'asReference'), priceHint: { amount: 0, unit: 'free', per: 'image' } },
      { id: 'face-detailer', label: 'Улучшить лица', caps: { ...caps(['enhanceFaces'], 'none', 0), maxCount: 1 }, priceHint: { amount: 0, unit: 'free', per: 'image' } },
    ] },
  ],
  limits: { maxFileMb: 20, maxReferences: 6, maxCount: 4 },
  reason: null,
};

const CREATE: ExecutorTask = { op: 'generate', hasImage: false, hasMask: false, quick: null };
const EDIT: ExecutorTask = { op: 'edit', hasImage: true, hasMask: false, quick: null };
const INPAINT: ExecutorTask = { op: 'inpaint', hasImage: true, hasMask: true, quick: null };

const row = (task: ExecutorTask, id: string) => executorRows(CATALOG, task).find(r => r.id === id)!;

describe('строки «Исполнитель»', () => {
  it('порядок: Авто, затем своя видеокарта, затем облако', () => {
    const rows = executorRows(CATALOG, CREATE);
    expect(rows.map(r => `${r.group}:${r.name}`)).toEqual([
      'auto:Авто',
      'local:Локальные модели · Авто', 'local:Qwen-Image 2.1', 'local:Улучшить лица',
      'cloud:fal · Авто', 'cloud:fal · FLUX Kontext', 'cloud:fal · FLUX Fill',
      'cloud:Higgsfield · Soul',
    ]);
  });

  it('цены справа: бесплатно, доллары и кредиты за штуку', () => {
    expect(row(CREATE, 'auto').price).toBe('бесплатно');
    expect(row(CREATE, 'local|qwen-image-2.1').price).toBe('бесплатно');
    expect(row(CREATE, 'fal|fal-ai/flux-pro/kontext').price).toBe('$0.04 / шт.');
    expect(row(CREATE, 'higgsfield|soul_2').price).toBe('2 кр. / шт.');
    expect(row(CREATE, 'fal|auto').price).toBe('$ за картинку');
    expect(rowPrice(CATALOG.providers[1], null)).toBe('кредиты');
  });

  it('«Авто» говорит, кем рисуем сейчас', () => {
    expect(row(CREATE, 'auto').sub).toBe('как в настройках · сейчас локально');
    const fal = { ...CATALOG, default: { provider: 'fal', model: 'fal-ai/flux-pro/kontext' } };
    expect(executorRows(fal, CREATE)[0]).toMatchObject({ sub: 'как в настройках · сейчас fal · FLUX Kontext', price: '$0.04 / шт.' });
  });

  it('модель, которая не возьмёт задачу, серая с причиной', () => {
    expect(row(CREATE, 'fal|fal-ai/flux-pro/v1/fill')).toMatchObject({ disabled: true, reason: 'Только правит готовую картинку — сначала загрузите её' });
    expect(row(EDIT, 'fal|fal-ai/flux-pro/v1/fill').disabled).toBeUndefined();
    expect(row(INPAINT, 'fal|fal-ai/flux-pro/kontext')).toMatchObject({ disabled: true, reason: 'Не правит по маске — сотрите кисть или возьмите другую модель' });
    expect(row(EDIT, 'local|face-detailer')).toMatchObject({ disabled: true, reason: 'Запускается кнопкой «Улучшить лица» в быстрых действиях' });
    // «Авто» не серое никогда: модель подберёт сервер
    expect(executorRows(CATALOG, INPAINT).filter(r => r.name.endsWith('Авто')).every(r => !r.disabled)).toBe(true);
  });

  it('операцию без промпта серит поставщик, который её не умеет, а не модель', () => {
    const rmbg: ExecutorTask = { op: 'removeBackground', hasImage: true, hasMask: false, quick: 'removeBackground' };
    expect(row(rmbg, 'local|qwen-image-2.1')).toMatchObject({ disabled: true, reason: 'Локальные модели не умеют убирать фон' });
    const faces: ExecutorTask = { op: 'enhanceFaces', hasImage: true, hasMask: false, quick: 'enhanceFaces' };
    expect(row(faces, 'local|qwen-image-2.1').disabled).toBeUndefined();
    expect(row(faces, 'fal|fal-ai/flux-pro/kontext').disabled).toBe(true);
  });

  it('лежащий поставщик выбирается, но с пометкой', () => {
    expect(row(CREATE, 'higgsfield|soul_2')).toMatchObject({ badges: [{ label: 'не отвечает', tone: 'warning' }] });
    expect(row(CREATE, 'higgsfield|soul_2').disabled).toBeUndefined();
  });
});

describe('выбор и сводка «Чем»', () => {
  it('выбор туда и обратно: «Авто» — пусто, иначе поставщик и модель со слешами', () => {
    expect(executorValue(CATALOG, { provider: null, model: null })).toBe(AUTO_EXECUTOR);
    expect(executorSettings(AUTO_EXECUTOR)).toEqual({ provider: null, model: null });
    const id = executorValue(CATALOG, { provider: 'fal', model: 'fal-ai/flux-pro/v1/fill' });
    expect(id).toBe('fal|fal-ai/flux-pro/v1/fill');
    expect(executorSettings(id)).toEqual({ provider: 'fal', model: 'fal-ai/flux-pro/v1/fill' });
    // Поставщик без модели — его «Авто»; модель без поставщика — у поставщика администратора
    expect(executorValue(CATALOG, { provider: 'fal', model: null })).toBe('fal|auto');
    expect(executorValue(CATALOG, { provider: null, model: 'qwen-image-2.1' })).toBe('local|qwen-image-2.1');
    expect(executorRows(CATALOG, CREATE).map(r => r.id)).toContain(executorValue(CATALOG, { provider: 'fal', model: null }));
  });

  it('«Авто · локально · модель», которой реально пойдёт запуск', () => {
    expect(executorSummary(CATALOG, { provider: null, model: null }, { provider: 'local', model: 'qwen-image-2.1' }))
      .toEqual({ name: 'Авто', parts: ['локально', 'Qwen-Image 2.1'] });
    expect(executorSummary(CATALOG, { provider: null, model: null }, null)).toEqual({ name: 'Авто', parts: ['локально'] });
  });

  it('явная модель — её имя; операцию без промпта ведёт другая — «сделает …»', () => {
    expect(executorSummary(CATALOG, { provider: 'fal', model: 'fal-ai/flux-pro/kontext' }, { provider: 'fal', model: 'fal-ai/flux-pro/kontext' }))
      .toEqual({ name: 'fal · FLUX Kontext', parts: [] });
    expect(executorSummary(CATALOG, { provider: 'fal', model: 'fal-ai/flux-pro/kontext' }, { provider: 'fal', model: 'fal-ai/flux-pro/v1/fill' }))
      .toEqual({ name: 'fal · FLUX Kontext', parts: ['сделает FLUX Fill'] });
    expect(executorSummary(CATALOG, { provider: 'local', model: 'qwen-image-2.1' }, null))
      .toEqual({ name: 'Qwen-Image 2.1', parts: ['локально'] });
  });
});
