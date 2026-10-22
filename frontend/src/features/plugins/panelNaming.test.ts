// Имя плагина в карточке раздела «Плагины»: человеческое (title объявленной панели),
// а не технический slug. Мутация: заменить pluginDisplayName на humanizeId — первый
// тест красный («Weather» вместо «Погода»).

import { describe, it, expect } from 'vitest';
import { humanizeId, panelDisplayName, pluginDisplayName } from './panelNaming';
import type { PluginInfo } from '../../lib/api';

function plugin(id: string, panels?: { id: string; title: string; entry: string }[]): PluginInfo {
  return {
    id, version: '1.0.0', sha256: '', requested: [], granted: [], enabled: true,
    source: `upload:${id}`, secrets: [],
    ...(panels ? { contributes: { panels } } : {}),
  };
}

describe('Имя плагина в карточке (pluginDisplayName)', () => {
  it('Панель «Погода»: карточка показывает «Погода», а не «Weather»', () => {
    const p = plugin('weather', [{ id: 'main', title: 'Погода', entry: 'panel/index.html' }]);
    expect(pluginDisplayName(p)).toBe('Погода');
  });

  it('Плагин без панелей — прежнее поведение: humanizeId', () => {
    expect(pluginDisplayName(plugin('notes-tasks'))).toBe('Notes Tasks');
  });

  it('Панель с пустым title — откат к humanizeId', () => {
    const p = plugin('weather', [{ id: 'main', title: '', entry: 'panel/index.html' }]);
    expect(pluginDisplayName(p)).toBe('Weather');
  });

  it('Несколько панелей — берётся первая с непустым title', () => {
    const p = plugin('weather', [
      { id: 'a', title: '', entry: 'a.html' },
      { id: 'b', title: 'Прогноз', entry: 'b.html' },
    ]);
    expect(pluginDisplayName(p)).toBe('Прогноз');
  });

  it('humanizeId из {publisher}/{slug} — по slug (общая логика, не копия в компоненте)', () => {
    expect(humanizeId('acme/weather')).toBe('Weather');
  });

  it('panelDisplayName: title панели, иначе имя плагина', () => {
    const p = plugin('weather');
    expect(panelDisplayName(p, { id: 'main', title: 'Погода', entry: 'x' })).toBe('Погода');
    expect(panelDisplayName(p, { id: 'main', title: '', entry: 'x' })).toBe('Weather');
  });
});
