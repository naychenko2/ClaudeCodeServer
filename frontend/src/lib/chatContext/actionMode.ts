// Мост «действие → режим поля»: пока выбрано run-действие, поле ввода ведёт себя как режим подсистемы
// (свой буфер текста, плейсхолдер, кнопка запуска вместо «Отправить», черновик по ключу объекта),
// но режим этот собирается из ContextAction и ActionRun, а не берётся из слота composer-mode. Так
// машинерия поля (черновики, затравка, строка режима) работает без второй копии.

import { createElement } from 'react';
import { RunLabel } from '../../components/generation/RunLabel';
import type { ComposerModeApi } from '../subsystems/registryCore';
import type { ActionPreset, ActionRun, ContextAction } from './types';

export const ACTION_MODE_ID = 'ctx-action';
export const NO_TEXT_PLACEHOLDER = 'ⓘ Текст не нужен — нажмите запуск';

// Черновик текста действия: ключ `{kind:ref}:{actionId}` (genDrafts)
export const actionDraftKey = (objectKey: string, actionId: string) => `${objectKey}:${actionId}`;

export function actionComposerMode(o: {
  action: ContextAction;
  run: ActionRun;
  objectKey: string;
  // Предвыбор вертикали: затравка текста; key — повод, на один key подставляется один раз
  preset: { key: string; value: ActionPreset } | null;
}): ComposerModeApi {
  const { action, run } = o;
  return {
    title: action.label,
    icon: null,
    isAvailable: () => true,
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
