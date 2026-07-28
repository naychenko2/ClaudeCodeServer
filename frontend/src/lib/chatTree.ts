// Иерархия списка чатов: сборка леса по Session.parentSessionId и
// персистентность свёрнутых веток. Раздельно по областям, как chatFilters:
// 'global' и каждый projectId. Спецификация — docs/mockups/chat-list-tree-spec.md.
import { useEffect, useRef, useState } from 'react';
import type { Session } from '../types';
import type { ChatSortOrder } from './chatFilters';

const COLLAPSE_KEY_PREFIX = 'cc_chat_tree_collapsed:';

// === Память свёрнутых веток (Set id чатов) ===
export function useTreeCollapse(scopeKey: string) {
  const [collapsedIds, setCollapsedIds] = useState<Set<string>>(() => loadCollapsed(scopeKey));
  const scopeRef = useRef(scopeKey);

  useEffect(() => {
    if (scopeRef.current === scopeKey) return;
    scopeRef.current = scopeKey;
    setCollapsedIds(loadCollapsed(scopeKey));
  }, [scopeKey]);

  const toggleCollapse = (id: string) => {
    const next = new Set(collapsedIds);
    if (next.has(id)) next.delete(id); else next.add(id);
    try { localStorage.setItem(COLLAPSE_KEY_PREFIX + scopeKey, JSON.stringify([...next])); } catch { /* квота */ }
    setCollapsedIds(next);
  };

  return { collapsedIds, toggleCollapse };
}

function loadCollapsed(scopeKey: string): Set<string> {
  try {
    const raw = localStorage.getItem(COLLAPSE_KEY_PREFIX + scopeKey);
    if (!raw) return new Set();
    const arr = JSON.parse(raw);
    return Array.isArray(arr) ? new Set(arr.filter((x): x is string => typeof x === 'string')) : new Set();
  } catch {
    return new Set();
  }
}

// === Сборка леса и плоского списка строк дерева ===

interface TreeNode {
  chat: Session;
  children: TreeNode[];
  // Максимум updatedAt по всему поддереву — по нему сортируются корни
  maxActivity: number;
  // Чатов во ВСЁМ поддереве (без самого узла) и сколько из них в работе — бейдж
  // свёрнутой ветки. Именно всё поддерево, а не прямые дети: у свёрнутого узла
  // спрятаны и внуки, счётчик обязан их учитывать.
  groupCount: number;
  groupRunningCount: number;
}

// Готовая строка для рендера ChatTreeRow: глубина, геометрия связей, accent-путь
export interface ChatTreeRowData {
  chat: Session;
  depth: number;
  // Максимум updatedAt по всему поддереву узла — ключ секционирования корней
  // (корень попадает в дневную группу по активности поддерева, а не по своей дате)
  maxActivity: number;
  // Последний ребёнок у своего родителя — вертикаль-связь обрывается на elbow
  isLast: boolean;
  hasChildren: boolean;
  collapsed: boolean;
  // Бейдж свёрнутой ветки: сколько чатов спрятано во ВСЁМ поддереве и сколько из них
  // сейчас в работе (starting/working/waiting). Считается по поддереву, а не по прямым
  // детям — свёрнутый узел прячет и внуков.
  groupCount: number;
  groupRunningCount: number;
  // Строка лежит на пути корень→активный чат (сам активный или его предок)
  onActivePath: boolean;
  // Вертикаль-связь к родителю подсвечена accent (путь к активному чату проходит здесь)
  segAccent: boolean;
  elbowAccent: boolean;
  // Вертикаль под chevron ведёт к активному потомку
  stubAccent: boolean;
  // Сквозные вертикали предковых уровней (индекс = уровень оси)
  ancestors: { show: boolean; accent: boolean }[];
}

export interface ChatTreeResult {
  rows: ChatTreeRowData[];
  // Связей родитель→ребёнок в отрисованном лесу нет — показать подсказку-empty-state
  linkCount: number;
  // Всего чатов в отрисованном лесу (без учёта collapse) — для счётчика «скрыто фильтрами»
  renderedCount: number;
}

const activity = (c: Session) => new Date(c.updatedAt).getTime();

// Чат «в работе» прямо сейчас: агент думает/выполняет (starting, working) либо ждёт ответа
// пользователя на разрешение или вопрос (waiting). Набор совпадает с PULSE_STATUSES из
// StatusIndicator (там по нему мигает точка) — при правке держать синхронно.
// Статус active сюда НЕ входит: ход уже завершён, процесс просто жив, и точка статуса
// красит его зелёным именно как «не работу».
const RUNNING_STATUSES = new Set<Session['status']>(['starting', 'working', 'waiting']);

