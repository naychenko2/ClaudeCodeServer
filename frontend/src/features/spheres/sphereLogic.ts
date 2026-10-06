import type { SphereMemoryEntry, SphereMemoryResponse, SphereOverview } from '../../types';

// Чистая логика экранов сфер без React и сети: на ней стоят тесты (окружение vitest — node).

type HttpError = Error & { status?: number; body?: { personas?: number } };

export type LoadErrorKind = 'notFound' | 'network';

// Причина, по которой страница сферы не загрузилась: 404 — сферы нет (удалена с другого
// устройства, чужая ссылка), всё остальное — сбой, который лечится повтором
export function describeLoadError(e: unknown): { kind: LoadErrorKind; text: string } {
  const status = (e as HttpError | null)?.status;
  if (status === 404) return { kind: 'notFound', text: 'Такой сферы нет — возможно, её удалили.' };
  const msg = e instanceof Error && e.message ? e.message : '';
  return { kind: 'network', text: msg || 'Не удалось загрузить сферу' };
}

export type DeleteMode = 'loading' | 'error' | 'blocked' | 'confirm';

export function deleteDialogMode(s: { memory: number | null; personas: number; error: string }): DeleteMode {
  if (s.error) return 'error';
  if (s.memory === null) return 'loading';
  return s.personas > 0 ? 'blocked' : 'confirm';
}

// Итог неудачного DELETE: 409 — персоны успели появиться (число — из тела), остальное — текст ошибки
export function deleteFailure(e: unknown): { personas: number } | { error: string } {
  const err = e as HttpError;
  if (err?.status === 409) return { personas: err.body?.personas ?? 1 };
  return { error: err?.message || 'Не удалось удалить сферу' };
}

export type RemoveOutcome =
  | { kind: 'done' }
  | { kind: 'confirm'; team: string[] }
  | { kind: 'error'; error: string };

// «Убрать из сферы»: без команды проект уходит сразу, без вопроса; с командой — нужно подтверждение
export async function startRemoveFlow(deps: {
  loadTeam: () => Promise<SphereOverview['team']>;
  remove: () => Promise<void>;
}): Promise<RemoveOutcome> {
  try {
    const team = await deps.loadTeam();
    if (team.length > 0) return { kind: 'confirm', team: team.map(m => m.name) };
    await deps.remove();
    return { kind: 'done' };
  } catch (e) {
    return { kind: 'error', error: e instanceof Error ? e.message : 'Не удалось убрать проект из сферы' };
  }
}

// Редьюсеры полок памяти: ответ сервера применяем к уже показанному, без перезагрузки
export function memoryAdopted(m: SphereMemoryResponse, projectId: string, entryId: string, adopted: SphereMemoryEntry): SphereMemoryResponse {
  return {
    ...m,
    sphere: [adopted, ...m.sphere.filter(e => e.id !== adopted.id)],
    projects: m.projects.map(p => p.projectId === projectId
      ? { ...p, entries: p.entries.filter(e => e.id !== entryId) }
      : p),
  };
}

export function memoryAdded(m: SphereMemoryResponse, added: SphereMemoryEntry): SphereMemoryResponse {
  return { ...m, sphere: [added, ...m.sphere] };
}

export function memoryRemoved(m: SphereMemoryResponse, entryId: string): SphereMemoryResponse {
  return { ...m, sphere: m.sphere.filter(e => e.id !== entryId) };
}

export const MEMORY_TYPE_LABEL: Record<string, string> = {
  decision: 'решение', convention: 'договорённость', fact: 'факт', glossary: 'термин',
};
