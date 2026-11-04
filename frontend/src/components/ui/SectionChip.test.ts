// Меню чипа раздела: чистая часть (sectionMenuItems) + статический рендер триггера.
// Окружение node, без DOM: действие пункта проверяем через его onSelect — ровно то,
// что делает MenuItem в компоненте.
import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { SectionChip, sectionMenuItems, SECTION_CHIP_ALL_LABEL, SECTION_CHIP_TITLE, type SectionChipOption, type SectionChipValue } from './SectionChip';

const OPTIONS: SectionChipOption[] = [
  { key: 'aihome', label: 'AI Home' },
  { key: 'home', label: 'Дом и семья' },
];

const items = (value: SectionChipValue, allowAll = true) => {
  const pick = vi.fn();
  const list = sectionMenuItems(value, OPTIONS, allowAll, 'Следовать за открытой страницей', pick);
  return { list, pick };
};

const itemByLabel = (list: ReturnType<typeof items>['list'], label: string) =>
  list.find((e) => e.kind === 'item' && e.label === label);

describe('SectionChip меню', () => {
  it('пункт «Все разделы» зовёт onChange({mode:\'all\'})', () => {
    const { list, pick } = items({ mode: 'follow' });
    const all = itemByLabel(list, SECTION_CHIP_ALL_LABEL);
    expect(all).toBeTruthy();
    all!.onSelect();
    expect(pick).toHaveBeenCalledWith({ mode: 'all' });
  });
  it('пункт раздела зовёт onChange({mode:\'section\',key})', () => {
    const { list, pick } = items({ mode: 'all' });
    const home = itemByLabel(list, 'Дом и семья');
    expect(home).toBeTruthy();
    home!.onSelect();
    expect(pick).toHaveBeenCalledWith({ mode: 'section', key: 'home' });
  });
  it('allowAll=false — пункта «Все разделы» нет, разделы остаются', () => {
    const { list } = items({ mode: 'follow' }, false);
    expect(itemByLabel(list, SECTION_CHIP_ALL_LABEL)).toBeUndefined();
    expect(itemByLabel(list, 'AI Home')).toBeTruthy();
  });
  it('при follow у «Следовать…» галка; при выбранном разделе — галка у его пункта', () => {
    const follow = itemByLabel(items({ mode: 'follow' }).list, 'Следовать за открытой страницей');
    expect(follow!.checked).toBe(true);
    const notFollow = itemByLabel(items({ mode: 'section', key: 'home' }).list, 'Следовать за открытой страницей');
    expect(notFollow!.checked).toBe(false);
    const own = itemByLabel(items({ mode: 'section', key: 'home' }).list, 'Дом и семья');
    expect(own!.checked).toBe(true);
  });
  it('порядок меню: follow, разделитель, «Все разделы», разделы в порядке options', () => {
    const { list } = items({ mode: 'follow' });
    expect(list.map((e) => e.kind)).toEqual(['item', 'sep', 'item', 'item', 'item']);
    expect(list[0].kind === 'item' && list[0].label).toBe('Следовать за открытой страницей');
    expect(list[2].kind === 'item' && list[2].label).toBe(SECTION_CHIP_ALL_LABEL);
  });
});

describe('SectionChip триггер', () => {
  it('title — подсказка макета; при follow перед текстом значок Link2, без него — нет', () => {
    const follow = renderToStaticMarkup(createElement(SectionChip, {
      value: { mode: 'follow' }, options: OPTIONS, label: 'AI Home', onChange: () => {},
    }));
    expect(follow).toContain(`title="${SECTION_CHIP_TITLE}"`);
    expect(follow).toContain('lucide-link');
    const section = renderToStaticMarkup(createElement(SectionChip, {
      value: { mode: 'section', key: 'home' }, options: OPTIONS, label: 'Дом и семья', onChange: () => {},
    }));
    expect(section).not.toContain('lucide-link');
    expect(section).toContain('Дом и семья');
  });
});