export const isChatRunning = (c: Session) => RUNNING_STATUSES.has(c.status);

// Потолок числа в бейдже свёрнутой ветки: бейдж вылезает из своей gutter-колонки поверх
// карточки, и «128/12» накрыл бы точку статуса вместе с началом названия чата
export const formatGroupCount = (n: number) => (n > 99 ? '99+' : String(n));

/**
 * Все потомки чата (без него самого) — запретные цели при перетаскивании: вложить
 * чат в собственного потомка значило бы замкнуть кольцо. Бэкенд это тоже отклоняет
 * (SessionManager.SetParent), здесь — чтобы drop-зона просто не подсвечивалась.
 * out-набор защищает от цикла, уже лежащего в данных.
 */
export function collectDescendants(chats: Session[], rootId: string): Set<string> {
  const childrenOf = new Map<string, string[]>();
  for (const c of chats) {
    const pid = c.parentSessionId;
    if (!pid || pid === c.id) continue;
    const bucket = childrenOf.get(pid);
    if (bucket) bucket.push(c.id); else childrenOf.set(pid, [c.id]);
  }
  const out = new Set<string>();
  const walk = (id: string) => {
    for (const kid of childrenOf.get(id) ?? []) {
      if (out.has(kid) || kid === rootId) continue;
      out.add(kid);
      walk(kid);
    }
  };
  walk(rootId);
  return out;
}

/**
 * Дерево чатов из плоского массива по parentSessionId, рекурсивно на любую глубину.
 * Фильтр применяется к КАЖДОМУ узлу (isVisible): видимый родитель тянет только видимых
 * детей; скрытый узел «прокалывается» — его видимые потомки поднимаются к ближайшему
 * видимому предку (или в корни). Так множество видимых чатов совпадает с плоским
 * списком и не зависит от вида. Защита от циклов — visited-набор.
 */
