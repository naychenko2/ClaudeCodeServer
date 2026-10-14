// Иерархия списка чатов: сборка леса по Session.parentSessionId и
// персистентность свёрнутых веток. Раздельно по областям, как chatFilters:
// 'global' и каждый projectId. Спецификация — docs/design/mockups/chat-list-tree-spec.md.
import { useEffect, useRef, useState } from 'react';
import type { Session } from '../types';
import { chatStatusOf, sortKeyOf, type ChatSortOrder } from './chatFilters';
import { hasUnread } from './chatReadState';

const COLLAPSE_KEY_PREFIX = 'cc_chat_tree_collapsed:';
// Ветки, которые пользователь ЯВНО раскрыл: нужны только длинным веткам, свёрнутым
// по умолчанию (AUTO_COLLAPSE_CHILDREN), — иначе раскрытие не пережило бы перезагрузку
const EXPAND_KEY_PREFIX = 'cc_chat_tree_expanded:';

// Ветка, у которой видимых прямых детей больше этого числа, свёрнута по умолчанию —
// пока пользователь не раскрыл её сам и пока через неё не идёт путь к открытому чату
export const AUTO_COLLAPSE_CHILDREN = 5;

// Полный список id чатов области — с меткой, ДЛЯ КАКОЙ области он загружен. Без метки
// при смене проекта хук получал бы в первом же рендере список старого проекта под
// ключом нового и стирал бы из памяти новой области все её ветки
export interface KnownChatIds {
  scope: string;
  ids: readonly string[];
}

// === Память свёрнутых веток: явно свёрнутые и явно раскрытые id чатов ===
// known — полный список id чатов области: когда он приехал, из обоих наборов
// вычищаются удалённые чаты, иначе localStorage копил бы их бесконечно
export function useTreeCollapse(scopeKey: string, known?: KnownChatIds) {
  const [state, setState] = useState(() => ({ scope: scopeKey, ...loadState(scopeKey) }));
  // Смена области — перечитываем её память прямо в рендере (состояние от пропа), а не
  // эффектом: иначе один кадр показывал бы свёрнутость чужой области
  let cur = state;
  if (state.scope !== scopeKey) {
    cur = { scope: scopeKey, ...loadState(scopeKey) };
    setState(cur);
  }

  // Список чужой области (ещё не перезагрузился после смены) — как будто его нет.
  // Пустой список — ещё не загрузился (или чатов нет вовсе): чистить по нему тоже нельзя
  const knownKey = known && known.scope === scopeKey ? known.ids.join(',') : '';
  // Последний известный состав области — им же чистится и запись при переключении ветки
  const knownRef = useRef<Set<string> | null>(null);
  useEffect(() => {
    knownRef.current = knownKey ? new Set(knownKey.split(',')) : null;
    if (!knownRef.current) return;
    // Чистится только хранилище: удалённые чаты в дереве не рисуются, и держать их
    // в состоянии рендера безвредно — следующая запись уйдёт уже без них
    const stored = loadState(scopeKey);
    const collapsed = keepKnown(stored.collapsed, knownRef.current);
    const expanded = keepKnown(stored.expanded, knownRef.current);
    if (collapsed.size === stored.collapsed.size && expanded.size === stored.expanded.size) return;
    saveState(scopeKey, collapsed, expanded);
  }, [scopeKey, knownKey]);

  // collapsedNow — что ветка показывает СЕЙЧАС (с учётом свёрнутости по умолчанию):
  // по одному id не понять, явное это состояние или автоматическое
  const toggleCollapse = (id: string, collapsedNow: boolean) => {
    const collapsed = keepKnown(cur.collapsed, knownRef.current);
    const expanded = keepKnown(cur.expanded, knownRef.current);
    if (collapsedNow) { collapsed.delete(id); expanded.add(id); }
    else { expanded.delete(id); collapsed.add(id); }
    saveState(scopeKey, collapsed, expanded);
    setState({ scope: scopeKey, collapsed, expanded });
  };

  return { collapsedIds: cur.collapsed, expandedIds: cur.expanded, toggleCollapse };
}

// Копия набора без удалённых чатов; состав области неизвестен — копия как есть
const keepKnown = (s: Set<string>, known: Set<string> | null) =>
  new Set(known ? [...s].filter(id => known.has(id)) : s);

function loadState(scopeKey: string) {
  return {
    collapsed: loadIdSet(COLLAPSE_KEY_PREFIX + scopeKey),
    expanded: loadIdSet(EXPAND_KEY_PREFIX + scopeKey),
  };
}

