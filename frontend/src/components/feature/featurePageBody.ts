// Чистая логика тела страницы фичи (волна 2 «Обзор фич», шаг Ф2.5).
// Фронт НЕ пересчитывает — только раскладывает. «Быстрый трек» (страница = конвейер)
// рантайм использует только счётчики задач; остальное (чаты/шаги/лента) — замороженные
// блоки, их чистые функции оставлены для тестов. Все функции чистые: тестируются в node.

import type { FeatureIndexFeature } from '../../types/featureIndex';
import type { FeatureRun } from '../../types/featureRun';

// === Задачи ===

// Счётчики задач: готово / в работе / впереди.
export interface TaskCounters {
  done: number;
  inProgress: number;
  pending: number;
  total: number;
}

// Счётчики из run: epic + steps + reviews + qa + final + merge + cut + plan.
// Статус: done / inProgress / todo. Контракт: UI обязан пережить отсутствие любого
// поля ответа — списки, которых бэк не отдал, раскладываются как пустые.
export function taskCounters(run: FeatureRun): TaskCounters {
  const all = [
    // N-epic: одиночный epic — той же защитой, что и списки (контракт: UI обязан
    // пережить отсутствие любого поля; без epic → [], а не undefined в массиве).
    ...(run.epic ? [run.epic] : []),
    ...(run.steps ?? []),
    ...(run.reviews ?? []),
    ...(run.qa ?? []),
    ...(run.final ? [run.final] : []),
    ...(run.merge ? [run.merge] : []),
    ...(run.cut ?? []),
    ...(run.plan ?? []),
  ];
  let done = 0, inProgress = 0, pending = 0;
  for (const t of all) {
    if (t.status === 'done') done++;
    else if (t.status === 'inProgress') inProgress++;
    else pending++;
  }
  return { done, inProgress, pending, total: all.length };
}

// Число задач фичи — ОДИН фолбэк (индекс → иначе сумма по шагам): taskCount из индекса
// имеет приоритет; фичи нет в индексе (null) — фолбэк на taskCounters(run).total.
export function featureTaskTotal(feature: FeatureIndexFeature | null, run: FeatureRun): number {
  if (feature?.taskCount != null) return feature.taskCount;
  return taskCounters(run).total;
}