export function buildChatTreeRows(
  chats: Session[],
  opts: {
    isVisible: (c: Session) => boolean;
    collapsedIds: Set<string>;
    activeId: string | null;
    // Направление сортировки детей и корней (дефолт — свежие сверху)
    sortOrder?: ChatSortOrder;
  },
): ChatTreeResult {
  const dir = opts.sortOrder === 'oldest' ? 1 : -1;
  const byId = new Map(chats.map(c => [c.id, c]));
  const childrenOf = new Map<string, Session[]>();
  const topCandidates: Session[] = [];
  for (const c of chats) {
    const pid = c.parentSessionId;
    if (pid && pid !== c.id && byId.has(pid)) {
      const bucket = childrenOf.get(pid);
      if (bucket) bucket.push(c); else childrenOf.set(pid, [c]);
    } else {
      topCandidates.push(c);
    }
  }

  // Сборка узлов DFS от кандидатов в корни; visited защищает от циклов parentSessionId
  const visited = new Set<string>();
  const buildNode = (chat: Session): TreeNode => {
    visited.add(chat.id);
    const kids = (childrenOf.get(chat.id) ?? [])
      .filter(k => !visited.has(k.id))
      .sort((a, b) => dir * (activity(a) - activity(b)))
      .map(buildNode);
    return {
      chat,
      children: kids,
      maxActivity: Math.max(activity(chat), ...kids.map(k => k.maxActivity)),
      groupCount: kids.reduce((n, k) => n + 1 + k.groupCount, 0),
      groupRunningCount: kids.reduce(
        (n, k) => n + (isChatRunning(k.chat) ? 1 : 0) + k.groupRunningCount, 0),
    };
  };
  const topNodes = topCandidates.map(buildNode);
  // Чаты, не достижимые из кандидатов (цикл ссылок) — разрываем, поднимая в корни
  for (const c of chats) {
    if (!visited.has(c.id)) topNodes.push(buildNode(c));
  }

  // Фильтр по всему дереву: скрытый узел «прокалывается» — его видимые дети
  // поднимаются к ближайшему видимому предку (или в корни). Метрики поддерева
  // пересчитываются по отфильтрованному составу, чтобы бейджи свёрнутых веток
  // считали только видимых потомков. Множество видимых чатов совпадает с плоским
  // списком — hiddenCount не зависит от вида.
  const filterForest = (nodes: TreeNode[]): TreeNode[] => {
    const visit = (node: TreeNode, sink: TreeNode[]) => {
      const kids: TreeNode[] = [];
      for (const k of node.children) visit(k, kids);
      if (opts.isVisible(node.chat)) {
        sink.push({
          chat: node.chat,
          children: kids,
          maxActivity: Math.max(activity(node.chat), ...kids.map(k => k.maxActivity)),
          groupCount: kids.reduce((n, k) => n + 1 + k.groupCount, 0),
          groupRunningCount: kids.reduce(
            (n, k) => n + (isChatRunning(k.chat) ? 1 : 0) + k.groupRunningCount, 0),
        });
      } else {
        // узел скрыт фильтром — прокол: его видимые дети уходят уровнем выше
        for (const k of kids) sink.push(k);
      }
    };
    const roots: TreeNode[] = [];
    nodes.forEach(n => visit(n, roots));
    return roots;
  };
  const roots = filterForest(topNodes);

  // Закреплённые корни сверху (без группового заголовка), дальше — по активности
  // поддерева в направлении sortOrder
  roots.sort((a, b) => {
    const pin = Number(b.chat.isPinned ?? false) - Number(a.chat.isPinned ?? false);
    return pin !== 0 ? pin : dir * (a.maxActivity - b.maxActivity);
  });

  let linkCount = 0;
  let renderedCount = 0;
  const countNode = (n: TreeNode) => {
    renderedCount++;
    linkCount += n.children.length;
    n.children.forEach(countNode);
  };
  roots.forEach(countNode);

  // Предки активного чата — для accent-подсветки пути корень→активный
  const parentOf = new Map<string, string>();
  const fillParents = (n: TreeNode) => {
    for (const k of n.children) { parentOf.set(k.chat.id, n.chat.id); fillParents(k); }
  };
  roots.forEach(fillParents);
  const activeAncestors = new Set<string>();
  if (opts.activeId && (parentOf.has(opts.activeId) || roots.some(r => r.chat.id === opts.activeId))) {
    let cur = parentOf.get(opts.activeId);
    while (cur) { activeAncestors.add(cur); cur = parentOf.get(cur); }
  }
  const onPath = (id: string) => id === opts.activeId || activeAncestors.has(id);

  // Флаттен с collapse: свёрнутое поддерево не рендерится вовсе.
  // ancestors строки — сквозные вертикали осей 0..depth-2 (ось своей seg-линии
  // depth-1 в массив не входит); passBelow — продолжение родительской оси через
  // ПОДдерево узла (у узла есть следующие сиблинги) — становится записью
  // ancestors у его детей.
  const rows: ChatTreeRowData[] = [];
  const emit = (
    node: TreeNode, depth: number, isLast: boolean, segAccent: boolean,
    passBelow: { show: boolean; accent: boolean },
    ancestors: { show: boolean; accent: boolean }[],
  ) => {
    const collapsed = opts.collapsedIds.has(node.chat.id) && node.children.length > 0;
    rows.push({
      chat: node.chat,
      depth,
      maxActivity: node.maxActivity,
      isLast,
      hasChildren: node.children.length > 0,
      collapsed,
      groupCount: node.groupCount,
      groupRunningCount: node.groupRunningCount,
      onActivePath: onPath(node.chat.id),
      segAccent,
      elbowAccent: onPath(node.chat.id),
      stubAccent: activeAncestors.has(node.chat.id),
      ancestors,
    });
    if (collapsed) return;
    // Индекс ребёнка на пути к активному чату — до него (включительно) ось accent
    const qIndex = node.children.findIndex(k => onPath(k.chat.id));
    const childAncestors = depth === 0 ? [] : [...ancestors, passBelow];
    node.children.forEach((k, i) => {
      emit(
        k, depth + 1, i === node.children.length - 1,
        qIndex >= 0 && i <= qIndex,
        { show: i < node.children.length - 1, accent: qIndex >= 0 && i < qIndex },
        childAncestors,
      );
    });
  };
  roots.forEach(r => emit(r, 0, true, false, { show: false, accent: false }, []));

  return { rows, linkCount, renderedCount };
}

// Нарезка плоских строк дерева на сегменты по корням: depth 0 открывает новый
// сегмент, строки depth>0 до следующего корня — его видимое поддерево (уже после
// collapse). Используется секционированием корней (дни/теги) — секция рендерит
// корень с дочерними строками под собой.
export function splitChatTreeByRoots(rows: ChatTreeRowData[]): ChatTreeRowData[][] {
  const segments: ChatTreeRowData[][] = [];
  for (const row of rows) {
    if (row.depth === 0 || segments.length === 0) segments.push([row]);
    else segments[segments.length - 1].push(row);
  }
  return segments;
}
