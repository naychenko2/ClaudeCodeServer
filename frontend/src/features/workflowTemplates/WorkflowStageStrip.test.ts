import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { WorkflowStage } from './types';

vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 1280, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener() {}, removeEventListener() {} }),
}));

const { WorkflowStageStrip } = await import('./WorkflowStageStrip');

const noop = () => {};
// React ставит <!-- --> между соседними текстовыми узлами — для сравнения подписей убираем.
const render = (stages: WorkflowStage[], extra: Record<string, unknown> = {}) => renderToStaticMarkup(createElement(WorkflowStageStrip, {
  stages, readOnly: false, specialties: [], personas: [], mcpChoices: [], errorsFor: () => [],
  onStagesChange: noop, onStageChange: noop, onRenameStage: noop, onRemoveStage: noop, ...extra,
})).replace(/<!-- -->/g, '');

const full = (): WorkflowStage[] => [
  { role: 'cut', presence: 'on' },
  { role: 'design', presence: 'off' },
  { role: 'plan', presence: 'on', after: ['cut'] },
  { role: 'step', presence: 'on', id: 'step-back', lane: 'back', after: ['plan'] },
  { role: 'step', presence: 'optional', id: 'step-front', lane: 'front', after: ['plan'] },
];

describe('лента этапов — разметка', () => {
  it('«после» именами, теги, «Разрез», выключенный — «—» и «нет в этом шаблоне», рамка колонки', () => {
    const h = render(full());
    expect(h).toContain('Разрез');
    expect(h).toContain('после: Разрез');
    expect(h).not.toContain('после: cut');
    expect(h).toContain('всегда');
    expect(h).toContain('по решению');
    expect(h).toContain('нет в этом шаблоне');
    expect(h).toContain('>—<');
    expect(h).toContain('⇉ колонка 3 · параллельно');
    expect(h).toContain('Этапы · порядок = маршрут');
    expect(h).toContain('+ Этап');
  });

  it('пустой шаблон: «Этапов пока нет» и «Взять состав» только при наличии пресета', () => {
    const empty = render([], { fullCycleStages: full() });
    expect(empty).toContain('Этапов пока нет');
    expect(empty).toContain('Взять состав «Полного цикла»');
    expect(render([])).not.toContain('Взять состав');
  });

  it('только чтение: ни ручек, ни добавления', () => {
    const h = render(full(), { readOnly: true });
    expect(h).not.toContain('Переставить этап');
    expect(h).not.toContain('Добавить этап');
  });
});
