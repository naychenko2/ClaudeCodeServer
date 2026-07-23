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
