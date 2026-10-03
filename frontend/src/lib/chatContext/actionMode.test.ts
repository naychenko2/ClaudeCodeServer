import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { actionComposerMode, actionDraftKey, NO_TEXT_PLACEHOLDER } from './actionMode';
import { stubActionRun } from './actionRunStub';
import type { ContextAction } from './types';

const act = (over: Partial<ContextAction> = {}): ContextAction =>
  ({ id: 'edit', kind: 'run', label: 'Изменить', hint: 'h', op: 'edit', text: 'required', placeholder: 'Что изменить на картинке…', ...over });
const ctx = { projectId: 'p', sessionId: 's' };

describe('действие как режим поля', () => {
  it('плейсхолдер и подпись кнопки — из действия и useActionRun', () => {
    const run = { ...stubActionRun(act()), label: '✦ Изменить · 3 вар. · $0.12', labelParts: { name: '✦ Изменить', tail: ' · 3 вар. · $0.12' } };
    const m = actionComposerMode({ action: act(), run, objectKey: 'image:{}', preset: null });
    expect(m.placeholder(ctx)).toBe('Что изменить на картинке…');
    expect(renderToStaticMarkup(m.submitLabel!(ctx) as never)).toContain('✦ Изменить');
    expect(renderToStaticMarkup(m.submitLabel!(ctx) as never)).toContain(' · 3 вар. · $0.12');
    expect(m.draftKey!(ctx)).toBe(actionDraftKey('image:{}', 'edit'));
  });

  it('текст не нужен: подсказка про запуск и запуск при пустом поле', () => {
    const a = act({ text: 'none', placeholder: undefined });
    const run = { ...stubActionRun(a), labelParts: { name: '✦ Изменить', tail: ' · $0.12' }, run: vi.fn(async () => {}) };
    const m = actionComposerMode({ action: a, run, objectKey: 'k', preset: null });
    expect(m.placeholder(ctx)).toBe(NO_TEXT_PLACEHOLDER);
    void m.emptySubmit!(ctx)!.run();
    expect(run.run).toHaveBeenCalledWith('');
    // Подпись кнопки при пустом поле — те же два куска, цена не режется
    expect(renderToStaticMarkup(m.emptySubmit!(ctx)!.label as never)).toContain('data-run-label-tail');
  });

  it('обязательный текст: при пустом поле запуска нет; отправка идёт в run', async () => {
    const run = { ...stubActionRun(act()), run: vi.fn(async () => {}) };
    const m = actionComposerMode({ action: act(), run, objectKey: 'k', preset: null });
    expect(m.emptySubmit!(ctx)).toBeNull();
    await m.onSubmit(ctx, 'убери тень');
    expect(run.run).toHaveBeenCalledWith('убери тень');
  });

  it('предвыбор даёт затравку поля, текст поля уходит в run.setText', () => {
    const setText = vi.fn();
    const run = { ...stubActionRun(act()), setText };
    const m = actionComposerMode({ action: act(), run, objectKey: 'k', preset: { key: 'k1', value: { actionId: 'edit', prefill: 'в стиле акварели' } } });
    expect(m.prefill!(ctx)).toEqual({ key: 'k1', text: 'в стиле акварели' });
    m.onTextChange!(ctx, 'abc');
    expect(setText).toHaveBeenCalledWith('abc');
  });
});
