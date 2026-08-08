// Глобальный сигнал «Claude ждёт ответа человека»: признак живёт внутри ленты
// (ChatPanel считает незакрытые permission_request / ask_question), а показать его
// нужно на одной кнопке AI-хаба, видимой из любого раздела. ChatPanel регистрирует
// тут ждущий чат, AiLauncher подписывается через useAiAwaiting и ставит состояние
// «нужен ответ» выше «работы». Устройство повторяет lib/ai/busy.ts, но хранит не
// счётчик, а список ждущих чатов — он нужен, чтобы клик по кнопке вёл именно туда.

import { useSyncExternalStore } from 'react';
import type { Project, Session } from '../../types';

export interface AiAwaitingRec {
  chatId: string;
  // Имя для подписи строки в списке ждущих (балун FAB)
  name: string;
  // Сессия целиком — нужна для навигации: проектный чат WorkspacePage ставит активным
  // через sessionStorage(cc_pending_session), внепроектный открывается по id.
  session: Session;
  // Проектный чат — открыть воркспейс проекта; отсутствует у чата вне проекта.
  project?: Project;
}

const records = new Map<string, AiAwaitingRec>();
const listeners = new Set<() => void>();
const emit = () => listeners.forEach(l => l());

// Стабильный снимок для useSyncExternalStore: пересобираем только при смене состава,
// иначе каждое чтение getSnapshot давало бы новый массив → бесконечный ререндер.
let snapshot: AiAwaitingRec[] = [];
function recompute() {
  snapshot = records.size === 0 ? EMPTY : Array.from(records.values());
  emit();
}
const EMPTY: AiAwaitingRec[] = [];

// Регистрация ждущего чата. Повторный вызов с тем же chatId обновляет запись
// (напр. сменилось имя) — AiLauncher получит свежий снимок.
export function registerAiAwaiting(rec: AiAwaitingRec): void {
  records.set(rec.chatId, rec);
  recompute();
}

// Снять чат — ответили, ход завершился, компонент размонтировался.
export function unregisterAiAwaiting(chatId: string): void {
  if (records.delete(chatId)) recompute();
}

function subscribe(cb: () => void): () => void {
  listeners.add(cb);
  return () => { listeners.delete(cb); };
}
const getSnapshot = () => snapshot;

export function useAiAwaiting(): AiAwaitingRec[] {
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
}
