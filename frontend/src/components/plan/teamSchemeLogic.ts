// Чистая логика разворота КОМАНДНОГО плана схемой (флаг visual-plan). В отличие от
// схемы обычного плана (PlanScheme + PlanMap с бэка) модель здесь НЕ участвует:
// схема собирается детерминированно из структуры TeamPlan — волны → под-задачи с
// исполнителями и файлами. Вынесена из компонента по тому же правилу, что
// schemeLogic.ts: юнит-тесты без браузерного окружения, компонент только рисует.
//
// Группировка по волнам и подписи волн — сознательная КОПИЯ семантики TeamPlanView
// (groupByWave/waveHint): текстовая карточка и её схема обязаны показывать волны
// одинаково, но кодом компонент карточки не делим — TeamPlanView не трогаем.

import type { PlanMapNumber, TeamPlanSubtask } from '../../types';

// Подпись группы волны: первая идёт параллельно, следующие ждут предыдущую.
// Копия waveHint из TeamPlanView — менять поведение здесь одноместно нельзя.
export function waveHint(wave: number): string {
  if (wave <= 1) return 'параллельно';
  if (wave === 2) return 'после первой';
  return `после ${wave - 1}-й`;
}

// Волна схемы: номер + готовая подпись + под-задачи. Внутри волны порядок исходного
// массива (его задаёт планировщик) — схема не переупорядочивает чужие решения.
export interface TeamSchemeWave {
  wave: number;
  hint: string;
  items: TeamPlanSubtask[];
}

// Под-задачи по волнам, в порядке возрастания номера волны. Копия семантики
// groupByWave из TeamPlanView: Map по номеру волны + сортировка групп по возрастанию.
// Номера могут быть несмежными (1, 3, 7) — пустых «пропущенных» волн не выдумываем.
export function groupByWave(subtasks: TeamPlanSubtask[]): TeamSchemeWave[] {
  const map = new Map<number, TeamPlanSubtask[]>();
  for (const s of subtasks) {
    const arr = map.get(s.wave) ?? [];
    arr.push(s);
    map.set(s.wave, arr);
  }
  return [...map.entries()]
    .sort((a, b) => a[0] - b[0])
    .map(([wave, items]) => ({ wave, hint: waveHint(wave), items }));
}

// Производные счётчики «Сути»: считаются по фактическим subtasks, а не берутся из
// waveCount/executorCount плана (их пишет бэкенд для подзаголовка карточки; схема
// самодостаточна и живёт по структуре, которую показывает).
export interface TeamSchemeCounts {
  subtasks: number;   // всего под-задач
  waves: number;      // уникальных волн
  executors: number;  // уникальных НАЗНАЧЕННЫХ исполнителей
  unassigned: number; // под-задач без исполнителя
  files: number;      // уникальных файлов в работе
}

export function countsOf(subtasks: TeamPlanSubtask[]): TeamSchemeCounts {
  const waves = new Set<number>();
  const executors = new Set<string>();
  const files = new Set<string>();
  let unassigned = 0;
  for (const s of subtasks) {
    waves.add(s.wave);
    // null/undefined/пустая строка — «не назначен» (тот же falsy-контракт, что у
    // ExecutorChip: personaId ? имя : «не назначен»)
    if (s.executorPersonaId) executors.add(s.executorPersonaId);
    else unassigned++;
    for (const f of s.files) {
      if (f) files.add(f);
    }
  }
  return {
    subtasks: subtasks.length,
    waves: waves.size,
    executors: executors.size,
    unassigned,
    files: files.size,
  };
}

// Пункт блока «Требует вашего внимания» — детерминированный, без модели:
//  • file-conflict — файл более чем в одной под-задаче (две руки в одном файле);
//  • no-executor — под-задача без исполнителя (запускать некому).
// Компонент резолвит имена/заголовки по id сам — здесь только адреса.
export type TeamSchemeAttention =
  | { kind: 'file-conflict'; file: string; subtaskIds: string[] }
  | { kind: 'no-executor'; subtaskId: string };

export function attentionOf(subtasks: TeamPlanSubtask[]): TeamSchemeAttention[] {
  // Файл → id под-задач-владельцев. Set хранит порядок добавления: subtaskIds идут
  // в порядке под-задач, а сами конфликты — в порядке первого появления файла.
  const owners = new Map<string, Set<string>>();
  for (const s of subtasks) {
    for (const f of s.files) {
      if (!f) continue;
      const ids = owners.get(f) ?? new Set<string>();
      ids.add(s.id);
      owners.set(f, ids);
    }
  }
  // Повтор файла ВНУТРИ одной под-задачи — не конфликт: Set дедуплицирует id.
  const conflicts: TeamSchemeAttention[] = [];
  for (const [file, ids] of owners) {
    if (ids.size > 1) conflicts.push({ kind: 'file-conflict', file, subtaskIds: [...ids] });
  }
  const noExecutor: TeamSchemeAttention[] = subtasks
    .filter(s => !s.executorPersonaId)
    .map(s => ({ kind: 'no-executor' as const, subtaskId: s.id }));
  return [...conflicts, ...noExecutor];
}

// Итоговая модель схемы — всё, что нужно компоненту, одной сборкой.
export interface TeamScheme {
  waves: TeamSchemeWave[];
  counts: TeamSchemeCounts;
  attention: TeamSchemeAttention[];
}

export function buildTeamScheme(subtasks: TeamPlanSubtask[]): TeamScheme {
  return {
    waves: groupByWave(subtasks),
    counts: countsOf(subtasks),
    attention: attentionOf(subtasks),
  };
}

// Склонение числительного (ру): копия plural из TeamPlanView — карточка и схема
// склоняют одинаково, делиться кодом с компонентом не будем.
function plural(n: number, one: string, few: string, many: string): string {
  const m10 = n % 10, m100 = n % 100;
  if (m10 === 1 && m100 !== 11) return one;
  if (m10 >= 2 && m10 <= 4 && (m100 < 10 || m100 >= 20)) return few;
  return many;
}

// Ряд чисел «Сути» в формате PlanMap.numbers — компонент рисует пилюлями так же,
// как PlanScheme. Нулевые счётчики не показываем: «0 исполнителей» рядом с «N без
// исполнителя» — шум. Пустой план (0 под-задач) — ряд не строим вовсе.
export function countNumbers(counts: TeamSchemeCounts): PlanMapNumber[] {
  if (counts.subtasks === 0) return [];
  const numbers: PlanMapNumber[] = [{
    value: String(counts.subtasks),
    label: plural(counts.subtasks, 'под-задача', 'под-задачи', 'под-задач'),
  }];
  if (counts.waves > 0) {
    numbers.push({
      value: String(counts.waves),
      label: plural(counts.waves, 'волна', 'волны', 'волн'),
    });
  }
  if (counts.executors > 0) {
    numbers.push({
      value: String(counts.executors),
      label: plural(counts.executors, 'исполнитель', 'исполнителя', 'исполнителей'),
    });
  }
  if (counts.unassigned > 0) {
    numbers.push({
      value: String(counts.unassigned),
      label: plural(
        counts.unassigned,
        'под-задача без исполнителя', 'под-задачи без исполнителя', 'под-задач без исполнителя',
      ),
    });
  }
  if (counts.files > 0) {
    numbers.push({
      value: String(counts.files),
      label: plural(counts.files, 'файл в работе', 'файла в работе', 'файлов в работе'),
    });
  }
  return numbers;
}
