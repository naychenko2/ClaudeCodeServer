// Единая точка правды подписей ценовых классов: легенда (мн. ч.) и пилюля (ед. ч.).
import { describe, it, expect } from 'vitest';
import { COST_TIERS, COST_TIER_KEYS, COST_TIER_PILL } from '../costTiers';

describe('costTiers: подписи классов', () => {
  it('легенда — прилагательные во множественном числе, порядок free → cheap → expensive', () => {
    expect(COST_TIER_KEYS.map(k => COST_TIERS[k].label)).toEqual(['Бесплатные', 'Дешёвые', 'Дорогие']);
  });

  it('пилюля провайдера — прилагательные в единственном числе, строчными', () => {
    expect(COST_TIER_KEYS.map(k => COST_TIER_PILL[k])).toEqual(['бесплатный', 'дешёвый', 'дорогой']);
  });
});
