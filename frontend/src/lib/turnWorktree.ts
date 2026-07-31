// Дерево ХОДА: агент внутри хода может вызвать встроенный инструмент EnterWorktree
// и уйти в собственный git worktree, минуя Session.worktreePath (тот отвечает только
// за дерево ЧАТА, переключаемое кнопкой «Отдельное дерево»). Это состояние сервер
// отдаёт полем session_started.turnWorktree — оно переживает перезагрузку (пишется
// в историю), поэтому для восстановленной ленты достаточно последнего session_started.
//
// Особый случай — самый первый ход, где вызван EnterWorktree: сам этот ход стартовал
// ДО переключения папки, поэтому его же session_started несёт ещё старую cwd, и
// turnWorktree на нём пуст. Пока не пришёл СЛЕДУЮЩИЙ session_started, единственный
// источник данных — сам tool_use EnterWorktree: его текстовый результат — источник
// истины (реальная папка может отличаться от запрошенной, напр. коллизия имени
// уводит бэк на «{branch}-2»). Формулировка зависит от режима инструмента: «Created
// worktree at <path> on branch <branch>» при создании нового дерева (параметр name),
// «Entered worktree at <path> on branch <branch>» при входе в уже существующее
// (параметр path) — регэксп ниже намеренно не якорится на конкретный глагол,
// цепляется только за инвариантную середину фразы «worktree at … on branch …».
// Результата ещё нет (ход в процессе) — временный фолбэк на input.path, если агент
// передал его явно; результат ЕСТЬ, но не распознан регэкспом — null: лучше пусто,
// чем выдать запрошенный путь за фактический.
//
// Третий (низкоприоритетный) механизм — эвристика по путям изменённых файлов: если
// агент ушёл в дерево мимо EnterWorktree (напр. git worktree add через Bash) или
// результат EnterWorktree не распознан, о нём скажут tool_use Edit/Write/NotebookEdit
// с file_path внутри сегмента .claude/worktrees/<имя>/. Сматчить можно ложно (агент
// из проекта осознанно правит файл в оставшемся на диске дереве) — поэтому эвристика
// ниже всех по приоритету, а следующий session_started в любом случае её перекроет.
import type { ChatItem } from '../types';
import { basename } from './paths';

export interface TurnTree {
  path: string;
  name: string;
}

// Не якоримся на глаголе (Created/Entered — см. комментарий выше) и на переносах строк
// (флаг s): любая будущая правка формулировки CLI, не задевающая саму структуру
// «worktree at … on branch …», индикатор не сломает.
const ENTER_WORKTREE_RESULT_RE = /\bworktree at (.+?)\s+on branch \S+/is;

// Warn однократно на tool_use: computeTurnTree пересчитывается на каждой text_delta
// (useMemo по items в ChatPanel), без дедупликации нераспознанный результат спамил
// бы консоль сотнями строк за ход.
const warnedEnterWorktreeIds = new Set<string>();

function parseEnterWorktreeTool(it: Extract<ChatItem, { kind: 'tool_use' }>): TurnTree | null {
  if (typeof it.result === 'string') {
    const m = ENTER_WORKTREE_RESULT_RE.exec(it.result);
    if (m) {
      const path = m[1].trim();
      return { path, name: basename(path) };
    }
    // Результат ПРИШЁЛ, но не распознан (текст CLI изменился, коллизия имени увела
    // фактический путь в сторону от запрошенного и т.п.) — честнее показать пусто,
    // чем выдать ЗАПРОШЕННЫЙ агентом путь за факт: реальная папка могла отличаться.
    // Warn — диагностика поломки формулировки CLI: если индикатор молчит на живом
    // EnterWorktree, по тексту в консоли видно, под какую форму чинить регэксп.
    // Пустая строка — легитимный результат без текста, диагностировать нечего.
    if (it.result !== '' && !warnedEnterWorktreeIds.has(it.id)) {
      warnedEnterWorktreeIds.add(it.id);
      console.warn('[turnWorktree] результат EnterWorktree не распознан регэкспом:', it.result);
    }
    return null;
  }
  // Результат ещё не пришёл (ход в процессе) — единственное, что есть, это входной
  // path, если агент передал его явно; это лучше, чем не показывать ничего
  const input = it.input as { path?: string } | null | undefined;
  if (input?.path) return { path: input.path, name: basename(input.path) };
  return null;
}

// Эвристика по путям изменённых файлов (третий приоритет): только Edit/Write/NotebookEdit
// и только сегмент .claude/worktrees/<имя>/ — «файл вне корня проекта» признаком дерева
// НЕ является (скретчпады, чужие проекты). Оба разделителя: / и \.
const FILE_EDIT_TOOL_NAMES = new Set(['edit', 'write', 'notebookedit']);
// При вложенных деревьях (.claude/worktrees внутри дерева) матчится ВНЕШНЕЕ —
// осознанный компромисс: кейс экзотический, следующий session_started всё чинит.
const WORKTREE_SEGMENT_RE = /(?:^|[\\/])\.claude[\\/]worktrees[\\/]([^\\/]+)[\\/]/i;

function parseFileEditTool(it: Extract<ChatItem, { kind: 'tool_use' }>): TurnTree | null {
  const input = it.input as { file_path?: unknown } | null | undefined;
  if (typeof input?.file_path !== 'string') return null;
  const fp = input.file_path;
  const m = WORKTREE_SEGMENT_RE.exec(fp);
  if (!m) return null;
  // Путь до <имя> включительно: отрезаем завершающий разделитель из совпадения
  const path = fp.slice(0, m.index + m[0].length - 1);
  return { path, name: m[1] };
}

// Идём с конца ленты и накапливаем кандидатов до первого session_started:
// - заданный turnWorktree session_started — истина бэка; сильнее неё только
//   EnterWorktree ПОСЛЕ неё (ход стартовал в wt-A, агент внутри того же хода
//   перешёл в wt-B) — эвристика по файлам истину бэка НЕ перебивает;
// - пустой (null) — «ход стартовал в проекте», но кандидаты ПОСЛЕ него — действия
//   того же хода уже после старта (первый ход, уход в дерево), им доверяем;
// - кандидаты: распарсенный EnterWorktree сильнее эвристики по путям файлов.
// Всё раньше session_started к текущему ходу не относится — поиск обрывается.
export function computeTurnTree(items: ChatItem[]): TurnTree | null {
  let enterCandidate: TurnTree | null = null;
  let fileCandidate: TurnTree | null = null;
  for (let i = items.length - 1; i >= 0; i--) {
    const it = items[i];
    if (it.kind === 'session_started') {
      if (it.turnWorktree) return enterCandidate ?? it.turnWorktree;
      return enterCandidate ?? fileCandidate ?? null;
    }
    // Сабагентские tool_use (parentToolUseId) не считаются: сабагент легально
    // правит своё изолированное дерево, cwd основного хода это не меняет.
    if (it.kind !== 'tool_use' || it.isError || it.parentToolUseId) continue;
    const tool = it.name.toLowerCase();
    if (!enterCandidate && tool === 'enterworktree') {
      enterCandidate = parseEnterWorktreeTool(it);
    } else if (!fileCandidate && FILE_EDIT_TOOL_NAMES.has(tool)) {
      fileCandidate = parseFileEditTool(it);
    }
  }
  return enterCandidate ?? fileCandidate;
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
