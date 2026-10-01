import { describe, expect, it } from 'vitest';
import { mobileSummary, settingsToggle, summaryOpensPanel } from './settingsToggle';

describe('сводка полосы «Картинки»', () => {
  it('панель открыта колонкой — акцентная обводка и подсказка «открыты справа»', () => {
    expect(settingsToggle('panel', true)).toEqual({ on: true, title: 'Настройки открыты в панели «Картинки» справа' });
    expect(settingsToggle('panel', false)).toEqual({ on: false, title: 'Открыть настройки в панели «Картинки»' });
  });

  it('без флага обводка — по открытой карточке', () => {
    expect(settingsToggle('card', true).on).toBe(true);
    expect(settingsToggle('card', false).on).toBe(false);
  });

  it('свёрнутая полоса: сводка открывает панель, без флага — разворачивает полосу', () => {
    expect(summaryOpensPanel('panel')).toBe(true);
    expect(summaryOpensPanel('sheet')).toBe(true);
    expect(summaryOpensPanel('card')).toBe(false);
  });

  it('телефон: варианты и цена', () => {
    expect(mobileSummary(2, '≈ $0.08')).toBe('2 вар. · ≈ $0.08');
    expect(mobileSummary(2, '≈ 4 кредита')).toBe('2 вар. · ≈ 4 кр.');
    expect(mobileSummary(1, null)).toBe('1 вар.');
  });
});
