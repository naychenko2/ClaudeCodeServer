// Разбор гостевой реплики-доклада о завершении делегированной задачи (модель Z).
// Маркер — контракт с бэкендом: TaskExecutionService.DelegationReportMarker
// (backend/ClaudeHomeServer/Services/TaskExecutionService.cs). Текст реплики:
// `${MARKER}${title}\n\n${body}` — см. BuildDelegationReportText там же.
export const DELEGATION_REPORT_MARKER = '↩ Отчёт по делегированной задаче: ';

export interface DelegationReport {
  title: string;
  body: string;
}

// Возвращает {title, body}, если текст — гостевая реплика-доклад; иначе null
// (обычная реплика персоны рендерится как раньше, без изменений).
export function parseDelegationReport(text: string): DelegationReport | null {
  if (!text.startsWith(DELEGATION_REPORT_MARKER)) return null;
  const rest = text.slice(DELEGATION_REPORT_MARKER.length);
  const nlIdx = rest.indexOf('\n');
  if (nlIdx === -1) return { title: rest, body: '' };
  return { title: rest.slice(0, nlIdx), body: rest.slice(nlIdx).replace(/^\n+/, '') };
}

// Хвост обрезанного итога — тоже контракт с бэкендом
// (TaskExecutionService.DelegationReportTruncatedTail, требование B2 спеки).
export const DELEGATION_REPORT_TRUNCATED_TAIL = 'Итог показан не полностью — открыть задачу';

// Снять хвост-приглашение, когда открывать уже нечего (задачу удалили после доклада):
// звать к действию, которого нет, — хуже, чем показать обрезанный итог как есть.
export function stripTruncatedTail(body: string): string {
  const trimmed = body.trimEnd();
  return trimmed.endsWith(DELEGATION_REPORT_TRUNCATED_TAIL)
    ? trimmed.slice(0, -DELEGATION_REPORT_TRUNCATED_TAIL.length).trimEnd()
    : body;
}

// Подпись счётчика приложенных файлов на карточке доклада: «1 файл», «3 файла», «5 файлов»
export function filesCountLabel(count: number): string {
  const mod100 = count % 100;
  const mod10 = count % 10;
  const word = mod100 >= 11 && mod100 <= 14 ? 'файлов'
    : mod10 === 1 ? 'файл'
      : mod10 >= 2 && mod10 <= 4 ? 'файла'
        : 'файлов';
  return `${count} ${word}`;
}
