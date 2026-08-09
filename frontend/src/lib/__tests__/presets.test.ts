import { describe, it, expect, vi } from 'vitest';

// Стор тянет api (сеть) — мокаем, тестируем чистую логику лексики preset:*,
// подписей цепочки и форматтера строки «Сейчас пойдёт»
vi.mock('../api', () => ({ api: {} }));

import {
  isPresetRoute, presetIdOf, presetValueLabel, cellPresetLabel, chainStepLabel, chainSummary,
  stepsWord, substitutionsWord, placesWord, isBrokenPresetRoute, formatEffectiveLine,
  isChainStepDimmed, resolvePlacePreset,
} from '../presets';
import type { ScopedPreset, ModelPreviewResponse } from '../../types';

const PRESETS: ScopedPreset[] = [
  { id: 'p1', name: 'Рабочая', description: null, steps: ['opus', 'glm-5.2', 'deepseek-v4'], scope: 'owner' },
  { id: 'p2', name: 'Дешёвый фон', description: null, steps: ['tier:weak', 'local'], scope: 'global' },
];

const CTX = { tierModels: { strong: 'opus', medium: 'sonnet', weak: 'haiku' }, ollamaModel: 'qwen3' };

describe('preset-лексика', () => {
  it('распознаёт ссылку preset:{id} независимо от регистра префикса', () => {
    expect(isPresetRoute('preset:p1')).toBe(true);
    expect(isPresetRoute('Preset:p1')).toBe(true);
    expect(presetIdOf('preset:p1')).toBe('p1');
    expect(presetIdOf('preset:')).toBeNull();
    expect(presetIdOf('sonnet')).toBeNull();
    expect(presetIdOf('')).toBeNull();
    expect(presetIdOf(null)).toBeNull();
  });

  it('подпись выбранного пресета — имя и длина цепочки', () => {
    expect(presetValueLabel('preset:p1', PRESETS)).toBe('Рабочая · 3 шага');
    expect(presetValueLabel('preset:p2', PRESETS)).toBe('Дешёвый фон · 2 шага');
  });

  it('битая ссылка — честная пометка, а не сырой id', () => {
    expect(presetValueLabel('preset:gone', PRESETS)).toBe('Пресет удалён — работает настройка по умолчанию');
    expect(isBrokenPresetRoute('preset:gone', PRESETS)).toBe(true);
    expect(isBrokenPresetRoute('preset:p1', PRESETS)).toBe(false);
    expect(isBrokenPresetRoute('sonnet', PRESETS)).toBe(false);
  });

  it('склонение «шагов» и «мест»', () => {
    expect(stepsWord(1)).toBe('1 шаг');
    expect(stepsWord(2)).toBe('2 шага');
    expect(stepsWord(5)).toBe('5 шагов');
    expect(placesWord(1)).toBe('1 месте');
    expect(placesWord(3)).toBe('3 местах');
  });

  it('склонение «раз/раза» для бюджета подмен (диапазон клампа 1..5)', () => {
    expect(substitutionsWord(1)).toBe('1 раз');
    expect(substitutionsWord(2)).toBe('2 раза');
    expect(substitutionsWord(4)).toBe('4 раза');
    expect(substitutionsWord(5)).toBe('5 раз');
  });
});

describe('подписи шагов цепочки', () => {
  it('модель — её label из каталога, уровень — «…(модели по умолч.)», local — локальная', () => {
    expect(chainStepLabel('opus', CTX)).toBe('Opus');
    expect(chainStepLabel('tier:strong', CTX)).toBe('Сильная (модели по умолч.)');
    expect(chainStepLabel('local', CTX)).toBe('Локальная · qwen3');
  });

  it('сводка цепочки — порядок шагов стрелками', () => {
    expect(chainSummary(PRESETS[0], CTX)).toBe('Opus → glm-5.2 → deepseek-v4');
    expect(chainSummary(PRESETS[1], CTX)).toBe('Слабая (модели по умолч.) → Локальная · qwen3');
  });
});

