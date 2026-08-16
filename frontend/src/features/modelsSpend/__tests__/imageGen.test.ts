import { describe, expect, it } from 'vitest';
import {
  AUTO, MODEL_PICKER_PROVIDER, canPickModel, modelOf, placeOf, setupHint,
  withPlaceModel, withPlaceProvider,
} from '../ImageGenSection';
import type { ImageGenerationSettings, ImageGeneratorInfo, ImagePlaceSettings } from '../../../types';

// Логика секции «Картинки» вкладки «Применение». Настройка идёт ПО МЕСТАМ (иконка проекта
// и аватар персоны — отдельно), поэтому проверяем: правка одного места не задевает другое,
// активный провайдер места считается как на бэке, и модель выбирается только у fal.

const ICON = 'project-icon';
const AVATAR = 'persona-avatar';

const provider = (key: string, enabled: boolean,
  models: ImageGeneratorInfo['models'] = []): ImageGeneratorInfo =>
  ({ key, displayName: key === 'fal' ? 'fal.ai' : key, enabled, models });

const place = (key: string, mode: string, providers: ImageGeneratorInfo[],
  models: Record<string, string | null> = {}): ImagePlaceSettings => {
  const active = mode === AUTO
    ? (providers.find(p => p.enabled)?.key ?? null)
    : (providers.find(p => p.key === mode && p.enabled)?.key ?? null);
  return {
    key,
    title: key === ICON ? 'Иконка проекта' : 'Аватар персоны',
    provider: mode,
    activeProvider: active,
    enabled: active !== null,
    model: active ? (models[active] ?? null) : null,
    models: { ...Object.fromEntries(providers.map(p => [p.key, null])), ...models },
  };
};

const settings = (providers: ImageGeneratorInfo[],
  iconMode = AUTO, avatarMode = AUTO,
  iconModels: Record<string, string | null> = {},
  avatarModels: Record<string, string | null> = {}): ImageGenerationSettings => ({
  providers,
  places: [place(ICON, iconMode, providers, iconModels), place(AVATAR, avatarMode, providers, avatarModels)],
});

const both = [provider('glif', true), provider('fal', true, [{ id: 'fal-ai/flux/schnell', displayName: 'Flux Schnell' }])];

describe('строка места', () => {
  it('ищется по ключу без учёта регистра', () => {
    const s = settings(both);
    expect(placeOf(s, 'PROJECT-ICON')?.title).toBe('Иконка проекта');
  });

  it('незнакомого места нет — и это видно, а не молчаливый undefined', () => {
    expect(placeOf(settings(both), 'chat-sticker')).toBeNull();
  });
});

describe('активный провайдер места', () => {
  it('в режиме auto берётся первый включённый в порядке ответа сервера', () => {
    const s = settings([provider('glif', false), provider('fal', true)]);
    expect(placeOf(s, ICON)?.activeProvider).toBe('fal');
  });

  it('при явном выборе — выбранный', () => {
    const s = settings(both, 'fal');
    expect(placeOf(s, ICON)?.activeProvider).toBe('fal');
    expect(placeOf(s, AVATAR)?.activeProvider).toBe('glif');
  });
});

describe('оптимистичный снимок выбора генератора', () => {
  it('явный выбор делает активным его же и только в своём месте', () => {
    const s = settings(both);
    const next = withPlaceProvider(s, ICON, 'fal');
    expect(placeOf(next, ICON)?.provider).toBe('fal');
    expect(placeOf(next, ICON)?.activeProvider).toBe('fal');
    expect(placeOf(next, ICON)?.enabled).toBe(true);
    // Соседнее место не тронуто
    expect(placeOf(next, AVATAR)?.provider).toBe(AUTO);
    expect(placeOf(next, AVATAR)?.activeProvider).toBe('glif');
  });

  it('возврат в auto отдаёт первого включённого в порядке ответа сервера', () => {
    const s = settings(both, 'fal');
    expect(placeOf(withPlaceProvider(s, ICON, AUTO), ICON)?.activeProvider).toBe('glif');
  });

  it('выбор ненастроенного провайдера гасит генерацию места, а не врёт про активного', () => {
    const s = settings([provider('glif', true), provider('fal', false)]);
    const next = withPlaceProvider(s, ICON, 'fal');
    expect(placeOf(next, ICON)?.activeProvider).toBeNull();
    expect(placeOf(next, ICON)?.enabled).toBe(false);
  });

  it('модель места пересчитывается под нового активного провайдера', () => {
    const s = settings(both, 'glif', AUTO, { fal: 'fal-ai/flux/schnell' });
    expect(placeOf(withPlaceProvider(s, ICON, 'fal'), ICON)?.model).toBe('fal-ai/flux/schnell');
  });

  it('исходный снимок не мутируется — иначе откат после ошибки нечем делать', () => {
    const s = settings(both);
    withPlaceProvider(s, ICON, 'fal');
    expect(placeOf(s, ICON)?.provider).toBe(AUTO);
    expect(placeOf(s, ICON)?.activeProvider).toBe('glif');
  });
});

describe('оптимистичный снимок модели', () => {
  const base = settings(both, 'fal', 'fal');

  it('модель проставляется только своему месту', () => {
    const next = withPlaceModel(base, ICON, 'fal', 'fal-ai/flux/schnell');
    expect(modelOf(placeOf(next, ICON)!, 'fal')).toBe('fal-ai/flux/schnell');
    expect(modelOf(placeOf(next, AVATAR)!, 'fal')).toBeNull();
  });

  it('модель активного провайдера уезжает и в подпись места', () => {
    const next = withPlaceModel(base, ICON, 'fal', 'fal-ai/flux/schnell');
    expect(placeOf(next, ICON)?.model).toBe('fal-ai/flux/schnell');
  });

  it('пустое значение = сброс к дефолту драйвера', () => {
    const chosen = withPlaceModel(base, ICON, 'fal', 'fal-ai/flux/schnell');
    expect(modelOf(placeOf(withPlaceModel(chosen, ICON, 'fal', ''), ICON)!, 'fal')).toBeNull();
  });

  it('прежний снимок остаётся нетронутым', () => {
    withPlaceModel(base, ICON, 'fal', 'fal-ai/flux/schnell');
    expect(modelOf(placeOf(base, ICON)!, 'fal')).toBeNull();
  });

  it('ключ провайдера в словаре моделей сверяется без учёта регистра', () => {
    const p: ImagePlaceSettings = { ...place(ICON, 'fal', both), models: { FAL: 'fal-ai/flux/schnell' } };
    expect(modelOf(p, 'fal')).toBe('fal-ai/flux/schnell');
  });
});

describe('пикер модели', () => {
  it('активен только при явно выбранном fal', () => {
    expect(MODEL_PICKER_PROVIDER).toBe('fal');
    expect(canPickModel(placeOf(settings(both, 'fal'), ICON)!)).toBe(true);
  });

  it('при «Автоматически» и glif модель выбирает генератор', () => {
    expect(canPickModel(placeOf(settings(both), ICON)!)).toBe(false);
    expect(canPickModel(placeOf(settings(both, 'glif'), ICON)!)).toBe(false);
  });
});

describe('подсказка о ненастроенном провайдере', () => {
  it('называет конкретный ключ конфига', () => {
    expect(setupHint('fal')).toContain('Fal:ApiKey');
    expect(setupHint('glif')).toContain('Glif:McpToken');
  });

  it('для незнакомого провайдера подсказка общая, а не пустая', () => {
    expect(setupHint('midjourney')).toMatch(/ключ доступа/);
  });
});
