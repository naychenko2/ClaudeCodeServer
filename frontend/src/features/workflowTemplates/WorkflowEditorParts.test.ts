import { describe, expect, it, vi } from 'vitest';
import { createElement } from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { CONFLICT_MESSAGE, canSave, discardChanges } from './workflowEditorLogic';
import type { WorkflowTemplate } from './types';

vi.stubGlobal('window', Object.assign(new EventTarget(), {
  innerWidth: 1280, innerHeight: 900,
  matchMedia: () => ({ matches: false, addEventListener() {}, removeEventListener() {} }),
}));

const { EditorFooter, EditorNotices } = await import('./WorkflowEditorParts');

const noop = () => {};
const html = (el: React.ReactElement) => renderToStaticMarkup(el).replace(/<!-- -->/g, '');

const notices = (over: Record<string, unknown> = {}) => html(createElement(EditorNotices, {
  isBuiltIn: false, readOnly: false, canReset: true, failure: null, conflictVersion: null, errorItems: [],
  onReset: noop, onReload: noop, onSaveAsNew: noop, onJumpToStage: noop, ...over,
}));
const footer = (over: Record<string, unknown> = {}) => html(createElement(EditorFooter, {
  isNew: false, isBuiltIn: false, isMobile: false, canWrite: true, readOnly: false,
  canSave: true, dirty: true, busy: false, onDiscard: noop, onSave: noop, onDelete: noop, ...over,
}));

const conflict = { kind: 'conflict' as const, message: CONFLICT_MESSAGE, stageErrors: {}, generalErrors: [] };
const count = (h: string, s: string) => h.split(s).length - 1;

describe('F21: плашки и футер', () => {
  it('409: номер версии, обе кнопки, «в другом окне» один раз', () => {
    const h = notices({ failure: conflict, conflictVersion: 7 });
    expect(h).toContain('теперь это v7. Ваши правки не записаны.');
    expect(h).toContain('Загрузить v7 — мои правки пропадут');
    expect(h).toContain('Сохранить мои правки новым шаблоном');
    expect(count(h, 'в другом окне')).toBe(1);
    expect(count(h, CONFLICT_MESSAGE)).toBeLessThanOrEqual(1);
  });

  it('«Сохранить» при 409 отключена (canSave), в футере — disabled', () => {
    const t: WorkflowTemplate = { id: 'a', name: 'A', version: 1, stages: [{ role: 'step', presence: 'on' }] };
    expect(canSave(t, conflict)).toBe(false);
    const h = footer({ canSave: canSave(t, conflict) });
    expect(h).toMatch(/<button[^>]*disabled[^>]*>(<[^>]*>)*Сохранить<\/button>/);
  });

  it('ошибки: заголовок «2 ошибки», ссылки-кнопки на этапы', () => {
    const h = notices({ errorItems: [{ text: 'этап «step»: плохо', stageIndex: 1 }, { text: 'общая', stageIndex: null }] });
    expect(h).toContain('Сохранить нельзя — 2 ошибки');
    expect(h).toContain('<button');
    expect(h).toContain('общая');
  });

  it('встроенный: Notice, «Сбросить к исходному», «Удалить» с aria-disabled и подсказкой', () => {
    const n = notices({ isBuiltIn: true });
    expect(n).toContain('Встроенный шаблон удалить нельзя');
    expect(n).toContain('Сбросить к исходному');
    expect(notices({ isBuiltIn: true, canReset: false })).not.toContain('Сбросить к исходному');
    const f = footer({ isBuiltIn: true });
    expect(f).toContain('aria-disabled="true"');
    expect(f).toContain('title="Встроенный шаблон удалить нельзя"');
    expect(f).toContain('Удалить');
    expect(footer()).not.toContain('aria-disabled');
  });

  it('мобила: «Удалить» скрыта; «Отменить» отключена без правок', () => {
    expect(footer({ isMobile: true })).not.toContain('Удалить');
    expect(footer({ dirty: false })).toMatch(/<button[^>]*disabled[^>]*>(<[^>]*>)*Отменить<\/button>/);
  });

  it('«Отменить» откатывает к снимку: копия без общих этапов', () => {
    const saved: WorkflowTemplate = { id: 'a', name: 'A', version: 1, stages: [{ role: 'step', presence: 'on', prompt: 'x' }] };
    const edited = { ...saved, name: 'B', stages: [{ ...saved.stages[0], prompt: 'y' }] };
    const back = discardChanges(saved);
    expect(back).toEqual(saved);
    expect(back.stages[0]).not.toBe(saved.stages[0]);
    expect(edited.name).toBe('B');
  });
});
