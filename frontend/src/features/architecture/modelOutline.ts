// Разбор файла модели Viaduct (persist-обёртка zustand: { state: { model: FlatC4Model } })
// в плоский список элементов для навигатора панели и мобильного списка-обзора.
// Модель чужая — читаем защитно: всё, чего нет или что не того типа, просто пропускаем.

export type ArchLevel = 'system' | 'container' | 'component' | 'code';

export interface ArchElement {
  id: string;
  name: string;
  level: ArchLevel;
  parentId: string | null;
  technology?: string;
  description?: string;
  connections: number;
  // Теги элемента (генератор ставит «кандидат» и «нет в коде»); нет тегов — undefined
  tags?: string[];
  // Внешний элемент (вне границ системы) — навигатор уносит такие в свёрнутую группу
  external?: boolean;
}

export const LEVEL_LABEL: Record<ArchLevel, string> = {
  system: 'Системы',
  container: 'Контейнеры',
  component: 'Компоненты',
  code: 'Код',
};

const LEVELS: { level: ArchLevel; key: string; parent: string | null }[] = [
  { level: 'system', key: 'systems', parent: null },
  { level: 'container', key: 'containers', parent: 'systemId' },
  { level: 'component', key: 'components', parent: 'containerId' },
  { level: 'code', key: 'codeElements', parent: 'componentId' },
];

function str(v: unknown): string | undefined {
  return typeof v === 'string' && v.trim() ? v : undefined;
}

function tagsOf(v: unknown): string[] | undefined {
  if (!Array.isArray(v)) return undefined;
  const tags = v.filter((t): t is string => typeof t === 'string' && t.trim() !== '');
  return tags.length ? tags : undefined;
}

export function parseOutline(content: string | null): ArchElement[] {
  if (!content) return [];
  let doc: unknown;
  try { doc = JSON.parse(content); } catch { return []; }
  const model = (doc as { state?: { model?: Record<string, unknown> } })?.state?.model;
  if (!model || typeof model !== 'object') return [];
  const out: ArchElement[] = [];
  for (const { level, key, parent } of LEVELS) {
    const list = model[key];
    if (!Array.isArray(list)) continue;
    for (const raw of list) {
      if (!raw || typeof raw !== 'object') continue;
      const el = raw as Record<string, unknown>;
      const id = str(el.id);
      if (!id) continue;
      out.push({
        id,
        name: str(el.name) ?? id,
        level,
        parentId: parent ? str(el[parent]) ?? null : null,
        technology: str(el.technology),
        description: str(el.description),
        connections: Array.isArray(el.connections) ? el.connections.length : 0,
        tags: tagsOf(el.tags),
        external: el.external === true || undefined,
      });
    }
  }
  return out;
}

export interface OutlineNode {
  el: ArchElement;
  children: OutlineNode[];
}

// Дерево «система → контейнеры → компоненты» для навигатора панели. Порядок — как в файле.
// Элемент с висящим parentId становится корнем: из списка он не пропадает.
// Схлопывание: единственный ребёнок-лист, повторяющий родителя по имени или описанию,
// скрывается — генератор кладёт в контейнер-сборку компонент-двойник, и в списке это
// шум (элемент на холсте остаётся, найти его можно поиском).
export function buildOutlineTree(elements: ArchElement[]): OutlineNode[] {
  const ids = new Set(elements.map(e => e.id));
  const byParent = new Map<string | null, ArchElement[]>();
  for (const e of elements) {
    const p = e.parentId && e.parentId !== e.id && ids.has(e.parentId) ? e.parentId : null;
    const list = byParent.get(p);
    if (list) list.push(e); else byParent.set(p, [e]);
  }
  // visited — защита от цикла в кривом файле (id разных уровней совпали)
  const visited = new Set<string>();
  const build = (el: ArchElement): OutlineNode => {
    visited.add(el.id);
    const kids = (byParent.get(el.id) ?? []).filter(k => !visited.has(k.id)).map(build);
    const only = kids.length === 1 ? kids[0] : null;
    const twin = only !== null && only.children.length === 0
      && (only.el.name === el.name || (!!only.el.description && only.el.description === el.description));
    return { el, children: twin ? [] : kids };
  };
  return (byParent.get(null) ?? []).map(build);
}

// Путь элемента для выдачи поиска: имена предков через « › », без самого элемента
export function outlinePath(elements: ArchElement[], id: string): string {
  const byId = new Map(elements.map(e => [e.id, e]));
  const names: string[] = [];
  const seen = new Set<string>([id]);
  let p = byId.get(id)?.parentId ?? null;
  while (p && !seen.has(p)) {
    const parent = byId.get(p);
    if (!parent) break;
    seen.add(p);
    names.unshift(parent.name);
    p = parent.parentId;
  }
  return names.join(' › ');
}

// Имя модели для шапки: единственная система — её имя, иначе null (подставят имя проекта)
export function modelTitle(elements: ArchElement[]): string | null {
  const systems = elements.filter(e => e.level === 'system');
  return systems.length === 1 ? systems[0].name : null;
}