describe('приглушение шагов за пределом бюджета (isChainStepDimmed)', () => {
  // Формула спеки и счётчика FallbackLlmSessionAdapter: N подмен → рабочие шаги 1..N+1
  it('budget=2: шаги 1–3 рабочие, 4–5 приглушены («обычно не используется»)', () => {
    const flags = [0, 1, 2, 3, 4].map(i => isChainStepDimmed(i, 2));
    expect(flags).toEqual([false, false, false, true, true]);
  });

  it('budget=4 (дефолт): вся цепочка из 5 шагов рабочая', () => {
    const flags = [0, 1, 2, 3, 4].map(i => isChainStepDimmed(i, 4));
    expect(flags).toEqual([false, false, false, false, false]);
  });

  it('budget=1: рабочие только шаги 1–2', () => {
    const flags = [0, 1, 2, 3, 4].map(i => isChainStepDimmed(i, 1));
    expect(flags).toEqual([false, false, true, true, true]);
  });
});

describe('resolvePlacePreset — пресет места каталога для триггера', () => {
  it('поле preset ответа сильнее развёрнутого route: имя и шаги из ответа', () => {
    // После выбора пресета Describe отдаёт route = первый шаг, имя — только в preset
    const r = resolvePlacePreset('tier:medium',
      { id: 'p2', name: 'Дешёвый фон', steps: ['tier:weak', 'local'] }, PRESETS, true);
    expect(r.presetId).toBe('p2');
    expect(r.broken).toBe(false);
    expect(r.preset?.name).toBe('Дешёвый фон');
  });

  it('не-preset назначение: preset=null в ответе — пресета нет', () => {
    const r = resolvePlacePreset('tier:medium', null, PRESETS, true);
    expect(r).toEqual({ preset: null, broken: false, presetId: null });
  });

  it('битая ссылка по контракту: name=null — «удалён», даже до загрузки списка', () => {
    const r = resolvePlacePreset('tier:medium',
      { id: 'gone', name: null, steps: [] }, [], false);
    expect(r.broken).toBe(true);
    expect(r.preset).toBeNull();
    expect(r.presetId).toBe('gone');
  });

  it('битая ссылка по списку: id не найден в загруженном списке', () => {
    const r = resolvePlacePreset('tier:medium',
      { id: 'gone', name: 'Удалённая', steps: ['opus'] }, PRESETS, true);
    expect(r.broken).toBe(true);
    expect(r.preset).toBeNull();
  });

  it('пока список грузится, id «не найден» — не битый: имя берётся из ответа', () => {
    const r = resolvePlacePreset('tier:medium',
      { id: 'p3', name: 'Свежая', steps: ['opus'] }, [], false);
    expect(r.broken).toBe(false);
    expect(r.preset).toEqual({ id: 'p3', name: 'Свежая', steps: ['opus'] });
  });

  it('переходный период без поля preset: ссылка разбирается из самого route', () => {
    const r = resolvePlacePreset('preset:p2', undefined, PRESETS, true);
    expect(r.presetId).toBe('p2');
    expect(r.preset?.name).toBe('Дешёвый фон');
    // …а незагруженный список не даёт ложного «удалён»
    expect(resolvePlacePreset('preset:p2', undefined, [], false).broken).toBe(false);
  });
});

describe('formatEffectiveLine — строка «Сейчас пойдёт»', () => {
  const base: ModelPreviewResponse = {
    model: null, source: null, tier: null, tierOrigin: null, preset: null, chain: [],
  };

  it('уровень + источник по образцу спеки', () => {
    const line = formatEffectiveLine({
      ...base, model: 'sonnet', source: 'specialty-cell', tier: 'medium', tierOrigin: 'persona',
    });
    expect(line).toBe('Сейчас пойдёт: Sonnet · уровень «Средняя» у персоны, модель — от специальности');
  });

  it('битый пресет — честная пометка с именем, если оно известно', () => {
    expect(formatEffectiveLine({
      ...base, preset: { id: 'p9', name: 'Рабочая', steps: [], broken: true },
    })).toBe('Сейчас пойдёт: модель по умолчанию — пресет «Рабочая» удалён');
    expect(formatEffectiveLine({
      ...base, preset: { id: 'p9', name: null, steps: [], broken: true },
    })).toBe('Сейчас пойдёт: модель по умолчанию — пресет удалён');
  });

  it('пустой резолв — строки нет', () => {
    expect(formatEffectiveLine(base)).toBeNull();
  });

  it('модель без уровня — только источник', () => {
    expect(formatEffectiveLine({ ...base, model: 'opus', source: 'persona-model' }))
      .toBe('Сейчас пойдёт: Opus · модель — из персоны');
  });

  it('назначение места и слоты', () => {
    expect(formatEffectiveLine({ ...base, model: 'glm-5.2', source: 'place-assignment' }))
      .toBe('Сейчас пойдёт: glm-5.2 · модель — из назначения места');
    expect(formatEffectiveLine({
      ...base, model: 'haiku', source: 'owner-slot', tier: 'weak', tierOrigin: 'place',
    })).toBe('Сейчас пойдёт: Haiku · уровень «Слабая» у места, модель — из ваших «Моделей по умолчанию»');
  });

  it('tierText переопределяет разбор tierOrigin (строка матрицы ≠ «задан задачей»)', () => {
    expect(formatEffectiveLine({
      ...base, model: 'opus', source: 'specialty-cell', tier: 'strong', tierOrigin: 'task',
    }, { tierText: 'уровень «Сильная»' }))
      .toBe('Сейчас пойдёт: Opus · уровень «Сильная», модель — от специальности');
  });
});

