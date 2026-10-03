import { describe, expect, it } from 'vitest';
import { CATALOG, PREFS, scene } from '../mocks';
import { resolveScene } from '../scene/model';
import { sceneChip } from './summary';

describe('чип сцены полосы', () => {
  const s = scene('scene-5');
  const r = resolveScene(s, null, PREFS, CATALOG);
  it('пока цены нет — «цена уточняется», а не пустое место', () => {
    const c = sceneChip(s, r, null);
    expect(c.text).toContain('цена уточняется');
    expect(c.short).toBe('Сц. 5 · цена…');
  });
  it('цена есть — остаётся в обеих формах', () => {
    const c = sceneChip(s, r, '≈ $3.20');
    expect(c.text).toBe('Сцена 5 · Veo 3.1 · 8 с · 2 вар. · ≈ $3.20');
    expect(c.short).toBe('Сц. 5 · ≈ $3.20');
    expect(c.price).toBe('≈ $3.20');
    // узкий вид отдаёт имя и цену раздельно: режется имя, цена остаётся целой
    expect([c.shortName, c.shortPrice]).toEqual(['Сц. 5', '≈ $3.20']);
  });
  it('новая сцена без выбора цену не обещает', () => {
    expect(sceneChip(null, r, null).text).not.toContain('уточняется');
  });
});
