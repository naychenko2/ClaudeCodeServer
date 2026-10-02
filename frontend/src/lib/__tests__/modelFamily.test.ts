import { describe, it, expect, beforeEach, vi } from 'vitest';
import { modelFamily, modelLabel, contextWindowFor, loadModels, getModels, FALLBACK_MODELS } from '../models';
import { api } from '../api';

vi.mock('../api', () => ({ api: { models: { list: vi.fn() } } }));

// Модели Claude живут только семействами: версионные id и суффикс [1m] из старых сессий
// и из session_started обязаны сводиться к семейству, сторонние модели — не трогаться.
describe('modelFamily', () => {
  it.each([
    ['claude-opus-4-8', 'opus'],
    ['opus[1m]', 'opus'],
    ['claude-fable-5-1[1m]', 'fable'],
    ['claude-haiku-4-5-20251001', 'haiku'],
    ['claude-sonnet-5', 'sonnet'],
    ['Opus', 'opus'],
    ['sonnet', 'sonnet'],
  ])('%s → %s', (id, family) => {
    expect(modelFamily(id)).toBe(family);
  });

  it.each(['glm-5.2[1m]', 'deepseek-v4-pro', 'direct:qwen/qwen3', 'gpt-opus-like'])(
    'сторонняя модель %s остаётся как есть', id => {
      expect(modelFamily(id)).toBe(id);
    });
});

describe('modelLabel', () => {
  beforeEach(() => vi.clearAllMocks());

  it('версионный id и [1m] подписываются семейством, а не сырым id', () => {
    expect(modelLabel('claude-opus-4-8')).toBe('Opus');
    expect(modelLabel('opus[1m]')).toBe('Opus');
    expect(modelLabel('claude-fable-5-1[1m]')).toBe('Fable');
    expect(modelLabel('claude-haiku-4-5-20251001')).toBe('Haiku');
  });

  it('незнакомая сторонняя модель показывается как есть', () => {
    expect(modelLabel('glm-5.2[1m]')).toBe('glm-5.2[1m]');
  });

  it('в fallback есть все четыре семейства, включая fable', () => {
    expect(FALLBACK_MODELS.map(m => m.value)).toEqual(['', 'opus', 'fable', 'sonnet', 'haiku']);
  });

  it('resolvedVersion из каталога доезжает до опции', async () => {
    vi.mocked(api.models.list).mockResolvedValue({
      models: [
        { value: 'default', displayName: 'По умолчанию' },
        { value: 'opus', displayName: 'Opus', provider: 'claude', resolvedVersion: 'Opus 5.5' },
        { value: 'haiku', displayName: 'Haiku', provider: 'claude', resolvedVersion: null },
      ],
    });
    await loadModels();
    const opus = getModels().find(m => m.value === 'opus');
    expect(opus?.resolvedVersion).toBe('Opus 5.5');
    expect(opus?.description).toBe('Универсальная · сложные повседневные задачи');
    expect(getModels().find(m => m.value === 'haiku')?.resolvedVersion).toBeUndefined();
    expect(modelLabel('claude-opus-5-5')).toBe('Opus');
  });
});

describe('contextWindowFor — окно по семейству', () => {
  it('opus без [1m] не деградирует до 200k', () => {
    expect(contextWindowFor('opus')).toBe(1_000_000);
    expect(contextWindowFor('claude-opus-5-5')).toBe(1_000_000);
    expect(contextWindowFor('fable')).toBe(1_000_000);
    expect(contextWindowFor('sonnet')).toBe(1_000_000);
    expect(contextWindowFor('haiku')).toBe(200_000);
    expect(contextWindowFor('claude-haiku-4-5-20251001')).toBe(200_000);
  });
});
