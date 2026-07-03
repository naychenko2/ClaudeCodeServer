// Разбор блока `export const meta = { … }` из скрипта Workflow-инструмента.
// Единая реализация для ленты чата (ChatPanel) и артефактов (useSessionArtifacts).
// Блок меты вырезается по балансу фигурных скобок, чтобы регексы полей
// не зацепили одноимённые поля из тела скрипта.

export interface WorkflowPhase {
  title: string;
  detail?: string;
}

export interface WorkflowMeta {
  name?: string;
  description?: string;
  phases?: WorkflowPhase[];
}

// Значение строкового поля меты: `поле: 'значение'` (кавычки любые, без переносов)
function metaField(metaStr: string, field: string): string | undefined {
  return metaStr.match(new RegExp(`${field}\\s*:\\s*['"\`]([^'"\`\\n]+)['"\`]`))?.[1];
}

export function parseWorkflowMeta(input: unknown): WorkflowMeta | null {
  const inp = input as Record<string, unknown> | null;
  const script = typeof inp?.script === 'string' ? inp.script : null;
  if (!script) return null;

  const metaStart = script.indexOf('export const meta');
  if (metaStart === -1) return null;
  const braceStart = script.indexOf('{', metaStart);
  if (braceStart === -1) return null;

  let depth = 0, metaEnd = -1;
  for (let i = braceStart; i < script.length; i++) {
    if (script[i] === '{') depth++;
    else if (script[i] === '}') { depth--; if (depth === 0) { metaEnd = i; break; } }
  }
  if (metaEnd === -1) return null;
  const metaStr = script.slice(braceStart, metaEnd + 1);

  const name = metaField(metaStr, 'name');
  const description = metaField(metaStr, 'description');

  const phases: WorkflowPhase[] = [];
  const phasesPos = metaStr.indexOf('phases:');
  if (phasesPos !== -1) {
    const bracketStart = metaStr.indexOf('[', phasesPos);
    if (bracketStart !== -1) {
      let bd = 0, bracketEnd = -1;
      for (let i = bracketStart; i < metaStr.length; i++) {
        if (metaStr[i] === '[') bd++;
        else if (metaStr[i] === ']') { bd--; if (bd === 0) { bracketEnd = i; break; } }
      }
      if (bracketEnd !== -1) {
        const phasesStr = metaStr.slice(bracketStart + 1, bracketEnd);
        const phaseRe = /\{[^}]*title:\s*['"`]([^'"`]+)['"`](?:[^}]*detail:\s*['"`]([^'"`]+)['"`])?[^}]*\}/g;
        let m;
        while ((m = phaseRe.exec(phasesStr)) !== null) phases.push({ title: m[1], detail: m[2] });
      }
    }
  }
  return { name, description, phases: phases.length > 0 ? phases : undefined };
}

// Заголовок workflow для UI. Приоритет — человекочитаемый meta.description
// (пишется обычным языком), kebab-имя meta.name / input.name — фоллбэк.
export function workflowName(input: Record<string, unknown>): string {
  const meta = parseWorkflowMeta(input);
  if (meta?.description) return meta.description;
  if (meta?.name) return meta.name;
  // Сохранённый workflow запускается по имени, без script
  if (typeof input.name === 'string' && input.name) return input.name;
  return 'Workflow';
}
