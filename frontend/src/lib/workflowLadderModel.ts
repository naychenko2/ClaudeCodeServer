// Модель лестницы маршрута (feature-workflows, F01): чистые функции без UI — колонки по глубине,
// параллельные группы, дуги и пересчёт по разрешённому маршруту. Одна на блок подтверждения,
// граф страницы и редактор. Зеркало серверного WorkflowGraph.Resolve/Columns
// (backend/ClaudeHomeServer.Tasks/WorkflowGraph.cs): снятый этап обходится — потомки получают
// его предшественников. Порядок stages — топологический, как на бэке; валидации здесь нет.

export interface LadderStageInput {
  id: string;
  after: string[];
  presence: string;
  // Съёмна ли роль этапа (результат isRemovableStage по роли; несъёмные cut/step/final/merge — false).
  skippable: boolean;
}

export interface LadderColumn {
  index: number;
  stageIds: string[];
  // В колонке больше одного этапа — параллельная группа.
  parallel: boolean;
}

export interface LadderEdge {
  from: string;
  to: string;
  // Ребро идёт через ≥1 колонку — рисуется дугой.
  far: boolean;
}

export type LadderLock = 'role' | 'template';

export interface Ladder {
  columns: LadderColumn[];
  edges: LadderEdge[];
  // Реально снятые этапы (presence=off и снятые съёмные), в порядке маршрута.
  removed: string[];
  // Этапы, которые человек снять не может: роль несъёмная или шаблон не оставляет выбора.
  locked: Record<string, LadderLock>;
}

export function buildLadder(stages: LadderStageInput[], removedIds: Set<string>): Ladder {
  const locked: Record<string, LadderLock> = Object.create(null);
  const removed: string[] = [];
  // Для снятого этапа — его after, уже развёрнутый через ранее снятые.
  const inherited: Record<string, string[]> = Object.create(null);
  const kept: { id: string; after: string[] }[] = [];

  for (const stage of stages) {
    const after: string[] = [];
    for (const parent of stage.after) {
      for (const p of inherited[parent] ?? [parent]) if (!after.includes(p)) after.push(p);
    }

    const optional = stage.presence === 'optional';
    if (!stage.skippable) locked[stage.id] = 'role';
    else if (!optional && stage.presence !== 'off') locked[stage.id] = 'template';

    const drop = stage.presence === 'off' || (optional && stage.skippable && removedIds.has(stage.id));
    if (drop) {
      inherited[stage.id] = after;
      removed.push(stage.id);
    } else {
      kept.push({ id: stage.id, after });
    }
  }

  // Глубина = длиннейший путь от корня; предок вне оставшихся глубины не даёт.
  const depth: Record<string, number> = Object.create(null);
  // Предки каждого этапа (транзитивно) — для отсева транзитивных рёбер.
  const ancestors: Record<string, Set<string>> = Object.create(null);
  const edges: LadderEdge[] = [];
  for (const node of kept) {
    const known = node.after.filter(p => depth[p] !== undefined);
    depth[node.id] = known.reduce((col, p) => Math.max(col, depth[p] + 1), 0);
    const anc = new Set<string>();
    for (const p of known) {
      anc.add(p);
      for (const a of ancestors[p]) anc.add(a);
    }
    ancestors[node.id] = anc;
    // Ребро p→node лишнее, если другой родитель сам стоит после p.
    for (const p of known) {
      if (known.some(q => q !== p && ancestors[q].has(p))) continue;
      edges.push({ from: p, to: node.id, far: depth[node.id] - depth[p] > 1 });
    }
  }

  const columns: LadderColumn[] = [];
  for (const node of kept) {
    const col = (columns[depth[node.id]] ??= { index: depth[node.id], stageIds: [], parallel: false });
    col.stageIds.push(node.id);
    col.parallel = col.stageIds.length > 1;
  }
  return { columns: columns.filter(Boolean), edges, removed, locked };
}
