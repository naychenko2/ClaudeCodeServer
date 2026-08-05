import type { Session } from '../types';

// Состояние окна воркспейса, запоминаемое для каждого проекта:
// активный чат, открытый файл, режимы панелей. Зеркалируется в localStorage,
// поэтому переживает и переключение проектов, и перезагрузку PWA.
// Вкладки левой панели проекта — источник правды и для типа, и для восстановления
// из localStorage. Раньше union жил двумя копиями (здесь и в WorkspacePage), а
// рядом стоял третий, рукописный список допустимых значений при чтении — из-за
// чего «Навыки» не переживали перезагрузку: вкладку добавили, в список забыли.
export const LEFT_TABS = ['sessions', 'files', 'changes', 'tasks', 'personas', 'skills', 'tools'] as const;
export type LeftTab = typeof LEFT_TABS[number];

export function isLeftTab(v: unknown): v is LeftTab {
  return typeof v === 'string' && (LEFT_TABS as readonly string[]).includes(v);
}

export interface WorkspaceUIState {
  activeSession: Session | null;
  openFile: string | null;
  fileFullscreen: boolean;
  leftTab: LeftTab;
  fileSubTab?: 'files' | 'knowledge';
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
