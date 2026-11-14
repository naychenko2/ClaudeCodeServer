// Модель графа маршрута «после» (feature-workflows, G6a, разрез 2
// docs/research/ai-fabrika-workflow-2-cut-2026-11.md §1.1–1.2): чистые функции по списку
// этапов — ни DOM, ни запросов, ни состояния. Зеркало серверного WorkflowGraph
// (backend/ClaudeHomeServer.Tasks/WorkflowGraph.cs), но ТОЛЬКО для показа редактора:
// истина валидации остаётся за WorkflowRules (несогласие — 400 со своим списком), модель
// не повторяет правила висящих веток, одиночных ролей и линий — превью рисует то, что дано.
// Порядок stages = порядок маршрута (топологический): функции список не переставляют.
// Ключ этапа — id (после normalizeStages), не индекс и не роль.

import type { WorkflowStage } from './types';

// Trim как серверный Normalize: пустое или из одних пробелов — не значение (null);
// сравнение id — точное, как Ordinal-словари на бэке.
function trimOrNull(s: string | null | undefined): string | null {
  const t = (s ?? '').trim();
  return t === '' ? null : t;
}

// Этапы для показа: id ??= роль, after ??= [предыдущий в списке] (у первого — []), trim
// значений (зеркало WorkflowGraph.Normalize). Возвращает КОПИЮ — состояние редактора
// от чтения не меняется. Идемпотентна: повторное применение ничего не меняет, поэтому
// остальные функции этой модели вызывают её сами и работают и на сырых, и на нормализованных этапах.
export function normalizeStages(stages: WorkflowStage[]): WorkflowStage[] {
  const result: WorkflowStage[] = [];
  stages.forEach((stage, i) => {
    const role = (stage?.role ?? '').trim();
    const id = trimOrNull(stage?.id) ?? role;
    const lane = trimOrNull(stage?.lane);
    const after = stage?.after != null
      ? stage.after.map(a => (a ?? '').trim())
      : i === 0 ? [] : [result[i - 1].id ?? ''];
    result.push({ ...stage, id, lane, after });
  });
  return result;
}

// Кандидаты в поле «after» этапа index: id этапов, стоящих СТРОГО раньше в списке
// (разрез 2 §1.6: ссылка на себя или вперёд — ошибка сервера). Уже выбранные after
// не исключаются: поле показывает весь белый список позиций, а не только дописанное.
export function afterOptions(stages: WorkflowStage[], index: number): string[] {
  const upto = Math.max(0, Math.min(index, stages.length));
  return normalizeStages(stages).slice(0, upto).map(s => s.id ?? '');
}

// Переименование id этапа: ссылки правятся каскадом — везде, где ссылались на старый id,
// теперь новый. after при этом материализуется normalizeStages, поэтому каскад доходит
// и до неявных «предыдущий в списке». Пустой или не изменившийся newId, неизвестный oldId —
// список возвращается как есть: slug — дело серверного валидатора, а пустое имя
// просто сломало бы ссылки. Дубли newId здесь не проверяются — их скажет сервер (400).
export function renameStageId(stages: WorkflowStage[], oldId: string, newId: string): WorkflowStage[] {
  const to = trimOrNull(newId);
  if (!to || to === oldId) return stages;
  const norm = normalizeStages(stages);
  if (!norm.some(s => s.id === oldId)) return stages;
  return norm.map(s => ({
    ...s,
    id: s.id === oldId ? to : s.id,
    after: (s.after ?? []).map(a => (a === oldId ? to : a)),
  }));
}

// Удаление этапа: его самого нет, его id убран из after остальных. Рёбра здесь НЕ обходят
// удалённый (транзитивный обход — дело серверного Resolve в подтверждённом маршруте):
// граф редактора остаётся как есть, а что с ним не так — скажет сервер при сохранении.
// Неизвестного id нет — список возвращается как есть.
export function removeStage(stages: WorkflowStage[], id: string): WorkflowStage[] {
  const norm = normalizeStages(stages);
  if (!norm.some(s => s.id === id)) return stages;
  return norm.filter(s => s.id !== id).map(s => ({ ...s, after: (s.after ?? []).filter(a => a !== id) }));
}

