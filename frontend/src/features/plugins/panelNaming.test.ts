// Человекочитаемое имя УСТАНОВЛЕННОГО пакета (D2-ф-д): title первой панели манифеста,
// иначе humanizeId — то же правило, что на бэке (PluginDisplayName.Of). Список плагинов
// и его диалог согласия берут это имя из одной функции. Мутация: свести
// pluginDisplayName к humanizeId → первый тест краснеет.
import { describe, expect, it } from 'vitest';
import type { PluginInfo } from '../../lib/api';
import { pluginDisplayName } from './panelNaming';

const BASE: PluginInfo = {
  id: 'local-1/weather', version: '1.0.0', sha256: '', requested: [], granted: [],
  enabled: true, source: 'upload:weather', secrets: [],
};

describe('pluginDisplayName: имя установленного пакета', () => {
  it('title первой панели → «Погода», а не humanizeId(id)', () => {
    const p: PluginInfo = {
      ...BASE,
      contributes: { panels: [{ id: 'p1', title: 'Погода', entry: 'panel/index.html' }] },
    };
    expect(pluginDisplayName(p)).toBe('Погода');
  });

  it('панелей нет или title пуст → прежний запасной вариант humanizeId(id) = «Weather»', () => {
    expect(pluginDisplayName(BASE)).toBe('Weather');
    const blankTitle: PluginInfo = {
      ...BASE,
      contributes: { panels: [{ id: 'p1', title: '   ', entry: 'panel/index.html' }] },
    };
    expect(pluginDisplayName(blankTitle)).toBe('Weather');
  });
});
