import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { ComposerActionRow, type ComposerActionRowProps } from '../ComposerActionRow';
import type { ContextAction } from '../../../lib/chatContext/types';

const run = (id: string, over: Partial<ContextAction> = {}): ContextAction => ({ id, kind: 'run', label: id, hint: `хинт ${id}`, op: id, ...over });
const base: ComposerActionRowProps = { actions: [run('edit'), run('removeBg')], selectedId: 'edit', onSelect: () => {}, question: null, isMobile: false };
const html = (p: Partial<ComposerActionRowProps> = {}) => renderToStaticMarkup(createElement(ComposerActionRow, { ...base, ...p }));

describe('ComposerActionRow', () => {
  it('«Чат» первым, выбранное действие — радио с aria-checked', () => {
    const h = html();
    expect(h.indexOf('data-action-chip="__chat"')).toBeLessThan(h.indexOf('data-action-chip="edit"'));
    expect(h).toMatch(/data-action-chip="edit"[^>]*aria-checked="true"/);
    expect(h).toMatch(/data-action-chip="removeBg"[^>]*aria-checked="false"/);
    expect(h).toMatch(/data-action-chip="__chat"[^>]*aria-checked="false"/);
  });

  it('«Чат» выбран, когда выбранного действия нет', () => {
    expect(html({ selectedId: null })).toMatch(/data-action-chip="__chat"[^>]*aria-checked="true"/);
  });

  it('вход и меню — пунктирные кнопки без radio; серый чип помечен и с причиной в тултипе', () => {
    const h = html({
      actions: [
        run('stems', { disabledReason: 'Выделите кусок на волне' }),
        { id: 'mark', kind: 'editor', label: 'Отметить', hint: 'Открыть редактор на кисти', open: () => {} },
        { id: 'frame', kind: 'menu', label: 'Кадр A', hint: 'откуда взять', items: () => [] },
      ],
      selectedId: null,
    });
    expect(h).toMatch(/data-action-chip="stems"[^>]*data-gray=""/);
    expect(h).toContain('stems: Выделите кусок на волне');
    expect(h).toMatch(/data-action-chip="mark"[^>]*role="button"/);
    expect(h).toMatch(/data-action-chip="frame"[^>]*role="button"/);
    expect(h).toContain('dashed');
  });

  it('вопрос рисуется строкой под чипами только у выбранного действия с вопросом', () => {
    const q = { param: 'stemSet', title: 'Набор', options: [{ value: 'a', label: 'Вокал + минус' }, { value: 'b', label: '4 стема' }] };
    const actions = [run('stems', { question: q })];
    const h = html({ actions, selectedId: 'stems', question: { value: 'a', onChange: () => {} } });
    expect(h).toContain('data-action-question');
    expect(h).toContain('Набор:');
    expect(h).toContain('Вокал + минус');
    // «Чат» выбран — вопроса нет
    expect(html({ actions, selectedId: null, question: { value: 'a', onChange: () => {} } })).not.toContain('data-action-question');
  });

  it('седьмое действие (шестое от вида) — исключение', () => {
    expect(() => html({ actions: Array.from({ length: 6 }, (_, i) => run(`a${i}`)) })).toThrow(/потолок/);
  });
});
