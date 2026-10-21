import { describe, expect, it } from 'vitest';
import type { PersonaBinding } from '../../types';
import {
  BINDING_TYPE_META, BINDING_TYPE_ORDER, bindingLabel, catalogTypeFor,
} from './bindingMeta';

// Привязка типа «Плагин» (ADR-031): цель — составная строка «plugin:{id}/{skill}»,
// где {id} = {publisher}/{slug}. Подпись резолвится из цели, не из каталога бэка.

function pluginBinding(target: string): PersonaBinding {
  return {
    id: 'b1', type: 'plugin', target,
    condition: '', mode: 'auto',
    createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z',
  };
}

describe('bindingMeta · тип «Плагин»', () => {
  it('тип в порядке сетки и в метаданных', () => {
    expect(BINDING_TYPE_ORDER).toContain('plugin');
    expect(BINDING_TYPE_META['plugin'].name).toBe('Плагин');
  });

  it('catalogTypeFor: plugin → plugin (не «project»)', () => {
    expect(catalogTypeFor('plugin')).toBe('plugin');
  });

  it('bindingLabel: навык — часть цели после последнего «/»', () => {
    const label = bindingLabel(pluginBinding('plugin:local-abc/weekly-report/weekly-report'), new Map());
    expect(label).toBe('Плагин · навык «Weekly Report»');
  });

  it('bindingLabel: навык с подчёркиванием humanize-ится', () => {
    const label = bindingLabel(pluginBinding('plugin:p/slug/my_skill_name'), new Map());
    expect(label).toBe('Плагин · навык «My Skill Name»');
  });

  it('bindingLabel: цель без «plugin:»-префикса — деградирует, не падает', () => {
    // цель без префикса: lastSlash всё равно находит skill
    const label = bindingLabel(pluginBinding('local-abc/weekly-report/weekly-report'), new Map());
    expect(label).toBe('Плагин · навык «Weekly Report»');
  });
});