function saveState(scopeKey: string, collapsed: Set<string>, expanded: Set<string>) {
  try {
    localStorage.setItem(COLLAPSE_KEY_PREFIX + scopeKey, JSON.stringify([...collapsed]));
    localStorage.setItem(EXPAND_KEY_PREFIX + scopeKey, JSON.stringify([...expanded]));
  } catch { /* квота */ }
}

function loadIdSet(key: string): Set<string> {
  try {
    const raw = localStorage.getItem(key);
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
  // Сигналы свёрнутой ветки по тому же поддереву: ждут ответа (waiting — входят и в
  // groupRunningCount), с ошибкой (error/orphaned) и непрочитанные (стор chatReadState)
  groupWaitingCount: number;
  groupErrorCount: number;
  groupUnreadCount: number;
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
  // Сигналы поддерева для пилюли свёрнутой ветки — см. TreeNode
  groupWaitingCount: number;
  groupErrorCount: number;
  groupUnreadCount: number;
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
  // Всего чатов в отрисованном лесу (без учёта collapse) — для счётчика «скрыто фильтрами»
  renderedCount: number;
}

const activity = (c: Session) => new Date(c.updatedAt).getTime();

// Чат «в работе» прямо сейчас: агент думает/выполняет (starting, working) либо ждёт ответа
// пользователя на разрешение или вопрос (waiting). Набор совпадает с «дышащими» статусами
// из STATUS_GLOW (breath: true — по ним переливается ореол карточки в StatusIndicator) —
// при правке держать синхронно. Статус active сюда НЕ входит: ход уже завершён, процесс
// просто жив, и ореола у него нет вовсе — ровно как «не работа».
const RUNNING_STATUSES = new Set<Session['status']>(['starting', 'working', 'waiting']);

// bgWorkIds — чаты с живой ФОНОВОЙ работой любого вида (стор agentsPresence: агенты либо
// команда в фоне — дев-сервер, watch): статус у них уже Active, но работа идёт, и в счётчике
// свёрнутой ветки они обязаны считаться живыми — иначе бейдж разъедется с переливом самой
// карточки, который у обоих видов фона одинаковый
export const isChatRunning = (c: Session, bgWorkIds?: ReadonlySet<string>) =>
  RUNNING_STATUSES.has(c.status) || bgWorkIds?.has(c.id) === true;

// Потолок числа в бейдже свёрнутой ветки: бейдж вылезает из своей gutter-колонки поверх
// карточки, и «128/12» накрыл бы точку статуса вместе с началом названия чата
export const formatGroupCount = (n: number) => (n > 99 ? '99+' : String(n));

// Главный сигнал свёрнутой ветки — им красится пилюля со счётчиком. Приоритет:
// ошибка → ждёт ответа → работает → непрочитано. «Работает» — живые чаты БЕЗ ждущих:
// waiting входит в groupRunningCount, но у него свой, более срочный сигнал
export type CollapsedSignal = 'error' | 'waiting' | 'running' | 'unread' | 'none';

type SignalCounts = Pick<ChatTreeRowData,
  'groupCount' | 'groupRunningCount' | 'groupWaitingCount' | 'groupErrorCount' | 'groupUnreadCount'>;

export function collapsedSignal(r: SignalCounts): CollapsedSignal {
  if (r.groupErrorCount > 0) return 'error';
  if (r.groupWaitingCount > 0) return 'waiting';
  if (r.groupRunningCount - r.groupWaitingCount > 0) return 'running';
  if (r.groupUnreadCount > 0) return 'unread';
  return 'none';
}

// Подсказка свёрнутой ветки: цвет пилюли несёт один сигнал, а здесь — все сразу
export function collapsedBranchHint(r: SignalCounts): string {
  const parts = [`вложенных чатов ${r.groupCount}`];
  if (r.groupErrorCount > 0) parts.push(`с ошибкой ${r.groupErrorCount}`);
  if (r.groupWaitingCount > 0) parts.push(`ждут ответа ${r.groupWaitingCount}`);
  const working = r.groupRunningCount - r.groupWaitingCount;
  if (working > 0) parts.push(`в работе ${working}`);
  if (r.groupUnreadCount > 0) parts.push(`непрочитанных ${r.groupUnreadCount}`);
  return `Развернуть: ${parts.join(', ')}`;
}

/**
 * Исходное множество ПЛЮС все предки его участников — наследование признака вверх по
 * ветке (значок «правки не зафиксированы»: родитель отвечает за работу потомков).
 * Обход по той же связи parentSessionId, по которой строится дерево, иначе значок
 * разошёлся бы с нарисованной иерархией.
 *
 * Считать нужно по ПОЛНОМУ списку чатов проекта, а не по отфильтрованному: предок,
 * скрытый фильтром, всё равно остаётся звеном цепочки к видимому прародителю.
 * seen обрывает циклы в данных (их же сторожит buildChatTree) и заодно не даёт
 * переобходить общие участки веток.
 */
export function withAncestors(chats: Session[], ids: ReadonlySet<string>): Set<string> {
  if (ids.size === 0) return new Set();
  const parentOf = new Map<string, string>();
  for (const c of chats) {
    const pid = c.parentSessionId;
    if (pid && pid !== c.id) parentOf.set(c.id, pid);
  }
  const out = new Set<string>();
  for (const id of ids) {
    let cur: string | undefined = id;
    while (cur && !out.has(cur)) {
      out.add(cur);
      cur = parentOf.get(cur);
    }
  }
  return out;
}

/**
 * Корень ветки чата — по той же связи parentSessionId, что у дерева: подъём к предку,
 * которого нет в списке или у которого нет родителя. Нужен секционерам плоского списка,
 * которым секцию задаёт корень (режим «Темы»: тема — у корня, вложенные чаты её не
 * несут). Список — ПОЛНЫЙ: предок, скрытый фильтром, остаётся звеном к корню. Цикл в
 * данных обрывается на месте — чат считается корнем сам себе.
 */
export function rootResolver(chats: Session[]): (s: Session) => Session {
  const byId = new Map(chats.map(c => [c.id, c]));
  return s => {
    const seen = new Set<string>([s.id]);
    let cur = s;
    for (;;) {
      const pid = cur.parentSessionId;
      const parent = pid && pid !== cur.id ? byId.get(pid) : undefined;
      if (!parent) return cur;
      if (seen.has(parent.id)) return s;
      seen.add(parent.id);
      cur = parent;
    }
  };
}

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
    // Явно свёрнутые пользователем ветки
    collapsedIds: Set<string>;
    // Явно раскрытые: снимают свёрнутость длинной ветки по умолчанию
    expandedIds?: Set<string>;
    activeId: string | null;
    // Направление сортировки детей и корней (дефолт — свежие сверху)
    sortOrder?: ChatSortOrder;
    // Чаты с живой фоновой работой (стор agentsPresence: агенты или команда в фоне) —
    // в счётчике свёрнутой ветки они живые, хотя статус сессии у них уже Active
    bgWorkIds?: ReadonlySet<string>;
  },
): ChatTreeResult {
  const { field, dir } = sortKeyOf(opts.sortOrder ?? 'newest');
  // Ключ сортировки узла. По активности корни идут по maxActivity поддерева (корень
  // с живым ребёнком не тонет в старых днях), а у сортировки ПО СОЗДАНИЮ понятия
  // «создание поддерева» нет вовсе — там ключ строго собственный createdAt узла,
  // иначе свежий потомок тянул бы старый корень наверх.
  const byCreated = field === 'createdAt';
  const chatTs = (c: Session) => new Date(c[field]).getTime();
  const nodeKey = (n: TreeNode) => (byCreated ? chatTs(n.chat) : n.maxActivity);
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

  // Узел с метриками поддерева — одна формула и для сырого леса, и для отфильтрованного.
  // Счётчики суммируются по детям: сам узел в свои счётчики не входит
  const sum = (kids: TreeNode[], own: (c: Session) => boolean, sub: (k: TreeNode) => number) =>
    kids.reduce((n, k) => n + (own(k.chat) ? 1 : 0) + sub(k), 0);
  const makeNode = (chat: Session, kids: TreeNode[]): TreeNode => ({
    chat,
    children: kids,
    maxActivity: Math.max(activity(chat), ...kids.map(k => k.maxActivity)),
    groupCount: sum(kids, () => true, k => k.groupCount),
    groupRunningCount: sum(kids, c => isChatRunning(c, opts.bgWorkIds), k => k.groupRunningCount),
    // Ждущие и ошибки — тем же chatStatusOf, что у чипов фильтра: иначе пилюля
    // считала бы «ждёт ответа» чат, который фильтр относит к выполненным
    groupWaitingCount: sum(kids, c => chatStatusOf(c) === 'waiting', k => k.groupWaitingCount),
    groupErrorCount: sum(kids, c => chatStatusOf(c) === 'error', k => k.groupErrorCount),
    groupUnreadCount: sum(kids, c => hasUnread(c.updatedAt, c.id, c.lastReadAt), k => k.groupUnreadCount),
  });

  // Сборка узлов DFS от кандидатов в корни; visited защищает от циклов parentSessionId
  const visited = new Set<string>();
  const buildNode = (chat: Session): TreeNode => {
    visited.add(chat.id);
    const kids = (childrenOf.get(chat.id) ?? [])
      .filter(k => !visited.has(k.id))
      .sort((a, b) => dir * (chatTs(a) - chatTs(b)))
      .map(buildNode);
    return makeNode(chat, kids);
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
        sink.push(makeNode(node.chat, kids));
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

  // Закреплённые корни сверху (без группового заголовка), дальше — по ключу узла
  // (активность поддерева либо собственное создание) в направлении sortOrder
  roots.sort((a, b) => {
    const pin = Number(b.chat.isPinned ?? false) - Number(a.chat.isPinned ?? false);
    return pin !== 0 ? pin : dir * (nodeKey(a) - nodeKey(b));
  });

  let renderedCount = 0;
  const countNode = (n: TreeNode) => {
    renderedCount++;
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

  // Флаттен. В отличие от прежнего поведения, свёрнутое поддерево НЕ вырезается
  // из массива: дети свёрнутого узла остаются в rows, а их видимость решает
  // рендер — контейнер-аниматор grid 0fr↔1fr вокруг детей узла. Без этого React
  // размонтировал бы детей мгновенно, и двусторонняя анимация схлопывания высоты
  // была бы невозможна. ancestors строки — сквозные вертикали осей 0..depth-2
  // (ось своей seg-линии depth-1 в массив не входит); passBelow — продолжение
  // родительской оси через ПОДдерево узла (у узла есть следующие сиблинги) —
  // становится записью ancestors у его детей.
  const rows: ChatTreeRowData[] = [];
  const emit = (
    node: TreeNode, depth: number, isLast: boolean, segAccent: boolean,
    passBelow: { show: boolean; accent: boolean },
    ancestors: { show: boolean; accent: boolean }[],
  ) => {
    const id = node.chat.id;
    // Явное сворачивание пользователя — всегда; длинная ветка — по умолчанию, пока её
    // явно не раскрыли и пока через неё не идёт путь к открытому чату (иначе открытый
    // чат спрятался бы под свёрнутым предком)
    const collapsed = node.children.length > 0 && (
      opts.collapsedIds.has(id)
      || (node.children.length > AUTO_COLLAPSE_CHILDREN
        && !opts.expandedIds?.has(id)
        && !activeAncestors.has(id)));
    rows.push({
      chat: node.chat,
      depth,
      maxActivity: node.maxActivity,
      isLast,
      hasChildren: node.children.length > 0,
      collapsed,
      groupCount: node.groupCount,
      groupRunningCount: node.groupRunningCount,
      groupWaitingCount: node.groupWaitingCount,
      groupErrorCount: node.groupErrorCount,
      groupUnreadCount: node.groupUnreadCount,
      onActivePath: onPath(node.chat.id),
      segAccent,
      elbowAccent: onPath(node.chat.id),
      stubAccent: activeAncestors.has(node.chat.id),
      ancestors,
    });
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

  return { rows, renderedCount };
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

/**
 * Что подать секционеру (groupChats/groupByTags работают по Session) за корень сегмента.
 * При сортировке ПО АКТИВНОСТИ корень попадает в дневную секцию по maxActivity поддерева
 * (корень с живым ребёнком не тонет в старых днях) — отдаём синтетическую сессию с
 * подменённой датой. При сортировке ПО СОЗДАНИЮ подмена увела бы корень в чужой день:
 * понятия «создание поддерева» нет, и там едет настоящая сессия корня с её createdAt.
 * Точка ОДНА на оба списка (ChatList и SessionList) — разъехавшиеся копии дали бы
 * глобальному и проектному спискам разные оси секционирования.
 */
export function rootChatForSection(seg: ChatTreeRowData[], sortOrder: ChatSortOrder): Session {
  const root = seg[0];
  if (sortKeyOf(sortOrder).field === 'createdAt') return root.chat;
  return { ...root.chat, updatedAt: new Date(root.maxActivity).toISOString() } as Session;
}
