// Сводка изменений claude CLI вместо поштучного списка: счётчики по виду пункта.
import { describe, expect, it } from 'vitest';
import { cliChangesSummary } from '../cliChangesSummary';

describe('cliChangesSummary', () => {
  it('считает по первому слову в порядке «новое → изменения → улучшения → исправления»', () => {
    const changes = [
      { items: ['Fixed a', 'Fixed b', 'Added x', 'Improved y'] },
      { items: ['Changed z', 'Fixed c', 'Windows: fixed paths'] },
    ];
    expect(cliChangesSummary(changes))
      .toBe('2 версии: 1 новая возможность, 1 изменение, 1 улучшение, 3 исправления, 1 другое');
  });

  it('склонение на больших числах', () => {
    const items = [...Array(38).fill('Added x'), ...Array(270).fill('Fixed y')];
    expect(cliChangesSummary([{ items }, { items: [] }, { items: [] }, { items: [] }]))
      .toBe('4 версии: 38 новых возможностей, 270 исправлений');
  });

  it('слово в середине пункта вид не определяет; пусто — null', () => {
    expect(cliChangesSummary([{ items: ['Reverted Fixed thing'] }])).toBe('1 версия: 1 другое');
    expect(cliChangesSummary([])).toBeNull();
  });
});
