// Перенос в новый чат по порогу контекста (docs/features/chat-handoff.md, раздел «Вторая
// точка входа»): на длинном контексте агент деградирует, поэтому при warn/danger рядом со
// «Сжать контекст» стоит «Перенести в новый чат», а при переходе в danger — один тост на чат.
// Пороги — пользовательские ContextThresholds (диалог «Пороги»), своих тут нет.
import type { ChatItem } from '../types';
import type { ContextEstimate } from './context';
import { promptCacheState } from './promptCache';
import { handoffWarm } from './chatHandoff';

// Кнопка переноса в ряду действий контекста — только когда контекст распух
export function contextHandoffOffered(level: ContextEstimate['level']): boolean {
  return level === 'warn' || level === 'danger';
}

// Путь переноса по статусу кэша на момент клика: ленты нет или статус unknown (сторонний
// провайдер, локальный движок) — холодный. Разовый расчёт, без таймера
export function contextHandoffWarm(cacheItems: ChatItem[] | undefined, now: number = Date.now()): boolean {
  return !!cacheItems && handoffWarm(promptCacheState(cacheItems, now).status);
}

const KEY_PREFIX = 'cc-ctx-handoff-toast:';

type ToastStorage = Pick<Storage, 'getItem' | 'setItem'>;

function defaultStorage(): ToastStorage | null {
  try { return typeof window !== 'undefined' ? window.sessionStorage : null; } catch { return null; }
}

// Пора ли показать тост «Контекст почти заполнен», и если да — сразу отметить показ. Один
// раз на чат в рамках вкладки: после сжатия и нового роста до danger тост не повторяется.
// Во время хода и сжатия молчим и показ не тратим. Без хранилища — без тоста (как у
// promptCacheToast: лучше промолчать, чем напоминать на каждой перерисовке)
export function takeContextDangerToast(
  sessionId: string | undefined, level: ContextEstimate['level'], isWaiting: boolean, isCompacting: boolean,
  storage: ToastStorage | null = defaultStorage(),
): boolean {
  if (!sessionId || isWaiting || isCompacting || level !== 'danger' || !storage) return false;
  const key = KEY_PREFIX + sessionId;
  try {
    if (storage.getItem(key)) return false;
    storage.setItem(key, '1');
  } catch {
    return false;
  }
  return true;
}
