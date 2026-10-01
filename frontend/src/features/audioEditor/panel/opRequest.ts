// Просьба карточки нити к панели «Звук» переключить операцию («Обрезать», «Перегенерировать кусок»,
// threadStore.requestOperation). Каждую просьбу панель отрабатывает ровно раз: отработанный seq
// помним на уровне модуля — панель могла смонтироваться уже после просьбы или пересоздаться.

import { getOperationRequest, type OperationRequest } from '../thread/threadStore';

const handled = new Map<string, number>();

// Ещё не отработанная просьба (не помечает её)
export function pendingOperation(sessionId: string | null): OperationRequest | null {
  const r = getOperationRequest(sessionId);
  if (!sessionId || !r || (handled.get(sessionId) ?? 0) >= r.seq) return null;
  return r;
}

// Забрать просьбу: второй вызов на тот же seq вернёт null
export function takeOperation(sessionId: string | null): OperationRequest | null {
  const r = pendingOperation(sessionId);
  if (r) handled.set(sessionId!, r.seq);
  return r;
}

export const __resetOperationRequests = () => handled.clear();
