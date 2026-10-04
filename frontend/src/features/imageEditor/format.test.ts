import { describe, expect, it } from 'vitest';
import type { ImageEditOp, ImageEditProvider } from './api';
import { freeSum, modelBlockReason, money, priceSum, priceText, providerHint, providerOps } from './format';

describe('строка цены локальных моделей', () => {
  it('«Бесплатно · ≈ 1 мин · в очереди 2»', () => {
    expect(priceSum(0, 'free', false, { etaSeconds: 60, queueLength: 2 })).toBe('Бесплатно · ≈ 1 мин · в очереди 2');
    expect(priceSum(0, 'free', false, { etaSeconds: 40, queueLength: 0 })).toBe('Бесплатно · ≈ 40 с');
  });

  it('время неизвестно — «время уточняется», пустая очередь не пишется', () => {
    expect(freeSum(null)).toBe('Бесплатно · время уточняется');
    expect(priceSum(0, 'free', true)).toBe('Бесплатно · время уточняется');
    expect(priceSum(0, 'free', false, { etaSeconds: null, queueLength: 3 })).toBe('Бесплатно · время уточняется · в очереди 3');
  });

  it('с вариантами и у денег — как раньше', () => {
    expect(priceText(0, 'free', false, 2, { etaSeconds: 120, queueLength: 1 })).toBe('Бесплатно · ≈ 2 мин · в очереди 1 · 2 варианта');
    expect(priceSum(0.12, 'usd', true)).toBe('≈ $0.12');
    expect(money(0, 'free')).toBe('Бесплатно');
  });
});

describe('поставщик «Локальные модели» в выборе', () => {
  const local: ImageEditProvider = {
    key: 'local', label: 'Локальные модели', priceUnit: 'free', models: [
      { id: 'auto', label: 'Авто' },
      { id: 'qwen-image-2.1', label: 'Qwen-Image 2.1', caps: { ops: ['generate', 'edit', 'inpaint'], mask: 'asReference', maxReferences: 15, maxCount: 4, faceByReferences: true } },
      { id: 'face-detailer', label: 'Улучшить лица', caps: { ops: ['enhanceFaces'], mask: 'none', maxReferences: 0, maxCount: 1, faceByReferences: false } },
    ],
  };

  it('операции — объединение caps моделей; без caps — неизвестно', () => {
    expect(providerOps(local)).toEqual(['generate', 'edit', 'inpaint', 'enhanceFaces']);
    expect(providerOps({ ...local, models: [...local.models, { id: 'x', label: 'X' }] })).toBeNull();
  });

  it('модель «Улучшить лица» промптом не выбирается', () => {
    expect(modelBlockReason(local.models[2], true, false)).toBe('Умеет только «Улучшить лица» — для этой задачи не годится');
    expect(modelBlockReason(local.models[1], true, true)).toBe('');
    expect(providerHint(local)).toBe('бесплатно, на своей видеокарте');
  });
});

describe('модели одной операции при правке без маски', () => {
  const caps = (ops: ImageEditOp[], mask: 'native' | 'none') => ({ ops, mask, maxReferences: 0, maxCount: 1, faceByReferences: false });
  const fill = { id: 'fal-ai/flux-pro/v1/fill', label: 'FLUX Pro Fill', caps: caps(['inpaint'], 'native') };
  const expand = { id: 'fal-ai/bria/expand', label: 'Bria Expand', caps: caps(['outpaint'], 'none') };
  const removeBg = { id: 'fal-ai/bria/background/remove', label: 'Bria: убрать фон', caps: caps(['removeBackground'], 'none') };

  it('серые с причиной: сервер котировки ответил бы 400 «так не умеет»', () => {
    expect(modelBlockReason(fill, true, false)).toBe('Правит только по маске — отметьте место кистью');
    expect(modelBlockReason(expand, true, false)).toBe('Только дорисовывает за края — операция «Дорисовать за края»');
    expect(modelBlockReason(removeBg, true, false)).toBe('Только убирает фон — операция «Убрать фон»');
  });

  it('своей операцией доступны: FLUX Fill — с маской, Bria — своими операциями', () => {
    expect(modelBlockReason(fill, true, true)).toBe('');
    expect(modelBlockReason(fill, true, true, 'edit')).toBe('');
    expect(modelBlockReason(expand, true, false, 'outpaint')).toBe('');
    expect(modelBlockReason(removeBg, true, false, 'removeBackground')).toBe('');
  });
});
