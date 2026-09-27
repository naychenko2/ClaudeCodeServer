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
      });
    }
  }
  return out;
}

// Имя модели для шапки: единственная система — её имя, иначе null (подставят имя проекта)
export function modelTitle(elements: ArchElement[]): string | null {
  const systems = elements.filter(e => e.level === 'system');
  return systems.length === 1 ? systems[0].name : null;
}