describe('cellPresetLabel — подпись пресета в узкой ячейке', () => {
  // Подмена каталога для теста: имена шагов «как в проде» (Opus, …); в реальности они
  // приходят из modelLabel модели и chainStepLabel 'tier:strong' → «Сильная (модели по умолч.)»
  const bigPreset: ScopedPreset = {
    id: 'big', name: 'Цепочка 2', description: null,
    steps: ['opus', 'glm-5.2', 'deepseek-v4', 'qwen3.8-max'],
    scope: 'global',
  };
  const allPresets: ScopedPreset[] = [...PRESETS, bigPreset];

  it('1 шаг — целиком, без «+N»', () => {
    const r = cellPresetLabel('preset:p2', allPresets, CTX);
    expect(r.label).toBe('Слабая (модели по умолч.) → Локальная · qwen3');
    expect(r.title).toBe('Дешёвый фон: Слабая (модели по умолч.) → Локальная · qwen3');
  });

  it('2 шага — целиком, без «+N»', () => {
    // Соберём двухшаговый пресет: «tier:strong» → «Сильная (модели по умолч.)», затем модель
    const twoStep: ScopedPreset = {
      id: 'two', name: 'Парные', description: null, steps: ['tier:strong', 'opus'], scope: 'owner',
    };
    const r = cellPresetLabel('preset:two', [...allPresets, twoStep], CTX);
    expect(r.label).toBe('Сильная (модели по умолч.) → Opus');
    expect(r.title).toBe('Парные: Сильная (модели по умолч.) → Opus');
  });

  it('3+ шагов — голова (первые 2) + «+N» в label, полный состав и имя — в title', () => {
    const r = cellPresetLabel('preset:big', allPresets, CTX);
    expect(r.label).toBe('Opus → glm-5.2 · +2');
    expect(r.title).toBe('Цепочка 2: Opus → glm-5.2 → deepseek-v4 → qwen3.8-max');
  });

  it('битая ссылка — короткая пометка в label, полное пояснение — в title', () => {
    const r = cellPresetLabel('preset:gone', allPresets, CTX);
    expect(r.label).toBe('Пресет удалён');
    expect(r.title).toBe('Пресет удалён — работает настройка по умолчанию');
  });

  it('не-preset — обычная подпись маршрута, title пустой (не дублируем строку)', () => {
    const r = cellPresetLabel('sonnet', allPresets, CTX);
    expect(r.label).toBe('Sonnet');
    expect(r.title).toBe('');
  });

  it('пустой route — идёт в routeLabel целиком (как routeDisplayLabel), title не дублируем', () => {
    // В проде пустой route до cellPresetLabel не доходит: ExceptionsBlock рисует RoutePicker
    // только при value, иначе — прочерк. Но если вдруг дойдёт (битая строка), единообразие
    // с routeDisplayLabel важнее, чем «пустая подпись» — иначе ячейка будет выглядеть как
    // «нет данных», а не «не задан». title пустой, чтобы не дублировать тот же текст.
    expect(cellPresetLabel('', allPresets, CTX)).toEqual({ label: 'не задан', title: '' });
  });
});
