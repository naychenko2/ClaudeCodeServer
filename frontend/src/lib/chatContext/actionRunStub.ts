// ВРЕМЕННО (1ф-3): заглушка низа панели «Контекст» на типе ActionRun. Живой useActionRun —
// цена, запуск и прогресс через вклад вида — приходит в 1ф-4 и заменяет эту функцию целиком;
// до тех пор кнопка не запускает ничего, а цены нет.
import type { ActionRun, ContextAction } from './types';

export function stubActionRun(action: ContextAction | null): ActionRun {
  return {
    action,
    label: action ? `✦ ${action.label}` : '',
    quote: null,
    state: 'idle',
    progress: null,
    result: null,
    run: async () => {},
  };
}
