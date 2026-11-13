// Модель графа процесса фичи (C3a, разрез 2 docs/research/ai-fabrika-workflow-2-cut-2026-11.md
// §1.7, §1.9). ЧИСТАЯ: три функции над FeatureRun.stages, без состояния и запросов.
//
// Фронт НИЧЕГО не достраивает и не пересчитывает (решение разреза, карточка C3a):
// - колонки — строго по полю `column` с бэка (WorkflowGraph.Columns, WorkflowGraph.cs:109-120);
//   значение отсутствующее (старый ответ) — 0, как ставит бэк для легаси-стадии
//   (FeatureProjector.cs:804-814); значения не сжимаются и не перенумеровываются;
// - рёбра — строго по полю `after`; нессылающееся ребро (id нет в stages) не рисуется —
//   провести его не между чем;
// - состояния и тона — как пришли; компонент только красит.
// Легаси-фича (Column=0, After=[] у всех) даёт одну колонку с порядком run.stages — это и есть
// «графа нет» в данных; рисовать её цепочкой — решение страницы (C3b), не модели.

import type { FeatureRun, FeatureStage } from '../../types/featureRun';

// Id стадии для графа и выбора: поле `id` с бэка; отсутствующее (старый ответ) — ключ,
// ровно как делает бэк для легаси-стадии (FeatureRun.cs:279-281: «у легаси-стадии — её ключ»).
// Это фолбэк контракта, не вторая арифметика: для живых ответов id всегда есть.
export function stageNodeId(stage: FeatureStage): string {
  return stage.id ?? stage.key;
}

// Колонка графа: значение `column` с бэка и её стадии. Стадии внутри колонки — в порядке
// run.stages (контракт C3a «порядок»; бэк отдаёт список топологическим, WorkflowGraph.cs:9-10).
export interface GraphColumn {
  column: number;
  stages: FeatureStage[];
}

// Колонки по полю `column`, по возрастанию значения. Пусто/отсутствие stages — пустой список.
export function graphColumns(run: FeatureRun): GraphColumn[] {
  const byValue = new Map<number, FeatureStage[]>();
  for (const stage of run.stages ?? []) {
    const col = Number.isFinite(stage.column) ? (stage.column as number) : 0;
    const list = byValue.get(col);
    if (list) list.push(stage);
    else byValue.set(col, [stage]);
  }
  return [...byValue.entries()]
    .sort((a, b) => a[0] - b[0])
    .map(([column, stages]) => ({ column, stages }));
}

// Ребро графа [from → to]: каждая запись `after` стадии — ребро от предшественника к ней.
export interface GraphEdge {
  from: string;
  to: string;
}

// Рёбра по полю `after`, в порядке обхода run.stages. Число рёбер = сумма элементов всех
// `after` (на живых данных: бэк разрешает after только на id этапов маршрута, разрез §1.6).
// Ссылка на отсутствующую стадию ребра не даёт: рисовать его не между чем.
export function graphEdges(run: FeatureRun): GraphEdge[] {
  const stages = run.stages ?? [];
  const known = new Set(stages.map(stageNodeId));
  const edges: GraphEdge[] = [];
  for (const stage of stages) {
    const to = stageNodeId(stage);
    for (const from of stage.after ?? []) {
      if (known.has(from)) edges.push({ from, to });
    }
  }
  return edges;
}

// Изначально выбранная стадия (контракт C3a): первая active в порядке run.stages, иначе
// последняя done, иначе null — выбирать нечего (например, фича ещё не начиналась).
// «Первая/последняя» — по порядку run.stages, не по колонкам: порядок списка даёт бэк.
export function defaultSelected(run: FeatureRun): string | null {
  const stages = run.stages ?? [];
  for (const stage of stages) {
    if (stage.state === 'active') return stageNodeId(stage);
  }
  for (let i = stages.length - 1; i >= 0; i -= 1) {
    if (stages[i]!.state === 'done') return stageNodeId(stages[i]!);
  }
  return null;
}

// Подпись «⇉ параллельно» — одна на рамку десктопа, скобку мобилы и легенду (макет P23/P30).
export const PARALLEL_LABEL = '⇉ параллельно';

// Порядковый номер колонки (0..n-1) каждой стадии: значения `column` с бэка не меняются,
// это только ранг в списке колонок — чтобы отличить «соседнюю» колонку от «через колонку».
export function columnRanks(cols: GraphColumn[]): Map<string, number> {
  const ranks = new Map<string, number>();
  cols.forEach((col, rank) => {
    for (const s of col.stages) ranks.set(stageNodeId(s), rank);
  });
  return ranks;
}

// Дальнее ребро идёт через ≥1 колонку (макет P25): рисуется обходом под графом пунктиром.
// Колонки не пересчитываются — ранги берутся из graphColumns (поле `column` с бэка).
export function isFarEdge(ranks: Map<string, number>, edge: GraphEdge): boolean {
  return (ranks.get(edge.to) ?? 0) - (ranks.get(edge.from) ?? 0) > 1;
}

// Подпись справа от заголовка «Процесс» (макет P14): подтверждённый маршрут — «Имя vN»
// (имя шаблона, если известно, иначе его id), предложение — «предложение · ещё не
// подтверждено», остальное (легаси, state=none) — «фиксированный список».
export function graphCaption(run: FeatureRun, templateName?: string | null): string {
  const route = run.epic?.workflow;
  if (route) return `${templateName || route.templateId} v${route.confirmedVersion}`;
  const proposal = run.epic?.workflowProposal;
  if (proposal && proposal.state !== 'none' && proposal.templateId) return 'предложение · ещё не подтверждено';
  return 'фиксированный список';
}
