// Якорь чтения файла со страницы фичи (FS-F1c): taskId — дерево задачи, branch — подпись ветки.
// Открытие из дерева или чата якоря не несёт (null) — вьюер читает корень проекта.
export interface FileTaskAnchor { path: string; taskId?: string; branch?: string }

export function nextFileTaskAnchor(path: string, opts?: { taskId?: string; branch?: string }): FileTaskAnchor | null {
  return opts?.taskId || opts?.branch ? { path, taskId: opts.taskId, branch: opts.branch } : null;
}

// Якорь действует только на файл, для которого его поставили (как openFileSessionId).
export function anchorFor(anchor: FileTaskAnchor | null, openFile: string | null): { taskId?: string; branch?: string } {
  return anchor && anchor.path === openFile ? { taskId: anchor.taskId, branch: anchor.branch } : {};
}
