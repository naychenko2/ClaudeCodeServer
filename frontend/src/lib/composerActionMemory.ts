// Память поля ввода для режима действия (ADR-023 §Д2): затравка текста, черновик выбранного элемента
// и кнопка запуска. Чистые функции под юнит-тестом; состояние держит поле (refs), здесь только правила.

import type { ReactNode } from 'react';
import type { ActionMode, ActionModeCtx } from './chatContext/actionMode';

// Затравка поля режима (ActionMode.prefill). key — повод (режим + нить): фиксируется
// с первого рендера на нём, даже пока текста нет, — иначе первый запуск свежей нити,
// пришедший после отправки, вернул бы промпт в очищенное поле. auto — текст, который
// положили мы сами: пока человек его не трогал, смена нити заменяет его новым
export interface PrefillState { key: string | null; auto: string | null }

export function nextPrefill(
  state: PrefillState, next: { key: string; text: string | null } | null, field: string,
): { state: PrefillState; field: string } {
  if (!next || next.key === state.key) return { state, field };
  const untouched = !field.trim() || (state.auto !== null && field === state.auto);
  if (!untouched) return { state: { key: next.key, auto: null }, field };
  return { state: { key: next.key, auto: next.text }, field: next.text ?? '' };
}

// Текст поля режима — черновик выбранного элемента (ActionMode.draftKey). key — последний
// элемент, под которым набирали. Набранное под ним уже лежит его черновиком (поле пишет его на
// каждой правке), поэтому при смене элемента поле получает черновик нового или пустеет, а пустое
// заполняет затравка нового. Нетронутую затравку (auto) и пустое поле не уносим: им нечего терять
export interface ModeDraftState { key: string | null }

export function nextModeDraft(
  state: ModeDraftState, nextKey: string | null, field: string, auto: string | null,
  draftOf: (key: string) => string | null,
): { state: ModeDraftState; field: string } {
  if (!nextKey || nextKey === state.key) return { state, field };
  const own = draftOf(nextKey);
  if (own !== null) return { state: { key: nextKey }, field: own };
  const touched = !!field.trim() && field !== auto;
  return { state: { key: nextKey }, field: state.key && touched ? '' : field };
}

// Что писать черновиком элемента по тексту поля: null — черновика нет (пусто или
// нетронутая затравка)
export const modeDraftText = (field: string, auto: string | null): string | null =>
  field.trim() && field !== auto ? field : null;

// Кнопка запуска строки режима: с текстом — отправка текста, как всегда; при пустом поле —
// запуск emptySubmit («↻ Ещё 2»), если он есть. Без него (или он вернул null) подпись режима,
// кнопка гаснет на пустом поле
export interface ModeSubmitButton {
  // 'text' — отправка набранного; 'empty' — запуск при пустом поле
  kind: 'text' | 'empty';
  disabled: boolean;
  label: ReactNode | null;
  run: (() => Promise<void> | void) | null;
}

export function modeSubmitButton(mode: ActionMode, ctx: ActionModeCtx, hasText: boolean, blocked: boolean): ModeSubmitButton {
  const empty = hasText ? null : mode.emptySubmit?.(ctx) ?? null;
  if (empty) return { kind: 'empty', disabled: blocked, label: empty.label, run: empty.run };
  return { kind: 'text', disabled: !hasText || blocked, label: mode.submitLabel ? mode.submitLabel(ctx) : null, run: null };
}
