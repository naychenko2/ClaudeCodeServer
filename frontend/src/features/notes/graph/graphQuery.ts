// Поисковые запросы графа: единый синтаксис для фильтра и групповой раскраски.
//   tag:идея | tag:#идея — точный тег (регистр не важен)
//   source:personal      — ключ источника или подстрока его подписи
//   слово | "фраза"      — подстрока заголовка
// Термы объединяются через AND; префикс «-» — отрицание терма.
// Призрачные узлы (ghost) матчатся только по заголовку: tag:/source: для них ложны.

export interface QueryTerm {
  kind: 'tag' | 'source' | 'title';
  value: string;   // нормализовано: lowercase, тег без ведущего «#»
  negate: boolean;
}

// Узел в объёме, достаточном для матчинга (совместим с NoteGraphNode и SimNode)
export interface QueryableNode {
  title: string;
  source: string;
  sourceLabel: string;
  ghost: boolean;
  tags?: string[];
}

const TOKEN = /(-)?(?:(tag|source):)?(?:"([^"]*)"|([^\s"]+))/gi;

export function parseQuery(q: string): QueryTerm[] {
  const terms: QueryTerm[] = [];
  for (const m of q.matchAll(TOKEN)) {
    const kind = (m[2]?.toLowerCase() ?? 'title') as QueryTerm['kind'];
    let value = (m[3] ?? m[4] ?? '').trim().toLowerCase();
    if (kind === 'tag') value = value.replace(/^#/, '');
    if (!value) continue;
    terms.push({ kind, value, negate: m[1] === '-' });
  }
  return terms;
}

export function matchNode(node: QueryableNode, terms: QueryTerm[]): boolean {
  for (const t of terms) {
    let hit: boolean;
    switch (t.kind) {
      case 'tag':
        hit = !node.ghost && (node.tags ?? []).some(x => x.toLowerCase() === t.value);
        break;
      case 'source':
        hit = !node.ghost && (node.source.toLowerCase() === t.value || node.sourceLabel.toLowerCase().includes(t.value));
        break;
      default:
        hit = node.title.toLowerCase().includes(t.value);
    }
    if (t.negate ? hit : !hit) return false;
  }
  return true;
}
