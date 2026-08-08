import type { Session } from '../types';

// Состояние окна воркспейса, запоминаемое для каждого проекта:
// активный чат, открытый файл, режимы панелей. Зеркалируется в localStorage,
// поэтому переживает и переключение проектов, и перезагрузку PWA.
// Вкладки левой панели проекта — источник правды и для типа, и для восстановления
// из localStorage. Раньше union жил двумя копиями (здесь и в WorkspacePage), а
// рядом стоял третий, рукописный список допустимых значений при чтении — из-за
// чего «Навыки» не переживали перезагрузку: вкладку добавили, в список забыли.
export const LEFT_TABS = ['sessions', 'files', 'changes', 'tasks', 'knowledge', 'personas', 'skills', 'tools'] as const;
export type LeftTab = typeof LEFT_TABS[number];

export function isLeftTab(v: unknown): v is LeftTab {
  return typeof v === 'string' && (LEFT_TABS as readonly string[]).includes(v);
}

export interface WorkspaceUIState {
  activeSession: Session | null;
  openFile: string | null;
  leftTab: LeftTab;
}

const key = (projectId: string) => `ws:${projectId}`;

// Режим просмотра файла в центре (сплит рядом с чатом ↔ на весь экран) — ГЛОБАЛЬНОЕ
// предпочтение, одно на все проекты. Раньше оно жило в WorkspaceUIState (per-project)
// и его перебивала каждая точка открытия файла, из-за чего режим приходилось
// переключать в каждом файле заново. Теперь тумблер в шапке файла пишет сюда, а
// точки открытия только читают. Дефолт (нет ключа) — false = сплит рядом с чатом.
const FILE_VIEW_KEY = 'cc_file_view_fullscreen';

export function loadFileFullscreenPref(): boolean {
  try {
    return localStorage.getItem(FILE_VIEW_KEY) === '1';
  } catch {
    return false;
  }
}

export function saveFileFullscreenPref(fullscreen: boolean): void {
  try {
    localStorage.setItem(FILE_VIEW_KEY, fullscreen ? '1' : '0');
  } catch {
    // переполнение/недоступность localStorage — не критично
  }
}

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
