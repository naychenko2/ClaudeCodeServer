// Фикстура ActionRun для витрины и юнитов: подпись без цены, запуск ничего не делает. Хосты продукта
// её НЕ импортируют (сторож useActionRunParity.test.ts) — у них один источник, useActionRun.
import type { ActionRun, ContextAction } from './types';

export function stubActionRun(action: ContextAction | null): ActionRun {
  return {
    action,
    label: action ? `✦ ${action.label}` : '',
    labelParts: { name: action ? `✦ ${action.label}` : '', tail: '' },
    quote: null,
    state: 'idle',
    progress: null,
    result: null,
    run: async () => {},
    stop: null,
    text: '',
    answer: null,
    setText: () => {},
    blocked: action?.disabledReason ?? null,
    params: [],
    setParam: () => {},
  };
}
