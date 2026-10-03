import { describe, expect, it } from 'vitest';
import { buildKindRegistry } from './registry';
import type { ContextKindApi } from './types';

const api = (...kinds: string[]): ContextKindApi =>
  ({ kinds, icon: () => null, actions: () => [], preview: () => null });

describe('реестр видов контекста', () => {
  it('собирает вид → вклад по всем kinds', () => {
    const img = api('image', 'image-character');
    const reg = buildKindRegistry([{ name: 'imageEditor', action: img }, { name: 'audio', action: api('audio') }]);
    expect(reg.get('image')).toBe(img);
    expect(reg.get('image-character')).toBe(img);
    expect(reg.has('audio')).toBe(true);
    expect(reg.has('video')).toBe(false);
  });

  it('дубль kind в двух вкладах — исключение с именами владельцев', () => {
    expect(() => buildKindRegistry([
      { name: 'imageEditor', action: api('image') },
      { name: 'other', action: api('audio', 'image') },
    ])).toThrow(/«image».*imageEditor.*other/);
  });

  it('дубль внутри одного вклада — тоже исключение', () => {
    expect(() => buildKindRegistry([{ name: 'x', action: api('image', 'image') }])).toThrow(/«image»/);
  });

  it('вклад без action пропускается', () => {
    expect(buildKindRegistry([{ name: 'x' }]).size).toBe(0);
  });
});
