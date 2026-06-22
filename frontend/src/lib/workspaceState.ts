import type { Session } from '../types';

// Состояние окна воркспейса, запоминаемое для каждого проекта:
// активный чат, открытый файл, режимы панелей. Зеркалируется в localStorage,
// поэтому переживает и переключение проектов, и перезагрузку PWA.
export interface WorkspaceUIState {
  activeSession: Session | null;
  openFile: string | null;
  fileFullscreen: boolean;
  leftTab: 'sessions' | 'files';
  chatDockExpanded: boolean;
}

const key = (projectId: string) => `ws:${projectId}`;

export function loadWorkspaceState(projectId: string): Partial<WorkspaceUIState> | null {
  try {
    const raw = localStorage.getItem(key(projectId));
    return raw ? (JSON.parse(raw) as Partial<WorkspaceUIState>) : null;
  } catch {
    return null;
  }
}

export function saveWorkspaceState(projectId: string, state: WorkspaceUIState) {
  try {
    localStorage.setItem(key(projectId), JSON.stringify(state));
  } catch {
    // переполнение/недоступность localStorage — не критично
  }
}
