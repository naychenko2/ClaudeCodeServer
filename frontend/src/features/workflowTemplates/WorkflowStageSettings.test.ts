// F23: настройка этапа — оболочка (шторка/экран), пустое состояние «Этап не выбран»,
// выбранная строка ленты (рамка accent + focus-тень, aria-pressed). Статический рендер.
import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import type { WorkflowStage } from './types';

vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 1280, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener() {}, removeEventListener() {} }),
}));
const state = { mobile: false };
vi.mock('../../lib/breakpoints', async orig => ({ ...(await orig<object>()), useIsMobile: () => state.mobile }));
vi.mock('../../lib/models', async orig => ({ ...(await orig<object>()), useModels: () => [] }));
vi.mock('../../lib/presets', async orig => ({ ...(await orig<object>()), usePresets: () => [] }));

const { WorkflowStageSettings } = await import('./WorkflowStageSettings');
const { WorkflowStageRow } = await import('./WorkflowStageRow');

const noop = () => {};
const stages: WorkflowStage[] = [
  { role: 'cut', presence: 'on' },
  { role: 'review', presence: 'on', after: ['cut'] },
];
const html = (stage: WorkflowStage | null) => renderToStaticMarkup(createElement(WorkflowStageSettings, {
  stage, stages, index: 1, readOnly: false, specialties: [], personas: [], mcpChoices: [],
  onChange: noop, onRenameId: noop, onRemove: noop, onClose: noop,
})).replace(/<!-- -->/g, '');

describe('настройка этапа — оболочка', () => {
  it('десктоп: шторка 540px, заголовок «Этап «Ревью» review», «✕», без «Готово»', () => {
    state.mobile = false;
    const h = html(stages[1]);
    expect(h).toContain('width:540px');
    expect(h).toContain('Этап «Ревью»');
    expect(h).toContain('review');
    expect(h).toContain('aria-label="Закрыть настройку этапа"');
    expect(h).not.toContain('Готово');
    expect(h).toContain('Присутствие');
  });

  it('мобила: экран на всю область с «‹» и «Готово», без шторки 540px', () => {
    state.mobile = true;
    const h = html(stages[1]);
    expect(h).toContain('Готово');
    expect(h).toContain('aria-label="Назад к этапам"');
    expect(h).not.toContain('width:540px');
    expect(h).not.toContain('Закрыть настройку этапа');
  });

  it('без выбора — «Этап не выбран» с пояснением и без полей', () => {
    state.mobile = false;
    const h = html(null);
    expect(h).toContain('Этап не выбран');
    expect(h).toContain('появятся исполнитель, промпт, модели, права и таймаут');
    expect(h).not.toContain('Присутствие');
  });
});

describe('строка этапа — выбор', () => {
  const row = (selected: boolean) => renderToStaticMarkup(createElement(WorkflowStageRow, {
    stage: stages[1], index: 1, errors: [], readOnly: false, selected, onSelect: noop,
  })).replace(/<!-- -->/g, '');

  it('выбранная — aria-pressed=true, рамка accent и focus-тень; невыбранная — нет', () => {
    const on = row(true);
    expect(on).toContain('aria-pressed="true"');
    expect(on).toContain('box-shadow:var(--shadow-focus)');
    const off = row(false);
    expect(off).toContain('aria-pressed="false"');
    expect(off).not.toContain('box-shadow:var(--shadow-focus)');
  });

  it('строка не раскрывается: полей настройки в ней нет', () => {
    expect(row(true)).not.toContain('Присутствие');
  });
});
