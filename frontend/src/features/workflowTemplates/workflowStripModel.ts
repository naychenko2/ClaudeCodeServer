// Модель ленты этапов редактора (F20, вариант A): чистые функции без DOM и React —
// номера колонок, имена «после», строка «кто», тег присутствия, группы «параллельно».
// Граф и перестановку считает workflowGraphModel (normalizeStages/previewColumns/moveStage).

import { normalizeStages, previewColumns, removeStageReconnect } from './workflowGraphModel';
import type { WorkflowStage } from './types';

export interface StripPerson { id: string; name: string; role?: string | null }

// Подпись этапа в ленте: имя из шаблона, иначе подпись роли (линия у шагов — отдельным тегом).
export function stageDisplayName(stage: WorkflowStage, roleLabel: Record<string, string>): string {
  return (stage.title ?? '').trim() || roleLabel[stage.role] || stage.role;
}

// Номер колонки каждого этапа списка (с 1) или null у выключенного: выключенные из маршрута
// вынимаются с пересвязкой, поэтому номера остальных не «дырявые».
export function stripColumns(stages: WorkflowStage[]): (number | null)[] {
  const norm = normalizeStages(stages);
  let live = norm;
  for (const s of norm) if (s.presence === 'off') live = removeStageReconnect(live, s.id ?? '');
  const cols = previewColumns(live);
  return norm.map(s => (s.presence === 'off' ? null : (cols[s.id ?? ''] ?? 0) + 1));
}

// Имена этапов, которых ждёт этот (после normalizeStages). Неизвестный id показывается как есть.
export function afterNames(stages: WorkflowStage[], index: number, roleLabel: Record<string, string>): string[] {
  const norm = normalizeStages(stages);
  const byId = new Map(norm.map(s => [s.id ?? '', s]));
  return (norm[index]?.after ?? []).map(id => {
    const s = byId.get(id);
    return s ? nameWithLane(s, roleLabel) : id;
  });
}

const LANE_LABEL: Record<string, string> = { back: 'бэк', front: 'фронт', other: 'другое' };

// «Шаги · бэк»: этап одной роли на разных линиях различим по линии.
export function nameWithLane(stage: WorkflowStage, roleLabel: Record<string, string>): string {
  const name = stageDisplayName(stage, roleLabel);
  const lane = stage.lane ? (LANE_LABEL[stage.lane] ?? stage.lane) : null;
  return lane && !(stage.title ?? '').trim() ? `${name} · ${lane}` : name;
}

// Строка «кто»: персона → «Роль · Имя»; специальность → её подпись; шаг без выбора —
// «исполнитель из плана»; у прочих ролей без выбора — null (строка не рисуется).
export function whoLabel(
  stage: WorkflowStage,
  persons: StripPerson[],
  specialtyLabels: Record<string, string>,
): string | null {
  if (stage.personaId) {
    const p = persons.find(x => x.id === stage.personaId);
    if (p) return p.role ? `${p.role} · ${p.name}` : p.name;
  }
  if (stage.specialty && stage.specialty !== 'none') return specialtyLabels[stage.specialty] ?? stage.specialty;
  return stage.role === 'step' ? 'исполнитель из плана' : null;
}

// Тег присутствия: всегда / по решению / нет.
export function presenceTag(presence: string): { text: string; tone: 'success' | 'warning' | 'neutral' } {
  if (presence === 'optional') return { text: 'по решению', tone: 'warning' };
  if (presence === 'off') return { text: 'нет', tone: 'neutral' };
  return { text: 'всегда', tone: 'success' };
}

export interface StripGroup { column: number | null; from: number; to: number; parallel: boolean }

// Группы подряд идущих включённых этапов одной колонки: ≥2 — рамка «параллельно».
// Выключенный этап группу не продолжает и в неё не входит.
export function stripGroups(stages: WorkflowStage[]): StripGroup[] {
  const cols = stripColumns(stages);
  const groups: StripGroup[] = [];
  cols.forEach((c, i) => {
    const last = groups[groups.length - 1];
    if (last && c !== null && last.column === c) { last.to = i; last.parallel = true; return; }
    groups.push({ column: c, from: i, to: i, parallel: false });
  });
  return groups;
}

// Целевая позиция при перетаскивании: индекс строки под указателем.
// Клавиатура: -1 вверх, +1 вниз; за границу списка не уходит (null — двигать некуда).
export function keyboardTarget(index: number, delta: -1 | 1, count: number): number | null {
  const to = index + delta;
  return to < 0 || to >= count ? null : to;
}
