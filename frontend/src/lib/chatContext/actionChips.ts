// Набор чипов строки действий поля ввода (ADR-023 §Д2): первый «Чат» ставит хост, вид отдаёт не больше
// пяти действий. Лишнее хост НЕ обрезает: в dev бросает исключение (сторож каталога), в проде
// рисует как есть — обрезка молча прятала бы действие от человека.

import type { ContextAction } from './types';

export const CHAT_CHIP_ID = '__chat';
export const MAX_VIEW_ACTIONS = 5;

export interface ActionChip {
  // id действия; у «Чата» — CHAT_CHIP_ID
  id: string;
  kind: 'chat' | ContextAction['kind'];
  label: string;
  hint: string;
  action: ContextAction | null;
}

export const CHAT_HINT = 'Сообщение Claude — запуск генератора не нужен';

export function actionChips(actions: readonly ContextAction[]): ActionChip[] {
  if (actions.length > MAX_VIEW_ACTIONS && import.meta.env.DEV) {
    throw new Error(`Вид отдал ${actions.length} действий, потолок — ${MAX_VIEW_ACTIONS} (+ «Чат» от хоста): лишнее переносится в редактор`);
  }
  const ids = new Set<string>();
  for (const a of actions) {
    if (a.id === CHAT_CHIP_ID || ids.has(a.id)) throw new Error(`Действие вида «${a.id}» не уникально или занимает id «Чата»`);
    ids.add(a.id);
  }
  return [
    { id: CHAT_CHIP_ID, kind: 'chat', label: 'Чат', hint: CHAT_HINT, action: null },
    ...actions.map(a => ({ id: a.id, kind: a.kind, label: a.label, hint: a.disabledReason ? `${a.label}: ${a.disabledReason}` : a.hint, action: a })),
  ];
}

// Чип выбирается радио: только «Чат» и run-действие без серости
export const chipSelectable = (c: ActionChip): boolean =>
  c.kind === 'chat' || (c.kind === 'run' && !c.action?.disabledReason);
