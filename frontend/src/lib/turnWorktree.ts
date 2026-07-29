// Дерево ХОДА: агент внутри хода может вызвать встроенный инструмент EnterWorktree
// и уйти в собственный git worktree, минуя Session.worktreePath (тот отвечает только
// за дерево ЧАТА, переключаемое кнопкой «Отдельное дерево»). Это состояние сервер
// отдаёт полем session_started.turnWorktree — оно переживает перезагрузку (пишется
// в историю), поэтому для восстановленной ленты достаточно последнего session_started.
//
// Особый случай — самый первый ход, где вызван EnterWorktree: сам этот ход стартовал
// ДО переключения папки, поэтому его же session_started несёт ещё старую cwd, и
// turnWorktree на нём пуст. Пока не пришёл СЛЕДУЮЩИЙ session_started, единственный
// источник данных — сам tool_use EnterWorktree: его текстовый результат «Entered
// worktree at <path> on branch <branch>» — источник истины (реальная папка может
// отличаться от запрошенной, напр. коллизия имени уводит бэк на «{branch}-2»).
// Результата ещё нет (ход в процессе) — временный фолбэк на input.path, если агент
// передал его явно; результат ЕСТЬ, но не распознан регэкспом — null: лучше пусто,
// чем выдать запрошенный путь за фактический.
import type { ChatItem } from '../types';
import { basename } from './paths';

export interface TurnTree {
  path: string;
  name: string;
}

const ENTER_WORKTREE_RESULT_RE = /Entered worktree at (.+?) on branch \S+/i;

function parseEnterWorktreeTool(it: Extract<ChatItem, { kind: 'tool_use' }>): TurnTree | null {
  if (typeof it.result === 'string') {
    const m = ENTER_WORKTREE_RESULT_RE.exec(it.result);
    if (m) {
      const path = m[1].trim();
      return { path, name: basename(path) };
    }
    // Результат ПРИШЁЛ, но не распознан (текст CLI изменился, коллизия имени увела
    // фактический путь в сторону от запрошенного и т.п.) — честнее показать пусто,
    // чем выдать ЗАПРОШЕННЫЙ агентом путь за факт: реальная папка могла отличаться
    return null;
  }
  // Результат ещё не пришёл (ход в процессе) — единственное, что есть, это входной
  // path, если агент передал его явно; это лучше, чем не показывать ничего
  const input = it.input as { path?: string } | null | undefined;
  if (input?.path) return { path: input.path, name: basename(input.path) };
  return null;
}

// Идём с конца ленты: первый встреченный session_started — источник истины (доверяем
// бэку, включая null — явный сигнал «ход вернулся в проект»). Если раньше встретился
// успешный EnterWorktree — используем его как временное значение текущего хода.
export function computeTurnTree(items: ChatItem[]): TurnTree | null {
  for (let i = items.length - 1; i >= 0; i--) {
    const it = items[i];
    if (it.kind === 'session_started') return it.turnWorktree ?? null;
    if (it.kind === 'tool_use' && it.name.toLowerCase() === 'enterworktree' && !it.isError) {
      const parsed = parseEnterWorktreeTool(it);
      if (parsed) return parsed;
    }
  }
  return null;
}

// Индексы session_started, которым в ленте есть что показать разделителем: 'entered' —
// этот ход ушёл в дерево агента; 'returned' — предыдущий session_started был в дереве,
// а этот — уже снова в проекте (turnWorktree пропал). Остальные session_started
// (в т.ч. самый первый в чате без turnWorktree — до него дерева агента не было
// и возвращаться неоткуда) прозрачны для ленты, как и раньше.
export function sessionStartedBoundaries(items: ChatItem[]): Map<number, 'entered' | 'returned'> {
  const boundaries = new Map<number, 'entered' | 'returned'>();
  let prevActive = false;
  let sawStarted = false;
  for (let i = 0; i < items.length; i++) {
    const it = items[i];
    if (it.kind !== 'session_started') continue;
    const active = !!it.turnWorktree;
    if (active) boundaries.set(i, 'entered');
    else if (sawStarted && prevActive) boundaries.set(i, 'returned');
    prevActive = active;
    sawStarted = true;
  }
  return boundaries;
}