// Удаление этапа действием человека: наследники удалённого подцепляются к его родителям
// (как серверный Resolve для снятых этапов, но уже в самом шаблоне), чтобы после удаления
// ветка не оторвалась от маршрута молча. У удалённого корня наследники сами становятся корнями.
// Неизвестного id нет — список возвращается как есть.
export function removeStageReconnect(stages: WorkflowStage[], id: string): WorkflowStage[] {
  const norm = normalizeStages(stages);
  const removed = norm.find(s => s.id === id);
  if (!removed) return stages;
  const parents = (removed.after ?? []).filter(a => a !== id);
  return norm.filter(s => s.id !== id).map(s => {
    const after = s.after ?? [];
    if (!after.includes(id)) return s;
    const next: string[] = [];
    for (const a of after) for (const b of a === id ? parents : [a]) if (!next.includes(b)) next.push(b);
    return { ...s, after: next };
  });
}

// Свободный id по роли: «qa», затем «qa-2», «qa-3»… — slug остаётся [a-z0-9-].
function freeId(taken: Set<string>, role: string): string {
  const base = trimOrNull(role) ?? 'stage';
  if (!taken.has(base)) return base;
  for (let n = 2; ; n++) if (!taken.has(`${base}-${n}`)) return `${base}-${n}`;
}

// Добавление этапа (в том числе возврат удалённого): встаёт в конец маршрута — так порядок
// остаётся топологическим при любых родителях. after — id существующих этапов; неизвестные
// отбрасываются (висячую ссылку редактор сам не создаёт), повторы схлопываются.
// Правила одиночных ролей здесь не проверяются — их скажет stageErrors/сервер.
export function addStage(stages: WorkflowStage[], opts: { role: string; after: string[] }): WorkflowStage[] {
  const norm = normalizeStages(stages);
  const ids = new Set(norm.map(s => s.id ?? ''));
  const after = Array.from(new Set(opts.after.map(a => a.trim()).filter(a => ids.has(a))));
  const role = opts.role.trim();
  return [...norm, { role, presence: 'on', id: freeId(ids, role), after }];
}

// Перестановка этапа с пересвязкой «после»: этап вынимается как при removeStageReconnect
// (наследники подцепляются к его родителям), затем вставляется на позицию `to` (индекс в
// списке без него) в цепочку — после предыдущего по списку; тот, кто стоял на этом месте и
// ссылался на этого предыдущего, теперь ссылается на перемещённый. Так маршрут не рвётся и
// остаётся топологическим. Тот же индекс, вне списка — список как есть; `to` клампится.
export function moveStage(stages: WorkflowStage[], from: number, to: number): WorkflowStage[] {
  if (!Number.isInteger(from) || !Number.isInteger(to) || from < 0 || from >= stages.length) return stages;
  const norm = normalizeStages(stages);
  const moved = norm[from];
  const movedId = moved.id ?? '';
  const rest = removeStageReconnect(norm, movedId);
  const at = Math.max(0, Math.min(to, rest.length));
  if (at === from) return stages;
  const prevId = at > 0 ? rest[at - 1].id ?? '' : null;
  const placed: WorkflowStage = { ...moved, after: prevId === null ? [] : [prevId] };
  const next = rest.map((s, i) => {
    if (i !== at) return s;
    const after = s.after ?? [];
    if (prevId === null) return after.length === 0 ? { ...s, after: [movedId] } : s;
    return after.includes(prevId) ? { ...s, after: after.map(a => (a === prevId ? movedId : a)) } : s;
  });
  next.splice(at, 0, placed);
  return next;
}

// Колонка этапа для мини-превью (G6b): глубина = длиннейший путь от корня (корень — 0),
// зеркало WorkflowGraph.Columns. Ребро на неизвестный id колонки не даёт (на бэке —
// то же: нет глубины предка — нет и колонки от него). Валидации здесь нет: битый граф
// колонку тоже получит — её нарисует превью, а что с ним не так (висящая ссылка, дубль id),
// скажет сервер. Несколько корней — не ошибка: граф без единого стока тоже валиден на входе.
export function previewColumns(stages: WorkflowStage[]): Record<string, number> {
  // Объект без прототипа — как Ordinal-словарь на бэке: id «constructor» не должен
  // доставать глубину из Object.prototype.
  const depth: Record<string, number> = Object.create(null);
  for (const stage of normalizeStages(stages)) {
    let column = 0;
    for (const parent of stage.after ?? []) {
      const d = depth[parent];
      if (d !== undefined) column = Math.max(column, d + 1);
    }
    depth[stage.id ?? ''] = column;
  }
  return depth;
}
