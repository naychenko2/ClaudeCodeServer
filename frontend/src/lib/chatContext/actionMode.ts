// Мост «действие → режим поля»: пока выбрано run-действие, поле ввода ведёт себя как режим
// (свой буфер текста, плейсхолдер, кнопка запуска вместо «Отправить», черновик по ключу объекта).
// Режим собирается из ContextAction и ActionRun; слота composer-mode больше нет (ADR-023 §Д2).

import { createElement, type ReactNode } from 'react';
import { RunLabel } from '../../components/generation/RunLabel';
import type { ActionPreset, ActionRun, ContextAction } from './types';

export const ACTION_MODE_ID = 'ctx-action';

export interface ActionModeCtx { projectId: string | null; sessionId: string | null }

// Запуск при ПУСТОМ поле («↻ Ещё 2 · бесплатно»): кнопка отправки берёт эту подпись и действие
export interface ActionEmptySubmit {
  label: ReactNode;
  run: () => Promise<void> | void;
}

// Режим поля ввода: поле читает только это
export interface ActionMode {
  title: string;
  icon: ReactNode;
  // Текст, который поле получает при входе в режим; key — повод, на один key подставляется один раз
  // и только в нетронутое поле
  prefill?: (ctx: ActionModeCtx) => { key: string; text: string | null } | null;
  // Ключ элемента, черновиком которого считается текст режима (genDrafts): смена выбора уносит
  // набранное в черновик прежнего элемента и возвращает в поле черновик нового
  draftKey?: (ctx: ActionModeCtx) => string | null;
  placeholder: (ctx: ActionModeCtx) => string;
  // Подпись кнопки отправки: «✦ Изменить · ≈ $0.15»
  submitLabel?: (ctx: ActionModeCtx) => ReactNode;
  // Подпись над полем
  hint?: (ctx: ActionModeCtx) => ReactNode;
  // Отправка мимо агента; текст режима хранится отдельно от черновика чата
  onSubmit: (ctx: ActionModeCtx, text: string) => Promise<void> | void;
  emptySubmit?: (ctx: ActionModeCtx) => ActionEmptySubmit | null;
  // Текст поля режима после каждой правки: панель «Контекст» считает по нему цену
  onTextChange?: (ctx: ActionModeCtx, text: string) => void;
}
export const NO_TEXT_PLACEHOLDER = 'ⓘ Текст не нужен — нажмите запуск';

// Черновик текста действия: ключ `{kind:ref}:{actionId}` (genDrafts)
export const actionDraftKey = (objectKey: string, actionId: string) => `${objectKey}:${actionId}`;

export function actionComposerMode(o: {
  action: ContextAction;
  run: ActionRun;
  objectKey: string;
  // Предвыбор вертикали: затравка текста; key — повод, на один key подставляется один раз
  preset: { key: string; value: ActionPreset } | null;
}): ActionMode {
  const { action, run } = o;
  return {
    title: action.label,
    icon: null,
    placeholder: () => (action.text === 'none' ? NO_TEXT_PLACEHOLDER : action.placeholder ?? action.label),
    submitLabel: () => createElement(RunLabel, { parts: run.labelParts }),
    onSubmit: (_ctx, text) => run.run(text),
    // Текст не обязателен — запуск при пустом поле штатный; обязательный текст кнопка ждёт
    emptySubmit: () => (action.text === 'required' || action.disabledReason ? null : { label: createElement(RunLabel, { parts: run.labelParts }), run: () => run.run('') }),
    onTextChange: (_ctx, text) => run.setText(text),
    draftKey: () => actionDraftKey(o.objectKey, action.id),
    prefill: o.preset ? () => ({ key: o.preset!.key, text: o.preset!.value.prefill ?? null }) : undefined,
  };
}
