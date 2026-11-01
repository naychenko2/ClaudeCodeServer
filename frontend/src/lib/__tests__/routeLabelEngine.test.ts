// Подпись места «локально»: именованный движок (Gemma на Video) важнее модели основного LocalLlm.
// Мутация: игнорировать engine — первый assert краснеет («Локальная · qwen3.8-27b»).
import { describe, expect, it } from 'vitest';
import { routeLabel } from '../modelProvidersShared';

describe('routeLabel: место на именованном локальном движке', () => {
  it('local + engine → подпись движка, а не основной модели', () => {
    expect(routeLabel('local', 'qwen3.8-27b', undefined, 'Gemma 4')).toBe('Локальная · Gemma 4');
  });
  it('local без engine → основная локальная модель, как раньше', () => {
    expect(routeLabel('local', 'qwen3.8-27b')).toBe('Локальная · qwen3.8-27b');
    expect(routeLabel('local', 'qwen3.8-27b', undefined, null)).toBe('Локальная · qwen3.8-27b');
  });
});
