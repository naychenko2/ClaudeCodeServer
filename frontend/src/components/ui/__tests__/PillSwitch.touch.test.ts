// Тач-цель сегмента PillSwitch в компакте (мобила): неактивный сегмент — одна иконка,
// без minWidth он выходил 36×40 (боковые вкладки формы персоны). Правило — обе стороны ≥ 40.
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { PillSwitch } from '../PillSwitch';
import { TB } from '../../../lib/design';

const options = [
  { value: 'a', label: 'Профиль', icon: createElement('i') },
  { value: 'b', label: 'Умения', icon: createElement('i') },
  { value: 'c', label: 'Память', icon: createElement('i') },
];
const html = (compact: boolean) =>
  renderToStaticMarkup(createElement(PillSwitch<string>, { value: 'a', options, onChange: () => {}, compact, isMobile: compact }));

describe('PillSwitch: тач-цель в компакте', () => {
  it('компакт: каждый сегмент min-width и min-height не меньше TB.iconHitMobile (40)', () => {
    expect(TB.iconHitMobile).toBeGreaterThanOrEqual(40);
    const out = html(true);
    const buttons = out.match(/<button[^>]*>/g) ?? [];
    expect(buttons).toHaveLength(3);
    for (const b of buttons) {
      expect(b).toContain(`min-width:${TB.iconHitMobile}px`);
      expect(b).toContain(`min-height:${TB.iconHitMobile}px`);
    }
  });

  it('десктоп без компакта: min-width не навязываем', () => {
    expect(html(false)).not.toContain('min-width:');
  });
});
