// Мобильная вкладка «Плагины» (дефект 71aa17c0): на мобиле (360px) слот «Панели
// плагинов» монтируется только в desktop-колонке. Добавлена вкладка 'plugins' в
// LEFT_TABS + leftTabOptions (по hasPlugins) + рендер PluginFrame.
//
// Тест: LEFT_TABS содержит 'plugins'; isLeftTab('plugins') = true.
// Мутация (убрать 'plugins' из LEFT_TABS) → красный.

import { describe, it, expect } from 'vitest';
import { LEFT_TABS, isLeftTab } from '../lib/workspaceState';

describe('WorkspacePage: мобильная вкладка «Плагины» (71aa17c0)', () => {
  it('LEFT_TABS содержит plugins', () => {
    expect(LEFT_TABS).toContain('plugins');
  });

  it('isLeftTab("plugins") = true', () => {
    expect(isLeftTab('plugins')).toBe(true);
  });

  it('isLeftTab("nonexistent") = false (регрессия: не все строки проходят)', () => {
    expect(isLeftTab('nonexistent')).toBe(false);
  });
});
