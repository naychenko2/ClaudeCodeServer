// Иерархия списка чатов: сборка леса по Session.parentSessionId и
// персистентность режима вида («Плоский/Иерархия») и свёрнутых веток.
// Раздельно по областям, как chatFilters: 'global' и каждый projectId.
// Спецификация — docs/mockups/chat-list-tree-spec.md.
import { useEffect, useRef, useState } from 'react';
import type { Session } from '../types';

export type ChatViewMode = 'flat' | 'tree';

const VIEW_KEY_PREFIX = 'cc_chat_view:';
const COLLAPSE_KEY_PREFIX = 'cc_chat_tree_collapsed:';

// === Режим вида списка (настройка вида, не фильтр) ===
export function useChatView(scopeKey: string) {
  const [view, setViewState] = useState<ChatViewMode>(() => loadView(scopeKey));
  const scopeRef = useRef(scopeKey);

  useEffect(() => {
    if (scopeRef.current === scopeKey) return;
    scopeRef.current = scopeKey;
    setViewState(loadView(scopeKey));
  }, [scopeKey]);

  const setView = (v: ChatViewMode) => {
    try { localStorage.setItem(VIEW_KEY_PREFIX + scopeKey, v); } catch { /* квота/приватный режим */ }
    setViewState(v);
  };

  return { view, setView };
}

function loadView(scopeKey: string): ChatViewMode {
  try {
    return localStorage.getItem(VIEW_KEY_PREFIX + scopeKey) === 'tree' ? 'tree' : 'flat';
  } catch {
    return 'flat';
  }
}

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
}

// Готовая строка для рендера ChatTreeRow: глубина, геометрия связей, accent-путь
export interface ChatTreeRowData {
  chat: Session;
  depth: number;
  // Последний ребёнок у своего родителя — вертикаль-связь обрывается на elbow
  isLast: boolean;
  hasChildren: boolean;
  collapsed: boolean;
  // Число ПРЯМЫХ детей — бейдж у свёрнутого chevron
  childCount: number;
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

/**
 * Дерево чатов из плоского массива по parentSessionId, рекурсивно на любую глубину.
 * Фильтры применяются только к КОРНЯМ (isRootVisible): видимый родитель всегда тянет
 * всех своих детей; у скрытого корня дети всплывают кандидатами в корни (сирота —
 * обычный корень без пометок). Защита от циклов — visited-набор.
 */
export function buildChatTreeRows(
  chats: Session[],
  opts: {
    isRootVisible: (c: Session) => boolean;
    collapsedIds: Set<string>;
    activeId: string | null;
  },
): ChatTreeResult {
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
      .sort((a, b) => activity(b) - activity(a))
      .map(buildNode);
    return {
      chat,
      children: kids,
      maxActivity: Math.max(activity(chat), ...kids.map(k => k.maxActivity)),
    };
  };
  const topNodes = topCandidates.map(buildNode);
  // Чаты, не достижимые из кандидатов (цикл ссылок) — разрываем, поднимая в корни
  for (const c of chats) {
    if (!visited.has(c.id)) topNodes.push(buildNode(c));
  }

  // Фильтр корней: скрытый корень исчезает, его дети — кандидаты в корни (рекурсивно)
  const roots: TreeNode[] = [];
  const promote = (node: TreeNode) => {
    if (opts.isRootVisible(node.chat)) roots.push(node);
    else node.children.forEach(promote);
  };
  topNodes.forEach(promote);

  // Закреплённые корни сверху (без группового заголовка), дальше — по активности поддерева
  roots.sort((a, b) => {
    const pin = Number(b.chat.isPinned ?? false) - Number(a.chat.isPinned ?? false);
    return pin !== 0 ? pin : b.maxActivity - a.maxActivity;
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
      isLast,
      hasChildren: node.children.length > 0,
      collapsed,
      childCount: node.children.length,
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
