// Общие подписи блоков «процесса» фичи (хвост ревью R-D1e, п.4): словарь статуса задачи и
// слово для пустого значения были дословными копиями в экране этапа и его области — здесь
// одна точка правды. Чистый модуль, без DOM.

// Подпись статуса задачи (TaskStatus) — строки области этапа и сводка экрана этапа.
// Вне словаря (бэк опередил фронт) — «не начата», как в stageStatusLabel.
const TASK_STATUS: Record<string, string> = { todo: 'не начата', inProgress: 'в работе', done: 'готово' };
export function taskStatusLabel(status?: string | null): string {
  return TASK_STATUS[status ?? 'todo'] ?? 'не начата';
}

// Слово для отсутствующего числа/значения: «null — словом, а не нулём» (D1d,
// docs/research/ai-fabrika-stage-metrics-cut-2026-11.md §4/§5).
export const NO_DATA = 'нет данных';
