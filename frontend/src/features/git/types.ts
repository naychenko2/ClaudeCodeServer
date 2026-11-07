// Типы домена «Git в разделе «Файлы»» (переехали из types/index.ts, S5-P4). types/index.ts ре-экспортирует их через `export type *` — внешние импорты не меняются.

// === Git в разделе «Файлы» ===

// Одно изменение файла: status — односимвольный код git (M/A/D/R/?/U);
// oldPath заполняется только для переименований (R)
export interface GitFileChange {
  path: string;
  status: string;
  oldPath?: string | null;
  // Статистика строк (git numstat): +added / −deleted. null — бинарный/не считалось.
  added?: number | null;
  deleted?: number | null;
}

// Чат, менявший файл (POST /api/projects/{id}/files/changed-by) — панель «Изменения»:
// бейдж в строке файла + список «Также меняли» в шапке диффа + фильтр «только файлы чата»
export interface ChangedBySession {
  sessionId: string;
  name: string;
  // Файл менялся чатом ТОЛЬКО вне заявленного хода (Bash/скрипты модели, человек в IDE
  // во время хода): в бейдж «Также меняли» такая запись не попадает, в фильтр
  // «только файлы чата» активного чата — попадает (решение прошлой итерации фичи)
  external: boolean;
}

// Статус рабочего дерева проекта (GET /api/projects/{id}/git/status)
export interface GitStatus {
  isRepo: boolean;
  branch: string | null;
  upstream: string | null;
  ahead: number;
  behind: number;
  detached: boolean;
  staged: GitFileChange[];
  unstaged: GitFileChange[];
  untracked: GitFileChange[];
  // Проект открыт как linked git worktree (UI показывает имя папки worktree вместо ветки)
  isWorktree: boolean;
}

// Ветка репозитория: current — текущая (HEAD)
export interface GitBranchInfo {
  name: string;
  current: boolean;
  upstream: string | null;
}

// Настройка промпта AI-описания коммита (GET/PUT /api/projects/{id}/git/commit-prompt)
export interface GitCommitPromptInfo {
  global: string | null;          // глобальный (per-user) промпт
  projectOverride: string | null; // проектный override
  useProject: boolean;            // активен ли проектный override
  effective: string;              // что реально применится к генерации
  default: string;                // дефолтные правила стиля (для плейсхолдера)
}

// Запись истории коммитов (GET /api/projects/{id}/git/log)
export interface GitLogEntry {
  sha: string;
  shortSha: string;
  author: string;
  email: string;
  date: string;
  subject: string;
}

// Детали коммита (GET /api/projects/{id}/git/commits/{sha})
export interface GitCommitDetail {
  sha: string;
  shortSha: string;
  author: string;
  email: string;
  date: string;
  subject: string;
  body: string;
  files: GitFileChange[];
}

// Запись стэша (GET /api/projects/{id}/git/stash)
export interface GitStashEntry {
  index: number;
  message: string;
  date: string;
}

// Строка blame файла (GET /api/projects/{id}/git/blame?path=)
export interface GitBlameLine {
  line: number;
  sha: string;
  shortSha: string;
  author: string;
  date: string;
  content: string;
}

// Удалённый репозиторий и авто-коммит (GET /api/projects/{id}/git/remote)
export interface GitRemoteInfo {
  serverEnabled: boolean;
  remoteUrl: string | null;
  // Фактический origin репозитория (null — не подключён). Отличается от remoteUrl:
  // тот — настройка проекта, и он пуст, если origin добавляли руками мимо продукта.
  originUrl: string | null;
  htmlUrl: string | null;
  autoCommit: boolean;
  autoPush: boolean;
}

// Пометка синхронизации файла/папки для офлайна (состояние FileNode)
export interface SyncMark {
  path: string;
  isDirectory: boolean;
}
